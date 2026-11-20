#!/usr/bin/env python3
"""
GitHub Releases upload script for DropWheel.
Uploads missing releases from D:\\plugins\\DropWheel-release-assets\\ to GitHub.
Verifies SHA-1 against manifest.json git blob IDs before upload.
"""

import json
import hashlib
import subprocess
import sys
import os
from pathlib import Path
from typing import Dict, List, Optional, Tuple

MANIFEST_PATH = Path("D:/plugins/DropWheel-release-assets/manifest.json")
ASSETS_ROOT = Path("D:/plugins/DropWheel-release-assets")
REPO = "yahyaabohashem332/DropWheel"

# Releases already on GitHub (from gh release list)
UPLOADED_TAGS = {
    "v1.0.0-build.1", "v1.0.0-build.2", "v1.0.0-build.3", "v1.0.0-build.4",
    "v1.0.0", "v1.1.0", "v1.2.0", "v1.3.0", "v1.3.1",
    "v1.21.0", "v1.22.0"
}

# Version name mapping: folder name -> display title prefix
VERSION_TITLES = {
    "v1.0.0-build.1": "Petal 1.0.0 (build 1 of 5)",
    "v1.0.0-build.2": "Petal 1.0.0 (build 2 of 5)",
    "v1.0.0-build.3": "Petal 1.0.0 (build 3 of 5)",
    "v1.0.0-build.4": "Petal 1.0.0 (build 4 of 5)",
    "v1.0.0": "Petal 1.0.0",
    "v1.1.0": "Petal 1.1.0",
    "v1.2.0": "Petal 1.2.0",
    "v1.3.0": "Petal 1.3.0",
    "v1.3.1": "Petal 1.3.1",
    "v1.3.2": "Petal 1.3.2",
    "v1.4.0": "Petal 1.4.0",
    "v1.5.0": "Petal 1.5.0",
    "v1.5.1": "Petal 1.5.1",
    "v1.5.2": "Petal 1.5.2",
    "v1.6.0": "Petal 1.6.0",
    "v1.7.0": "Petal 1.7.0",
    "v1.8.0": "Petal 1.8.0",
    "v1.8.1": "Petal 1.8.1",
    "v1.8.2": "Petal 1.8.2",
    "v1.8.3": "Petal 1.8.3",
    "v1.8.4": "Petal 1.8.4",
    "v1.9.0": "DropWheel 1.9.0",
    "v1.10.0": "DropWheel 1.10.0",
    "v1.11.0": "DropWheel 1.11.0",
    "v1.12.0": "DropWheel 1.12.0",
    "v1.13.0": "DropWheel 1.13.0",
    "v1.14.0": "DropWheel 1.14.0",
    "v1.15.0": "DropWheel 1.15.0",
    "v1.16.0": "DropWheel 1.16.0",
}

def compute_sha1(filepath: Path) -> str:
    """Compute SHA-1 of file (git blob format: 'blob <size>\0<content>')"""
    content = filepath.read_bytes()
    header = f"blob {len(content)}\0".encode()
    return hashlib.sha1(header + content).hexdigest()

def load_manifest() -> List[Dict]:
    """Load manifest.json"""
    with open(MANIFEST_PATH, 'r', encoding='utf-8') as f:
        return json.load(f)

def find_release_in_manifest(tag: str, manifest: List[Dict]) -> Optional[Dict]:
    """Find release entry in manifest by tag"""
    for entry in manifest:
        if entry.get('tag') == tag:
            return entry
    return None

def verify_assets(release_entry: Dict) -> Tuple[bool, List[str]]:
    """Verify all assets match their git blob IDs"""
    errors = []
    for asset in release_entry.get('assets', []):
        filepath = Path(asset['file'])
        if not filepath.exists():
            errors.append(f"Missing file: {filepath}")
            continue
        expected_blob = asset['blob']
        actual_blob = compute_sha1(filepath)
        if expected_blob != actual_blob:
            errors.append(f"SHA-1 mismatch for {filepath.name}: expected {expected_blob}, got {actual_blob}")
    return len(errors) == 0, errors

def get_release_notes(release_entry: Dict) -> str:
    """Build release notes from manifest entry"""
    parts = []
    if release_entry.get('notes'):
        parts.append(release_entry['notes'])
    if release_entry.get('subject'):
        parts.append(release_entry['subject'])
    if release_entry.get('body'):
        parts.append(release_entry['body'])

    # Add commit info
    commit = release_entry.get('commit', '')[:7]
    # The manifest has the old commit IDs (pre-rewrite)
    # We need to note both old and new
    parts.append(f"Built from commit {commit} (originally {commit} before the build files were taken out of history)")

    return "\n\n".join(parts)

def upload_release(tag: str, release_entry: Dict, dry_run: bool = False) -> bool:
    """Upload a single release to GitHub"""
    title = VERSION_TITLES.get(tag, tag)
    prerelease = release_entry.get('prerelease', False)
    notes = get_release_notes(release_entry)

    # Collect asset files
    asset_files = []
    for asset in release_entry.get('assets', []):
        filepath = Path(asset['file'])
        if filepath.exists():
            asset_files.append(str(filepath))
        else:
            print(f"  WARNING: Asset not found: {filepath}")

    if not asset_files:
        print(f"  No assets to upload for {tag}")
        return False

    print(f"\n{'='*60}")
    print(f"Uploading {tag} - {title}")
    print(f"  Prerelease: {prerelease}")
    print(f"  Assets: {len(asset_files)}")
    for af in asset_files:
        size_mb = Path(af).stat().st_size / (1024*1024)
        print(f"    - {Path(af).name} ({size_mb:.1f} MB)")

    if dry_run:
        print("  [DRY RUN] Skipping actual upload")
        return True

    # Build gh release create command
    cmd = [
        "gh", "release", "create", tag,
        "--repo", REPO,
        "--title", title,
        "--notes", notes
    ]
    if prerelease:
        cmd.append("--prerelease")
    cmd.extend(asset_files)

    print(f"  Running: {' '.join(cmd[:6])} ... ({len(asset_files)} files)")

    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
        if result.returncode == 0:
            print(f"  SUCCESS: {tag} uploaded")
            return True
        else:
            print(f"  FAILED: {result.stderr}")
            return False
    except subprocess.TimeoutExpired:
        print(f"  TIMEOUT: Upload took too long")
        return False
    except Exception as e:
        print(f"  ERROR: {e}")
        return False

def main():
    import argparse
    parser = argparse.ArgumentParser(description="Upload DropWheel releases to GitHub")
    parser.add_argument("--dry-run", action="store_true", help="Show what would be uploaded without uploading")
    parser.add_argument("--tag", help="Upload only a specific tag")
    parser.add_argument("--verify-only", action="store_true", help="Only verify SHA-1 hashes")
    args = parser.parse_args()

    print("Loading manifest...")
    manifest = load_manifest()
    print(f"Loaded {len(manifest)} release entries")

    # Determine which tags to process
    tags_to_process = []
    for entry in manifest:
        tag = entry.get('tag')
        if not tag:
            continue
        if tag in UPLOADED_TAGS:
            print(f"Skipping {tag} - already uploaded")
            continue
        if args.tag and tag != args.tag:
            continue
        tags_to_process.append(tag)

    print(f"\nReleases to process: {len(tags_to_process)}")
    for tag in tags_to_process:
        print(f"  - {tag}")

    if args.verify_only:
        print("\nVerifying SHA-1 hashes...")
        all_ok = True
        for tag in tags_to_process:
            entry = find_release_in_manifest(tag, manifest)
            if not entry:
                print(f"  {tag}: NOT IN MANIFEST")
                all_ok = False
                continue
            ok, errors = verify_assets(entry)
            if ok:
                print(f"  {tag}: OK")
            else:
                print(f"  {tag}: FAILED")
                for err in errors:
                    print(f"    - {err}")
                all_ok = False
        if all_ok:
            print("\nAll verifications passed!")
        else:
            print("\nSome verifications failed!")
            sys.exit(1)
        return

    if args.dry_run:
        print("\nDRY RUN - showing what would be uploaded:")
        for tag in tags_to_process:
            entry = find_release_in_manifest(tag, manifest)
            if entry:
                ok, errors = verify_assets(entry)
                status = "OK" if ok else "VERIFY FAILED"
                print(f"  {tag}: {status}")
                for asset in entry.get('assets', []):
                    print(f"    - {Path(asset['file']).name}")
        return

    # Actual upload
    print("\nStarting uploads...")
    success_count = 0
    for tag in tags_to_process:
        entry = find_release_in_manifest(tag, manifest)
        if not entry:
            print(f"  {tag}: NOT IN MANIFEST - skipping")
            continue

        ok, errors = verify_assets(entry)
        if not ok:
            print(f"  {tag}: Verification failed - skipping")
            for err in errors:
                print(f"    - {err}")
            continue

        if upload_release(tag, entry):
            success_count += 1
        else:
            print(f"  {tag}: Upload failed")

    print(f"\n{'='*60}")
    print(f"Done. {success_count}/{len(tags_to_process)} releases uploaded successfully.")

if __name__ == "__main__":
    main()