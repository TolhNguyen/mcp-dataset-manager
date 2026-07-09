// api/Services/DashboardShareService.cs
using Dapper;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public sealed record ShareRecord(
    Guid Id, Guid DashboardId, Guid UserId, string PinHash,
    DateTime ExpiresAt, int FailedPinCount, DateTime? LockedUntil, int ViewCount);

/// <summary>
/// Persistence for dashboard share links. Tokens and PINs are stored hashed only —
/// the plaintext pair leaves this class exactly once, in CreateAsync's response.
/// </summary>
public class DashboardShareService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<ApiResult<object>> CreateAsync(
        Guid userId, Guid dashboardId, string? pin, int? expiresInDays, string createdBy, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var owns = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dashboards WHERE id = @Id AND user_id = @UserId",
            new { Id = dashboardId, UserId = userId });
        if (owns == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
        }

        var active = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dashboard_shares
            WHERE dashboard_id = @Id AND revoked_at IS NULL AND expires_at > NOW()
            """, new { Id = dashboardId });
        if (active >= ShareCrypto.MaxActiveSharesPerDashboard)
        {
            return ApiResult<object>.Fail(ErrorCodes.ShareLimitReached,
                $"This dashboard already has {ShareCrypto.MaxActiveSharesPerDashboard} active share links. Revoke one first.");
        }

        if (pin is not null && (pin.Length < 4 || pin.Length > 32))
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "PIN must be 4-32 characters.");
        }

        var token = ShareCrypto.GenerateToken();
        var actualPin = pin ?? ShareCrypto.GeneratePin();
        var days = ShareCrypto.ClampExpiryDays(expiresInDays);
        var id = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddDays(days);

        await conn.ExecuteAsync("""
            INSERT INTO dashboard_shares (id, dashboard_id, user_id, token_hash, pin_hash, created_by, expires_at)
            VALUES (@Id, @DashboardId, @UserId, @TokenHash, @PinHash, @CreatedBy, @ExpiresAt)
            """, new
        {
            Id = id, DashboardId = dashboardId, UserId = userId,
            TokenHash = ShareCrypto.HashToken(token),
            PinHash = ShareCrypto.HashPin(actualPin),
            CreatedBy = createdBy, ExpiresAt = expiresAt
        });

        var publicUrl = (configuration["Oauth:PublicUrl"] ?? "http://localhost").TrimEnd('/');
        return ApiResult<object>.Ok(new
        {
            share_id = id,
            share_url = $"{publicUrl}/share/{token}",
            pin = actualPin,
            expires_at = expiresAt,
            note = "The link and PIN are shown only once. Deliver them to the viewer over two separate channels when possible."
        });
    }

    public async Task<ShareRecord?> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(ShareCrypto.TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ShareRecord>("""
            SELECT id AS Id, dashboard_id AS DashboardId, user_id AS UserId, pin_hash AS PinHash,
                   expires_at AS ExpiresAt, failed_pin_count AS FailedPinCount,
                   locked_until AS LockedUntil, view_count AS ViewCount
            FROM dashboard_shares
            WHERE token_hash = @TokenHash AND revoked_at IS NULL AND expires_at > NOW()
            """, new { TokenHash = ShareCrypto.HashToken(token.Trim()) });
    }

    public async Task<ApiResult<object>> ListAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync("""
            SELECT id AS share_id, created_by, created_at, expires_at, view_count, last_viewed_at,
                   (revoked_at IS NOT NULL) AS revoked
            FROM dashboard_shares s
            WHERE s.dashboard_id = @DashboardId AND s.user_id = @UserId AND s.revoked_at IS NULL AND s.expires_at > NOW()
            ORDER BY s.created_at DESC
            """, new { DashboardId = dashboardId, UserId = userId });
        return ApiResult<object>.Ok(new { shares = rows });
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(
            "UPDATE dashboard_shares SET revoked_at = NOW() WHERE id = @Id AND user_id = @UserId AND revoked_at IS NULL",
            new { Id = shareId, UserId = userId });
        return affected > 0;
    }

    public async Task RegisterPinFailureAsync(Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var failed = await conn.ExecuteScalarAsync<int>("""
            UPDATE dashboard_shares SET failed_pin_count = failed_pin_count + 1
            WHERE id = @Id RETURNING failed_pin_count
            """, new { Id = shareId });
        var lockedUntil = ShareCrypto.NextLockout(failed, DateTime.UtcNow);
        if (lockedUntil is not null)
        {
            await conn.ExecuteAsync(
                "UPDATE dashboard_shares SET locked_until = @Until WHERE id = @Id",
                new { Until = lockedUntil, Id = shareId });
        }
    }

    public async Task ResetPinFailuresAsync(Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE dashboard_shares SET failed_pin_count = 0, locked_until = NULL WHERE id = @Id",
            new { Id = shareId });
    }

    public async Task RecordViewAsync(Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE dashboard_shares SET view_count = view_count + 1, last_viewed_at = NOW() WHERE id = @Id",
            new { Id = shareId });
    }
}
