namespace SourceChat.Features.Shared;

public enum ChunkingStrategy
{
    Semantic,
    Section,
    Structure
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
    public List<SummaryChunk> SummaryChunks { get; set; } = [];
}

public class SummaryChunk
{
    public double Score { get; init; }
    public string Content { get; init; } = string.Empty;
}