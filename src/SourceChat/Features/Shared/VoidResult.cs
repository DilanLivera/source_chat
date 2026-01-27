using System.Diagnostics;

namespace SourceChat.Features.Shared;

/// <summary>
/// Represents the result of an operation that doesn't return a value (void).
/// </summary>
public sealed class VoidResult
{
    private readonly Error? _error;

    private VoidResult()
    {
        _error = null;
        IsSuccess = true;
    }

    private VoidResult(Error error)
    {
        Debug.Assert(error != null, "Error cannot be null");

        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public Error Error
    {
        get
        {
            if (IsSuccess)
            {
                throw new InvalidOperationException("Cannot access error of successful result");
            }

            return _error!;
        }
    }

    /// <summary>
    /// Creates a successful void result.
    /// </summary>
    public static VoidResult Success() => new();

    /// <summary>
    /// Creates a failed void result with the specified error.
    /// </summary>
    public static VoidResult Failure(Error error) => new(error);

    /// <summary>
    /// Creates a failed void result with the specified error message (creates a general failure error).
    /// </summary>
    public static VoidResult Failure(string errorMessage) => new(Error.Failure("GeneralFailure", errorMessage));

    /// <summary>
    /// Transforms the void result into a Result&lt;T&gt; if successful, otherwise returns the same failure.
    /// </summary>
    public Result<T> Map<T>(Func<T> mapper)
    {
        Debug.Assert(mapper != null, "Mapper function cannot be null");

        return IsSuccess
            ? Result<T>.Success(mapper())
            : Result<T>.Failure(Error);
    }

    /// <summary>
    /// Chains another void operation if this result is successful.
    /// </summary>
    public VoidResult Bind(Func<VoidResult> binder)
    {
        Debug.Assert(binder != null, "Binder function cannot be null");

        return IsSuccess
            ? binder()
            : Failure(Error);
    }

    /// <summary>
    /// Executes an action if the result is successful.
    /// </summary>
    public VoidResult OnSuccess(Action action)
    {
        Debug.Assert(action != null, "Action cannot be null");

        if (IsSuccess)
        {
            action();
        }

        return this;
    }

    /// <summary>
    /// Executes an action if the result is a failure.
    /// </summary>
    public VoidResult OnFailure(Action<Error> action)
    {
        Debug.Assert(action != null, "Action cannot be null");

        if (IsFailure)
        {
            action(Error);
        }

        return this;
    }

    public override string ToString() => IsSuccess ? "Success" : $"Failure({Error})";
}