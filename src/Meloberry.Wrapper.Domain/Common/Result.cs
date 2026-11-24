namespace Meloberry.Wrapper.Domain.Common;

/// <summary>Categorizes domain and application operation failures.</summary>
public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Io,
    Network,
    ExternalProcess,
    Security
}

/// <summary>Structured error value returned across all layers.</summary>
public sealed record Error(
    string Code,
    string Description,
    ErrorType Type,
    string? Hint = null);

/// <summary>Represents the outcome of an operation that produces no value.</summary>
public readonly struct Result
{
    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(Error error) => new(false, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Represents the outcome of an operation that produces a value on success.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, Error? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value on a failed Result: {Error?.Code} - {Error?.Description}");

    public static Result<T> Success(T value) => new(true, value, null);

    public static Result<T> Failure(Error error) => new(false, default, error ?? throw new ArgumentNullException(nameof(error)));
}
