#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Build Starter with optional demo data and a temporary latest-client installer."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "docs/baseline-evidence"
MARKER = ".q2jump-starter-data/owner"
DATA_MARKER = b"Q2JUMP_STARTER_DATA_V1\r\n"
STARTER_PAYLOAD = (
    MARKER, ".q2jump-starter-data/INSTALL.txt",
    ".q2jump-starter-data/DATA-MANIFEST.json",
    ".q2jump-starter-data/licenses/ORIGINAL-DATA-NOTICES.txt",
)
STARTER_DATA_SOURCE_FILES = (
    "scripts/build.py", "scripts/verify_package.py", "tests/test_build.py",
    "packaging/Q2JUMP-Starter.iss", "packaging/starter-inputs.json",
    "packaging/INSTALL.txt", "packaging/SOURCE.txt",
    "docs/baseline-evidence/baseline-lock.json",
    "docs/baseline-evidence/baseline-archives.json",
)
HELPER_SOURCE_FILES = (
    "build.ps1",
    "Starter.Client.cs", "Starter.Configuration.cs", "Starter.Content.cs",
    "Starter.Downloads.cs", "Starter.GameData.cs", "Starter.GitHub.cs",
    "Starter.Installation.cs", "Starter.Managed.cs",
    "Starter.ManagedConfiguration.cs", "Starter.ManagedState.cs",
    "Starter.Processes.cs", "Starter.Publication.cs", "Starter.Releases.cs",
    "Starter.Status.cs",
    "tests/InstallationTests.cs", "tests/GameDataTests.cs",
    "tests/ManagedConfigurationTests.cs", "tests/GitHubReleaseTests.cs",
)

STARTER_SOURCE_FILES = (
    STARTER_DATA_SOURCE_FILES
    + ("packaging/Q2JUMP-compact-dark.ico",)
    + tuple("client-helper/" + name for name in HELPER_SOURCE_FILES)
    + ("COPYING.txt", "packaging/licenses/engine-LICENSE.txt")
)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def verify_file(path, expected, size=None):
    if size is not None and path.stat().st_size != size:
        raise ValueError(f"Unexpected size: {path}")
    with path.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != expected.lower():
        raise ValueError(f"SHA256 mismatch: {path}")


def safe_relative(name):
    if not name or "\\" in name or ":" in name or name.startswith("/"):
        raise ValueError(f"Unsafe relative path: {name!r}")
    parts = name.split("/")
    for part in parts:
        if (part in ("", ".", "..") or part.rstrip(" .") != part or
                any(ord(c) < 32 or c in '<>"|?*' for c in part) or
                re.fullmatch(r"CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]", part.split(".")[0], re.I)):
            raise ValueError(f"Unsafe relative path: {name!r}")
    return PurePosixPath(name)


def archive_entries(archive):
    found = {}
    folded = set()
    for entry in archive.infolist():
        name = entry.filename.rstrip("/")
        safe_relative(name)
        if name.casefold() in folded:
            raise ValueError(f"Duplicate archive member: {name}")
        folded.add(name.casefold())
        kind = (entry.external_attr >> 16) & 0o170000
        if kind not in (0, 0o040000, 0o100000):
            raise ValueError(f"Archive link or special file: {name}")
        if not entry.is_dir():
            found[name] = entry
    return found


def load_baseline():
    files = json.loads((EVIDENCE / "baseline-lock.json").read_text(encoding="utf-8-sig"))
    archives = json.loads((EVIDENCE / "baseline-archives.json").read_text(encoding="utf-8-sig"))
    identities = {item["id"]: item for item in archives}
    if set(identities) != {"demo", "update"} or len(archives) != 2:
        raise ValueError("Expected exactly the demo and update archives")
    for spec in archives:
        safe_relative(spec["filename"])
        if not spec["url"].startswith("https://") or not re.fullmatch("[a-f0-9]{64}", spec["sha256"]):
            raise ValueError("Baseline archives require HTTPS and a pinned SHA256")
    destinations = set()
    for item in files:
        target = safe_relative(item["path"])
        safe_relative(item["source"])
        if item["archive"] not in identities or item["path"].casefold() in destinations:
            raise ValueError("Unknown archive or duplicate baseline destination")
        destinations.add(item["path"].casefold())
        data = item["path"] in {"baseq2/pak0.pak", "baseq2/pak1.pak", "baseq2/pak2.pak"}
        player = item["path"].startswith("baseq2/players/") and target.suffix in {".pcx", ".md2", ".wav"}
        notice = item["path"].startswith("licenses/original/") and target.suffix == ".txt"
        if not (data or player or notice):
            raise ValueError(f"Unapproved baseline payload: {target}")
        if item["size"] <= 0 or not re.fullmatch("[a-f0-9]{64}", item["sha256"]):
            raise ValueError(f"Invalid baseline identity: {target}")
    required = {"baseq2/pak0.pak", "baseq2/pak1.pak", "baseq2/pak2.pak"}
    if not required.issubset(destinations) or not any(p.startswith("licenses/original/") for p in destinations):
        raise ValueError("Baseline must include all three original PAKs and their notices")
    return files, archives


def verify_baseline(files, archives):
    for spec in archives:
        path = ROOT / "downloads/baseline" / spec["filename"]
        verify_file(path, spec["sha256"], spec["size"])
        with zipfile.ZipFile(path) as archive:
            entries = archive_entries(archive)
            for item in files:
                if item["archive"] != spec["id"]:
                    continue
                entry = entries[item["source"]]
                if entry.file_size != item["size"] or sha256(archive.read(entry)) != item["sha256"]:
                    raise ValueError(f"Baseline selection mismatch: {item['path']}")


def require_x64(data):
    if len(data) < 64 or data[:2] != b"MZ":
        raise ValueError("Expected Windows PE image")
    offset = struct.unpack_from("<I", data, 60)[0]
    if offset + 6 > len(data) or data[offset:offset + 4] != b"PE\0\0" or struct.unpack_from("<H", data, offset + 4)[0] != 0x8664:
        raise ValueError("Expected Windows x64 PE image")


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def put(payload, relative, data):
    safe_relative(relative)
    destination = payload / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("xb") as stream:
        stream.write(data)


def win(path):
    return str(path).replace("/", "\\")


def pascal(text):
    return "'" + str(text).replace("'", "''") + "'"


def generate_baseline(output, files, archives):
    rows = []
    checks = ["procedure CheckBaselineTargets(Root: String);", "begin"]
    matches = ["function BaselineMatches(Root: String): Boolean;", "begin", "  Result := False;"]
    staged = ["procedure VerifyStagedBaseline;", "begin"]
    for item in files:
        target = PurePosixPath(item["path"])
        source = "{tmp}\\baseline-" + item["archive"] + "\\" + win(item["source"])
        rows.append(f'Source: "{source}"; DestDir: "{{app}}\\{win(target.parent)}"; DestName: "{target.name}"; ExternalSize: {item["size"]}; Flags: external onlyifdoesntexist uninsneveruninstall; Check: NeedBaselineFile({pascal(win(item["path"]))}, {pascal(item["sha256"])})')
        path_expr = "Root + " + pascal("\\" + win(target))
        checks.append(f"  CheckMissingOrMatchingFile({path_expr}, {pascal(item['sha256'])});")
        matches.append(f"  if not MatchesFile({path_expr}, {pascal(item['sha256'])}) then exit;")
        staged.append(f"  RequireFile(ExpandConstant({pascal(source)}), {pascal(item['sha256'])});")
    checks += ["end;"]
    matches += ["  Result := True;", "end;"]
    staged += ["end;"]
    downloads = ["procedure AddBaselineDownloads;", "begin"]
    extraction = ["procedure AddBaselineExtractions;", "begin"]
    for spec in archives:
        filename = spec["id"] + ".zip"
        downloads.append(f"  DownloadPage.Add({pascal(spec['url'])}, {pascal(filename)}, {pascal(spec['sha256'])});")
        extraction.append(f"  ExtractionPage.Add(ExpandConstant('{{tmp}}\\{filename}'), ExpandConstant('{{tmp}}\\baseline-{spec['id']}'), True);")
    downloads.append("end;")
    extraction.append("end;")
    write(output / "baseline-files.iss", "\n".join(rows) + "\n")
    write(output / "baseline-code.iss", "\n\n".join("\n".join(block) for block in (checks, matches, staged, downloads, extraction)) + "\n")


def generate_packaged(output, payload):
    paths = sorted(p for p in payload.rglob("*") if p.is_file())
    if {p.relative_to(payload).as_posix() for p in paths} != set(STARTER_PAYLOAD):
        raise ValueError("Starter persists only its four baseline metadata files")
    paths.sort(key=lambda p: p.relative_to(payload).as_posix() != MARKER)
    rows = []
    checks = ["procedure CheckPackagedTargets(Root: String);", "begin"]
    verify = ["procedure VerifyInstalledPackaged(Root: String);", "begin"]
    records = []
    for path in paths:
        relative = path.relative_to(payload)
        name = relative.as_posix()
        digest = sha256(path.read_bytes())
        destination = "{app}\\" + win(relative.parent)
        rows.append(f'Source: "{{#BuildDir}}\\payload\\{win(relative)}"; DestDir: "{destination}"; DestName: "{relative.name}"; '
                    f'Flags: onlyifdoesntexist uninsneveruninstall; Check: NeedPackagedFile({pascal(win(name))}, {pascal(digest)})')
        expr = "Root + " + pascal("\\" + win(relative))
        checks.append(f"  CheckTargetFile({expr});")
        verify.append(f"  if not FileExists({expr}) then RaiseException('Starter metadata is missing.');")
        records.append({"path": name, "size": path.stat().st_size, "sha256": digest})
    checks.append("end;")
    verify.append("end;")
    write(output / "packaged-files.iss", "\n".join(rows) + "\n")
    write(output / "packaged-code.iss", "\n".join(checks + [""] + verify) + "\n")
    return records


def prepare(output):
    inputs = json.loads((ROOT / "packaging/starter-inputs.json").read_text(encoding="utf-8"))
    if (set(inputs) != {"product", "version", "compiler"} or inputs["product"] != "Q2JUMP Starter"
            or not isinstance(inputs["version"], str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", inputs["version"])):
        raise ValueError("Expected Starter build inputs, without bundled client or runtime payloads")
    files, archives = load_baseline()
    verify_baseline(files, archives)
    if output.exists():
        raise ValueError("Build output already exists; choose a new directory. Existing artifacts are preserved.")
    output.mkdir(parents=True)
    payload = output / "payload"
    put(payload, MARKER, DATA_MARKER)
    put(payload, ".q2jump-starter-data/INSTALL.txt", (ROOT / "packaging/INSTALL.txt").read_bytes())
    original_notices = []
    for spec in archives:
        with zipfile.ZipFile(ROOT / "downloads/baseline" / spec["filename"]) as archive:
            for item in files:
                if item["archive"] == spec["id"] and item["path"].startswith("licenses/original/"):
                    original_notices.append(item["path"] + "\n\n" + archive.read(item["source"]).decode("cp1252"))
    put(payload, ".q2jump-starter-data/licenses/ORIGINAL-DATA-NOTICES.txt", "\n\n".join(original_notices).encode("utf-8-sig"))
    metadata = {"schema": 1, "kind": "starter-data", "product": inputs["product"], "version": inputs["version"],
                "baselineDownloadEnabled": True, "permissionStatus": "unresolved", "archives": archives,
                "baselineFiles": files, "validation": "Local candidate only; native acceptance and baseline permission evidence remain unresolved."}
    put(payload, ".q2jump-starter-data/DATA-MANIFEST.json", (json.dumps(metadata, indent=2) + "\n").encode())
    generate_baseline(output, files, archives)
    records = generate_packaged(output, payload)
    write(output / "release.iss", '#define ReleaseVersion "' + inputs["version"] + '"\n#define BaselineDownloadEnabled 1\n')
    write(output / "packaged-files.json", json.dumps(records, indent=2) + "\n")
    source = output / "source-snapshot"
    source_records = []
    for relative in STARTER_SOURCE_FILES:
        data = (ROOT / relative).read_bytes()
        put(source, relative, data)
        source_records.append({"path": relative, "size": len(data), "sha256": sha256(data)})
    source_archive = output / "dist" / ("Q2JUMP-Starter-" + inputs["version"] + "-source.zip")
    source_archive.parent.mkdir()
    with zipfile.ZipFile(source_archive, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for item in source_records:
            archive.write(source / item["path"], item["path"])
    write(output / "starter-build-record.json", json.dumps({
        "product": inputs["product"], "version": inputs["version"], "baselineDownloadEnabled": True,
        "baselineFiles": len(files), "bundledFiles": len(records), "sourceFiles": source_records,
        "sourceArchive": {"path": source_archive.name, "size": source_archive.stat().st_size,
                          "sha256": sha256(source_archive.read_bytes())},
        "status": "local-enabled-candidate", "installerExecuted": False,
    }, indent=2) + "\n")
    print(f"Verified {len(records)} metadata files and {len(files)} demo/update baseline files; local data downloads and installation are enabled; permission evidence remains unresolved.")
    return inputs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New build directory; existing output is never overwritten")
    parser.add_argument("--prepare-only", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / "build"):
        parser.error("Output must be a new directory under this workspace's build/")
    inputs = prepare(output)
    if not args.prepare_only:
        helper_output = output / "compiled-client"
        helper_build = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            str(output / "source-snapshot/client-helper/build.ps1"), "-Output", str(helper_output)], capture_output=True, text=True)
        write(output / "client-build.log", helper_build.stdout + helper_build.stderr)
        if helper_build.returncode:
            raise SystemExit(helper_build.returncode)
        helper = helper_output / "Q2JUMP-Starter-Client.exe"
        require_x64(helper.read_bytes())
        helper_record = {"path": "compiled-client/" + helper.name, "size": helper.stat().st_size, "sha256": sha256(helper.read_bytes())}
        record_path = output / "starter-build-record.json"
        record = json.loads(record_path.read_text()); record["clientInstaller"] = helper_record
        write(record_path, json.dumps(record, indent=2) + "\n")
        write(output / "client-helper.iss", '#define ClientHelperSha256 "' + helper_record["sha256"] + '"\n')
        compiler = ROOT / inputs["compiler"]["file"]
        verify_file(compiler, inputs["compiler"]["sha256"])
        result = subprocess.run([str(compiler), "/DBuildDir=" + str(output), str(output / "source-snapshot/packaging/Q2JUMP-Starter.iss")], capture_output=True, text=True)
        write(output / "compile.log", result.stdout + result.stderr)
        print("\n".join(result.stdout.splitlines()[-10:]))
        if result.returncode:
            raise SystemExit(result.returncode)
        for artifact in (output / "dist").glob("*.exe"):
            digest = sha256(artifact.read_bytes())
            write(artifact.with_suffix(".exe.sha256"), f"{digest}  {artifact.name}\n")
            print(f"SETUP {artifact}\nSHA256 {digest}")


if __name__ == "__main__":
    main()
