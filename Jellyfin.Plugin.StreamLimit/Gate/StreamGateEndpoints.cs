using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// How the gate treats a request.
/// </summary>
public enum GateKind
{
    /// <summary>Not a playback endpoint; never gated.</summary>
    None = 0,

    /// <summary>Delivers media bytes; claims and holds a playback slot.</summary>
    MediaBytes = 1,

    /// <summary>Playback negotiation (PlaybackInfo); checked without claiming a slot.</summary>
    StartupCheck = 2,
}

/// <summary>
/// Maps Jellyfin API controller actions to their gate treatment.
/// </summary>
/// <remarks>
/// The action list was verified identical across Jellyfin 10.11.0, 12.0, 12.1 and 13.0-dev (master, 2026-09):
/// no playback endpoint was added, removed or renamed. Matching by controller and
/// action name (from <c>ControllerActionDescriptor</c>) is therefore stable and,
/// unlike path matching, immune to base-url prefixes and route casing. Several of
/// these endpoints are anonymous in Jellyfin (no [Authorize]) — a global resource
/// filter still runs on them, which is exactly why the gate uses one.
/// </remarks>
public static class StreamGateEndpoints
{
    private static readonly Dictionary<(string Controller, string Action), GateKind> Map = new()
    {
        // Progressive video: /Videos/{itemId}/stream(.{container}) — anonymous in Jellyfin.
        [("Videos", "GetVideoStream")] = GateKind.MediaBytes,
        [("Videos", "GetVideoStreamByContainer")] = GateKind.MediaBytes,

        // Progressive audio: /Audio/{itemId}/stream(.{container}) — anonymous in Jellyfin.
        [("Audio", "GetAudioStream")] = GateKind.MediaBytes,
        [("Audio", "GetAudioStreamByContainer")] = GateKind.MediaBytes,

        // Adaptive HLS: playlists and segments.
        [("DynamicHls", "GetLiveHlsStream")] = GateKind.MediaBytes,
        [("DynamicHls", "GetMasterHlsVideoPlaylist")] = GateKind.MediaBytes,
        [("DynamicHls", "GetMasterHlsAudioPlaylist")] = GateKind.MediaBytes,
        [("DynamicHls", "GetVariantHlsVideoPlaylist")] = GateKind.MediaBytes,
        [("DynamicHls", "GetVariantHlsAudioPlaylist")] = GateKind.MediaBytes,
        [("DynamicHls", "GetHlsVideoSegment")] = GateKind.MediaBytes,
        [("DynamicHls", "GetHlsAudioSegment")] = GateKind.MediaBytes,

        // Legacy HLS segments — partly anonymous in Jellyfin.
        [("HlsSegment", "GetHlsAudioSegmentLegacy")] = GateKind.MediaBytes,
        [("HlsSegment", "GetHlsVideoSegmentLegacy")] = GateKind.MediaBytes,
        [("HlsSegment", "GetHlsPlaylistLegacy")] = GateKind.MediaBytes,

        // Universal audio endpoint used by music clients.
        [("UniversalAudio", "GetUniversalAudioStream")] = GateKind.MediaBytes,

        // Live TV byte delivery — anonymous in Jellyfin.
        [("LiveTv", "GetLiveRecordingFile")] = GateKind.MediaBytes,
        [("LiveTv", "GetLiveStreamFile")] = GateKind.MediaBytes,

        // Full-file byte routes on LibraryController: /Items/{itemId}/File serves the
        // original media to ANY authenticated user and /Items/{itemId}/Download to
        // users with the Download permission — both playable while fetching, so they
        // count as streams too.
        [("Library", "GetFile")] = GateKind.MediaBytes,
        [("Library", "GetDownload")] = GateKind.MediaBytes,

        // Playback negotiation: deny here and well-behaved clients show a proper
        // "playback not allowed" dialog before ever requesting bytes.
        [("MediaInfo", "GetPlaybackInfo")] = GateKind.StartupCheck,
        [("MediaInfo", "GetPostedPlaybackInfo")] = GateKind.StartupCheck,
        [("MediaInfo", "OpenLiveStream")] = GateKind.StartupCheck,
    };

    /// <summary>
    /// Classifies a controller action.
    /// </summary>
    /// <param name="controllerName">The controller name (without the "Controller" suffix).</param>
    /// <param name="actionName">The action method name.</param>
    /// <returns>The gate treatment for this action.</returns>
    public static GateKind Classify(string? controllerName, string? actionName)
    {
        if (string.IsNullOrEmpty(controllerName) || string.IsNullOrEmpty(actionName))
        {
            return GateKind.None;
        }

        return Map.TryGetValue((controllerName, actionName), out var kind) ? kind : GateKind.None;
    }
}
