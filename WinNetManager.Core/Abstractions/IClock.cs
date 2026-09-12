
namespace WinNetManager.Core.Abstractions;

/// <summary>可注入的时钟，便于用虚拟时间测试状态机。</summary>
public interface IClock
{
    DateTime UtcNow { get; }
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;
}
