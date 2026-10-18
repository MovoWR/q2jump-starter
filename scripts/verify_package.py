#!/usr/bin/env python3
"""Compare statically unpacked Inno payload files to the build manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def payload_paths(unpacked):
    """Resolve innounp's literal directory names without executing setup code."""
    found = {}
    for path in unpacked.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(unpacked).as_posix()
        if relative in {"install_script.iss", "{tmp}/Q2JUMP-Starter-Client.exe"}:
            continue
        name = relative.removeprefix("{app}/")
        coded = re.fullmatch(r"\{code_StarterDestination_([^{}]+)\}/([^/]+)", relative)
        if coded:
            name = coded[1] + "/" + coded[2]
        if name in found:
            raise ValueError("Duplicate extracted payload destination: " + name)
        found[name] = path
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-dir", required=True, type=Path)
    parser.add_argument("--unpacked-dir", required=True, type=Path)
    args = parser.parse_args()
    payload = payload_paths(args.unpacked_dir)
    expected = json.loads((args.build_dir / "packaged-files.json").read_text())
    mismatches = []
    for item in expected:
        path = payload.get(item["path"])
        if path is None or hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]:
            mismatches.append(item["path"])
    extras = sorted(set(payload) - {i["path"] for i in expected})
    paks = sorted(name for name in payload if Path(name).suffix.lower() == ".pak")
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
