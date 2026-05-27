using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using ExcelDatasetManager.Api.Models;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public class AuthService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<ApiResult<AuthPayload>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return ApiResult<AuthPayload>.Fail(ErrorCodes.ValidationError, "Email is required.");
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8)
        {
            return ApiResult<AuthPayload>.Fail(ErrorCodes.PasswordTooShort, "Password must be at least 8 characters.");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE email = @Email",
            new { Email = email });

        if (exists > 0)
        {
            return ApiResult<AuthPayload>.Fail(ErrorCodes.EmailExists, "Email is already registered.");
        }

        var userId = Guid.NewGuid();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12);

        await conn.ExecuteAsync(
            "INSERT INTO users(id, email, password_hash) VALUES(@Id, @Email, @PasswordHash)",
            new { Id = userId, Email = email, PasswordHash = passwordHash });

        var user = new UserDto(userId, email);
        return ApiResult<AuthPayload>.Ok(new AuthPayload(user, CreateToken(user)));
    }

    public async Task<ApiResult<AuthPayload>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT id AS Id, email AS Email, password_hash AS PasswordHash FROM users WHERE email = @Email",
            new { Email = email });

        // Always run BCrypt.Verify, even with a fake hash, to keep timing roughly constant
        // and avoid leaking the existence of an email through response time.
        var fakeHash = "$2a$12$abcdefghijklmnopqrstuv1234567890abcdefghijklmnopqrstuv1234";
        var ok = row is not null
            ? BCrypt.Net.BCrypt.Verify(request.Password, row.PasswordHash)
            : BCrypt.Net.BCrypt.Verify(request.Password, fakeHash) && false;

        if (!ok || row is null)
        {
            return ApiResult<AuthPayload>.Fail(ErrorCodes.InvalidCredentials, "Invalid email or password.");
        }

        var user = new UserDto(row.Id, row.Email);
        return ApiResult<AuthPayload>.Ok(new AuthPayload(user, CreateToken(user)));
    }

    public async Task<ApiResult<UserDto>> MeAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var user = await conn.QuerySingleOrDefaultAsync<UserDto>(
            "SELECT id AS Id, email AS Email FROM users WHERE id = @Id",
            new { Id = userId });

        return user is null
            ? ApiResult<UserDto>.Fail(ErrorCodes.Unauthorized, "User not found.")
            : ApiResult<UserDto>.Ok(user);
    }

    private string CreateToken(UserDto user)
    {
        var key = configuration["Jwt:Key"]!;
        var issuer = configuration["Jwt:Issuer"]!;
        var audience = configuration["Jwt:Audience"]!;

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Email, user.Email)
            },
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    private sealed record UserRow(Guid Id, string Email, string PasswordHash);
}

public record AuthPayload(UserDto User, string Token);
