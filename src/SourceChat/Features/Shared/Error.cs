namespace SourceChat.Features.Shared;

/// <summary>
/// Represents an error that occurred during an operation.
/// </summary>
public sealed class Error
{
    public required string Code { get; init; }
    public required string Message { get; init; }

    /// <summary>
    /// Creates a general failure error with the specified message.
    /// </summary>
    public static Error Failure(string code, string message) => new()
    {
        Code = code,
        Message = message
    };

    public override string ToString() => $"{Code}: {Message}";
}