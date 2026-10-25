using System;
using System.Linq;
using Jellyfin.Plugin.StreamLimit.Gate;
using Jellyfin.Plugin.StreamLimit.Notifications;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class SlotAbortTests
{
    private static readonly Guid User = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");

    private readonly TestClock _clock = new();

    private StreamSlotTracker CreateTracker() => new(_clock, TimeSpan.FromSeconds(60));

    [Fact]
    public void AbortDevice_AbortsInFlightResponses_AndReleasesTheSlotNow()
    {
        // A direct-play download is one long response: a kick must cut it and free the
        // slot immediately, not after the abandoned-slot rule.
        var tracker = CreateTracker();
        var aborted = 0;
        tracker.TryEnter(User, "tv", 1, []);
        tracker.BeginRequest(User, "tv", () => aborted++);
        tracker.BeginRequest(User, "TV", () => aborted++);

        var (count, released) = tracker.AbortDevice(User, "tv");

        Assert.Equal(2, count);
        Assert.True(released);
        Assert.Equal(2, aborted);
        Assert.Empty(tracker.GetSnapshot());
        Assert.True(tracker.TryEnter(User, "phone", 1, []));
    }

    [Fact]
    public void AbortDevice_OnlyTouchesThatDevice()
    {
        var tracker = CreateTracker();
        var otherAborted = false;
        tracker.TryEnter(User, "tv", 2, []);
        tracker.BeginRequest(User, "tv", () => { });
        tracker.BeginRequest(User, "phone", () => otherAborted = true);

        tracker.AbortDevice(User, "tv");

        Assert.False(otherAborted);
        var phone = Assert.Single(tracker.GetSnapshot());
        Assert.Equal("phone", phone.DeviceId);
        Assert.Equal(1, phone.InFlight);
    }

    [Fact]
    public void AbortDevice_UnknownDevice_IsANoOp()
    {
        var tracker = CreateTracker();

        Assert.Equal((0, false), tracker.AbortDevice(User, "nothing"));
    }

    [Fact]
    public void AbortDevice_ThrowingAbort_NeverEscapes()
    {
        var tracker = CreateTracker();
        var secondCalled = false;
        tracker.BeginRequest(User, "tv", () => throw new ObjectDisposedException("connection"));
        tracker.BeginRequest(User, "tv", () => secondCalled = true);

        var (count, released) = tracker.AbortDevice(User, "tv");

        Assert.True(released);
        Assert.Equal(1, count);
        Assert.True(secondCalled);
    }

    [Fact]
    public void EndRequest_UnregistersItsResponse_NoLeak()
    {
        var tracker = CreateTracker();
        var called = false;
        var first = tracker.BeginRequest(User, "tv", () => called = true);
        var second = tracker.BeginRequest(User, "tv", () => called = true);

        tracker.EndRequest(User, "tv", first);
        Assert.Equal(1, tracker.GetSnapshot().Single().InFlight);

        tracker.EndRequest(User, "tv", second);
        Assert.Equal(0, tracker.GetSnapshot().Single().InFlight);

        // Nothing left to abort once both responses ended.
        var (count, released) = tracker.AbortDevice(User, "tv");
        Assert.Equal(0, count);
        Assert.True(released);
        Assert.False(called);
    }

    [Fact]
    public void EndRequest_OfAKickedResponse_DoesNotTouchTheNewSlot()
    {
        // The aborted request's finally runs EndRequest after the device already came
        // back with a new response: that new response must keep holding its slot.
        var tracker = CreateTracker();
        var old = tracker.BeginRequest(User, "tv", () => { });
        tracker.AbortDevice(User, "tv");
        tracker.BeginRequest(User, "tv", () => { });

        tracker.EndRequest(User, "tv", old);
        tracker.EndRequest(User, "tv", old);

        Assert.Equal(1, tracker.GetSnapshot().Single().InFlight);
    }

    [Fact]
    public void EndRequest_WithoutRegistration_EndsOneResponse()
    {
        var tracker = CreateTracker();
        tracker.BeginRequest(User, "tv");
        tracker.BeginRequest(User, "tv");

        tracker.EndRequest(User, "tv");

        Assert.Equal(1, tracker.GetSnapshot().Single().InFlight);
    }

    [Fact]
    public void ReleaseDevice_StillKeepsInFlightSlots()
    {
        // Automatic stop reports keep their behaviour: only the admin kick aborts.
        var tracker = CreateTracker();
        var aborted = false;
        tracker.BeginRequest(User, "tv", () => aborted = true);

        Assert.False(tracker.ReleaseDevice(User, "tv"));
        Assert.False(aborted);
    }
}

public class WebhookSaveTests
{
    [Theory]
    [InlineData("https://new.example/hook", "https://old.example/hook", "https://new.example/hook", false)]
    [InlineData(" https://new.example/hook ", "", "https://new.example/hook", false)]
    [InlineData("", "https://old.example/hook", "", false)]
    [InlineData("ftp://bad.example/x", "https://old.example/hook", "https://old.example/hook", true)]
    [InlineData("not a url", null, "", true)]
    [InlineData("javascript:alert(1)", "file:///etc/passwd", "", true)]
    public void SanitizeOnSave_KeepsPreviousUrlWhenInvalid(string? incoming, string? previous, string expected, bool ignored)
    {
        Assert.Equal((expected, ignored), WebhookPayloads.SanitizeOnSave(incoming, previous));
    }

    [Theory]
    [InlineData("ftp://user:secret@host/x", "ftp")]
    [InlineData("JavaScript:alert(1)", "javascript")]
    [InlineData("discord.com/api/webhooks/1/SECRET", "none")]
    [InlineData("no scheme here", "none")]
    [InlineData(null, "none")]
    [InlineData("we ird:x", "none")]
    public void DescribeScheme_RevealsTheSchemeOnly(string? url, string expected)
    {
        Assert.Equal(expected, WebhookPayloads.DescribeScheme(url));
    }
}
