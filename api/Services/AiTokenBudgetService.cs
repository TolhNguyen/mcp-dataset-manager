using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExcelDatasetManager.Api.Models;

namespace ExcelDatasetManager.Api.Services;

public sealed class AiTokenBudgetService(IConfiguration configuration)
{
    private static readonly ConcurrentDictionary<string, ConfirmationEntry> Confirmations = new();

    public AiBudgetDecision Decide(object payload, QueryOptions? options, string confirmationScope = "default")
    {
        var estimatedTokens = EstimateTokens(payload);
        var safeMaxTokens = options?.MaxTokens is > 0
            ? Math.Min(options.MaxTokens.Value, SafeMaxTokens)
            : SafeMaxTokens;
        var hardMaxTokens = HardMaxTokens;
        var responseMode = string.IsNullOrWhiteSpace(options?.ResponseMode)
            ? "ai_safe"
            : options.ResponseMode.Trim().ToLowerInvariant();

        if (responseMode == "summary")
        {
            return new AiBudgetDecision(
                Status: "summary",
                EstimatedTokens: estimatedTokens,
                SafeMaxTokens: safeMaxTokens,
                HardMaxTokens: hardMaxTokens,
                RequiresConfirmation: false,
                Blocked: false,
                AllowRaw: false,
                ConfirmationId: null);
        }

        if (estimatedTokens > hardMaxTokens)
        {
            return new AiBudgetDecision(
                Status: "blocked",
                EstimatedTokens: estimatedTokens,
                SafeMaxTokens: safeMaxTokens,
                HardMaxTokens: hardMaxTokens,
                RequiresConfirmation: false,
                Blocked: true,
                AllowRaw: false,
                ConfirmationId: null);
        }

        if (estimatedTokens <= safeMaxTokens)
        {
            return new AiBudgetDecision(
                Status: "safe",
                EstimatedTokens: estimatedTokens,
                SafeMaxTokens: safeMaxTokens,
                HardMaxTokens: hardMaxTokens,
                RequiresConfirmation: false,
                Blocked: false,
                AllowRaw: true,
                ConfirmationId: null);
        }

        if (options?.AllowLargeResult == true
            && !string.IsNullOrWhiteSpace(options.ConfirmationId)
            && IsConfirmationValid(options.ConfirmationId, confirmationScope, estimatedTokens))
        {
            return new AiBudgetDecision(
                Status: "confirmed",
                EstimatedTokens: estimatedTokens,
                SafeMaxTokens: safeMaxTokens,
                HardMaxTokens: hardMaxTokens,
                RequiresConfirmation: false,
                Blocked: false,
                AllowRaw: true,
                ConfirmationId: options.ConfirmationId);
        }

        var confirmationId = CreateConfirmation(confirmationScope, estimatedTokens);
        return new AiBudgetDecision(
            Status: "requires_confirmation",
            EstimatedTokens: estimatedTokens,
            SafeMaxTokens: safeMaxTokens,
            HardMaxTokens: hardMaxTokens,
            RequiresConfirmation: true,
            Blocked: false,
            AllowRaw: false,
            ConfirmationId: confirmationId);
    }

    public int EstimateTokens(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return Math.Max(1, (int)Math.Ceiling(json.Length / (double)CharsPerToken));
    }

    public int PreviewRows => configuration.GetValue<int?>("Query:PreviewRows") ?? 10;
    private int SafeMaxTokens => configuration.GetValue<int?>("Query:SafeMaxTokens") ?? 32000;
    private int HardMaxTokens => configuration.GetValue<int?>("Query:HardMaxTokens") ?? 512000;
    private int CharsPerToken => Math.Max(1, configuration.GetValue<int?>("Query:TokenEstimationCharsPerToken") ?? 4);
    private int ConfirmationTtlMinutes => Math.Max(1, configuration.GetValue<int?>("Query:ConfirmationTTLMinutes") ?? 15);

    private string CreateConfirmation(string scope, int estimatedTokens)
    {
        CleanupExpiredConfirmations();
        var id = Guid.NewGuid().ToString("N");
        Confirmations[id] = new ConfirmationEntry(
            ScopeHash: Hash(scope),
            EstimatedTokens: estimatedTokens,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(ConfirmationTtlMinutes));
        return id;
    }

    private static bool IsConfirmationValid(string id, string scope, int estimatedTokens)
    {
        CleanupExpiredConfirmations();
        if (!Confirmations.TryGetValue(id, out var entry)) return false;
        if (entry.ExpiresAt < DateTimeOffset.UtcNow) return false;
        if (!string.Equals(entry.ScopeHash, Hash(scope), StringComparison.Ordinal)) return false;
        return entry.EstimatedTokens == estimatedTokens;
    }

    private static void CleanupExpiredConfirmations()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in Confirmations)
        {
            if (item.Value.ExpiresAt < now)
            {
                Confirmations.TryRemove(item.Key, out _);
            }
        }
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private sealed record ConfirmationEntry(string ScopeHash, int EstimatedTokens, DateTimeOffset ExpiresAt);
}

public sealed record AiBudgetDecision(
    string Status,
    int EstimatedTokens,
    int SafeMaxTokens,
    int HardMaxTokens,
    bool RequiresConfirmation,
    bool Blocked,
    bool AllowRaw,
    string? ConfirmationId);
