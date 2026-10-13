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

        private static byte[] Archive(byte version)
        {
            using (var bytes = new MemoryStream()) {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
                    var executable = new byte[256];
                    executable[0] = (byte)'M'; executable[1] = (byte)'Z'; executable[100] = version;
                    BitConverter.GetBytes(64).CopyTo(executable, 60);
                    BitConverter.GetBytes((uint)0x4550).CopyTo(executable, 64);
                    BitConverter.GetBytes((ushort)0x8664).CopyTo(executable, 68);
                    BitConverter.GetBytes((ushort)0x20b).CopyTo(executable, 88);
                    using (var output = zip.CreateEntry("q2pro_race.exe").Open())
                        output.Write(executable, 0, executable.Length);
                    using (var output = zip.CreateEntry("jump/pics/prochars.png").Open())
                        output.WriteByte(version);
                }
                return bytes.ToArray();
            }
        }

        private static Dictionary<string, object> Asset(long id, int revision, string created, byte[] archive)
        {
            string name = "q2pro_race-windows-x64-r" + revision + ".zip";
            return new Dictionary<string, object> {
                { "id", id }, { "name", name }, { "created_at", created }, { "state", "uploaded" },
                { "digest", "sha256:" + ReleaseJson.Digest(archive) }, { "size", archive.Length },
                { "browser_download_url", ReleaseVerifier.Origin + "/releases/download/latest/" + name }
            };
        }

        private static GitHubGameRelease Fetch(Dictionary<string, object>[] assets,
            Dictionary<string, object> expected, byte[] archive)
        {
            byte[] metadata = Json(new Dictionary<string, object> {
                { "id", 1 }, { "tag_name", "latest" }, { "draft", false }, { "body", "Offline fixture." },
                { "html_url", ReleaseVerifier.Origin + "/releases/tag/latest" }, { "assets", assets }
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
                if (uri.AbsoluteUri == GitHubGameRelease.ApiUrl) response = metadata;
                else if (uri.AbsoluteUri == GitHubGameRelease.SourceApiUrl) response = source;
                else {
                    Check(uri.AbsoluteUri == (string)expected["browser_download_url"], "Downloaded an older or unrelated asset.");
                    response = archive;
                }
                return new DownloadReply(200, null, response.Length, new MemoryStream(response, false));
            });
            var release = GitHubGameRelease.Fetch(downloads, CancellationToken.None);
            Check(requests == 3, "Unexpected release requests.");
            Check(release.PackageUrl == (string)expected["browser_download_url"], "Wrong selected asset.");
            return release;
        }

        private static int Main()
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
                var unrelated = new[] { "q2pro_race-windows-x86-r3824.zip", "q2pro_race-darwin-arm64-r3824.zip", "q2pro_race_starter.exe" }
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
                Reject(() => Fetch(new[] { new Dictionary<string, object> { { "name", "q2pro_race_starter.exe" } } }, current, newArchive));
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
            Console.WriteLine("GitHub release fixtures: " + passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
