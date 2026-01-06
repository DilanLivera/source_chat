using System.Xml.Linq;

namespace SourceChat;

internal class XmlParser : IFileParser
{
    public bool CanParse(string filePath) =>
        Path.GetExtension(filePath).Equals(".xml", StringComparison.OrdinalIgnoreCase);

    public async Task<(string content, Dictionary<string, string> metadata)> ParseAsync(string filePath)
    {
        string content = await File.ReadAllTextAsync(filePath);
        Dictionary<string, string> metadata = new();

        try
        {
            XDocument doc = XDocument.Parse(content);

            if (doc.Root != null)
            {
                metadata["root_element"] = doc.Root.Name.LocalName;

                int elementCount = doc.Descendants().Count();
                metadata["element_count"] = elementCount.ToString();

                IEnumerable<string> uniqueElements = doc.Descendants()
                                                        .Select(e => e.Name.LocalName)
                                                        .Distinct()
                                                        .Take(10);
                metadata["element_types"] = string.Join(", ", uniqueElements);
            }

            metadata["file_type"] = "xml";
            metadata["language"] = "XML";

            return (content, metadata);
        }
        catch (Exception ex)
        {
            metadata["parse_error"] = ex.Message;
            metadata["file_type"] = "xml";
            metadata["language"] = "XML";

            return (content, metadata);
        }
    }
}