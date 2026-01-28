using SourceChat.Features.Shared;

namespace SourceChat.Features.Query;

/// <summary>
/// Error definitions for query operations.
/// </summary>
internal static class QueryErrors
{
    public static Error CollectionNotFound() =>
        Error.Failure(code: "CollectionNotFound", message: "No data has been ingested yet. Please run the 'ingest' command first.");

    public static Error CollectionAccessError(string exceptionMessage) =>
        Error.Failure(code: "CollectionAccessError", message: $"Unable to access the 'data' collection. Please ensure files have been ingested first. {exceptionMessage}");

    public static Error QueryExecutionError(string exceptionMessage) =>
        Error.Failure(code: "QueryExecutionError", message: $"Error during query execution: {exceptionMessage}");

    public static Error NoSearchResults() =>
        Error.Failure(code: "NoSearchResults", message: "I couldn't find any relevant information in the codebase to answer your question.");
}