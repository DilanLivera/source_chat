using System.Text.RegularExpressions;

namespace SourceChat;

internal partial class CSharpParser : IFileParser
{
    [GeneratedRegex(@"namespace\s+([\w\.]+)", RegexOptions.Compiled)]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"(?:public|private|protected|internal|static)?\s*(?:partial\s+)?(?:class|interface|struct|enum)\s+(\w+)", RegexOptions.Compiled)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"(?:public|private|protected|internal|static|virtual|override|async)?\s+[\w<>\[\]]+\s+(\w+)\s*\([^)]*\)", RegexOptions.Compiled)]
    private static partial Regex MethodRegex();

    [GeneratedRegex(@"///\s*<summary>(.*?)<\/summary>", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex XmlSummaryRegex();

    public bool CanParse(string filePath) =>
        Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    public async Task<(string content, Dictionary<string, string> metadata)> ParseAsync(string filePath)
    {
        string content = await File.ReadAllTextAsync(filePath);
        Dictionary<string, string> metadata = new();

        // Extract namespace
        Match namespaceMatch = NamespaceRegex().Match(content);
        if (namespaceMatch.Success)
        {
            metadata["namespace"] = namespaceMatch.Groups[1].Value;
        }

        // Extract classes
        MatchCollection classMatches = ClassRegex().Matches(content);
        List<string> classes = classMatches.Select(m => m.Groups[1].Value).ToList();
        if (classes.Count != 0)
        {
            metadata["classes"] = string.Join(", ", classes);
        }

        // Extract methods
        MatchCollection methodMatches = MethodRegex().Matches(content);
        List<string> methods = methodMatches.Select(m => m.Groups[1].Value).Distinct().ToList();
        if (methods.Count != 0)
        {
            metadata["methods"] = string.Join(", ", methods.Take(10)); // Limit to first 10
            metadata["method_count"] = methods.Count.ToString();
        }

        // Extract XML documentation summaries
        List<string> summaries = [];
        MatchCollection summaryMatches = XmlSummaryRegex().Matches(content);
        foreach (Match match in summaryMatches)
        {
            string summary = match.Groups[1].Value.Trim();
            summary = Regex.Replace(summary, @"\s+", " "); // Normalize whitespace
            if (!string.IsNullOrWhiteSpace(summary))
            {
                summaries.Add(summary);
            }
        }

        if (summaries.Count != 0)
        {
            metadata["xml_summaries"] = string.Join(" | ", summaries);
        }

        metadata["file_type"] = "csharp";
        metadata["language"] = "C#";

        return (content, metadata);
    }
}