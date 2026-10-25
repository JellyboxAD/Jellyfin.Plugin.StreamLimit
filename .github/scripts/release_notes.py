"""Write the GitHub release description for a tag (run by the release workflow).

Usage: release_notes.py <tag> <output.md>

The body is built from the same sources as the manifest (Directory.Build.props for the two
versions, the matching "### [vX]" changelog section of build.yaml), so the release page, the
plugin catalog and the repository never disagree.
"""

import pathlib
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from update_manifest import REPO, read_changelog, read_versions  # noqa: E402


def previous_tag(tag):
    tags = subprocess.run(["git", "tag", "--list", "v*", "--sort=-v:refname"],
                          capture_output=True, text=True, check=True).stdout.split()
    older = tags[tags.index(tag) + 1:] if tag in tags else tags
    return older[0] if older else None


def main():
    tag, output = sys.argv[1], pathlib.Path(sys.argv[2])
    default, jf12 = read_versions()
    changes = read_changelog(default)

    lines = [
        f"## Stream Limiter {tag}",
        "",
        "Install or update from **Dashboard → Plugins** (your server picks the right build), then restart Jellyfin.",
        "",
        "| Jellyfin server | Version | File |",
        "|---|---|---|",
        f"| 10.11.0 → 10.11.x | `{default}` | `StreamLimit-{tag}.zip` |",
        f"| 12.0 → 12.x | `{jf12}` | `StreamLimit-{tag}-jellyfin12.zip` |",
        "",
        "### Changes",
        changes,
        "",
        "### Upgrading",
        "Your limits and settings are kept. If the plugin shows *NotSupported* after updating: uninstall it, "
        "restart, reinstall it from the catalog and restart again.",
    ]

    prev = previous_tag(tag)
    if prev:
        lines += ["", f"**Full changelog:** https://github.com/{REPO}/compare/{prev}...{tag}"]

    output.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Release notes for {tag} written to {output}")


if __name__ == "__main__":
    main()
