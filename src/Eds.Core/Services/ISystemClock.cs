namespace Eds.Core.Services;

/// <summary>Abstracts "now" so time-dependent services (auto-close) are testable.</summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Real wall-clock implementation.</summary>
public sealed class SystemClock : ISystemClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
