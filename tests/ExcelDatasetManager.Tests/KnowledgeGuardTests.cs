using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class KnowledgeGuardTests
{
    // ---------- ValidateCreate ----------

    [Fact]
    public void ValidateCreate_accepts_valid_entry()
    {
        var error = KnowledgeGuard.ValidateCreate("note", "Revenue definition", "Revenue = gross sales minus returns.");
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_rejects_kind_not_in_set()
    {
        var error = KnowledgeGuard.ValidateCreate("not_a_kind", "Title", "Content");
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("column_meaning")]
    [InlineData("business_rule")]
    [InlineData("metric_definition")]
    [InlineData("join_hint")]
    [InlineData("document")]
    public void ValidateCreate_accepts_all_known_kinds(string kind)
    {
        var error = KnowledgeGuard.ValidateCreate(kind, "Title", "Content");
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_null_kind_defaults_to_note_and_is_valid()
    {
        var error = KnowledgeGuard.ValidateCreate(null, "Title", "Content");
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_rejects_empty_title()
    {
        var error = KnowledgeGuard.ValidateCreate("note", "", "Content");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_whitespace_only_title()
    {
        var error = KnowledgeGuard.ValidateCreate("note", "   ", "Content");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_null_title()
    {
        var error = KnowledgeGuard.ValidateCreate("note", null, "Content");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_title_over_255_chars()
    {
        var title = new string('a', 256);
        var error = KnowledgeGuard.ValidateCreate("note", title, "Content");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_accepts_title_exactly_255_chars()
    {
        var title = new string('a', 255);
        var error = KnowledgeGuard.ValidateCreate("note", title, "Content");
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_rejects_empty_content()
    {
        var error = KnowledgeGuard.ValidateCreate("note", "Title", "");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_null_content()
    {
        var error = KnowledgeGuard.ValidateCreate("note", "Title", null);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_content_over_4000_chars()
    {
        var content = new string('a', 4001);
        var error = KnowledgeGuard.ValidateCreate("note", "Title", content);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_accepts_content_exactly_4000_chars()
    {
        var content = new string('a', 4000);
        var error = KnowledgeGuard.ValidateCreate("note", "Title", content);
        Assert.Null(error);
    }

    // ---------- ValidateUpdate ----------

    [Fact]
    public void ValidateUpdate_rejects_when_no_fields_provided()
    {
        var error = KnowledgeGuard.ValidateUpdate(null, null, null);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateUpdate_accepts_title_only()
    {
        var error = KnowledgeGuard.ValidateUpdate("New title", null, null);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateUpdate_accepts_content_only()
    {
        var error = KnowledgeGuard.ValidateUpdate(null, "New content", null);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateUpdate_accepts_pinned_only()
    {
        var error = KnowledgeGuard.ValidateUpdate(null, null, true);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateUpdate_rejects_title_over_255_chars()
    {
        var title = new string('a', 256);
        var error = KnowledgeGuard.ValidateUpdate(title, null, null);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateUpdate_accepts_title_exactly_255_chars()
    {
        var title = new string('a', 255);
        var error = KnowledgeGuard.ValidateUpdate(title, null, null);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateUpdate_rejects_content_over_4000_chars()
    {
        var content = new string('a', 4001);
        var error = KnowledgeGuard.ValidateUpdate(null, content, null);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateUpdate_accepts_content_exactly_4000_chars()
    {
        var content = new string('a', 4000);
        var error = KnowledgeGuard.ValidateUpdate(null, content, null);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateUpdate_rejects_blank_title_when_provided()
    {
        var error = KnowledgeGuard.ValidateUpdate("   ", null, null);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateUpdate_rejects_blank_content_when_provided()
    {
        var error = KnowledgeGuard.ValidateUpdate(null, "   ", null);
        Assert.NotNull(error);
    }
}
