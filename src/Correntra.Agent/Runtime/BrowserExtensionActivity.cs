using System.Threading;

namespace Correntra.Agent.Runtime;

/// <summary>
/// Tracks when the genuine browser extension last talked to the agent over
/// the loopback bridge. Only requests that cleared BOTH gates — the pinned
/// extension origin and the shared bridge token — are counted as "the
/// extension was here"; web pages, foreign extensions and forged headers
/// never update it. Volatile: agent restarts reset it to null, which is
/// correct behaviour for the setup wizard (a restarted agent has seen no
/// extension yet this run).
/// </summary>
public static class BrowserExtensionActivity
{
    private static long _lastSeenTicks; // 0 == never this run

    /// <summary>Records a verified extension request. Thread-safe.</summary>
    public static void MarkSeen()
    {
        Volatile.Write(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>Last verified contact, or null when the extension has not
    /// reached this agent instance yet.</summary>
    public static DateTimeOffset? LastSeenUtc
    {
        get
        {
            long ticks = Volatile.Read(ref _lastSeenTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }
}
