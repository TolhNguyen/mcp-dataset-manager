using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Dapper;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.BackgroundJobs;
using ExcelDatasetManager.Api.Middleware;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
const string JwtCookieName = "edm_token";

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

                if (!hasBearerHeader && context.Request.Cookies.TryGetValue(JwtCookieName, out var token))
                {
                    context.Token = token;
                }
                else if (!hasBearerHeader
                         && IsDownloadPath(context.Request.Path)
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

    // Per-IP query throttle for the query endpoint.
    options.AddPolicy("query", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
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
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DatasetService>();
builder.Services.AddScoped<DatasetApiKeyService>();
builder.Services.AddScoped<UserApiKeyService>();
builder.Services.AddScoped<DuckDbQueryService>();
builder.Services.AddScoped<FileParserService>();
builder.Services.AddScoped<ManifestGenerator>();
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

app.Use(async (ctx, next) =>
{
    if (TryGetBearerToken(ctx, out var bearerToken))
    {
        ctx.Response.OnStarting(() =>
        {
            SetJwtCookie(ctx, bearerToken);
            return Task.CompletedTask;
        });
    }

    if (IsDownloadPath(ctx.Request.Path)
        && !TryGetBearerToken(ctx, out _)
        && !ctx.Request.Cookies.ContainsKey(JwtCookieName)
        && !ctx.Request.Query.ContainsKey("access_token"))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(GetDownloadBridgeHtml(), ctx.RequestAborted);
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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ============================================================
// DB init
// ============================================================

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

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

var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");

auth.MapPost("/register", async (RegisterRequest req, HttpContext ctx, AuthService svc, CancellationToken ct) =>
{
    var result = await svc.RegisterAsync(req, ct);
    if (result.Success)
    {
        SetJwtCookie(ctx, result.Data!.Token);
    }

    return result.Success
        ? Results.Ok(new { success = true, user = result.Data!.User, token = result.Data.Token })
        : Results.BadRequest(new { success = false, error = result.Error });
});

auth.MapPost("/login", async (LoginRequest req, HttpContext ctx, AuthService svc, CancellationToken ct) =>
{
    var result = await svc.LoginAsync(req, ct);
    if (result.Success)
    {
        SetJwtCookie(ctx, result.Data!.Token);
    }

    return result.Success
        ? Results.Ok(new { success = true, user = result.Data!.User, token = result.Data.Token })
        : Results.BadRequest(new { success = false, error = result.Error });
});

auth.MapGet("/me", async (ClaimsPrincipal principal, AuthService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId();
    if (userId is null) return Results.Unauthorized();
    var result = await svc.MeAsync(userId.Value, ct);
    return result.Success
        ? Results.Ok(new { success = true, user = result.Data })
        : Results.Unauthorized();
}).RequireAuthorization();

auth.MapPost("/logout", (HttpContext ctx) =>
{
    ClearJwtCookie(ctx);
    return Results.Ok(new
    {
        success = true,
        message = "Token issuance is stateless — client should discard its token."
    });
}).RequireAuthorization();

// ============================================================
// Dataset endpoints
// ============================================================

var datasets = app.MapGroup("/api/datasets").RequireAuthorization();

datasets.MapGet("/", async (ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var payload = await svc.ListAsync(userId, ct);
    return Results.Ok(new { success = true, limit = payload.Limit, datasets = payload.Datasets });
});

datasets.MapPost("/", async (HttpContext ctx, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;

    if (!ctx.Request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            success = false,
            error = new { code = ErrorCodes.InvalidRequest, message = "Content-Type must be multipart/form-data." }
        });
    }

    var form = await ctx.Request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    var name = form["name"].ToString();

    var result = await svc.UploadAsync(userId, name, file, ct);

    return result.Success
        ? Results.Ok(new { success = true, dataset = result.Data, message = "Dataset uploaded successfully and is being processed." })
        : Results.BadRequest(new { success = false, error = result.Error });
}).RequireAuthorization("JwtOnly");

datasets.MapGet("/{datasetId:guid}", async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.GetDetailAsync(userId, datasetId, ct);
    return result.Success
        ? Results.Ok(new { success = true, dataset = result.Data!.Dataset, tables = result.Data.Tables })
        : Results.NotFound(new { success = false, error = result.Error });
});

datasets.MapDelete("/{datasetId:guid}", async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.DeleteAsync(userId, datasetId, ct);
    return result.Success
        ? Results.Ok(new { success = true, data = result.Data })
        : Results.NotFound(new { success = false, error = result.Error });
}).RequireAuthorization("JwtOnly");

datasets.MapGet("/{datasetId:guid}/download/original",
    async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var dl = await svc.GetOriginalDownloadAsync(userId, datasetId, ct);
    if (dl is null) return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset or file not found." } });
    return Results.File(dl.Path, dl.ContentType, dl.DownloadName);
});

datasets.MapGet("/{datasetId:guid}/download/manifest",
    async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var dl = await svc.GetManifestDownloadAsync(userId, datasetId, ct);
    if (dl is null) return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Manifest not available yet." } });
    return Results.File(dl.Path, "text/markdown; charset=utf-8", dl.DownloadName);
});

// ============================================================
// API key management (JWT only — never via API key itself)
// ============================================================

datasets.MapPost("/{datasetId:guid}/api-keys",
    async (Guid datasetId, CreateDatasetApiKeyRequest req, ClaimsPrincipal principal, DatasetApiKeyService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.CreateAsync(userId, datasetId, req.Name, ct);
    return result.Success
        ? Results.Ok(new { success = true, data = result.Data })
        : Results.BadRequest(new { success = false, error = result.Error });
}).RequireAuthorization("JwtOnly");

datasets.MapGet("/{datasetId:guid}/api-keys",
    async (Guid datasetId, ClaimsPrincipal principal, DatasetApiKeyService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.ListAsync(userId, datasetId, ct);
    return result.Success
        ? Results.Ok(new { success = true, data = result.Data })
        : Results.NotFound(new { success = false, error = result.Error });
}).RequireAuthorization("JwtOnly");

datasets.MapDelete("/{datasetId:guid}/api-keys/{keyId:guid}",
    async (Guid datasetId, Guid keyId, ClaimsPrincipal principal, DatasetApiKeyService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.RevokeAsync(userId, datasetId, keyId, ct);
    return result.Success
        ? Results.Ok(new { success = true, data = result.Data })
        : Results.NotFound(new { success = false, error = result.Error });
}).RequireAuthorization("JwtOnly");

// ============================================================
// User personal access tokens (for MCP / long-lived integrations)
// JWT only — you can't bootstrap a new PAT using an existing PAT.
// ============================================================

var userKeys = app.MapGroup("/api/user/api-keys").RequireAuthorization("JwtOnly");

userKeys.MapPost("/", async (CreateDatasetApiKeyRequest req, ClaimsPrincipal principal, UserApiKeyService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.CreateAsync(userId, req.Name, ct);
    return result.Success
        ? Results.Ok(new { success = true, data = result.Data })
        : Results.BadRequest(new { success = false, error = result.Error });
});

userKeys.MapGet("/", async (ClaimsPrincipal principal, UserApiKeyService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.ListAsync(userId, ct);
    return Results.Ok(new { success = true, data = result.Data });
});

userKeys.MapDelete("/{tokenId:guid}", async (Guid tokenId, ClaimsPrincipal principal, UserApiKeyService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId()!.Value;
    var result = await svc.RevokeAsync(userId, tokenId, ct);
    return result.Success
        ? Results.Ok(new { success = true, data = result.Data })
        : Results.NotFound(new { success = false, error = result.Error });
});

// ============================================================
// Query endpoint (JWT OR API key, optional dataset scope check)
// ============================================================

app.MapPost("/api/datasets/{datasetId:guid}/query",
    async (Guid datasetId, QueryRequest req, ClaimsPrincipal principal, DuckDbQueryService svc, CancellationToken ct) =>
{
    var userId = principal.GetUserId();
    if (userId is null) return Results.Unauthorized();

    // If the principal came from an API key, enforce it matches the URL dataset.
    var scopedDatasetId = principal.GetScopedDatasetId();
    if (scopedDatasetId is not null && scopedDatasetId != datasetId)
    {
        return Results.Forbid();
    }

    var result = await svc.QueryAsync(userId.Value, datasetId, req, ct);
    return Results.Ok(result);
})
.RequireAuthorization("QueryAccess")
.RequireRateLimiting("query");

app.Run();

static void SetJwtCookie(HttpContext ctx, string token)
{
    ctx.Response.Cookies.Append("edm_token", token, new CookieOptions
    {
        HttpOnly = false,
        SameSite = SameSiteMode.Lax,
        Secure = ctx.Request.IsHttps,
        Path = "/",
        MaxAge = TimeSpan.FromDays(7)
    });
}

static void ClearJwtCookie(HttpContext ctx)
{
    ctx.Response.Cookies.Delete("edm_token", new CookieOptions
    {
        SameSite = SameSiteMode.Lax,
        Secure = ctx.Request.IsHttps,
        Path = "/"
    });
}

static bool IsDownloadPath(PathString path)
{
    return path.StartsWithSegments("/api/datasets", out var remaining)
           && remaining.Value?.Contains("/download/", StringComparison.OrdinalIgnoreCase) == true;
}

static bool TryGetBearerToken(HttpContext ctx, out string token)
{
    token = "";
    if (!ctx.Request.Headers.TryGetValue("Authorization", out var authorization))
    {
        return false;
    }

    const string prefix = "Bearer ";
    var value = authorization.ToString();
    if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    token = value[prefix.Length..].Trim();
    return !string.IsNullOrWhiteSpace(token);
}

static string GetDownloadBridgeHtml() => """
<!doctype html>
<html lang="vi">
<head>
    <meta charset="utf-8" />
    <title>Dang tai file...</title>
    <style>
        body { font-family: system-ui, sans-serif; margin: 48px; color: #0f172a; }
        .muted { color: #64748b; }
        .error { color: #b91c1c; }
    </style>
</head>
<body>
    <h1>Dang tai file...</h1>
    <p id="status" class="muted">Vui long doi trong giay lat.</p>
    <p><a href="/dashboard.html">Quay lai dashboard</a></p>
    <script>
    (async () => {
        const status = document.getElementById('status');
        const token = localStorage.getItem('edm_token');
        if (!token) {
            window.location.replace('/login.html');
            return;
        }

        try {
            const response = await fetch(window.location.href, {
                headers: { Authorization: `Bearer ${token}` }
            });

            if (!response.ok) {
                status.className = 'error';
                status.textContent = `Khong tai duoc file (HTTP ${response.status}).`;
                return;
            }

            const blob = await response.blob();
            const disposition = response.headers.get('content-disposition') || '';
            const encoded = disposition.match(/filename\*=UTF-8''([^;]+)/i);
            const quoted = disposition.match(/filename="?([^";]+)"?/i);
            const fileName = encoded ? decodeURIComponent(encoded[1]) : quoted ? quoted[1] : 'download';
            const url = URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(url);
            status.textContent = 'Da bat dau tai file.';
            setTimeout(() => history.length > 1 ? history.back() : window.location.replace('/dashboard.html'), 700);
        } catch (err) {
            status.className = 'error';
            status.textContent = err.message || 'Khong tai duoc file.';
        }
    })();
    </script>
</body>
</html>
""";

internal sealed record PendingParsingJobRow(Guid UserId, Guid DatasetId);
