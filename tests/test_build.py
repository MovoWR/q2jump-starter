"""Offline contract tests for packaging; never run a setup or game process."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import re
import stat
import struct
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("starter_build", ROOT / "scripts/build.py")
build = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build)


class BuildSafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="q2jump-build-tests-")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def test_verify_file_rejects_corruption_and_wrong_size(self):
        target = self.directory / "payload.bin"
        target.write_bytes(b"verified payload")
        expected = build.sha256(target.read_bytes())
        build.verify_file(target, expected, 16)
        with self.assertRaisesRegex(ValueError, "size"):
            build.verify_file(target, expected, 17)
        target.write_bytes(b"modified payload")
        with self.assertRaisesRegex(ValueError, "SHA256"):
            build.verify_file(target, expected, 16)

    def test_windows_unsafe_paths_are_rejected(self):
        names = ("", "/absolute", "../escape", "a/../b", "a/./b", "a//b",
                 "C:/escape", "a\\b", "a:stream", "file.", "dir /file",
                 "NUL", "con.txt", "dir/LPT9.bin", "COM1", "wild*card",
                 'bad"quote', "line\nbreak")
        for name in names:
            with self.subTest(name=name), self.assertRaises(ValueError):
                build.safe_relative(name)
        self.assertEqual(str(build.safe_relative("jump/pics/hud-icon.png")),
                         "jump/pics/hud-icon.png")

    def read_zip(self, members):
        buffer = io.BytesIO()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(buffer, "w") as archive:
                for name, data in members:
                    archive.writestr(name, data)
        buffer.seek(0)
        with zipfile.ZipFile(buffer) as archive:
            return build.archive_entries(archive)

    def test_zip_duplicate_and_case_collision_are_rejected(self):
        for other in ("jump/file.txt", "JUMP/FILE.TXT"):
            with self.subTest(other=other), self.assertRaisesRegex(ValueError, "Duplicate"):
                self.read_zip([("jump/file.txt", b"one"), (other, b"two")])

    def test_zip_traversal_and_links_are_rejected(self):
        for name in ("../outside.txt", "jump/../../outside.txt", "C:/outside.txt"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.read_zip([(name, b"unsafe")])
        link = zipfile.ZipInfo("jump/link")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        with self.assertRaisesRegex(ValueError, "link or special"):
            self.read_zip([(link, b"../../outside")])

    def test_zip_directories_are_not_payload_files(self):
        entries = self.read_zip([("jump/", b""), ("jump/pics/", b""),
                                 ("jump/pics/hud.png", b"image")])
        self.assertEqual(set(entries), {"jump/pics/hud.png"})

    def test_payload_put_refuses_overwrite_and_escape(self):
        build.put(self.directory, "jump/config.cfg", b"first")
        with self.assertRaises(FileExistsError):
            build.put(self.directory, "jump/config.cfg", b"second")
        with self.assertRaises(ValueError):
            build.put(self.directory, "../outside", b"bad")
        self.assertEqual((self.directory / "jump/config.cfg").read_bytes(), b"first")

    def test_pe_architecture_gate_rejects_x86_and_non_pe(self):
        pe = bytearray(128)
        pe[:2] = b"MZ"
        struct.pack_into("<I", pe, 60, 64)
        pe[64:68] = b"PE\0\0"
        struct.pack_into("<H", pe, 68, 0x8664)
        build.require_x64(pe)
        struct.pack_into("<H", pe, 68, 0x014c)
        with self.assertRaises(ValueError):
            build.require_x64(pe)
        with self.assertRaises(ValueError):
            build.require_x64(b"not executable")

    def test_packaged_metadata_is_retained_and_never_overwrites(self):
        payload = self.directory / "payload"
        for name in build.STARTER_PAYLOAD:
            build.put(payload, name, build.DATA_MARKER if name == build.MARKER
                      else ("metadata: " + name).encode())
        output = self.directory / "generated"
        records = build.generate_packaged(output, payload)
        rows = (output / "packaged-files.iss").read_text().splitlines()
        self.assertEqual(len(rows), len(build.STARTER_PAYLOAD))
        self.assertIn(build.MARKER.replace("/", "\\"), rows[0])
        code = (output / "packaged-code.iss").read_text()
        for row, item in zip(rows, records):
            self.assertIn("onlyifdoesntexist", row)
            self.assertIn("uninsneveruninstall", row)
            self.assertIn("Check: NeedPackagedFile(", row)
            self.assertIn(item["sha256"], row)
            self.assertIn(item["path"].replace("/", "\\"), code)
        self.assertIn("CheckTargetFile(", code)
        self.assertNotIn("CheckMissingOrMatchingFile(", code)
        self.assertEqual({r["path"] for r in records}, set(build.STARTER_PAYLOAD))

    def test_packaged_payload_rejects_clients_configs_and_other_files(self):
        for index, name in enumerate(("q2pro_race.exe", "Q2JUMP-Launcher.exe",
                                      "vcruntime140.dll", "jump/default.cfg",
                                      "user/jump/q2config.cfg", "baseq2/pak0.pak",
                                      ".q2jump-starter-data/extra.txt")):
            payload = self.directory / ("payload-" + str(index))
            for allowed in build.STARTER_PAYLOAD:
                build.put(payload, allowed, b"metadata")
            build.put(payload, name, b"unapproved file")
            with self.subTest(name=name), self.assertRaises(ValueError):
                build.generate_packaged(self.directory / ("generated-" + str(index)), payload)

    def test_starter_sources_use_one_compact_dark_icon_and_no_launcher_tree(self):
        self.assertTrue(all(not path.startswith("launcher/") for path in build.STARTER_SOURCE_FILES))
        self.assertIn("client-helper/build.ps1", build.STARTER_SOURCE_FILES)
        icons = [path for path in build.STARTER_SOURCE_FILES if path.lower().endswith(".ico")]
        self.assertEqual(icons, ["packaging/Q2JUMP-compact-dark.ico"])
        self.assertTrue((ROOT / icons[0]).read_bytes().startswith(b"\x00\x00\x01\x00"))


class DataOnlyPreparationTests(unittest.TestCase):
    """Exercise preparation using only tiny local demo and update ZIP fixtures."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="q2jump-data-build-tests-")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.root = self.directory / "workspace"
        self.root.mkdir()
        # Use the declared rebuild inventory, with no legacy engine inputs,
        # standalone application source, client archive or Visual C++ runtime available.
        for relative in build.STARTER_SOURCE_FILES:
            destination = self.root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes((ROOT / relative).read_bytes())
        self.evidence = self.root / "docs/baseline-evidence"
        self.evidence.mkdir(parents=True, exist_ok=True)
        self.files = []
        self.archives = []
        fixtures = {
            "demo": [("Install/Data/baseq2/pak0.pak", "baseq2/pak0.pak", b"demo pak"),
                     ("Install/License.txt", "licenses/original/demo-license.txt", b"Demo fixture notice")],
            "update": [("baseq2/pak1.pak", "baseq2/pak1.pak", b"update pak one"),
                       ("baseq2/pak2.pak", "baseq2/pak2.pak", b"update pak two"),
                       ("baseq2/players/male/grunt.pcx", "baseq2/players/male/grunt.pcx", b"player fixture"),
                       ("License.txt", "licenses/original/update-license.txt", b"Update fixture notice")],
        }
        downloads = self.root / "downloads/baseline"
        downloads.mkdir(parents=True)
        for identity, members in fixtures.items():
            archive = downloads / (identity + ".exe")
            with zipfile.ZipFile(archive, "w") as output:
                for source, destination, data in members:
                    output.writestr(source, data)
                    self.files.append({"archive": identity, "source": source,
                                       "path": destination, "size": len(data),
                                       "sha256": build.sha256(data)})
                # The upstream self-extracting packages also contain programs;
                # an explicit data manifest must not select them.
                output.writestr("quake2.exe", b"do not install")
                output.writestr("baseq2/gamex86.dll", b"do not install")
            self.archives.append({"id": identity, "filename": archive.name,
                                  "url": "https://example.invalid/" + archive.name,
                                  "size": archive.stat().st_size,
                                  "sha256": build.sha256(archive.read_bytes()),
                                  "format": "self-extracting-zip"})
        (self.evidence / "baseline-lock.json").write_text(json.dumps(self.files))
        (self.evidence / "baseline-archives.json").write_text(json.dumps(self.archives))
        self.output = self.root / "build/candidate"

    def prepare(self, output=None):
        with patch.object(build, "ROOT", self.root), patch.object(
                build, "EVIDENCE", self.evidence), patch.object(
                build.subprocess, "run", side_effect=AssertionError("Preparation must not execute a process")):
            return build.prepare(output or self.output)

    def test_prepare_requires_only_baseline_and_starter_sources(self):
        for forbidden in ("packaging/inputs.json", "packaging/runtime",
                          "packaging/source-archives.lock.json", "downloads/client"):
            self.assertFalse((self.root / forbidden).exists(), forbidden)
        self.prepare()
        payload = self.output / "payload"
        actual = {p.relative_to(payload).as_posix() for p in payload.rglob("*") if p.is_file()}
        self.assertEqual(actual, set(build.STARTER_PAYLOAD))
        self.assertEqual((payload / build.MARKER).read_bytes(), build.DATA_MARKER)
        self.assertFalse(any(Path(name).suffix.lower() in {".exe", ".dll", ".cfg", ".pak"}
                             for name in actual))
        rows = (self.output / "baseline-files.iss").read_text().splitlines()
        self.assertEqual(len(rows), len(self.files))
        self.assertFalse(any("quake2.exe" in row or "gamex86.dll" in row for row in rows))
        records = json.loads((self.output / "packaged-files.json").read_text())
        self.assertEqual({item["path"] for item in records}, actual)
        for item in records:
            build.verify_file(payload / item["path"], item["sha256"], item["size"])
        manifests = list(payload.rglob("DATA-MANIFEST.json"))
        self.assertEqual(len(manifests), 1)
        manifest = json.loads(manifests[0].read_text(encoding="utf-8-sig"))
        self.assertEqual(manifest["kind"], "starter-data")
        self.assertIs(manifest["baselineDownloadEnabled"], True)
        self.assertEqual(manifest["permissionStatus"], "unresolved")
        self.assertEqual(manifest["baselineFiles"], self.files)
        self.assertEqual({item["id"] for item in manifest["archives"]}, {"demo", "update"})
        for forbidden in ("client", "launcher", "runtime", "sourceArchives"):
            self.assertNotIn(forbidden, manifest)

    def test_prepare_rejects_corrupted_baseline_before_writing_payload(self):
        archive = self.root / "downloads/baseline" / self.archives[0]["filename"]
        archive.write_bytes(archive.read_bytes() + b"modified")
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertFalse((self.output / "payload").exists())

    def test_prepare_preserves_existing_output(self):
        self.output.mkdir(parents=True)
        retained = self.output / "retained.txt"
        retained.write_bytes(b"existing artifact")
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.prepare()
        self.assertEqual(retained.read_bytes(), b"existing artifact")
        self.assertFalse((self.output / "payload").exists())

    def test_original_notices_from_both_archives_are_retained(self):
        self.prepare()
        notice = self.output / "payload/.q2jump-starter-data/licenses/ORIGINAL-DATA-NOTICES.txt"
        text = notice.read_text(encoding="utf-8-sig")
        self.assertIn("Demo fixture notice", text)
        self.assertIn("Update fixture notice", text)
        self.assertIn("licenses/original/demo-license.txt", text)
        self.assertIn("licenses/original/update-license.txt", text)


    def test_inputs_cannot_reintroduce_a_bundled_client_or_runtime(self):
        path = self.root / "packaging/starter-inputs.json"
        inputs = json.loads(path.read_text())
        for field in ("client", "launcher", "runtime", "source"):
            changed = dict(inputs, **{field: {"file": "obsolete-input"}})
            path.write_text(json.dumps(changed))
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.prepare()
            self.assertFalse(self.output.exists())

    def test_source_archive_can_rebuild_the_exact_data_manifest(self):
        self.prepare()
        record = json.loads((self.output / "starter-build-record.json").read_text())
        self.assertIs(record["baselineDownloadEnabled"], True)
        self.assertIs(record["installerExecuted"], False)
        self.assertEqual(record["status"], "local-enabled-candidate")
        self.assertEqual(record["bundledFiles"], 4)
        self.assertEqual(record["baselineFiles"], len(self.files))
        self.assertEqual({item["path"] for item in record["sourceFiles"]},
                         set(build.STARTER_SOURCE_FILES))
        source = record["sourceArchive"]
        path = self.output / "dist" / source["path"]
        build.verify_file(path, source["sha256"], source["size"])
        exported = self.directory / "exported"
        with zipfile.ZipFile(path) as archive:
            self.assertEqual(set(archive.namelist()), set(build.STARTER_SOURCE_FILES))
            for item in record["sourceFiles"]:
                data = archive.read(item["path"])
                self.assertEqual(len(data), item["size"])
                self.assertEqual(build.sha256(data), item["sha256"])
                self.assertEqual(data, (self.root / item["path"]).read_bytes())
                build.put(exported, item["path"], data)
        # Original data is provided separately for local verification; it is
        # absent from the source ZIP and never rebundled into Setup.
        for archive in self.archives:
            relative = "downloads/baseline/" + archive["filename"]
            build.put(exported, relative, (self.root / relative).read_bytes())
        spec = importlib.util.spec_from_file_location("exported_starter_build", exported / "scripts/build.py")
        rebuilt = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(rebuilt)
        output = exported / "build/rebuilt"
        with patch.object(rebuilt.subprocess, "run", side_effect=AssertionError("No process may run")):
            rebuilt.prepare(output)
        for relative in build.STARTER_PAYLOAD:
            self.assertEqual((output / "payload" / relative).read_bytes(),
                             (self.output / "payload" / relative).read_bytes())
        for relative in ("release.iss", "baseline-files.iss", "baseline-code.iss",
                         "packaged-files.iss", "packaged-code.iss", "packaged-files.json"):
            self.assertEqual((output / relative).read_bytes(), (self.output / relative).read_bytes())
        self.assertIn("#define BaselineDownloadEnabled 1", (output / "release.iss").read_text())


class DataOnlyRecipeTests(unittest.TestCase):
    """Static recipe contracts; these do not establish native Setup behavior."""

    @classmethod
    def setUpClass(cls):
        cls.recipe = (ROOT / "packaging/Q2JUMP-Starter.iss").read_text()

    def routine(self, name):
        match = re.search(r"^(?:procedure|function) " + re.escape(name) + r"\b.*?(?=^(?:procedure|function) |\Z)",
                          self.recipe, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(match, name)
        return match.group()

    def test_enabled_recipe_checks_destinations_before_downloading_and_copying(self):
        self.assertRegex(self.recipe, r"#ifndef BaselineDownloadEnabled\s+#error")
        self.assertRegex(self.recipe, r"#if BaselineDownloadEnabled != 1\s+#error")
        self.assertNotIn("RequireBaselinePermission", self.recipe)
        prepare = self.routine("PrepareToInstall")
        steps = ("InstallBaseline := InstallingNewGame;", "CheckDestination;", "if InstallBaseline then begin",
                 "GetSpaceOnDisk64(", "AddBaselineDownloads;", "DownloadPage.Download;",
                 "AddBaselineExtractions;", "ExtractionPage.Extract;", "VerifyStagedBaseline;")
        positions = [prepare.index(step) for step in steps]
        self.assertEqual(positions, sorted(positions))
        self.assertGreater(prepare.rindex("CheckDestination;"), positions[-1])
        install = self.routine("CurStepChanged")
        preflight = install.index("CheckDestination;")
        self.assertLess(install.index("if CurStep = ssInstall then begin"), preflight)
        for step in ("ForceDirectories(", "SaveStringToFile(", "CopyFile("):
            self.assertLess(preflight, install.index(step), step)
        self.assertIn("CopyFile(TemporaryMarker, MarkerPath, True)", install)
        postinstall = install.index("if CurStep = ssPostInstall then begin")
        self.assertGreater(install.index("VerifyInstalledPackaged(Root);"), postinstall)
        self.assertGreater(install.index("HasGamePak(Root)"), postinstall)

    def test_setup_lets_player_select_folder_with_requested_default(self):
        setup = self.recipe.split("[Setup]", 1)[1].split("[Files]", 1)[0]
        values = dict(line.split("=", 1) for line in setup.splitlines()
                      if "=" in line and not line.lstrip().startswith(";"))
        self.assertEqual(values["DefaultDirName"], r"C:\Q2JUMP")
        self.assertEqual(values["AppendDefaultDirName"], "no")
        self.assertEqual(values["DirExistsWarning"], "no")
        self.assertEqual(values["DisableDirPage"], "no")
        self.assertEqual(values["UsePreviousAppDir"], "no")
        self.assertEqual(values["PrivilegesRequired"], "lowest")
        mode = self.routine("InitializeWizard")
        self.assertIn("CreateInputOptionPage(wpWelcome", mode)
        self.assertIn("Install a new game", mode)
        self.assertIn("Install or update Q2PRO Race in an existing Quake II folder", mode)
        destination = self.routine("CheckDestination")
        self.assertIn("if InstallingNewGame and not Owned then", destination)
        self.assertIn("if not InstallingNewGame and not HasGamePak(Root) then", destination)
        self.assertIn("Choose the Quake II root folder containing baseq2\\*.pak", destination)
        directory_next = self.routine("NextButtonClick")
        self.assertIn("CurPageID <> wpSelectDir", directory_next)
        self.assertIn("CheckDestination;", directory_next)
        self.assertIn("Result := False;", directory_next)

    def test_recipe_has_no_legacy_launcher_or_uninstall_actions(self):
        for section in ("Run", "Icons", "Dirs", "UninstallRun", "UninstallDelete", "InstallDelete"):
            self.assertNotIn("[" + section + "]", self.recipe)
        self.assertIn("Uninstallable=no", self.recipe)
        self.assertIn("SetupIconFile={#BuildDir}\\source-snapshot\\packaging\\Q2JUMP-compact-dark.ico", self.recipe)
        self.assertIn("UsePreviousAppDir=no", self.recipe)
        for hook in ("InitializeUninstall", "CurUninstallStepChanged"):
            self.assertNotIn(hook, self.recipe)
        for file in ("Q2JUMP-Launcher.exe", "vcruntime140.dll", "q2config.cfg", "default.cfg"):
            self.assertNotIn(file, self.recipe)

    def test_finish_actions_require_success_and_preserve_existing_shortcuts(self):
        wizard = self.routine("InitializeWizard")
        self.assertIn("DesktopShortcutCheck.Parent := WizardForm.FinishedPage", wizard)
        self.assertIn("LaunchGameCheck.Parent := WizardForm.FinishedPage", wizard)
        self.assertIn("DesktopShortcutCheck.Checked := True", wizard)
        self.assertIn("LaunchGameCheck.Checked := True", wizard)
        finish = self.routine("CurPageChanged")
        self.assertIn("DesktopShortcutCheck.Visible := VerificationError = ''", finish)
        self.assertIn("LaunchGameCheck.Visible := VerificationError = ''", finish)
        self.assertIn("GetFileAttributesW(DesktopShortcutPath)", finish)
        self.assertIn("DesktopShortcutCheck.Enabled := False", finish)
        shortcut = self.routine("CreateDesktopShortcut")
        self.assertLess(shortcut.index("GetFileAttributesW(ShortcutPath)"),
                        shortcut.index("CreateShellLink("))
        params = self.routine("GameLaunchParameters")
        self.assertIn('+set basedir "', params)
        self.assertIn("+set game jump +pushmenu main", params)
        next_button = self.routine("NextButtonClick")
        self.assertIn("if WizardSilent or (VerificationError <> '') then exit", next_button)
        self.assertIn("if DesktopShortcutCheck.Checked then begin", next_button)
        self.assertIn("not Exec(GamePath, GameLaunchParameters(Root)", next_button)
        self.assertIn("DesktopShortcutCheck.Caption := 'Desktop shortcut created'", next_button)

    def test_existing_data_must_match_and_is_never_replaced(self):
        missing = self.routine("CheckMissingOrMatchingFile")
        self.assertLess(missing.index("CheckTargetFile(Path);"), missing.index("FileExists(Path)"))
        self.assertIn("not MatchesFile(Path, Hash)", missing)
        self.assertIn("RaiseException(", missing)
        for name in ("NeedBaselineFile",):
            procedure = self.routine(name)
            self.assertLess(procedure.index("CheckMissingOrMatchingFile(Path, Hash);"),
                            procedure.rindex("Result :="))
            self.assertIn("not FileExists(Path)", procedure)
        target = self.routine("CheckTargetFile")
        self.assertIn("CheckTargetPath(Path);", target)
        self.assertIn("if DirExists(Path) then", target)
        self.assertIn("Attributes and $400", self.routine("CheckTargetPath"))
        self.assertIn("InstallationMarker + #13#10", self.routine("CurStepChanged"))
        self.assertEqual(build.DATA_MARKER, b"Q2JUMP_STARTER_DATA_V1\r\n")

    def test_client_handoff_follows_pak_presence_and_reports_failures(self):
        step = self.routine("CurStepChanged")
        self.assertLess(step.index("HasGamePak(Root)"), step.index("InstallLatestClient(Root)"))
        helper = self.routine("InstallLatestClient")
        self.assertLess(helper.index("RequireFile(HelperPath"), helper.index("ExecAndLogOutput("))
        self.assertIn("if ExitCode <> 0 then begin", helper)
        self.assertIn("if LastClientError <> '' then RaiseException(LastClientError)", helper)
        self.assertIn("ModeArg", helper)
        self.assertIn("VerificationError := GetExceptionMessage", step)
        self.assertIn("if VerificationError <> '' then Result := 1", self.routine("GetCustomSetupExitCode"))
        self.assertIn("q2jump-client.cancel", self.routine("CancelButtonClick"))
        presence = self.routine("HasGamePak")
        self.assertIn("\\baseq2", presence)
        self.assertNotIn("\\jump", presence)
        self.assertIn("Choose the Quake II root folder containing baseq2\\*.pak", self.routine("CheckDestination"))
        self.assertNotIn("GetSHA256OfFile", presence)
        self.assertNotIn("LoadStringFromFile", presence)

    def test_combined_starter_roots_are_rejected_before_data_ownership(self):
        reject = self.routine("RejectLegacyInstallation")
        self.assertIn("\\Q2JUMP.installation", reject)
        self.assertNotIn("'unins*'", reject)
        destination = self.routine("CheckDestination")
        self.assertLess(destination.index("RejectLegacyInstallation(Root);"),
                        destination.index("LoadStringFromFile("))
        self.assertIn("CheckPackagedTargets(Root);", destination)
        self.assertIn("if InstallingNewGame then CheckBaselineTargets(Root);", destination)
        prepare = self.routine("PrepareToInstall")
        self.assertGreater(prepare.rindex("CheckDestination;"), prepare.index("VerifyStagedBaseline;"))


class BaselineManifestTests(unittest.TestCase):
    def setUp(self):
        self.files = json.loads((build.EVIDENCE / "baseline-lock.json").read_text(encoding="utf-8-sig"))
        self.archives = json.loads((build.EVIDENCE / "baseline-archives.json").read_text(encoding="utf-8-sig"))
        self.temp = tempfile.TemporaryDirectory(prefix="q2jump-manifest-tests-")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def load(self, files=None, archives=None):
        (self.directory / "baseline-lock.json").write_text(json.dumps(self.files if files is None else files))
        (self.directory / "baseline-archives.json").write_text(json.dumps(self.archives if archives is None else archives))
        with patch.object(build, "EVIDENCE", self.directory):
            return build.load_baseline()

    def test_current_manifest_is_accepted(self):
        files, archives = self.load()
        self.assertEqual(files, self.files)
        self.assertEqual(len(archives), 2)

    def test_downloaded_baseline_archives_match_pinned_payload(self):
        files, archives = build.load_baseline()
        missing = [spec["filename"] for spec in archives
                   if not (build.ROOT / "downloads/baseline" / spec["filename"]).is_file()]
        if missing:
            self.skipTest("Pinned baseline archives are absent: " + ", ".join(missing))
        build.verify_baseline(files, archives)
    def test_empty_or_missing_required_pak_is_rejected(self):
        with self.assertRaises(ValueError):
            self.load(files=[])
        for required in ("baseq2/pak0.pak", "baseq2/pak1.pak", "baseq2/pak2.pak"):
            files = [item for item in self.files if item["path"] != required]
            with self.subTest(required=required), self.assertRaises(ValueError):
                self.load(files=files)

    def test_baseline_cannot_install_stock_executables_or_player_config(self):
        for target in ("quake2.exe", "baseq2/gamex86.dll", "user/jump/q2config.cfg", "jump/default.cfg"):
            files = copy.deepcopy(self.files)
            files.append(dict(files[0], path=target))
            with self.subTest(target=target), self.assertRaises(ValueError):
                self.load(files=files)

    def test_duplicate_destination_and_unknown_archive_are_rejected(self):
        files = copy.deepcopy(self.files)
        files.append(dict(files[0]))
        with self.assertRaises(ValueError):
            self.load(files=files)
        files = copy.deepcopy(self.files)
        files[0]["archive"] = "untrusted"
        with self.assertRaises(ValueError):
            self.load(files=files)

    def test_unpinned_or_insecure_archive_is_rejected(self):
        for change in ({"url": "http://example.invalid/demo.exe"}, {"sha256": "not-a-hash"}):
            archives = copy.deepcopy(self.archives)
            archives[0].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.load(archives=archives)

    def test_generated_baseline_is_an_explicit_retained_whitelist(self):
        output = self.directory / "generated"
        build.generate_baseline(output, self.files, self.archives)
        rows = (output / "baseline-files.iss").read_text().splitlines()
        self.assertEqual(len(rows), len(self.files))
        for row in rows:
            self.assertNotIn("*", row)
            self.assertIn("onlyifdoesntexist", row)
            self.assertIn("uninsneveruninstall", row)
            self.assertIn("Check: NeedBaselineFile(", row)
        code = (output / "baseline-code.iss").read_text()
        self.assertIn("CheckMissingOrMatchingFile(", code)
        for item, row in zip(self.files, rows):
            self.assertIn(item["sha256"], row)
            self.assertIn(item["sha256"], code)
        for spec in self.archives:
            self.assertIn(spec["sha256"], code)


if __name__ == "__main__":
    unittest.main()
