using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DocumentImporterTests
{
    [Fact]
    public void Splits_three_headings_into_three_sections()
    {
        var md = "# Doanh thu\nNội dung 1\n## Chi phí\nNội dung 2\n### Lợi nhuận\nNội dung 3";
        var s = DocumentImporter.Split(md);
        Assert.Equal(3, s.Count);
        Assert.Equal("Doanh thu", s[0].Title);
        Assert.Contains("Nội dung 1", s[0].Content);
        Assert.Equal("Chi phí", s[1].Title);
        Assert.Equal("Lợi nhuận", s[2].Title);
    }

    [Fact]
    public void Preamble_before_first_heading_becomes_its_own_section()
    {
        var md = "Giới thiệu chung về dữ liệu.\n# Bảng orders\nMô tả orders";
        var s = DocumentImporter.Split(md, "Tài liệu");
        Assert.Equal(2, s.Count);
        Assert.Equal("Tài liệu", s[0].Title);
        Assert.Contains("Giới thiệu", s[0].Content);
        Assert.Equal("Bảng orders", s[1].Title);
    }

    [Fact]
    public void Long_body_is_truncated_to_max_content()
    {
        var md = "# H\n" + new string('x', KnowledgeGuard.MaxContentChars + 500);
        var s = DocumentImporter.Split(md);
        Assert.Single(s);
        Assert.True(s[0].Content.Length <= KnowledgeGuard.MaxContentChars);
    }

    [Fact]
    public void Empty_or_whitespace_yields_no_sections()
    {
        Assert.Empty(DocumentImporter.Split(""));
        Assert.Empty(DocumentImporter.Split("   \n  \n"));
    }

    [Fact]
    public void Heading_with_no_body_is_skipped()
    {
        var md = "# Empty heading\n# Real heading\nBody here";
        var s = DocumentImporter.Split(md);
        Assert.Single(s);
        Assert.Equal("Real heading", s[0].Title);
    }
}
