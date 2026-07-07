using System.Text;
using System.Text.RegularExpressions;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Splits an uploaded Markdown/plain-text document into knowledge sections, one per heading.
/// Content before the first heading becomes a leading section titled after the document.
/// Each section's content is capped at KnowledgeGuard.MaxContentChars.
/// </summary>
public static class DocumentImporter
{
    private static readonly Regex HeadingLine = new(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    public static List<(string Title, string Content)> Split(string markdown, string fallbackTitle = "Tài liệu")
    {
        var sections = new List<(string Title, string Content)>();
        if (string.IsNullOrWhiteSpace(markdown)) return sections;

        string currentTitle = fallbackTitle;
        var body = new StringBuilder();

        void Flush()
        {
            var content = body.ToString().Trim();
            if (content.Length > 0)
            {
                if (content.Length > KnowledgeGuard.MaxContentChars)
                {
                    content = content[..KnowledgeGuard.MaxContentChars];
                }
                var title = currentTitle.Trim();
                if (title.Length == 0) title = fallbackTitle;
                if (title.Length > KnowledgeGuard.MaxTitleChars) title = title[..KnowledgeGuard.MaxTitleChars];
                sections.Add((title, content));
            }
            body.Clear();
        }

        foreach (var line in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var m = HeadingLine.Match(line);
            if (m.Success)
            {
                Flush();
                currentTitle = m.Groups[1].Value;
            }
            else
            {
                body.Append(line).Append('\n');
            }
        }
        Flush();

        return sections;
    }
}
