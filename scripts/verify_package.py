#!/usr/bin/env python3
"""Compare statically unpacked Inno payload files to the build manifest."""
import argparse
import hashlib
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-dir", required=True, type=Path)
    parser.add_argument("--unpacked-dir", required=True, type=Path)
    args = parser.parse_args()
    payload = args.unpacked_dir / "{app}"
    expected = json.loads((args.build_dir / "packaged-files.json").read_text())
    mismatches = []
    for item in expected:
        path = payload / item["path"]
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]:
            mismatches.append(item["path"])
    extras = sorted({p.relative_to(payload).as_posix() for p in payload.rglob("*") if p.is_file()} - {i["path"] for i in expected})
    paks = [str(p.relative_to(payload)) for p in payload.rglob("*.pak")]
    report = {"scope": "Static compiled-package contents; installer and game not executed", "expectedFiles": len(expected), "mismatches": mismatches, "unexpectedFiles": extras, "bundledOriginalPaks": paks}
    record = json.loads((args.build_dir / "starter-build-record.json").read_text())
    helper = record.get("clientInstaller")
    if helper:
        path = args.unpacked_dir / "{tmp}/Q2JUMP-Starter-Client.exe"
        if not path.is_file() or path.stat().st_size != helper["size"] or hashlib.sha256(path.read_bytes()).hexdigest() != helper["sha256"]:
            mismatches.append("temporary client helper")
        report["temporaryClientHelperMatched"] = "temporary client helper" not in mismatches
    text = json.dumps(report, indent=2) + "\n"
    (args.build_dir / "package-verification.json").write_text(text)
    print(text)
    if mismatches or extras or paks:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
