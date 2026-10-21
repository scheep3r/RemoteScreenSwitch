using RdpMonitorSwitcher.Core.Models;

namespace RdpMonitorSwitcher.Core.Services;

/// <summary>Decides whether and when to reconnect after a connection loss.</summary>
public sealed class ReconnectPolicy
{
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];

    public ReconnectPolicy(int maxAttempts = 10, IReadOnlyList<TimeSpan>? delays = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxAttempts);
        MaxAttempts = maxAttempts;
        Delays = delays is { Count: > 0 } ? delays : DefaultDelays;
    }

    public int MaxAttempts { get; }

    public IReadOnlyList<TimeSpan> Delays { get; }

    /// <summary>Only connection losses are retried; user actions and server decisions are final.</summary>
    public static bool IsRetryable(DisconnectReason reason) => reason == DisconnectReason.Network;

    /// <param name="attempt">1-based number of the reconnect attempt about to be made.</param>
    /// <returns>The delay before the attempt, or null to give up.</returns>
    public TimeSpan? GetDelay(DisconnectReason reason, int attempt)
    {
        if (!IsRetryable(reason) || attempt < 1 || attempt > MaxAttempts)
            return null;
        return Delays[Math.Min(attempt - 1, Delays.Count - 1)];
    }
}
