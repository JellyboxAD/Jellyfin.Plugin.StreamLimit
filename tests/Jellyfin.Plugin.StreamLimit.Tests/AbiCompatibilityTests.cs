using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class AbiCompatibilityTests
{
    /// <summary>
    /// A server only loads the plugin if its own Jellyfin assemblies are at least the version the plugin was
    /// compiled against. Building against a floating 10.11.* picked 10.11.11 and broke every older 10.11.x
    /// server (issue #19), so the plugin must always reference the first release of its line (x.y.0.0).
    /// </summary>
    [Fact]
    public void JellyfinReferences_TargetFirstReleaseOfTheLine()
    {
        var jellyfinRefs = typeof(Plugin).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("MediaBrowser.", System.StringComparison.Ordinal)
                        || a.Name.StartsWith("Jellyfin.", System.StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(jellyfinRefs);
        Assert.All(jellyfinRefs, a =>
        {
            Assert.True(
                a.Version!.Build == 0 && a.Version.Revision == 0,
                $"{a.Name} is referenced as {a.Version}; build against Jellyfin x.y.0 so older patch releases can load the plugin.");
        });
    }
}
