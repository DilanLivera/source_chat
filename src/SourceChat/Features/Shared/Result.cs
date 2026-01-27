using System.Diagnostics;

namespace SourceChat.Features.Shared;

/// <summary>
/// Represents the result of an operation that can either succeed with a value or fail with an error.
/// </summary>
/// <typeparam name="T">The type of the success value</typeparam>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        Debug.Assert(value != null, "Success value cannot be null");

        _value = value;
        _error = null;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        Debug.Assert(error != null, "Error cannot be null");

        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value
    {
        get
        {
            if (IsFailure)
            {
                throw new InvalidOperationException($"Cannot access value of failed result. Error: {_error}");
            }

            return _value!;
        }
    }

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
    /// Creates a successful result with the specified value.
    /// </summary>
    public static Result<T> Success(T value) => new(value);

    /// <summary>
    /// Creates a failed result with the specified error.
    /// </summary>
    public static Result<T> Failure(Error error) => new(error);

    /// <summary>
    /// Creates a failed result with the specified error message (creates a general failure error).
    /// </summary>
    public static Result<T> Failure(string errorMessage) => new(Error.Failure("GeneralFailure", errorMessage));

    /// <summary>
    /// Transforms the result if successful, otherwise returns the same failure.
    /// </summary>
    public Result<TNew> Map<TNew>(Func<T, TNew> mapper)
    {
        Debug.Assert(mapper != null, "Mapper function cannot be null");

        return IsSuccess
            ? Result<TNew>.Success(mapper(Value))
            : Result<TNew>.Failure(Error);
    }

    /// <summary>
    /// Transforms the result if successful, otherwise returns the same failure.
    /// Allows the mapper to return a Result for chaining operations that can fail.
    /// </summary>
    public Result<TNew> Bind<TNew>(Func<T, Result<TNew>> binder)
    {
        Debug.Assert(binder != null, "Binder function cannot be null");

        return IsSuccess
            ? binder(Value)
            : Result<TNew>.Failure(Error);
    }

    /// <summary>
    /// Chains a void operation (that returns VoidResult) if this result is successful.
    /// This allows chaining from Result&lt;T&gt; to VoidResult.
    /// </summary>
    public VoidResult BindToVoid(Func<T, VoidResult> binder)
    {
        Debug.Assert(binder != null, "Binder function cannot be null");

        return IsSuccess
            ? binder(Value)
            : VoidResult.Failure(Error);
    }

    /// <summary>
    /// Executes a void operation if this result is successful, ignoring the value.
    /// This allows chaining side-effect operations that don't return meaningful data.
    /// </summary>
    public VoidResult ThenVoid(Func<VoidResult> operation)
    {
        Debug.Assert(operation != null, "Operation cannot be null");

        return IsSuccess
            ? operation()
            : VoidResult.Failure(Error);
    }

    /// <summary>
    /// Executes an action if the result is successful.
    /// </summary>
    public Result<T> OnSuccess(Action<T> action)
    {
        Debug.Assert(action != null, "Action cannot be null");

        if (IsSuccess)
        {
            action(Value);
        }

        return this;
    }

    /// <summary>
    /// Executes an action if the result is a failure.
    /// </summary>
    public Result<T> OnFailure(Action<Error> action)
    {
        Debug.Assert(action != null, "Action cannot be null");

        if (IsFailure)
        {
            action(Error);
        }

        return this;
    }

    /// <summary>
    /// Returns the value if successful, otherwise returns the default value.
    /// </summary>
    public T GetValueOrDefault(T defaultValue = default!) => IsSuccess ? Value : defaultValue;

    /// <summary>
    /// Implicit conversion from T to Result<T> for convenience.
    /// </summary>
    public static implicit operator Result<T>(T value) => Success(value);

    public override string ToString() => IsSuccess ? $"Success({Value})" : $"Failure({Error})";
}