using Jellyfin.Plugin.StreamLimit.Gate;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class StreamGateEndpointsTests
{
    [Theory]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("Videos", "GetVideoStreamByContainer")]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("Audio", "GetAudioStreamByContainer")]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist")]
    [InlineData("DynamicHls", "GetVariantHlsVideoPlaylist")]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    [InlineData("DynamicHls", "GetHlsAudioSegment")]
    [InlineData("DynamicHls", "GetLiveHlsStream")]
    [InlineData("HlsSegment", "GetHlsVideoSegmentLegacy")]
    [InlineData("UniversalAudio", "GetUniversalAudioStream")]
    [InlineData("LiveTv", "GetLiveStreamFile")]
    [InlineData("LiveTv", "GetLiveRecordingFile")]
    public void MediaByteEndpoints_AreGated(string controller, string action)
    {
        Assert.Equal(GateKind.MediaBytes, StreamGateEndpoints.Classify(controller, action));
    }

    [Theory]
    [InlineData("MediaInfo", "GetPlaybackInfo")]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo")]
    [InlineData("MediaInfo", "OpenLiveStream")]
    public void NegotiationEndpoints_AreStartupChecked(string controller, string action)
    {
        Assert.Equal(GateKind.StartupCheck, StreamGateEndpoints.Classify(controller, action));
    }

    [Theory]
    [InlineData("Items", "GetItems")]
    [InlineData("Image", "GetItemImage")]
    [InlineData("Trickplay", "GetTrickplayTileImage")]
    [InlineData("Subtitle", "GetSubtitle")]
    [InlineData("Sessions", "ReportPlaybackStart")]
    [InlineData("System", "GetSystemInfo")]
    [InlineData(null, "GetVideoStream")]
    [InlineData("Videos", null)]
    public void OtherEndpoints_AreNotGated(string? controller, string? action)
    {
        Assert.Equal(GateKind.None, StreamGateEndpoints.Classify(controller, action));
    }
}
