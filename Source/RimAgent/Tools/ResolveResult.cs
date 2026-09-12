namespace RimAgent.Tools
{
    /// <summary>解析器的回傳：成功帶值，失敗帶可直接轉述給模型的錯誤訊息。</summary>
    public sealed class ResolveResult<T>
    {
        private ResolveResult(bool success, T value, string error)
        {
            Success = success;
            Value = value;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public T Value { get; }
        public string Error { get; }

        public static ResolveResult<T> Ok(T value)
        {
            return new ResolveResult<T>(true, value, string.Empty);
        }

        public static ResolveResult<T> Fail(string error)
        {
            return new ResolveResult<T>(false, default, error);
        }
    }
}
