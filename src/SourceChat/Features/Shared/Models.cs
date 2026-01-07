namespace SourceChat.Features.Shared;

public enum ChunkingStrategy
{
    Semantic,
    Section,
    Structure
}

public class DocumentMetadata
{
    public required string FilePath { get; init; }
    public required string FileType { get; init; }
    public DateTime LastModified { get; init; }
    public long FileSize { get; init; }
    public Dictionary<string, string> CustomMetadata { get; init; } = new();
}

public class CodeMetadata
{
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public List<string> Methods { get; set; } = [];
    public List<string> Properties { get; set; } = [];
    public string? XmlSummary { get; set; }
}

public class FileTrackingInfo
{
    public DateTime LastModified { get; set; }
    public string Hash { get; set; } = string.Empty;
    public DateTime LastProcessed { get; set; }
}

public class IngestionResult
{
    public int FilesProcessed { get; set; }
    public int TotalChunks { get; set; }
    public int Errors { get; set; }
}