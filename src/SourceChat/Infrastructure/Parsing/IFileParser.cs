namespace SourceChat.Infrastructure.Parsing;

internal interface IFileParser
{
    public bool CanParse(string filePath);
    public Task<(string content, Dictionary<string, string> metadata)> ParseAsync(string filePath);
}