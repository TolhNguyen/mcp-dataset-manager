using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Dapper;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.BackgroundJobs;
using ExcelDatasetManager.Api.Endpoints;
using ExcelDatasetManager.Api.Middleware;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using ExcelDatasetManager.Api.Services.Connectors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// Configuration validation (fail fast on missing/weak secrets)
// ============================================================

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32 || jwtKey.StartsWith("CHANGE_ME"))
{
    throw new InvalidOperationException(
        "Configuration Jwt:Key must be set to a strong random secret of at least 32 characters. " +
        "Set it via the JWT_KEY environment variable or appsettings.");
}

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

var encryptionKey = builder.Configuration["Encryption:MasterKey"];
if (string.IsNullOrWhiteSpace(encryptionKey) || encryptionKey.Length < 32)
{
    throw new InvalidOperationException(
        "Configuration Encryption:MasterKey must be set to a random secret of at least 32 characters. " +
        "Set it via the EDM_ENCRYPTION_KEY environment variable.");
}

// ============================================================
// JSON: snake_case across the wire for spec consistency
// ============================================================

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

// ============================================================
// CORS
// ============================================================

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length == 0 || allowedOrigins.Contains("*"))
        {
            // In development we may want to fall back to "any origin" — only do that explicitly via "*".
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }
        else
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

// ============================================================
// Auth: JWT (user-scoped) + API key (dataset-scoped)
// ============================================================

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var hasBearerHeader =
                    context.Request.Headers.TryGetValue("Authorization", out var authorization)
                    && authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

                if (!hasBearerHeader && context.Request.Cookies.TryGetValue(JwtCookie.CookieName, out var token))
                {
                    context.Token = token;
                }
                else if (!hasBearerHeader
                         && JwtCookie.IsDownloadPath(context.Request.Path)
                         && context.Request.Query.TryGetValue("access_token", out var queryToken))
                {
                    context.Token = queryToken.ToString();
                }

                return Task.CompletedTask;
            }
        };

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    // Default policy accepts BOTH JWT and API key (user-scoped PAT or dataset-scoped key).
    // This lets the MCP server (using a PAT) call list/get/download endpoints, and lets
    // browsers (using JWT) call everything as before.
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationOptions.SchemeName)
        .RequireAuthenticatedUser()
        .Build();

    // "QueryAccess" — same as default, kept as a separate policy so future tightening is localized.
    options.AddPolicy("QueryAccess", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationOptions.SchemeName);
        policy.RequireAuthenticatedUser();
    });

    // "JwtOnly" — for management endpoints we want to keep out of API-key reach
    // (creating/revoking tokens, deleting datasets, uploading new files).
    options.AddPolicy("JwtOnly", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    });
});

// ============================================================
// Rate limiting (login + global)
// ============================================================

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(
            """{"success":false,"error":{"code":"TOO_MANY_REQUESTS","message":"Too many requests. Please try again shortly."}}""",
            ct);
    };

    // Per-IP fixed window for the auth endpoints.
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Per-user query throttle for authenticated callers, falling back to IP for anonymous requests.
    options.AddPolicy("query", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitPartitionKey.For(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// ============================================================
// Upload size (per spec)
// ============================================================

var maxUploadMb = builder.Configuration.GetValue<int?>("Upload:MaxFileSizeMb") ?? 100;
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUploadMb * 1024L * 1024L;
});
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = maxUploadMb * 1024L * 1024L;
});

// ============================================================
// Services
// ============================================================

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<MigrationRunner>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DatasetService>();
builder.Services.AddScoped<DatasetApiKeyService>();
builder.Services.AddScoped<UserApiKeyService>();
builder.Services.AddScoped<OAuthService>();
builder.Services.AddScoped<DuckDbQueryService>();
builder.Services.AddScoped<FileParserService>();
builder.Services.AddScoped<ManifestGenerator>();
builder.Services.AddSingleton<IExternalDbConnector, PostgresDbConnector>();
builder.Services.AddSingleton<IExternalDbConnector, MySqlDbConnector>();
builder.Services.AddSingleton<IExternalDbConnector, MsSqlDbConnector>();
builder.Services.AddSingleton<IExternalDbConnector, BigQueryDbConnector>();
builder.Services.AddScoped<DbConnectionService>();
builder.Services.AddScoped<ExternalSchemaService>();
builder.Services.AddSingleton<AiTokenBudgetService>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<FileStorageService>();
builder.Services.AddSingleton<HeaderNormalizer>();
builder.Services.AddSingleton<QueryValidator>();
builder.Services.AddSingleton<ParquetWriter>();
builder.Services.AddSingleton<ParsingJobQueue>();
builder.Services.AddHostedService<ParsingHostedService>();

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Catch-all exception handler MUST be first so it wraps every other middleware.
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Behind a trusted reverse proxy (Caddy), honor X-Forwarded-* so rate limiting
// and logs see the real client IP. Only enabled explicitly via config.
if (builder.Configuration.GetValue<bool?>("Proxy:TrustForwardedHeaders") == true)
{
    var forwardedOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };
    forwardedOptions.KnownNetworks.Clear();
    forwardedOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedOptions);
}

app.Use(async (ctx, next) =>
{
    if (JwtCookie.TryGetBearerToken(ctx, out var bearerToken))
    {
        ctx.Response.OnStarting(() =>
        {
            JwtCookie.Set(ctx, bearerToken);
            return Task.CompletedTask;
        });
    }

    if (JwtCookie.IsDownloadPath(ctx.Request.Path)
        && !JwtCookie.TryGetBearerToken(ctx, out _)
        && !ctx.Request.Cookies.ContainsKey(JwtCookie.CookieName)
        && !ctx.Request.Query.ContainsKey("access_token"))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(
            Path.Combine(app.Environment.WebRootPath, "download-bridge.html"),
            ctx.RequestAborted);
        return;
    }

    await next();
});

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.PhysicalPath;
        if (path is not null
            && (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    }
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// ============================================================
// DB init
// ============================================================

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MigrationRunner>().RunAsync();

    var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    var parsingQueue = scope.ServiceProvider.GetRequiredService<ParsingJobQueue>();
    await using var conn = await dataSource.OpenConnectionAsync();
    var pendingJobs = await conn.QueryAsync<PendingParsingJobRow>("""
        SELECT user_id AS UserId, id AS DatasetId
        FROM datasets
        WHERE status = 'processing'
        ORDER BY created_at
        """);

    foreach (var job in pendingJobs)
    {
        await parsingQueue.EnqueueAsync(new ParsingJob(job.UserId, job.DatasetId), CancellationToken.None);
    }
}

// ============================================================
// Endpoints
// ============================================================

app.MapGet("/health", () => Results.Ok(new { status = "ok", app = "Excel Dataset Manager", version = "1.0" }));

app.MapAuthEndpoints();
app.MapDatasetEndpoints();
app.MapApiKeyEndpoints();
app.MapQueryEndpoints();
app.MapOAuthEndpoints();
app.MapConnectionEndpoints();

app.Run();

internal sealed record PendingParsingJobRow(Guid UserId, Guid DatasetId);
