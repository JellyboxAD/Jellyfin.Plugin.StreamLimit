using System;
using System.Linq;
using Jellyfin.Plugin.StreamLimit.Gate;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class PlaybackTrackerTests
{
    private static readonly Guid User = Guid.Parse("5f0c1c52-8a43-4d0a-9d55-2b8f1f3c7e11");
    private static readonly Func<PlaybackRef, bool> NoTranscode = _ => false;
    private static readonly Func<PlaybackRef, bool> AllTranscode = _ => true;

    private readonly TestClock _clock = new();

    private PlaybackTracker CreateTracker() => new(_clock);

    // Keeps the given playbacks fetching (HLS-like) for the given time, one request every 5 s.
    private void Play(PlaybackTracker tracker, TimeSpan duration, params (string Device, string Psid)[] playbacks)
    {
        var step = TimeSpan.FromSeconds(5);
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += step)
        {
            _clock.Advance(step);
            foreach (var (device, psid) in playbacks)
            {
                tracker.Observe(User, device, psid, "item", true);
            }
        }
    }

    [Fact]
    public void TwoTabsOfOneDevice_OverLimit_NewestRefusedAfterGrace()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc" };
        tracker.Observe(User, "pc", "tab-a", "item", true);
        _clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Observe(User, "pc", "tab-b", "item", true);

        // Too young: it may be replacing tab-a.
        Assert.Empty(tracker.Enforce(User, held, 2, 0, NoTranscode));

        Play(tracker, TimeSpan.FromSeconds(25), ("pc", "tab-a"), ("pc", "tab-b"));
        var denial = Assert.Single(tracker.Enforce(User, held, 2, 0, NoTranscode));

        Assert.Equal("tab-b", denial.PlaySessionId);
        Assert.Equal(PlaybackDenialReason.PlaybackLimit, denial.Reason);
        Assert.Equal(2, denial.Limit);
        Assert.Equal(PlaybackDenialReason.PlaybackLimit, tracker.Observe(User, "pc", "tab-b", "item", true)!.DeniedReason);
        Assert.Null(tracker.Observe(User, "pc", "tab-a", "item", true)!.DeniedReason);
    }

    [Fact]
    public void QualitySwitch_OldPlaybackStops_NothingRefused()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc" };
        tracker.Observe(User, "pc", "before-switch", "item", true);
        _clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Observe(User, "pc", "after-switch", "item", true);

        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "after-switch"));

        Assert.Empty(tracker.Enforce(User, held, 2, 0, NoTranscode));
    }

    [Fact]
    public void SecondTab_RefusedWithinSeconds()
    {
        var tracker = CreateTracker();
        string[] held = { "pc" };
        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "tab-a"));
        tracker.Observe(User, "pc", "tab-b", "item", true);

        // One more request of each (the HLS segment interval), and the overlap is confirmed.
        Play(tracker, TimeSpan.FromSeconds(5), ("pc", "tab-a"), ("pc", "tab-b"));

        var denial = Assert.Single(tracker.Enforce(User, held, 1, 0, NoTranscode));
        Assert.Equal("tab-b", denial.PlaySessionId);
    }

    [Fact]
    public void QualitySwitch_OldStillCountedButSilent_NotRefused()
    {
        var tracker = CreateTracker();
        string[] held = { "pc" };
        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "before-switch"));
        tracker.Observe(User, "pc", "after-switch", "item", true);

        // 10 s later the old playback is still live (last request < 15 s ago) but made no
        // request since the switch: no overlap.
        Play(tracker, TimeSpan.FromSeconds(10), ("pc", "after-switch"));

        Assert.Equal(2, tracker.CountLive(User));
        Assert.Empty(tracker.Enforce(User, held, 1, 0, NoTranscode));
    }

    [Fact]
    public void DirectPlaySwitch_TailOfOldDownload_NotRefused()
    {
        var tracker = CreateTracker();
        var slots = new StreamSlotTracker(_clock);
        string[] held = { "pc" };

        tracker.Observe(User, "pc", "old", "movie", true);
        var oldDownload = slots.BeginRequest(User, "pc", () => { });
        tracker.Attach(User, "pc", "old", "movie", oldDownload);
        _clock.Advance(TimeSpan.FromSeconds(30));
        tracker.Observe(User, "pc", "new", "movie", true);

        // The old response still sends a few bytes right after the switch, then goes quiet.
        _clock.Advance(TimeSpan.FromSeconds(3));
        slots.Touch(oldDownload);
        Play(tracker, TimeSpan.FromSeconds(10), ("pc", "new"));

        Assert.Empty(tracker.Enforce(User, held, 1, 0, NoTranscode));
    }

    [Fact]
    public void TwoDirectPlayTabs_RefusedOnceTheOldDownloadKeepsGoing()
    {
        var tracker = CreateTracker();
        var slots = new StreamSlotTracker(_clock);
        string[] held = { "pc" };

        tracker.Observe(User, "pc", "tab-a", "movie-a", true);
        var downloadA = slots.BeginRequest(User, "pc", () => { });
        tracker.Attach(User, "pc", "tab-a", "movie-a", downloadA);
        _clock.Advance(TimeSpan.FromSeconds(30));
        tracker.Observe(User, "pc", "tab-b", "movie-b", true);
        var downloadB = slots.BeginRequest(User, "pc", () => { });
        tracker.Attach(User, "pc", "tab-b", "movie-b", downloadB);

        _clock.Advance(TimeSpan.FromSeconds(6));
        slots.Touch(downloadA);
        Assert.Empty(tracker.Enforce(User, held, 1, 0, NoTranscode));

        _clock.Advance(TimeSpan.FromSeconds(4));
        slots.Touch(downloadA);
        slots.Touch(downloadB);
        var denial = Assert.Single(tracker.Enforce(User, held, 1, 0, NoTranscode));
        Assert.Equal("tab-b", denial.PlaySessionId);
    }

    [Fact]
    public void TwoTabs_UnderLimit_NothingRefused()
    {
        var tracker = CreateTracker();
        string[] held = { "pc" };
        tracker.Observe(User, "pc", "tab-a", "item", true);
        tracker.Observe(User, "pc", "tab-b", "item", true);

        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "tab-a"), ("pc", "tab-b"));

        Assert.Empty(tracker.Enforce(User, held, 2, 0, NoTranscode));
    }

    [Fact]
    public void OnePlaybackPerDevice_IsTheDeviceRule_NotRefusedHere()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc", "phone" };
        Play(tracker, TimeSpan.FromSeconds(30), ("tv", "a"), ("pc", "b"), ("phone", "c"));

        // Devices over the limit are the device gate's business (it refuses the new device).
        Assert.Empty(tracker.Enforce(User, held, 2, 0, NoTranscode));
    }

    [Fact]
    public void TranscodeLimit_NewestTranscodeRefused()
    {
        var tracker = CreateTracker();
        tracker.Observe(User, "tv", "first", "item", true);
        _clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Observe(User, "pc", "second", "item", true);

        Play(tracker, TimeSpan.FromSeconds(25), ("tv", "first"), ("pc", "second"));
        var denial = Assert.Single(tracker.Enforce(User, Array.Empty<string>(), 0, 1, AllTranscode));

        Assert.Equal("second", denial.PlaySessionId);
        Assert.Equal(PlaybackDenialReason.TranscodeLimit, denial.Reason);
    }

    [Fact]
    public void TranscodeLimit_RemuxesAndAudioDoNotCount()
    {
        var tracker = CreateTracker();
        Play(tracker, TimeSpan.FromSeconds(30), ("tv", "first"), ("pc", "second"));
        tracker.Observe(User, "phone", "music", "song", false);

        Assert.Empty(tracker.Enforce(User, Array.Empty<string>(), 0, 1, NoTranscode));
    }

    [Fact]
    public void TranscodeLookupFailure_NeverRefuses()
    {
        var tracker = CreateTracker();
        Play(tracker, TimeSpan.FromSeconds(30), ("tv", "first"), ("pc", "second"));

        Assert.Empty(tracker.Enforce(User, Array.Empty<string>(), 0, 1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void LiveDownload_KeepsCounting_AndIsAbortedOnRefusal()
    {
        var tracker = CreateTracker();
        var slots = new StreamSlotTracker(_clock);
        var abortedA = false;
        var abortedB = false;
        string[] held = { "tv", "pc" };

        tracker.Observe(User, "pc", "tab-a", "movie-a", true);
        var downloadA = slots.BeginRequest(User, "pc", () => abortedA = true);
        tracker.Attach(User, "pc", "tab-a", "movie-a", downloadA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Observe(User, "pc", "tab-b", "movie-b", true);
        var downloadB = slots.BeginRequest(User, "pc", () => abortedB = true);
        tracker.Attach(User, "pc", "tab-b", "movie-b", downloadB);

        // Single long responses: no new request, only bytes written.
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            slots.Touch(downloadA);
            slots.Touch(downloadB);
        }

        var denial = Assert.Single(tracker.Enforce(User, held, 2, 0, NoTranscode));
        Assert.Equal("tab-b", denial.PlaySessionId);
        foreach (var abort in denial.Aborts)
        {
            abort();
        }

        Assert.True(abortedB);
        Assert.False(abortedA);
    }

    [Fact]
    public void StopReport_ForgetsThePlayback()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc" };
        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "tab-a"), ("pc", "tab-b"));
        Assert.Single(tracker.Enforce(User, held, 2, 0, NoTranscode));

        tracker.End(User, "pc", "tab-b");

        var again = tracker.Observe(User, "pc", "tab-b", "item", true)!;
        Assert.True(again.IsNew);
        Assert.Null(again.DeniedReason);
    }

    [Fact]
    public void RefusedPlayback_ForgottenAfterItGivesUp()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc" };
        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "tab-a"), ("pc", "tab-b"));
        Assert.Single(tracker.Enforce(User, held, 2, 0, NoTranscode));

        _clock.Advance(PlaybackTracker.ForgetAfter + TimeSpan.FromSeconds(1));
        tracker.Sweep();

        Assert.Null(tracker.GetDenial(User, "pc", "tab-b", "item"));
    }

    [Fact]
    public void RefusedPlayback_RecoversOnceTheOtherStreamStops()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc" };
        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "tab-a"), ("pc", "tab-b"));
        Assert.Single(tracker.Enforce(User, held, 2, 0, NoTranscode));

        // tab-a is closed without a stop report; tab-b keeps retrying.
        Play(tracker, PlaybackTracker.DenialHolds, ("pc", "tab-b"));
        Assert.Empty(tracker.Enforce(User, held, 2, 0, NoTranscode));
        Assert.Null(tracker.GetDenial(User, "pc", "tab-b", "item"));
    }

    [Fact]
    public void RefusedPlayback_StillOverLimit_RefusedAgain()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc" };
        Play(tracker, TimeSpan.FromSeconds(30), ("pc", "tab-a"), ("pc", "tab-b"));
        Assert.Single(tracker.Enforce(User, held, 2, 0, NoTranscode));

        Play(tracker, PlaybackTracker.DenialHolds, ("pc", "tab-a"), ("pc", "tab-b"));
        var again = Assert.Single(tracker.Enforce(User, held, 2, 0, NoTranscode));
        Assert.Equal("tab-b", again.PlaySessionId);
    }

    [Fact]
    public void RequestWithoutPlaySessionId_JoinsThePlaybackOfTheSameItem()
    {
        var tracker = CreateTracker();
        string[] held = { "tv", "pc" };

        // Playlist with a PlaySessionId, then requests of the same item without one.
        for (var i = 0; i < 8; i++)
        {
            tracker.Observe(User, "pc", i == 0 ? "only-playback" : null, "item", true);
            _clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, tracker.CountLive(User));
        Assert.Empty(tracker.Enforce(User, held, 2, 0, NoTranscode));
    }

    [Fact]
    public void HasOtherLivePlayback_OnlyCountsOtherAllowedPlaybacksOfTheDevice()
    {
        var tracker = CreateTracker();
        tracker.Observe(User, "pc", "tab-a", "item", true);
        Assert.False(tracker.HasOtherLivePlayback(User, "pc", "tab-a", "item"));

        tracker.Observe(User, "pc", "tab-b", "item", true);
        tracker.Observe(User, "tv", "other-device", "item", true);
        Assert.True(tracker.HasOtherLivePlayback(User, "pc", "tab-a", "item"));
        Assert.False(tracker.HasOtherLivePlayback(User, "tv", "other-device", "item"));
    }

    [Fact]
    public void RequestWithoutPlaySessionId_KeyedByItem()
    {
        var tracker = CreateTracker();

        Assert.NotNull(tracker.Observe(User, "pc", null, "movie", false));
        Assert.Null(tracker.Observe(User, "pc", null, null, false));
    }

    [Fact]
    public void Sweep_ReturnsUsersWithSeveralLivePlaybacks()
    {
        var tracker = CreateTracker();
        tracker.Observe(User, "pc", "tab-a", "item", true);
        Assert.Empty(tracker.Sweep());

        tracker.Observe(User, "pc", "tab-b", "item", true);
        Assert.Equal(new[] { User }, tracker.Sweep().ToArray());
    }

    [Theory]
    [InlineData("-i file.mkv -map 0:0 -codec:v:0 copy -codec:a:0 aac", true)]
    [InlineData("-i file.mkv -c:v copy -c:a copy", true)]
    [InlineData("-i file.mkv -codec:v:0 libx264 -preset veryfast", false)]
    [InlineData("-i file.mkv -codec:a:0 copy -codec:v:0 h264_nvenc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVideoCopy_DetectsRemux(string? arguments, bool expected)
        => Assert.Equal(expected, PlaybackLimitEnforcer.IsVideoCopy(arguments));

    [Theory]
    [InlineData("GetVideoStream", "true", false)]
    [InlineData("GetVideoStream", null, true)]
    [InlineData("GetVideoStreamByContainer", "false", true)]
    [InlineData("GetHlsVideoSegment", null, true)]
    [InlineData("GetMasterHlsVideoPlaylist", null, true)]
    [InlineData("GetAudioStream", null, false)]
    [InlineData("GetHlsAudioSegment", null, false)]
    [InlineData("GetFile", null, false)]
    public void CanTranscodeVideo_OnlyVideoFfmpegRoutes(string action, string? staticValue, bool expected)
        => Assert.Equal(expected, StreamGateEndpoints.CanTranscodeVideo(action, staticValue));
}
