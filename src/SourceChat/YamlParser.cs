namespace SourceChat;

internal class YamlParser : IFileParser
{
    public bool CanParse(string filePath)
    {
        string ext = Path.GetExtension(filePath);

        return ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(string content, Dictionary<string, string> metadata)> ParseAsync(string filePath)
    {
        string content = await File.ReadAllTextAsync(filePath);
        Dictionary<string, string> metadata = new();

        // Count top-level keys (simple heuristic)
        string[] lines = content.Split('\n');
        List<string> topLevelKeys = lines
                                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
                                    .Where(l => l.Contains(':') && !l.StartsWith(' ') && !l.StartsWith('\t'))
                                    .Select(l => l.Split(':')[0].Trim())
                                    .ToList();

        if (topLevelKeys.Any())
        {
            metadata["top_level_keys"] = string.Join(", ", topLevelKeys.Take(10));
            metadata["key_count"] = topLevelKeys.Count.ToString();
        }

        metadata["file_type"] = "yaml";
        metadata["language"] = "YAML";

        return (content, metadata);
    }
}