namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Pure validation rules for dashboards and dashboard widgets. No I/O, no DB access —
/// safe to unit test directly (see DashboardGuardTests).
///
/// Note: SQL read-only validation (checking the query is a SELECT, safe against the
/// dataset's dialect, etc.) is NOT done here — it requires the dataset context and is
/// performed by the service layer. This guard only checks title/sql-non-empty/chartType.
/// </summary>
public static class DashboardGuard
{
    public static readonly string[] ChartTypes = ["table", "line", "bar", "pie", "stat"];

    public const string KindGrid = "grid";
    public const string KindCustom = "custom";

    public const int MinRefreshSec = 30;
    public const int MaxWidgetsPerDashboard = 20;
    public const int MaxDashboardsPerUser = 10;
    public const int MaxTitleChars = 255;

    /// <summary>
    /// Validates a widget create request. Returns an error message, or null when valid.
    /// </summary>
    public static string? ValidateCreate(string? title, string? sql, string? chartType)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "title is required.";
        }
        if (title.Length > MaxTitleChars)
        {
            return $"title must be at most {MaxTitleChars} characters.";
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return "sql is required.";
        }

        if (chartType is null || !ChartTypes.Contains(chartType))
        {
            return $"chartType must be one of: {string.Join(", ", ChartTypes)}.";
        }

        return null;
    }

    /// <summary>
    /// Clamps a requested refresh interval (seconds) to the allowed minimum.
    /// A null request defaults to 60 seconds; otherwise the value is floored at MinRefreshSec.
    /// </summary>
    public static int ClampRefresh(int? requested) => requested is null ? 60 : Math.Max(MinRefreshSec, requested.Value);
}
