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


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def check_not_republished(versions, version, checksum):
    """A version already in the manifest must be the same build: servers cache by version."""
    for existing in versions:
        if existing["version"] == version and existing.get("checksum", "").upper() != checksum:
            sys.exit(f"Version {version} is already in manifest.json with checksum {existing.get('checksum')}, "
                     f"but the new zip has {checksum}. Bump the version in Directory.Build.props instead of "
                     f"republishing a different build under the same number.")


def check_line_ordering(versions, jf12):
    """Jellyfin installs the highest version whose targetAbi <= the server version, with no upper bound,
    so a 10.11 build numbered at or above the Jellyfin 12 build would be installed on Jellyfin 12 servers
    (net9 DLL, fails to load). The Jellyfin 12 build must outnumber every 10.11 build."""
    jf10_versions = [v["version"] for v in versions if v.get("targetAbi", "").startswith("10.")]
    too_high = [v for v in jf10_versions if version_key(v) >= version_key(jf12)]
    if too_high:
        sys.exit(f"Jellyfin 12 build {jf12} must be greater than every 10.11 build in manifest.json, "
                 f"but these are not lower: {', '.join(sorted(too_high, key=version_key))}. "
                 f"Fix the versions in Directory.Build.props (10.11 = 1.1.0.x, Jellyfin 12 = 1.1.1.x).")


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
    checksum10 = md5(release_dir / zip10)
    checksum12 = md5(release_dir / zip12)

    check_not_republished(package["versions"], default, checksum10)
    check_not_republished(package["versions"], jf12, checksum12)

    # Insert the 10.11 entry first, then the Jellyfin 12 one, so the newest line stays on top.
    upsert(package["versions"], {
        "version": default,
        "changelog": changelog,
        "targetAbi": "10.11.0.0",
        "sourceUrl": f"{base_url}/{zip10}",
        "timestamp": timestamp,
        "checksum": checksum10,
    })
    upsert(package["versions"], {
        "version": jf12,
        "changelog": (f"Jellyfin 12 build of v{default} (same changes — see the {default} entry). "
                      f"Servers pick the right build automatically: 10.11 installs {default}, Jellyfin 12 installs {jf12}."),
        "targetAbi": "12.0.0.0",
        "sourceUrl": f"{base_url}/{zip12}",
        "timestamp": timestamp,
        "checksum": checksum12,
    })

    # Checked on the final list (existing entries + the new 10.11 one), before anything is written.
    check_line_ordering(package["versions"], jf12)

    manifest_path.write_text(json.dumps(manifest, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"manifest.json: added {default} (10.11) and {jf12} (Jellyfin 12) for {tag}")


if __name__ == "__main__":
    main()
