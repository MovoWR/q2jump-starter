// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace Q2JumpStarter
{
    // Synthetic assets only; the client, original assets and network are never used.
    internal static class GameDataTests
    {
        private static int assertions;
        private static string workspace;
        private static readonly GameDataContract Contract = new GameDataContract(new string('a', 40), true, true, true, true);

        public static int Main()
        {
            workspace = Path.Combine(Path.GetTempPath(), "q2jump-game-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            try {
                TestLooseAndMissing();
                TestPackPrecedence();
                TestArchiveDotSegments();
                TestFormatsAndCorruptWinner();
                TestZip();
                TestMalformed();
                TestConfiguration();
                TestCancellationAndLimits();
                TestRequiredSubsetAndConfigOrder();
                TestStagedResourceOverlay();
                TestStagedResourceExclusions();
                TestStagedResourceChangesAndLinks();
                Console.WriteLine("Game data fixtures passed: " + assertions + " assertions.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally {
                string path = Path.GetFullPath(workspace);
                if (Path.GetDirectoryName(path) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    && Path.GetFileName(path).StartsWith("q2jump-game-data-", StringComparison.Ordinal)) Directory.Delete(path, true);
            }
        }

        private static void TestLooseAndMissing()
        {
            string root = Root("loose");
            Dictionary<string, byte[]> files = Assets();
            Loose(root, "baseq2", files);
            GameDataReport report = Inspect(root);
            Assert(report.Verified, "Complete loose baseline verifies.");
            Assert(!report.BaselineDownloadEnabled, "Baseline downloading stays disabled.");
            Assert(report.SourceRevision == Contract.SourceRevision, "Source contract identity is retained.");
            Assert(Asset(report, "pics/colormap.pcx").Source == "baseq2/pics/colormap.pcx", "Loose palette origin is recorded.");
            File.Delete(Path.Combine(root, "baseq2/pics/colormap.pcx"));
            report = Inspect(root);
            Assert(!report.Verified && Asset(report, "pics/colormap.pcx").State == GameDataState.Missing, "A missing mandatory palette blocks verification.");
            root = Root("empty");
            Assert(!Inspect(root).Verified, "Empty directory is not valid game data.");
        }

        private static void TestPackPrecedence()
        {
            string root = Root("order");
            Dictionary<string, byte[]> files = Assets();
            Loose(root, "baseq2", files);
            foreach (string name in new[] { "pak0.pak", "pak2.pak", "pak17.pak", "abc.pak", "z-custom.pak" })
                Pak(Path.Combine(root, "baseq2", name), files);
            // Independent expected order from the engine load/prepend rule, not inspector output.
            foreach (string winner in new[] { "z-custom.pak", "abc.pak", "pak17.pak", "pak2.pak", "pak0.pak" }) {
                GameDataReport report = Inspect(root);
                Assert(report.Verified, "Named PAK layout verifies: " + winner);
                Assert(Asset(report, "pics/colormap.pcx").Source == "baseq2/" + winner + ":pics/colormap.pcx", "Pack precedence: " + winner);
                File.Delete(Path.Combine(root, "baseq2", winner));
            }
            Pak(Path.Combine(root, "baseq2", "z.pak"), files);
            Loose(root, "jump", new Dictionary<string, byte[]> { { "pics/colormap.pcx", Pcx() } });
            Assert(Asset(Inspect(root), "pics/colormap.pcx").Source == "jump/pics/colormap.pcx", "Jump loose beats baseq2 archives.");
            Pak(Path.Combine(root, "jump", "any-name.pak"), new Dictionary<string, byte[]> { { "pics/colormap.pcx", Pcx() } });
            Assert(Asset(Inspect(root), "pics/colormap.pcx").Source == "jump/any-name.pak:pics/colormap.pcx", "Jump packs beat Jump loose.");
            root = Root("numeric");
            Pak(Path.Combine(root, "baseq2", "pak9.pak"), files);
            Pak(Path.Combine(root, "baseq2", "pak10.pak"), files);
            Assert(Asset(Inspect(root), "pics/colormap.pcx").Source.Contains("pak10.pak:"), "Numeric ordering is not lexicographic.");
        }

        private static void TestArchiveDotSegments()
        {
            foreach (bool zip in new[] { false, true }) {
                string root = Root(zip ? "zip-dot-paths" : "pak-dot-paths");
                var files = Assets();
                files["models/monsters/tank/../ctank/skin.pcx"] = Pcx();
                files["models/monsters/tank/../ctank/pain.pcx"] = Pcx();
                byte[] palette = files["pics/colormap.pcx"]; files.Remove("pics/colormap.pcx");
                files["pics/temp/.././colormap.pcx"] = palette;
                string path = Path.Combine(root, zip ? "baseq2/data.pkz" : "baseq2/pak0.pak");
                if (zip) Zip(path, files, true); else Pak(path, files);
                var report = Inspect(root);
                Assert(report.Verified, "Original-data dot segments are virtual archive paths.");
                Assert(Asset(report, "pics/colormap.pcx").State == GameDataState.Verified, "Normalized asset is found and decoded.");
                Assert(!Directory.Exists(Path.Combine(root, "models")), "Archive inspection never extracts entries.");
                files["pics/colormap.pcx"] = palette;
                if (zip) Zip(path, files, true); else Pak(path, files);
                Assert(Unverified(Inspect(root)), "Normalized duplicate paths remain ambiguous.");
                files.Remove("pics/colormap.pcx"); files["pics/../../outside"] = new byte[] { 1 };
                if (zip) Zip(path, files, true); else Pak(path, files);
                Assert(Unverified(Inspect(root)), "Paths above the archive root remain rejected.");
            }
        }

        private static void TestFormatsAndCorruptWinner()
        {
            string root = Root("formats");
            Loose(root, "baseq2", Assets());
            Loose(root, "jump", new Dictionary<string, byte[]> { { "pics/conchars.pcx", Pcx() } });
            Loose(root, "baseq2", new Dictionary<string, byte[]> { { "pics/conchars.png", Png() } });
            GameDataReport report = Inspect(root);
            Assert(report.Verified, "PNG alternative decodes.");
            Assert(Asset(report, "pics/conchars.pcx").Source == "baseq2/pics/conchars.png", "Image format is selected before search directory.");
            Loose(root, "jump", new Dictionary<string, byte[]> { { "pics/conchars.png", Encoding.ASCII.GetBytes("corrupt") } });
            report = Inspect(root);
            Assert(!report.Verified && Asset(report, "pics/conchars.pcx").State == GameDataState.Invalid, "Corrupt effective PNG does not fall through to valid base PNG or PCX.");
            Loose(root, "jump", new Dictionary<string, byte[]> { { "q2config.cfg", Encoding.ASCII.GetBytes("set r_override_textures 0\n") } });
            Assert(Inspect(root).Verified, "Override disabled selects original PCX before corrupt PNG.");
            root = Root("tga");
            Loose(root, "baseq2", Assets());
            File.Delete(Path.Combine(root, "baseq2/pics/conchars.pcx"));
            Loose(root, "baseq2", new Dictionary<string, byte[]> { { "pics/conchars.tga", Tga() } });
            Assert(Inspect(root).Verified, "TGA alternative is validated.");
            var noTga = new GameDataContract(new string('b', 40), false, false, false, true);
            Assert(!GameDataInspector.Inspect(root, noTga, CancellationToken.None).Verified, "Binary without the format cannot use it.");
        }

        private static void TestZip()
        {
            foreach (bool compressed in new[] { false, true }) {
                string root = Root(compressed ? "deflated" : "stored");
                Zip(Path.Combine(root, "baseq2", "data.pkz"), Assets(), compressed);
                Assert(Inspect(root).Verified, "PKZ " + (compressed ? "deflate" : "store") + " validates without extraction.");
                var unsupported = new GameDataContract(new string('c', 40), true, true, true, false);
                Assert(!GameDataInspector.Inspect(root, unsupported, CancellationToken.None).Verified, "No-zlib binary ignores PKZ.");
            }
            string corrupt = Root("zip-corrupt");
            string path = Path.Combine(corrupt, "baseq2/data.pkz");
            Zip(path, Assets(), false);
            byte[] bytes = File.ReadAllBytes(path);
            bytes[30 + "pics/colormap.pcx".Length + 128] ^= 1;
            File.WriteAllBytes(path, bytes);
            Assert(!Inspect(corrupt).Verified, "PKZ CRC failure cannot pass structure checks.");
        }

        private static void TestMalformed()
        {
            string root = Root("truncated");
            Loose(root, "baseq2", Assets());
            File.WriteAllBytes(Path.Combine(root, "baseq2/broken.pak"), new byte[] { 1, 2, 3 });
            Assert(Unverified(Inspect(root)), "Malformed archives are unverified, not overwrite permission.");
            root = Root("traversal");
            Pak(Path.Combine(root, "baseq2/unsafe.pak"), new Dictionary<string, byte[]> { { "../escape", new byte[] { 1 } } });
            Assert(Unverified(Inspect(root)), "Traversal archive names are unverified.");
            root = Root("duplicates");
            var files = Assets(); files.Add("PICS/COLORMAP.PCX", Pcx());
            Pak(Path.Combine(root, "baseq2/duplicate.pak"), files);
            Assert(Unverified(Inspect(root)), "Case-colliding archive names are unverified.");
            root = Root("bad-model");
            Loose(root, "baseq2", Assets());
            byte[] model = Md2(); Put32(model, 56, 999999);
            Loose(root, "baseq2", new Dictionary<string, byte[]> { { "players/male/tris.md2", model } });
            Assert(Asset(Inspect(root), "players/male/tris.md2").State == GameDataState.Invalid, "MD2 offsets are validated, not just signature.");
            root = Root("bad-sound");
            Loose(root, "baseq2", Assets());
            byte[] sound = Wav(); Array.Resize(ref sound, 20);
            Loose(root, "baseq2", new Dictionary<string, byte[]> { { "sound/misc/menu1.wav", sound } });
            Assert(Asset(Inspect(root), "sound/misc/menu1.wav").State == GameDataState.Invalid, "Truncated WAV blocks verification.");
            root = Root("bad-pcx");
            Loose(root, "baseq2", Assets());
            byte[] pcx = new byte[128]; Array.Copy(Pcx(), pcx, 128);
            Loose(root, "baseq2", new Dictionary<string, byte[]> { { "pics/conchars.pcx", pcx } });
            Assert(Asset(Inspect(root), "pics/conchars.pcx").State == GameDataState.Invalid, "PCX pixels must be present.");
        }

        private static void TestConfiguration()
        {
            string root = Root("config");
            Loose(root, "baseq2", Assets());
            string sentinel = Path.Combine(root, "never-created");
            string config = "// settings remain data\nset skin male/grunt\nbind x \"exec dangerous.cfg\"\nalias later \"exec dangerous.cfg\"\n";
            Loose(root, "jump", new Dictionary<string, byte[]> { { "q2config.cfg", Encoding.ASCII.GetBytes(config) } });
            Assert(Inspect(root).Verified, "Literal settings and inert binds/aliases are accepted without execution.");
            Assert(!File.Exists(sentinel) && File.ReadAllText(Path.Combine(root, "jump/q2config.cfg")) == config, "Inspection preserves config bytes.");
            foreach (string command in new[] { "exec custom.cfg", "link pics elsewhere", "set homedir elsewhere", "set skin $profile", "later", "if 1 exec custom.cfg", "set fs_autoexec 0" }) {
                Loose(root, "jump", new Dictionary<string, byte[]> { { "autoexec.cfg", Encoding.ASCII.GetBytes(command) } });
                Assert(Unverified(Inspect(root)), "Ambiguous/config-dependent lookup is unverified: " + command);
            }
            File.Delete(Path.Combine(root, "jump/autoexec.cfg"));
            Loose(root, "jump", new Dictionary<string, byte[]> { { "q2pro.menu", Encoding.ASCII.GetBytes("font unknown") } });
            Assert(Inspect(root).Verified, "Ignored external menu file does not affect builtin Jump menu.");
            Loose(root, "jump", new Dictionary<string, byte[]> { { "autoexec.cfg", Encoding.ASCII.GetBytes("set ui_external_menu 1") } });
            Assert(Unverified(Inspect(root)), "Enabled external menu assets are not guessed.");
            File.Delete(Path.Combine(root, "jump/autoexec.cfg"));
            File.Delete(Path.Combine(root, "jump/q2pro.menu"));
            Loose(root, "jump", new Dictionary<string, byte[]> { { "postinit.cfg", Encoding.ASCII.GetBytes("set skin female/athena\n") } });
            Assert(Asset(Inspect(root), "players/female/tris.md2").State == GameDataState.Missing, "Postinit configured model overrides defaults.");
        }

        private static void TestCancellationAndLimits()
        {
            string root = Root("cancel");
            using (var source = new CancellationTokenSource()) {
                source.Cancel(); bool cancelled = false;
                try { GameDataInspector.Inspect(root, Contract, source.Token); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled, "Cancellation is propagated, never reported as success.");
            }
            Loose(root, "baseq2", Assets());
            using (var file = File.Create(Path.Combine(root, "baseq2/pics/conchars.png"))) file.SetLength(GameDataInspector.MaximumAssetBytes + 1L);
            Assert(Asset(Inspect(root), "pics/conchars.pcx").State == GameDataState.Unverified, "Oversize assets are bounded and unverified.");
        }

        private static void TestRequiredSubsetAndConfigOrder()
        {
            string root = Root("minimal");
            Loose(root, "baseq2", new Dictionary<string, byte[]> {
                { "pics/colormap.pcx", Pcx() }, { "pics/conchars.pcx", Pcx() }, { "pics/prochars.pcx", Pcx() }
            });
            GameDataReport report = Inspect(root);
            Assert(report.Verified, "Missing decorative media does not require baseline replacement.");
            Assert(!Asset(report, "pics/m_cursor0.pcx").Required && Asset(report, "pics/m_cursor0.pcx").State == GameDataState.Missing, "Optional media still has an honest missing observation.");
            string[] changedFormats = Contract.ImageFormats; changedFormats[0] = "invalid";
            Assert(Contract.ImageFormats[0] == "png", "Contract formats cannot be changed through the public getter.");
            Loose(root, "jump", new Dictionary<string, byte[]> { { "q2config.cfg", Encoding.ASCII.GetBytes("set con_font missingfont") } });
            Assert(Inspect(root).Verified, "A missing configured console font falls back to conchars.");
            root = Root("config-order");
            Loose(root, "baseq2", Assets());
            Loose(root, "baseq2", new Dictionary<string, byte[]> { { "pics/conchars.png", Png() }, { "autoexec.cfg", Encoding.ASCII.GetBytes("set r_override_textures 0") } });
            Loose(root, "jump", new Dictionary<string, byte[]> { { "autoexec.cfg", Encoding.ASCII.GetBytes("set r_override_textures 1") } });
            Assert(Asset(Inspect(root), "pics/conchars.pcx").Source.EndsWith(".png", StringComparison.Ordinal), "Jump autoexec follows baseq2 autoexec.");
            Loose(root, "jump", new Dictionary<string, byte[]> { { "postexec.cfg", Encoding.ASCII.GetBytes("set r_override_textures 0") } });
            Assert(Asset(Inspect(root), "pics/conchars.pcx").Source.EndsWith(".pcx", StringComparison.Ordinal), "Postexec follows both autoexec files.");
            Pak(Path.Combine(root, "jump/config.pak"), new Dictionary<string, byte[]> { { "q2config.cfg", Encoding.ASCII.GetBytes("exec forbidden.cfg") } });
            Assert(Inspect(root).Verified, "Archived q2config is never treated as the real startup settings file.");
            root = Root("ambiguous-order");
            Pak(Path.Combine(root, "baseq2/pak1.pak"), Assets());
            Pak(Path.Combine(root, "baseq2/pak01.pak"), Assets());
            Assert(Unverified(Inspect(root)), "Equal numeric archive sort keys are reported as ambiguous.");
        }

        private static void TestStagedResourceOverlay()
        {
            string root = Root("overlay-selected"), stage = Root("overlay-stage");
            var files = Assets(); files.Remove("pics/prochars.pcx");
            Loose(root, "baseq2", files);
            string sentinel = Path.Combine(root, "preserved.txt"); File.WriteAllText(sentinel, "untouched");
            Assert(!Inspect(root).Verified, "Default inspection still detects a missing shipped font.");
            Loose(stage, "jump", new Dictionary<string, byte[]> { { "pics/prochars.png", Png() }, { "autoexec.cfg", Encoding.ASCII.GetBytes("exec unrelated.cfg") } });
            Pak(Path.Combine(stage, "jump/not-discovered.pak"), new Dictionary<string, byte[]> { { "pics/prochars.png", new byte[] { 1 } } });
            PackageFile font = StagedFile(stage, "jump/pics/prochars.png", "resource");
            GameDataReport report = GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { font });
            Assert(report.Verified, "Staged PNG supplies the missing shipped font without a selected Jump directory.");
            Assert(Asset(report, "pics/prochars.pcx").Source == "staged:jump/pics/prochars.png", "The effective staged source is identified.");
            Assert(!Directory.Exists(Path.Combine(root, "jump")) && File.ReadAllText(sentinel) == "untouched", "Overlay inspection writes nothing into the selected root.");
            Assert(!Inspect(root).Verified, "Overlay does not persist into ordinary inspections.");
            Loose(root, "jump", new Dictionary<string, byte[]> { { "pics/prochars.png", new byte[] { 1 } } });
            Assert(GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { font }).Verified, "A staged file replaces the existing loose path for inspection.");
            Assert(File.ReadAllBytes(Path.Combine(root, "jump/pics/prochars.png"))[0] == 1, "An overlaid selected file remains byte-for-byte unchanged.");
            Pak(Path.Combine(root, "jump/shadow.pak"), new Dictionary<string, byte[]> { { "pics/prochars.png", new byte[] { 1 } } });
            report = GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { font });
            Assert(!report.Verified && Asset(report, "pics/prochars.pcx").State == GameDataState.Invalid, "Selected Jump packs still shadow the staged loose resource.");
            Assert(Asset(report, "pics/prochars.pcx").Source.Contains("shadow.pak:"), "Invalid pack winner is reported instead of staging fallback.");
        }

        private static void TestStagedResourceExclusions()
        {
            string root = Root("overlay-exclusion-selected"), stage = Root("overlay-exclusion-stage");
            Loose(root, "baseq2", Assets());
            Loose(stage, "jump", new Dictionary<string, byte[]> { { "pics/prochars.png", Png() } });
            PackageFile font = StagedFile(stage, "jump/pics/prochars.png", "resource");
            var ignored = new PackageFile("q2pro_race.exe", 1, new string('a', 64), "client");
            Assert(GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { font, ignored }).Verified, "Full inventories may include non-resource files without reading or running them.");
            foreach (string path in new[] { "jump/q2config.cfg", "jump/autoexec.cfg", "jump/data.pak", "jump/data.pkz", "jump/data.zip", "jump/pics/font.lnk", "jump/maps/test.bsp", "baseq2/pics/colormap.pcx" }) {
                var forbidden = new PackageFile(path, 1, new string('a', 64), "resource");
                Assert(Unverified(GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { forbidden })), "Forbidden staged resource does not enter lookup: " + path);
            }
            foreach (string path in new[] { "../outside.png", "/outside.png", "jump/../outside.png", "jump/pics/font.png:stream" }) {
                bool rejected = false;
                try { new PackageFile(path, 1, new string('a', 64), "resource"); } catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "Package path contract rejects an outside or alternate-stream path: " + path);
            }
            var duplicate = new PackageFile("jump/PICS/PROCHARS.PNG", font.Size, font.Sha256, "resource");
            Assert(Unverified(GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { font, duplicate })), "Case-colliding staged resources are rejected.");
            var wrongSize = new PackageFile(font.Path, font.Size + 1, font.Sha256, "resource");
            Assert(Unverified(GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { wrongSize })), "Staged size must match the package before use.");
            var wrongHash = new PackageFile(font.Path, font.Size, new string('b', 64), "resource");
            Assert(Unverified(GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage, new[] { wrongHash })), "Staged hash must match the package before use.");
        }

        private static void TestStagedResourceChangesAndLinks()
        {
            string root = Root("overlay-changed-selected"), stage = Root("overlay-changed-stage");
            Loose(root, "baseq2", Assets());
            Loose(stage, "jump", new Dictionary<string, byte[]> { { "pics/prochars.png", Png() } });
            PackageFile font = StagedFile(stage, "jump/pics/prochars.png", "resource");
            GameDataReport report = GameDataInspector.Inspect(root, Contract, CancellationToken.None, stage,
                ChangeAfterAdmission(font, Path.Combine(stage, font.Path)));
            Assert(Unverified(report), "Changed staged bytes cannot pass even when size and timestamp are restored.");
            string target = Root("overlay-link-target"), linkedStage = Root("overlay-linked-stage");
            File.WriteAllBytes(Path.Combine(target, "prochars.png"), Png());
            Directory.CreateDirectory(Path.Combine(linkedStage, "jump"));
            string link = Path.Combine(linkedStage, "jump/pics");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                "/d /c mklink /J \"" + link + "\" \"" + target + "\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start)) {
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new Exception("Could not create the isolated junction fixture: " + output);
            }
            try {
                font = StagedFile(target, "prochars.png", "resource");
                font = new PackageFile("jump/pics/prochars.png", font.Size, font.Sha256, "resource");
                Assert(Unverified(GameDataInspector.Inspect(root, Contract, CancellationToken.None, linkedStage, new[] { font })), "Staged junction paths are rejected before target asset reads.");
            } finally { Directory.Delete(link); }
            Assert(File.Exists(Path.Combine(target, "prochars.png")), "Rejecting and removing the test junction preserves its target.");
        }

        private static IEnumerable<PackageFile> ChangeAfterAdmission(PackageFile file, string path)
        {
            yield return file;
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            byte[] data = File.ReadAllBytes(path); data[data.Length - 1] ^= 1;
            File.WriteAllBytes(path, data); File.SetLastWriteTimeUtc(path, timestamp);
        }

        private static PackageFile StagedFile(string root, string path, string role)
        {
            using (var input = File.OpenRead(Path.Combine(root, path)))
                return new PackageFile(path, input.Length, ContentService.Hash(input, CancellationToken.None), role);
        }

        private static GameDataReport Inspect(string root) { return GameDataInspector.Inspect(root, Contract, CancellationToken.None); }
        private static bool Unverified(GameDataReport report) { foreach (GameDataAsset asset in report.Assets) if (asset.State == GameDataState.Unverified) return !report.Verified; return false; }
        private static GameDataAsset Asset(GameDataReport report, string path) { foreach (GameDataAsset asset in report.Assets) if (asset.Path == path) return asset; throw new Exception("Missing observation: " + path); }
        private static void Assert(bool result, string text) { assertions++; if (!result) throw new Exception("FAIL: " + text); }
        private static string Root(string name) { string root = Path.Combine(workspace, name); Directory.CreateDirectory(root); return root; }
        private static void Loose(string root, string game, Dictionary<string, byte[]> files) { foreach (var file in files) { string path = Path.Combine(root, game, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, file.Value); } }

        private static Dictionary<string, byte[]> Assets()
        {
            var files = new Dictionary<string, byte[]>();
            foreach (string name in new[] { "colormap", "conchars", "prochars", "conback", "q2jump_background", "ch1" }) files.Add("pics/" + name + ".pcx", Pcx());
            for (int i = 0; i < 15; i++) files.Add("pics/m_cursor" + i + ".pcx", Pcx());
            foreach (string name in new[] { "menu1", "menu2", "menu3", "talk1" }) files.Add("sound/misc/" + name + ".wav", Wav());
            files.Add("players/male/tris.md2", Md2()); files.Add("players/male/grunt.pcx", Pcx());
            return files;
        }

        private static byte[] Pcx() { var data = new byte[898]; data[0] = 10; data[1] = 5; data[2] = 1; data[3] = 8; data[65] = 1; data[66] = 1; data[128] = 1; data[129] = 12; return data; }
        private static byte[] Tga() { var data = new byte[21]; data[2] = 2; data[12] = 1; data[14] = 1; data[16] = 24; return data; }
        private static byte[] Png() { using (var bitmap = new Bitmap(2, 2)) using (var stream = new MemoryStream()) { bitmap.SetPixel(1, 1, Color.Red); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray(); } }
        private static byte[] Wav() { var data = new byte[46]; PutText(data, 0, "RIFF"); Put32(data, 4, 38); PutText(data, 8, "WAVEfmt "); Put32(data, 16, 16); data[20] = 1; data[22] = 1; Put32(data, 24, 11025); Put32(data, 28, 11025); data[32] = 1; data[34] = 8; PutText(data, 36, "data"); Put32(data, 40, 2); return data; }
        private static byte[] Md2() { var data = new byte[136]; PutText(data, 0, "IDP2"); Put32(data, 4, 8); Put32(data, 8, 1); Put32(data, 12, 1); Put32(data, 16, 52); Put32(data, 24, 3); Put32(data, 28, 1); Put32(data, 32, 1); Put32(data, 40, 1); Put32(data, 44, 68); Put32(data, 48, 68); Put32(data, 52, 72); Put32(data, 56, 84); Put32(data, 64, 136); return data; }
        private static void Put32(byte[] data, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 4); }
        private static void PutText(byte[] data, int offset, string text) { Array.Copy(Encoding.ASCII.GetBytes(text), 0, data, offset, text.Length); }

        private static void Pak(string path, Dictionary<string, byte[]> files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var output = new FileStream(path, FileMode.Create)) using (var writer = new BinaryWriter(output)) {
                writer.Write(Encoding.ASCII.GetBytes("PACK")); writer.Write(0); writer.Write(files.Count * 64);
                var offsets = new List<int>();
                foreach (var file in files) { offsets.Add((int)output.Position); writer.Write(file.Value); }
                int directory = (int)output.Position, index = 0;
                foreach (var file in files) { var name = new byte[56]; PutText(name, 0, file.Key); writer.Write(name); writer.Write(offsets[index++]); writer.Write(file.Value.Length); }
                output.Position = 4; writer.Write(directory);
            }
        }

        private static void Zip(string path, Dictionary<string, byte[]> files, bool deflated)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var output = new FileStream(path, FileMode.Create)) using (var writer = new BinaryWriter(output)) {
                var central = new List<byte[]>();
                foreach (var file in files) {
                    byte[] name = Encoding.ASCII.GetBytes(file.Key), payload = file.Value;
                    if (deflated) using (var memory = new MemoryStream()) { using (var compressor = new DeflateStream(memory, CompressionMode.Compress, true)) compressor.Write(payload, 0, payload.Length); payload = memory.ToArray(); }
                    int local = (int)output.Position; uint crc = Crc(file.Value);
                    writer.Write(0x04034b50); writer.Write((short)20); writer.Write((short)0); writer.Write((short)(deflated ? 8 : 0)); writer.Write(0); writer.Write(crc); writer.Write(payload.Length); writer.Write(file.Value.Length); writer.Write((short)name.Length); writer.Write((short)0); writer.Write(name); writer.Write(payload);
                    using (var memory = new MemoryStream()) using (var record = new BinaryWriter(memory)) {
                        record.Write(0x02014b50); record.Write((short)20); record.Write((short)20); record.Write((short)0); record.Write((short)(deflated ? 8 : 0)); record.Write(0); record.Write(crc); record.Write(payload.Length); record.Write(file.Value.Length); record.Write((short)name.Length); record.Write((short)0); record.Write((short)0); record.Write((short)0); record.Write((short)0); record.Write(0); record.Write(local); record.Write(name); central.Add(memory.ToArray());
                    }
                }
                int start = (int)output.Position;
                foreach (byte[] row in central) writer.Write(row);
                int length = (int)output.Position - start;
                writer.Write(0x06054b50); writer.Write((short)0); writer.Write((short)0); writer.Write((short)files.Count); writer.Write((short)files.Count); writer.Write(length); writer.Write(start); writer.Write((short)0);
            }
        }

        // Deliberately independent CRC implementation for fixture construction.
        private static uint Crc(byte[] bytes) { uint result = 0xffffffff; foreach (byte item in bytes) { uint part = (result ^ item) & 255; for (int i = 0; i < 8; i++) part = (part & 1) != 0 ? 0xedb88320 ^ (part >> 1) : part >> 1; result = (result >> 8) ^ part; } return ~result; }
    }
}
