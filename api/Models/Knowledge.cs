namespace ExcelDatasetManager.Api.Models;

public record KnowledgeEntry(
    Guid Id, Guid DatasetId, string Kind, string Title, string Content,
    string Source, string CreatedBy, bool Pinned, DateTime? ArchivedAt,
    DateTime CreatedAt, DateTime UpdatedAt);

public record CreateKnowledgeRequest(string? Kind, string? Title, string? Content, bool? Pinned);
public record UpdateKnowledgeRequest(string? Title, string? Content, bool? Pinned);
