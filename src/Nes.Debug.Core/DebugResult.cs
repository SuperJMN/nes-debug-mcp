namespace Nes.Debug.Core;

public readonly struct DebugResult<T>
{
    private readonly T? value;

    private DebugResult(T value)
    {
        this.value = value;
        Error = null;
        IsSuccess = true;
    }

    private DebugResult(DebugError error)
    {
        value = default;
        Error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public DebugError? Error { get; }

    public T Value => IsSuccess
        ? value!
        : throw new InvalidOperationException($"Cannot read value from failed result: {Error?.Code}");

    public static DebugResult<T> Success(T value) => new(value);

    public static DebugResult<T> Failure(string code, string message) => new(new DebugError(code, message));
}
