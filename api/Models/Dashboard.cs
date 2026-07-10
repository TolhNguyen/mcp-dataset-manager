namespace ExcelDatasetManager.Api.Models;

public record Dashboard(
    Guid Id, Guid UserId, string Name, string? Description, string Kind, string CreatedBy,
    DateTime CreatedAt, DateTime UpdatedAt);

public record DashboardWidget(
    Guid Id, Guid DashboardId, Guid DatasetId, string Title, string Sql, string ChartType,
    string? ChartConfigJson, int RefreshIntervalSec, int Position, string Source, string CreatedBy,
    DateTime? ArchivedAt, DateTime CreatedAt, DateTime UpdatedAt);

public record CreateWidgetRequest(Guid? DatasetId, string? Title, string? Sql, string? ChartType,
    System.Text.Json.JsonElement? ChartConfig, int? RefreshIntervalSec, string? SchemaToken = null);
public record UpdateWidgetRequest(string? Title, string? Sql, string? ChartType,
    System.Text.Json.JsonElement? ChartConfig, int? RefreshIntervalSec, int? Position, string? SchemaToken = null);
public record CreateDashboardRequest(string? Name, string? Description);
// MCP convenience: create a widget, auto-creating the dashboard by name if needed.
public record CreateWidgetByDashboardNameRequest(string? DashboardName, Guid? DatasetId, string? Title,
    string? Sql, string? ChartType, System.Text.Json.JsonElement? ChartConfig, int? RefreshIntervalSec,
    string? SchemaToken = null);
