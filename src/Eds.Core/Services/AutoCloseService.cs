using Eds.Core.Locations;

namespace Eds.Core.Services;

/// <summary>
/// Closes opened EDS locations (containers / EncFS volumes) after a period of
/// inactivity. Platform-independent port of the keep-alive / auto-close behaviour
/// of the Android <c>LocationsServiceBase</c>: it reads each location's last
/// activity time (updated whenever its filesystem is accessed) and its per-location
/// timeout, and force-closes those that have gone idle.
///
/// <para>The decision is pure — <see cref="CloseIdleLocations(DateTimeOffset)"/>
/// takes an explicit "now", which keeps it deterministic in tests and lets a host
/// drive it from a timer via the parameterless overload (<see cref="ISystemClock"/>).
/// Wire <see cref="RunAsync"/> for a background loop, or call
/// <see cref="CloseIdleLocations()"/> on your own cadence (e.g. on screen-off).</para>
/// </summary>
public sealed class AutoCloseService
{
    private readonly LocationsManager _manager;
    private readonly ISystemClock _clock;

    public AutoCloseService(LocationsManager manager, ISystemClock? clock = null)
    {
        _manager = manager;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Closes idle locations using the injected clock. Returns how many were closed.</summary>
    public int CloseIdleLocations() => CloseIdleLocations(_clock.UtcNow);

    /// <summary>
    /// Closes every open EDS location whose idle time (relative to
    /// <paramref name="now"/>) has reached its timeout. A timeout of 0 (or less)
    /// means "never auto-close". Uses a forced close so a stuck handle can't keep
    /// a volume decrypted past its timeout.
    /// </summary>
    public int CloseIdleLocations(DateTimeOffset now)
    {
        int closed = 0;
        foreach (var loc in _manager.GetLoadedLocations(false))
        {
            if (loc is not IEdsLocation eds || !eds.IsOpen()) continue;
            int timeoutSeconds = eds.GetAutoCloseTimeout();
            if (timeoutSeconds <= 0) continue;
            var idle = now - eds.GetLastActivityTime();
            if (idle.TotalSeconds >= timeoutSeconds)
            {
                _manager.CloseLocation(loc, force: true);
                closed++;
            }
        }
        return closed;
    }

    /// <summary>Runs the idle check on a fixed cadence until cancelled.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                CloseIdleLocations();
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }
}
