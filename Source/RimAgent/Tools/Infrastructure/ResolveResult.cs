using System.Diagnostics.CodeAnalysis;

namespace RimAgent.Tools;

public class Result<T>
{
    protected Result(bool success, T? value, string? error)
    {
        Success = success;
        Value = value;
        Error = error ?? string.Empty;
    }

    [MemberNotNullWhen(true, nameof(Value))]
    public bool Success { get; }
    public T? Value { get; }
    public string Error { get; }

    public static Result<T> Ok(T value)
    {
        return new Result<T>(success: true, value, string.Empty);
    }

    public static Result<T> Fail(string error)
    {
        return new Result<T>(success: false, default, error);
    }
}