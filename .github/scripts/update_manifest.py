"""Add the just-released versions to manifest.json (run by the release workflow).

Usage: update_manifest.py <tag> <release_dir>

Versions come from Directory.Build.props (10.11 line = default <Version>, Jellyfin 12 line =
the net10.0 override), the changelog from the matching "### [vX]" section of build.yaml, and the
checksums from the zips the workflow just built, so the manifest can never drift from the release.
"""

import datetime
import hashlib
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
REPO = "JellyboxAD/Jellyfin.Plugin.StreamLimit"
GUID = "d98fbe02-daf3-4c09-a832-4b4e1d07326c"


def read_versions():
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    default = re.search(r"<PropertyGroup>\s*<Version>([\d.]+)</Version>", props).group(1)
    jf12 = re.search(r"'net10\.0'\">\s*<Version>([\d.]+)</Version>", props).group(1)
    return default, jf12


def read_changelog(version):
    text = (ROOT / "build.yaml").read_text(encoding="utf-8")
    match = re.search(r"### \[v" + re.escape(version) + r"\]\n(.*?)(?:\n\s*### \[|\Z)", text, re.S)
    if not match:
        sys.exit(f"No changelog section for v{version} in build.yaml")
    return "\n".join(line.strip() for line in match.group(1).strip().splitlines())


def md5(path):
    return hashlib.md5(path.read_bytes()).hexdigest().upper()


def upsert(versions, entry):
    versions[:] = [v for v in versions if v["version"] != entry["version"]]
    versions.insert(0, entry)


def main():
    tag, release_dir = sys.argv[1], pathlib.Path(sys.argv[2])
    default, jf12 = read_versions()
    changelog = read_changelog(default)
    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    base_url = f"https://github.com/{REPO}/releases/download/{tag}"

    manifest_path = ROOT / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    package = next(p for p in manifest if p["guid"] == GUID)

    zip10 = f"StreamLimit-{tag}.zip"
    zip12 = f"StreamLimit-{tag}-jellyfin12.zip"

    # Insert the 10.11 entry first, then the Jellyfin 12 one, so the newest line stays on top.
    upsert(package["versions"], {
        "version": default,
        "changelog": changelog,
        "targetAbi": "10.11.0.0",
        "sourceUrl": f"{base_url}/{zip10}",
        "timestamp": timestamp,
        "checksum": md5(release_dir / zip10),
    })
    upsert(package["versions"], {
        "version": jf12,
        "changelog": f"Jellyfin 12 build of v{default} (same changes).\n{changelog}",
        "targetAbi": "12.0.0.0",
        "sourceUrl": f"{base_url}/{zip12}",
        "timestamp": timestamp,
        "checksum": md5(release_dir / zip12),
    })

    manifest_path.write_text(json.dumps(manifest, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"manifest.json: added {default} (10.11) and {jf12} (Jellyfin 12) for {tag}")


if __name__ == "__main__":
    main()
