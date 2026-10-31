using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.StreamLimit.Gate;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

/// <summary>
/// The production "ghost streams" bug: slots held forever by open (or leaked) media
/// responses that never wrote a byte, locking users out.
/// </summary>
public class GhostSlotTests
{
    private static readonly Guid User = Guid.Parse("60215b81-93b8-4049-856a-5668308c7ca7");

    private readonly TestClock _clock = new();

    private StreamSlotTracker CreateTracker() => new(_clock, TimeSpan.FromSeconds(60));

    [Fact]
    public void ManyRegistrationsWithoutEndRequest_AreNotHeldAfterTheWindow_AndAreSwept()
    {
        // 258 registrations on one device, none ever ended (hung segment waits, half-open
        // connections, or a leak): the device must stop counting and be cleaned up.
        var tracker = CreateTracker();
        var aborted = 0;
        Assert.True(tracker.TryEnter(User, "android-native", 1, []));
        for (var i = 0; i < 258; i++)
        {
            tracker.BeginRequest(User, "android-native", () => Interlocked.Increment(ref aborted));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(tracker.TryEnter(User, "phone", 1, []));

        _clock.Advance(StreamSlotTracker.ActiveResponseWindow + TimeSpan.FromSeconds(1));

        // Not counted any more: the live view drops it and another device may stream.
        Assert.Empty(tracker.GetHeldDevices(User, []));
        var snapshot = Assert.Single(tracker.GetSnapshot());
        Assert.False(snapshot.Held);
        Assert.Equal(258, snapshot.InFlight);
        Assert.Equal(0, snapshot.LiveRequests);
        Assert.True(tracker.WouldAllow(User, "phone", 1, []));
        Assert.True(tracker.TryEnter(User, "phone", 1, []));

        // After the idle limit the sweep aborts the dead responses and forgets them.
        _clock.Advance(StreamSlotTracker.IdleRequestAbortAfter);
        Assert.Equal(258, tracker.Sweep());
        Assert.Equal(258, aborted);
        Assert.DoesNotContain(tracker.GetSnapshot(), s => s.DeviceId == "android-native");
    }

    [Fact]
    public void AbortedRequest_IsNeverCounted_AndIsStaleForAKick()
    {
        var tracker = CreateTracker();
        using var cts = new CancellationTokenSource();
        var abortCalled = false;
        tracker.BeginRequest(User, "tv", () => abortCalled = true, cts.Token);
        _clock.Advance(TimeSpan.FromSeconds(61));

        cts.Cancel();

        Assert.Empty(tracker.GetHeldDevices(User, []));
        var (aborted, stale, released) = tracker.AbortDevice(User, "tv");
        Assert.Equal(0, aborted);
        Assert.Equal(0, stale); // Already dropped by the prune of GetHeldDevices.
        Assert.False(released);
        Assert.False(abortCalled);
    }

    [Fact]
    public void Kick_DistinguishesLiveResponsesFromStaleRegistrations()
    {
        var tracker = CreateTracker();
        using var cts = new CancellationTokenSource();
        var aborts = 0;
        tracker.BeginRequest(User, "tv", () => aborts++);
        tracker.BeginRequest(User, "tv", () => aborts++, cts.Token);
        cts.Cancel();

        var (aborted, stale, released) = tracker.AbortDevice(User, "tv");

        Assert.Equal(1, aborted);
        Assert.Equal(1, stale);
        Assert.True(released);
        Assert.Equal(1, aborts);
    }

    [Fact]
    public void EndRequest_IsIdempotent()
    {
        // The gate ends a registration from its finally and from Response.OnCompleted.
        var tracker = CreateTracker();
        var first = tracker.BeginRequest(User, "tv");
        var second = tracker.BeginRequest(User, "tv");

        tracker.EndRequest(User, "tv", first);
        tracker.EndRequest(User, "tv", first);

        Assert.True(first.IsCompleted);
        Assert.Equal(1, tracker.GetSnapshot().Single().InFlight);
        tracker.EndRequest(User, "tv", second);
        Assert.Equal(0, tracker.GetSnapshot().Single().InFlight);
    }

    [Fact]
    public void ReleaseDevice_FreesASlotWhoseResponsesAreAllIdle()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "tv", 1, []);
        tracker.BeginRequest(User, "tv");
        _clock.Advance(TimeSpan.FromSeconds(30));

        // Idle for 30 s only: still live, a stop report racing a download tail keeps it.
        Assert.False(tracker.ReleaseDevice(User, "tv"));

        _clock.Advance(StreamSlotTracker.ActiveResponseWindow);

        // Idle beyond the window and the TTL: already not counted (nothing left to free),
        // although its response is still open.
        Assert.False(tracker.ReleaseDevice(User, "tv"));
        Assert.True(tracker.TryEnter(User, "phone", 1, []));
    }

    [Fact]
    public void ReleaseDevice_InsideTheTtl_StopsCountingDespiteIdleResponses()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "tv", 1, []);
        var request = tracker.BeginRequest(User, "tv");
        _clock.Advance(StreamSlotTracker.ActiveResponseWindow + TimeSpan.FromSeconds(1));
        tracker.TryEnter(User, "tv", 1, []); // A fresh request start: recent again.

        Assert.True(tracker.ReleaseDevice(User, "tv"));
        Assert.Empty(tracker.GetHeldDevices(User, []));

        // The idle response resumes writing: it was admitted before, it counts again.
        _clock.Advance(TimeSpan.FromSeconds(5));
        tracker.Touch(request);
        Assert.Equal(new[] { "tv" }, tracker.GetHeldDevices(User, []));
        Assert.False(tracker.TryEnter(User, "phone", 1, []));
    }

    [Fact]
    public void NotHeldSlot_DoesNotLetItsDeviceBypassTheLimit()
    {
        // A slot kept only for its idle responses is no "already streaming" pass.
        var tracker = CreateTracker();
        tracker.TryEnter(User, "tv", 1, []);
        tracker.BeginRequest(User, "tv");
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(tracker.TryEnter(User, "phone", 1, []));

        Assert.False(tracker.TryEnter(User, "tv", 1, []));
        Assert.False(tracker.WouldAllow(User, "tv", 1, []));
    }

    [Fact]
    public void WouldAllow_AppliesTheAbandonedSlotRuleLikeTryEnter()
    {
        // PlaybackInfo used to deny where the media request itself would have been admitted.
        var tracker = CreateTracker();
        tracker.TryEnter(User, "closed-tab", 1, []);
        _clock.Advance(StreamSlotTracker.AbandonedAfter + TimeSpan.FromSeconds(1));

        Assert.True(tracker.WouldAllow(User, "phone", 1, []));
        Assert.False(tracker.WouldAllow(User, "phone", 1, ["closed-tab"]));
    }

    [Fact]
    public void ResetUser_ReleasesEverySlot_AndAbortsLiveResponsesOnly()
    {
        var tracker = CreateTracker();
        var aborts = 0;
        var other = Guid.Parse("6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef");
        tracker.TryEnter(User, "tv", 3, []);
        tracker.BeginRequest(User, "tv", () => aborts++);
        var done = tracker.BeginRequest(User, "phone", () => aborts++);
        tracker.EndRequest(User, "phone", done);
        tracker.BeginRequest(other, "laptop", () => aborts++);

        var (released, aborted, stale) = tracker.ResetUser(User);

        Assert.Equal(2, released);
        Assert.Equal(1, aborted);
        Assert.Equal(0, stale);
        Assert.Equal(1, aborts);
        Assert.Empty(tracker.GetHeldDevices(User, []));
        Assert.Equal(new[] { "laptop" }, tracker.GetHeldDevices(other, []));
        Assert.Equal((0, 0, 0), tracker.ResetUser(Guid.NewGuid()));
    }

    [Fact]
    public void Snapshot_ReportsLiveAndRecentSeparately()
    {
        var tracker = CreateTracker();
        var request = tracker.BeginRequest(User, "tv");
        _clock.Advance(TimeSpan.FromSeconds(70));
        tracker.Touch(request);

        var live = tracker.GetSnapshot().Single();
        Assert.Equal(1, live.LiveRequests);
        Assert.True(live.Recent); // The write is traffic too.
        Assert.Equal(_clock.GetUtcNow(), live.LastSeenUtc);

        tracker.EndRequest(User, "tv", request);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(tracker.GetSnapshot());
    }

    [Fact]
    public void Sweep_RemovesExpiredSlotsOfUsersWhoNeverComeBack()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "tv", 1, []);
        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(0, tracker.Sweep());
        Assert.Empty(tracker.GetSnapshot());
    }

    [Fact]
    public void IdleAbort_SparesResponsesThatWrote()
    {
        var tracker = CreateTracker();
        var aborts = 0;
        var writing = tracker.BeginRequest(User, "tv", () => aborts++);
        tracker.BeginRequest(User, "tv", () => aborts++);
        for (var i = 0; i < 31; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            tracker.Touch(writing);
        }

        Assert.Equal(1, tracker.Sweep());
        Assert.Equal(1, aborts);
        var slot = tracker.GetSnapshot().Single();
        Assert.Equal(1, slot.InFlight);
        Assert.Equal(1, slot.LiveRequests);
    }
}
