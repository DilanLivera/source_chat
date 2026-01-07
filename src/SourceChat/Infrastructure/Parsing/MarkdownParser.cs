using System.Text.RegularExpressions;

namespace SourceChat.Infrastructure.Parsing;

internal partial class MarkdownParser : IFileParser
{
    [GeneratedRegex(@"^#{1,6}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex HeaderRegex();

    public bool CanParse(string filePath) =>
        Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase);

    public async Task<(string content, Dictionary<string, string> metadata)> ParseAsync(string filePath)
    {
        string content = await File.ReadAllTextAsync(filePath);
        Dictionary<string, string> metadata = new();

        // Extract headers
        List<string> headers = HeaderRegex()
                               .Matches(content)
                               .Select(m => m.Groups[1].Value.Trim())
                               .ToList();

        if (headers.Count != 0)
        {
            metadata["headers"] = string.Join(" > ", headers.Take(5)); // First 5 headers
            metadata["header_count"] = headers.Count.ToString();
            metadata["title"] = headers.First(); // First header as title
        }

        // Count code blocks
        int codeBlockCount = Regex.Matches(content, @"```[\s\S]*?```").Count;
        if (codeBlockCount > 0)
        {
            metadata["code_blocks"] = codeBlockCount.ToString();
        }

        // Count links
        int linkCount = Regex.Matches(content, @"\[([^\]]+)\]\(([^)]+)\)").Count;
        if (linkCount > 0)
        {
            metadata["links"] = linkCount.ToString();
        }

        metadata["file_type"] = "markdown";
        metadata["language"] = "Markdown";

        return (content, metadata);
    }
}