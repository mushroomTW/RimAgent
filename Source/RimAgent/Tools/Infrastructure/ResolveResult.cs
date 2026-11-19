namespace RimAgent.Tools;

/// <summary>成功帶值、失敗帶錯誤訊息的結果容器；解析器與命令結果共用此核心。</summary>
public class Result<T>
{
    protected Result(bool success, T value, string error)
    {
        Success = success;
        Value = value;
        Error = error ?? string.Empty;
    }

    public bool Success { get; }
    public T Value { get; }
    public string Error { get; }

    public static Result<T> Ok(T value)
    {
        return new Result<T>(true, value, string.Empty);
    }

    public static Result<T> Fail(string error)
    {
        return new Result<T>(false, default, error);
    }
}