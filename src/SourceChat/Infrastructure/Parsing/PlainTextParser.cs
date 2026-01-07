namespace SourceChat.Infrastructure.Parsing;

internal class PlainTextParser : IFileParser
{
    private static readonly string[] SupportedExtensions =
    [
        ".txt", ".log", ".config", ".ini"
    ];

    public bool CanParse(string filePath) => SupportedExtensions.Contains(Path.GetExtension(filePath),
                                                                          StringComparer.OrdinalIgnoreCase);

    public async Task<(string content, Dictionary<string, string> metadata)> ParseAsync(string filePath)
    {
        string content = await File.ReadAllTextAsync(filePath);
        Dictionary<string, string> metadata = new();

        int lines = content.Split('\n')
                           .Length;
        metadata["line_count"] = lines.ToString();
        metadata["file_type"] = "text";
        metadata["language"] = "Plain Text";

        return (content, metadata);
    }
}