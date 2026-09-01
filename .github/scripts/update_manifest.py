#!/usr/bin/env python3
"""Add (or replace) one version entry in the Jellyfin plugin repository manifest.

Plugin metadata comes from build.yaml so there is a single source of truth; the
per-release fields (version, checksum, download URL) are passed in by the release
workflow. Re-running for a version that already exists overwrites that entry, so
a re-run of a failed release is safe.
"""

import argparse
import json
import os
import sys
from datetime import datetime, timezone

import yaml


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build-yaml", default="build.yaml")
    parser.add_argument("--manifest", default="manifest.json")
    parser.add_argument("--version", required=True, help="four-part version, e.g. 1.2.3.0")
    parser.add_argument("--checksum", required=True, help="MD5 of the release zip")
    parser.add_argument("--source-url", required=True, help="download URL of the release zip")
    parser.add_argument("--changelog", default="")
    parser.add_argument("--changelog-file", default="", help="read the changelog from this file instead")
    parser.add_argument("--repo-url", default="", help="https://github.com/owner/repo")
    args = parser.parse_args()

    changelog = args.changelog
    if args.changelog_file:
        with open(args.changelog_file, encoding="utf-8") as handle:
            changelog = handle.read()

    with open(args.build_yaml, encoding="utf-8") as handle:
        meta = yaml.safe_load(handle)

    if os.path.exists(args.manifest):
        with open(args.manifest, encoding="utf-8") as handle:
            manifest = json.load(handle)
    else:
        manifest = []

    index = next((i for i, p in enumerate(manifest) if p.get("guid") == meta["guid"]), None)
    existing = manifest[index] if index is not None else {}

    # Rebuilt from build.yaml on every release, so that file stays authoritative.
    plugin = {
        "guid": meta["guid"],
        "name": meta["name"],
        "description": meta.get("description", "").strip(),
        "overview": meta.get("overview", "").strip(),
        "owner": meta.get("owner", ""),
        "category": meta.get("category", "General"),
        "imageUrl": meta.get("imageUrl") or existing.get("imageUrl", ""),
        "versions": existing.get("versions", []),
    }
    if index is None:
        manifest.append(plugin)
    else:
        manifest[index] = plugin

    entry = {
        "version": args.version,
        "changelog": (changelog or meta.get("changelog", "")).strip(),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": args.source_url,
        "checksum": args.checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    if args.repo_url:
        entry["repositoryUrl"] = args.repo_url

    versions = [v for v in plugin.get("versions", []) if v.get("version") != args.version]
    # Jellyfin reads the list top-down, so newest first.
    plugin["versions"] = [entry] + versions

    with open(args.manifest, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=4)
        handle.write("\n")

    print(f"manifest updated: {meta['name']} {args.version}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
