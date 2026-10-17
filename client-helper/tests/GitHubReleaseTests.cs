// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    internal static class GitHubReleaseTests
    {
        private static int passed, failed;
        private const string Earlier = "2026-09-25T12:00:00Z", Later = "2026-09-26T12:00:00Z";

        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid release was accepted.");
        }

        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }

        private static byte[] Json(object value)
        { return Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)); }

        private static byte[] Archive(byte version, GameClient client = null, byte resource = 1)
        {
            client = client ?? GameClient.Q2Pro;
            using (var bytes = new MemoryStream()) {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
                    var executable = new byte[256];
                    executable[0] = (byte)'M'; executable[1] = (byte)'Z'; executable[100] = version;
                    BitConverter.GetBytes(64).CopyTo(executable, 60);
                    BitConverter.GetBytes((uint)0x4550).CopyTo(executable, 64);
                    BitConverter.GetBytes((ushort)0x8664).CopyTo(executable, 68);
                    BitConverter.GetBytes((ushort)0x20b).CopyTo(executable, 88);
                    using (var output = zip.CreateEntry(client.Executable).Open())
                        output.Write(executable, 0, executable.Length);
                    using (var output = zip.CreateEntry("jump/pics/prochars.png").Open())
                        output.WriteByte(resource);
                }
                return bytes.ToArray();
            }
        }

        private static Dictionary<string, object> Asset(long id, int revision, string created, byte[] archive, GameClient client = null, bool upsName = false)
        {
            client = client ?? GameClient.Q2Pro;
            string name = Path.GetFileNameWithoutExtension(client.Executable) + "-windows-x64-" +
                (upsName ? revision + "ups" : "r" + revision) + ".zip";
            return new Dictionary<string, object> {
                { "id", id }, { "name", name }, { "created_at", created }, { "state", "uploaded" },
                { "digest", "sha256:" + ReleaseJson.Digest(archive) }, { "size", archive.Length },
                { "browser_download_url", client.Origin + "/releases/download/latest/" + name }
            };
        }

        private static GitHubGameRelease Fetch(Dictionary<string, object>[] assets,
            Dictionary<string, object> expected, byte[] archive, GameClient client = null)
        {
            client = client ?? GameClient.Q2Pro;
            byte[] metadata = Json(new Dictionary<string, object> {
                { "id", 1 }, { "tag_name", "latest" }, { "draft", false }, { "body", "Offline fixture." },
                { "html_url", client.Origin + "/releases/tag/latest" }, { "assets", assets }
            });
            byte[] source = Json(new Dictionary<string, object> {
                { "ref", "refs/tags/latest" }, { "object", new Dictionary<string, object> {
                    { "type", "commit" }, { "sha", new string('a', 40) }
                } }
            });
            int requests = 0;
            var downloads = new ReleaseDownloadClient((uri, token) => {
                requests++;
                byte[] response;
                if (uri.AbsoluteUri == client.ApiUrl) response = metadata;
                else if (uri.AbsoluteUri == client.SourceApiUrl) response = source;
                else {
                    Check(uri.AbsoluteUri == (string)expected["browser_download_url"], "Downloaded an older or unrelated asset.");
                    response = archive;
                }
                return new DownloadReply(200, null, response.Length, new MemoryStream(response, false));
            });
            var release = GitHubGameRelease.Fetch(downloads, CancellationToken.None, client: client);
            Check(requests == 3, "Unexpected release requests.");
            Check(release.PackageUrl == (string)expected["browser_download_url"], "Wrong selected asset.");
            return release;
        }

        private static ManagedGameService Service(GitHubGameRelease release)
        {
            return new ManagedGameService(downloads: new ReleaseDownloadClient((uri, token) => {
                byte[] bytes;
                if (uri.AbsoluteUri == release.Client.ApiUrl) bytes = release.MetadataBytes;
                else if (uri.AbsoluteUri == release.Client.SourceApiUrl) bytes = release.SourceBytes;
                else if (uri.AbsoluteUri == release.PackageUrl) bytes = release.ArchiveBytes;
                else throw new Exception("Unexpected download: " + uri);
                return new DownloadReply(200, null, bytes.Length, new MemoryStream(bytes, false));
            }), requireClosed: (root, files) => {}, client: release.Client);
        }

        private static GitHubGameRelease Release(GameClient client, byte version, byte resource = 1)
        {
            var archive = Archive(version, client, resource);
            var asset = Asset(version, version, Later, archive, client);
            return Fetch(new[] { asset }, asset, archive, client);
        }

        private static void WithInstallation(Action<string> test)
        {
            string root = Path.Combine(Path.GetTempPath(), "q2dual-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "baseq2"));
            Directory.CreateDirectory(Path.Combine(root, "jump"));
            File.WriteAllText(Path.Combine(root, "baseq2/pak0.pak"), "opaque existing PAK fixture");
            File.WriteAllText(Path.Combine(root, "jump/q2config.cfg"), "Q2PRO settings sentinel");
            File.WriteAllText(Path.Combine(root, "jump/config.cfg"), "R1Q2 settings sentinel");
            File.WriteAllText(Path.Combine(root, "vcruntime140.dll"), "presence fixture; never loaded");
            try { test(root); }
            finally { Directory.Delete(root, true); }
        }

        private static void DualClientTests()
        {
            Test("R1Q2 accepts current and ups package names with separate identity", () => {
                foreach (bool ups in new[] { false, true }) {
                    var archive = Archive(1, GameClient.R1Q2);
                    var asset = Asset(1, 40, Later, archive, GameClient.R1Q2, ups);
                    var release = Fetch(new[] { asset }, asset, archive, GameClient.R1Q2);
                    Check(release.Version == "40 ups" && release.Package.Component == "r1q2", "Wrong R1Q2 identity.");
                    var retained = GitHubGameRelease.Parse(release.MetadataBytes, release.SourceBytes, release.ArchiveBytes, CancellationToken.None, GameClient.R1Q2);
                    Check(retained.ManifestBytes.SequenceEqual(release.ManifestBytes), "R1Q2 receipt changed.");
                    Reject(() => GitHubGameRelease.Parse(release.MetadataBytes, release.SourceBytes, release.ArchiveBytes, CancellationToken.None));
                }
            });
            Test("selected repository and executable cannot authorize another client", () => {
                var wrong = Archive(1);
                var asset = Asset(1, 40, Later, wrong, GameClient.R1Q2);
                Reject(() => Fetch(new[] { asset }, asset, wrong, GameClient.R1Q2));
                var valid = Archive(1, GameClient.R1Q2);
                asset = Asset(1, 40, Later, valid, GameClient.R1Q2);
                asset["browser_download_url"] = GameClient.Q2Pro.Origin + "/releases/download/latest/" + asset["name"];
                Reject(() => Fetch(new[] { asset }, asset, valid, GameClient.R1Q2));
                Reject(() => ReleaseOrigins.Initial("https://github.com/other/r1q2_race/releases/download/latest/client.zip"));
                Reject(() => new ReleaseDownloadClient().GitHubMetadata("https://api.github.com/repos/other/r1q2_race/releases/tags/latest", 1000, CancellationToken.None));
            });
            Test("R1Q2 alone has separate receipts and backs up config.cfg", () => WithInstallation(root => {
                File.Delete(Path.Combine(root, "vcruntime140.dll"));
                var service = Service(Release(GameClient.R1Q2, 1));
                StarterClientProgram.Install(root, service, CancellationToken.None, message => {}, plan => true);
                Check(!Directory.Exists(Path.Combine(root, ".q2jump")), "R1Q2 claimed Q2PRO metadata.");
                Check(File.Exists(Path.Combine(root, ".q2jump-r1q2/installed.json")), "No R1Q2 receipt.");
                var backup = Directory.GetFiles(Path.Combine(root, ".q2jump-r1q2/configurations"), "config.cfg", SearchOption.AllDirectories).Single();
                Check(File.ReadAllText(backup) == "R1Q2 settings sentinel", "Missing R1Q2 settings backup.");
            }));
            Test("both install and update independently without changing settings", () => WithInstallation(root => {
                var q2 = Service(Release(GameClient.Q2Pro, 1));
                var r1 = Service(Release(GameClient.R1Q2, 1));
                StarterClientProgram.InstallClients(root, new[] { q2, r1 }, CancellationToken.None, message => {}, plan => true);
                byte[] q2Receipt = File.ReadAllBytes(Path.Combine(root, ".q2jump/installed.json"));
                StarterClientProgram.Install(root, Service(Release(GameClient.R1Q2, 2)), CancellationToken.None, message => {}, plan => true);
                Check(File.ReadAllBytes(Path.Combine(root, ".q2jump/installed.json")).SequenceEqual(q2Receipt), "R1Q2 replaced the Q2PRO receipt.");
                Check(q2.Check(root, CancellationToken.None).Installation.CanPlay, "R1Q2 invalidated Q2PRO.");
                byte[] r1Receipt = File.ReadAllBytes(Path.Combine(root, ".q2jump-r1q2/installed.json"));
                StarterClientProgram.Install(root, Service(Release(GameClient.Q2Pro, 2)), CancellationToken.None, message => {}, plan => true);
                Check(File.ReadAllBytes(Path.Combine(root, ".q2jump-r1q2/installed.json")).SequenceEqual(r1Receipt), "Q2PRO replaced the R1Q2 receipt.");
                Check(r1.Check(root, CancellationToken.None).Installation.CanPlay, "Q2PRO invalidated R1Q2.");
                Check(File.ReadAllText(Path.Combine(root, "jump/config.cfg")) == "R1Q2 settings sentinel", "R1Q2 settings changed.");
                Check(File.ReadAllText(Path.Combine(root, "jump/q2config.cfg")) == "Q2PRO settings sentinel", "Q2PRO settings changed.");
            }));
            Test("incompatible shared resources fail before replacing either client", () => WithInstallation(root => {
                var q2 = Service(Release(GameClient.Q2Pro, 1));
                StarterClientProgram.Install(root, q2, CancellationToken.None, message => {}, plan => true);
                try { Service(Release(GameClient.R1Q2, 2, 2)).Preview(root, false, CancellationToken.None); throw new Exception("Conflict accepted."); }
                catch (IOException error) { Check(error.Message.Contains("conflicts"), "Wrong failure."); }
                Check(!File.Exists(Path.Combine(root, "r1q2_race.exe")), "R1Q2 was installed before detecting conflict.");
                Check(q2.Check(root, CancellationToken.None).Installation.CanPlay, "Q2PRO damaged.");
            }));
            Test("interrupted R1Q2 update recovers without touching Q2PRO", () => WithInstallation(root => {
                var q2 = Service(Release(GameClient.Q2Pro, 1));
                var r1 = Service(Release(GameClient.R1Q2, 1));
                StarterClientProgram.InstallClients(root, new[] { q2, r1 }, CancellationToken.None, message => {}, plan => true);
                byte[] before = File.ReadAllBytes(Path.Combine(root, "r1q2_race.exe"));
                var update = Service(Release(GameClient.R1Q2, 2));
                var updatePlan = update.Preview(root, false, CancellationToken.None);
                int boundaries = 0;
                try {
                    update.Install(updatePlan, true, true, CancellationToken.None, message => {
                        if (message == "Applying approved files" && ++boundaries == 2) throw new IOException("fixture interruption");
                    });
                    throw new Exception("Interruption did not occur.");
                } catch (IOException error) { Check(error.Message == "fixture interruption", "Unexpected failure."); }
                Check(ManagedGameService.HasPending(root, "r1q2"), "R1Q2 journal missing.");
                update.Recover(root, CancellationToken.None);
                Check(File.ReadAllBytes(Path.Combine(root, "r1q2_race.exe")).SequenceEqual(before), "R1Q2 was not restored.");
                Check(q2.Check(root, CancellationToken.None).Installation.CanPlay, "Recovery damaged Q2PRO.");
                StarterClientProgram.InstallClients(root, new[] { q2, update }, CancellationToken.None, message => {}, preview => true);
            }));
        }

        private static int Main(string[] args)
        {
            byte[] oldArchive = Archive(1), newArchive = Archive(2);
            Test("single published x64 asset remains installable", () => {
                var asset = Asset(1, 3823, Later, newArchive);
                Check(Fetch(new[] { asset }, asset, newArchive).Version == "r3823", "Wrong release version.");
            });
            Test("latest upload wins independently of API order and branch revision count", () => {
                var old = Asset(10, 4000, Earlier, oldArchive);
                var current = Asset(20, 3823, Later, newArchive);
                foreach (var assets in new[] { new[] { old, current }, new[] { current, old } })
                    Check(Fetch(assets, current, newArchive).Version == "r3823", "Selected the largest revision instead of the latest upload.");
            });
            Test("same-second uploads use asset ID independently of API order", () => {
                var old = Asset(10, 3822, Later, oldArchive);
                var current = Asset(20, 3823, Later, newArchive);
                Fetch(new[] { old, current }, current, newArchive);
                Fetch(new[] { current, old }, current, newArchive);
            });
            Test("an older asset without a digest does not block the current upload", () => {
                var old = Asset(10, 3822, Earlier, oldArchive); old["digest"] = null;
                var current = Asset(20, 3823, Later, newArchive);
                Fetch(new[] { old, current }, current, newArchive);
            });
            Test("unrelated newer platform assets do not affect x64 selection", () => {
                var current = Asset(20, 3823, Earlier, newArchive);
                var unrelated = new[] { "q2pro_race-windows-x86-r3824.zip", "q2pro_race-darwin-arm64-r3824.zip", "q2jump-starter.exe" }
                    .Select(name => new Dictionary<string, object> { { "name", name }, { "created_at", Later } });
                Fetch(unrelated.Concat(new[] { current }).ToArray(), current, newArchive);
            });
            Test("retained releases can exceed one hundred assets", () => {
                var current = Asset(200, 3823, Later, newArchive);
                var assets = Enumerable.Range(1, 150).Select(id => Asset(id, 3000 + id, Earlier, oldArchive));
                Fetch(assets.Concat(new[] { current }).ToArray(), current, newArchive);
            });
            Test("invalid current asset identity never falls back to an older upload", () => {
                foreach (string field in new[] { "digest", "state", "browser_download_url", "size" }) {
                    var old = Asset(10, 3822, Earlier, oldArchive);
                    var current = Asset(20, 3823, Later, newArchive);
                    if (field == "digest") current[field] = null;
                    else if (field == "state") current[field] = "starter";
                    else if (field == "browser_download_url") current[field] = ReleaseVerifier.Origin + "/releases/download/latest/wrong.zip";
                    else current[field] = 0;
                    Reject(() => Fetch(new[] { old, current }, current, newArchive));
                }
            });
            Test("selected archive must still match its digest", () => {
                var current = Asset(20, 3823, Later, newArchive);
                current["digest"] = "sha256:" + new string('0', 64);
                Reject(() => Fetch(new[] { current }, current, newArchive));
            });
            Test("missing x64 assets and malformed upload timestamps are rejected", () => {
                var current = Asset(20, 3823, Later, newArchive);
                Reject(() => Fetch(new[] { new Dictionary<string, object> { { "name", "q2jump-starter.exe" } } }, current, newArchive));
                current["created_at"] = "not-a-timestamp";
                Reject(() => Fetch(new[] { current }, current, newArchive));
            });
            Test("retained multi-asset receipts reconstruct the same installed package", () => {
                var old = Asset(10, 3822, Earlier, oldArchive);
                var current = Asset(20, 3823, Later, newArchive);
                var fetched = Fetch(new[] { old, current }, current, newArchive);
                var retained = GitHubGameRelease.Parse(fetched.MetadataBytes, fetched.SourceBytes, fetched.ArchiveBytes, CancellationToken.None);
                Check(fetched.ManifestBytes.SequenceEqual(retained.ManifestBytes), "Retained receipt selected a different package.");
            });
            DualClientTests();
            if (args.Length == 1) Test("public release receipts and packages install together offline", () => {
                var q2 = GitHubGameRelease.Parse(File.ReadAllBytes(Path.Combine(args[0], "q2pro-release.json")),
                    File.ReadAllBytes(Path.Combine(args[0], "q2pro-source.json")), File.ReadAllBytes(Path.Combine(args[0], "q2pro.zip")), CancellationToken.None);
                var r1 = GitHubGameRelease.Parse(File.ReadAllBytes(Path.Combine(args[0], "r1q2-release.json")),
                    File.ReadAllBytes(Path.Combine(args[0], "r1q2-source.json")), File.ReadAllBytes(Path.Combine(args[0], "r1q2.zip")), CancellationToken.None, GameClient.R1Q2);
                WithInstallation(root => {
                    StarterClientProgram.InstallClients(root, new[] { Service(q2), Service(r1) }, CancellationToken.None, message => {}, plan => true);
                    Check(Service(q2).Check(root, CancellationToken.None).Installation.CanPlay, "Q2PRO not ready");
                    Check(Service(r1).Check(root, CancellationToken.None).Installation.CanPlay, "R1Q2 not ready");
                });
            });
            Console.WriteLine("GitHub release fixtures: " + passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
