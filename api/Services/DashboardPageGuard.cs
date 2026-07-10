using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Pure validation cho trang HTML custom của dashboard (kind='custom'). Không sanitize nội dung —
/// ranh giới an ninh là CSP sandbox ở route serve (DashboardPageHeaders), không phải regex ở đây.
/// Chỉ enforce: bắt buộc có nội dung + trần dung lượng theo BYTES UTF-8 (khớp cách Postgres đo TEXT).
/// </summary>
public static class DashboardPageGuard
{
    public const int MaxHtmlBytes = 2 * 1024 * 1024;

    public static string? ValidateHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "html is required.";
        }

        if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes)
        {
            return $"html must be at most {MaxHtmlBytes} bytes UTF-8 (2MB).";
        }

        return null;
    }
}
