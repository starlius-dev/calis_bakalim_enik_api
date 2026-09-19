namespace CalisBakalimEnik.Application.Common.Models;

public readonly record struct Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
}

/// <summary>Explicit success/failure without exceptions for expected outcomes.</summary>
public class Result
{
    protected Result(bool succeeded, Error error)
    {
        if (succeeded && error != Error.None)
            throw new InvalidOperationException("A successful result cannot carry an error.");
        if (!succeeded && error == Error.None)
            throw new InvalidOperationException("A failed result must carry an error.");

        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }
    public bool Failed => !Succeeded;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<T> Success<T>(T value) => new(value, true, Error.None);
    public static Result<T> Failure<T>(Error error) => new(default, false, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, bool succeeded, Error error) : base(succeeded, error) => _value = value;

    public T Value => Succeeded
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static implicit operator Result<T>(T value) => Success(value);
}
