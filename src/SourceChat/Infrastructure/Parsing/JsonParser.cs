using System.Text.Json;

namespace SourceChat.Infrastructure.Parsing;

internal class JsonParser : IFileParser
{
    public bool CanParse(string filePath) => Path.GetExtension(filePath)
                                                 .Equals(".json", StringComparison.OrdinalIgnoreCase);

    public async Task<(string content, Dictionary<string, string> metadata)> ParseAsync(string filePath)
    {
        string content = await File.ReadAllTextAsync(filePath);
        Dictionary<string, string> metadata = new();

        try
        {
            // Parse and pretty-print JSON
            using JsonDocument doc = JsonDocument.Parse(content);
            string prettyJson = JsonSerializer.Serialize(doc.RootElement,
                                                         new JsonSerializerOptions
                                                         {
                                                             WriteIndented = true
                                                         });

            // Extract some metadata
            JsonElement root = doc.RootElement;
            metadata["json_type"] = root.ValueKind.ToString();

            if (root.ValueKind == JsonValueKind.Object)
            {
                int propertyCount = root.EnumerateObject()
                                        .Count();
                metadata["property_count"] = propertyCount.ToString();

                IEnumerable<string> topLevelKeys = root.EnumerateObject()
                                                       .Select(p => p.Name)
                                                       .Take(10);
                metadata["top_level_keys"] = string.Join(", ", topLevelKeys);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                metadata["array_length"] = root.GetArrayLength()
                                               .ToString();
            }

            metadata["file_type"] = "json";
            metadata["language"] = "JSON";

            return (content: prettyJson, metadata);
        }
        catch (JsonException ex)
        {
            metadata["parse_error"] = ex.Message;
            metadata["file_type"] = "json";
            metadata["language"] = "JSON";

            return (content, metadata);
        }
    }
}