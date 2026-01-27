using SourceChat.Features.Shared;

namespace SourceChat.Features.Ingest;

/// <summary>
/// Error definitions for ingestion operations.
/// </summary>
internal static class IngestionErrors
{
    public static Error DirectoryNotFound(string path) =>
        Error.Failure(code: "DirectoryNotFound", message: $"Directory not found: {path}");

    public static Error FileProcessingError(string exceptionMessage) =>
        Error.Failure(code: "FileProcessingError", message: $"Error while processing file: {exceptionMessage}");

    public static Error FileProcessingFailed(string documentId) =>
        Error.Failure(code: "FileProcessingFailed", message: $"Failed to process document: {documentId}");

    public static Error FileTrackingError(string filePath, string exceptionMessage) =>
        Error.Failure(code: "FileTrackingError", message: $"Failed to track file: {filePath}. {exceptionMessage}");

    public static Error FileTrackingSaveError(string exceptionMessage) =>
        Error.Failure(code: "FileTrackingSaveError", message: $"Failed to save file tracking: {exceptionMessage}");

    public static Error CollectionNotFound() =>
        Error.Failure(code: "CollectionNotFound", message: "Collection 'data' does not exist. Ingestion may have failed.");

    public static Error SummaryRetrievalError(string exceptionMessage) =>
        Error.Failure(code: "SummaryRetrievalError", message: $"Failed to retrieve ingestion summary: {exceptionMessage}");

    public static Error DimensionMismatch(string expectedDimension, int actualDimension)
    {
        string message = $"""
                            Embedding dimension mismatch: The vector store expects '{expectedDimension}' dimensions, but the current embedding model produces '{actualDimension}' dimensions.
                            This usually happens when switching between different embedding models.
                            Please run 'clear' command to delete the existing database and try again.
                          """;

        return Error.Failure(code: "DimensionMismatch", message: message);
    }
}