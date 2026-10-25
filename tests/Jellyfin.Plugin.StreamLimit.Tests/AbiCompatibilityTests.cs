using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class AbiCompatibilityTests
{
    /// <summary>
    /// A server only loads the plugin if its own Jellyfin assemblies are at least the version the plugin was
    /// compiled against. Building against a floating 10.11.* picked 10.11.11 and broke every older 10.11.x
    /// server (issue #19), and on Jellyfin 12 every minor bumps the assembly version (12.3.0.0), so a build
    /// against 12.3.0 would not load on 12.0-12.2. The plugin must be built against exactly the first release
    /// its manifest entry targets (targetAbi: 10.11.0.0 / 12.0.0.0) and reference that version.
    /// </summary>
    [Fact]
    public void JellyfinReferences_TargetFirstReleaseOfTheLine()
    {
        var targetAbi = GetVersionMetadata("JellyfinTargetAbi");
        var jellyfinVersion = GetVersionMetadata("JellyfinVersion");

        Assert.True(
            jellyfinVersion.Major == targetAbi.Major
            && jellyfinVersion.Minor == targetAbi.Minor
            && jellyfinVersion.Build == 0,
            $"JellyfinVersion is {jellyfinVersion}; this build targets Jellyfin {targetAbi.Major}.{targetAbi.Minor}.0 and must be built against exactly that release.");

        var jellyfinRefs = typeof(Plugin).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("MediaBrowser.", StringComparison.Ordinal)
                        || a.Name.StartsWith("Jellyfin.", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(jellyfinRefs);
        Assert.All(jellyfinRefs, a =>
        {
            Assert.True(
                a.Version!.Major == targetAbi.Major
                && a.Version.Minor == targetAbi.Minor
                && a.Version.Build == 0
                && a.Version.Revision == 0,
                $"{a.Name} is referenced as {a.Version}; expected {targetAbi.Major}.{targetAbi.Minor}.0.0 so every server of the line can load the plugin.");
        });
    }

    private static Version GetVersionMetadata(string key)
    {
        // Stamped by the plugin csproj from $(JellyfinVersion) / $(JellyfinTargetAbi).
        var raw = typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

        Assert.False(string.IsNullOrEmpty(raw), $"The plugin assembly carries no {key} metadata.");
        Assert.True(Version.TryParse(raw, out var version), $"{key} '{raw}' is not a plain x.y.z version.");
        return version!;
    }
}
