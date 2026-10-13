// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    // GitHub publication is the game-release authority chosen by the maintainer.
    // Cached receipts are local integrity evidence, not publisher signatures.
    internal sealed class GitHubGameRelease : IManagedRelease
    {
        internal const string ApiUrl = "https://api.github.com/repos/MovoWR/q2pro_race/releases/tags/latest";
        internal const string SourceApiUrl = "https://api.github.com/repos/MovoWR/q2pro_race/git/ref/tags/latest";
        internal const int MaximumArchiveBytes = 64 * 1024 * 1024;
        private const long MaximumExpandedBytes = 256L * 1024 * 1024;
        internal readonly ManagedPackage Package;
        internal readonly string PackageUrl, PackageSha256, SourceRevision, Version, Changes, BuildUrl;
        internal readonly long PackageSize;
        private readonly byte[] metadata, source, archive, descriptor;
        private readonly Dictionary<string, byte[]> contents;
        internal byte[] MetadataBytes { get { return (byte[])metadata.Clone(); } }
        internal byte[] SourceBytes { get { return (byte[])source.Clone(); } }
        internal byte[] ArchiveBytes { get { return (byte[])archive.Clone(); } }
        internal byte[] DescriptorBytes { get { return (byte[])descriptor.Clone(); } }
        internal byte[] ManifestBytes { get { return Package.Manifest; } }
        ManagedPackage IManagedRelease.Package { get { return Package; } }
        string IManagedRelease.PackageUrl { get { return PackageUrl; } }
        string IManagedRelease.PackageSha256 { get { return PackageSha256; } }
        long IManagedRelease.PackageSize { get { return PackageSize; } }
        string IManagedRelease.SourceRevision { get { return SourceRevision; } }
        string IManagedRelease.Version { get { return Version; } }
        string IManagedRelease.Changes { get { return Changes; } }
        string IManagedRelease.BuildUrl { get { return BuildUrl; } }
        byte[] IManagedRelease.ManifestBytes { get { return ManifestBytes; } }
        GameDataContract IManagedRelease.BindGameData(byte[] bytes) { return BindGameData(bytes); }

        private sealed class Asset
        {
            internal long Id, Size;
            internal string Name, Url, Hash, Version, Changes;
        }
        private static Asset ReadAsset(byte[] bytes)
        {
            var value = ReleaseJson.Parse(bytes, ReleaseVerifier.MaximumMetadataBytes);
            if (ReleaseJson.String(value, "tag_name") != "latest" || ReleaseJson.Boolean(value, "draft") ||
                ReleaseJson.String(value, "html_url", 2048) != ReleaseVerifier.Origin + "/releases/tag/latest")
                throw new InvalidDataException("Expected the published q2pro_race latest release.");
            ReleaseJson.Integer(value, "id", 1, 9007199254740991L);
            Dictionary<string, object> selected = null;
            string selectedTime = null, selectedVersion = null;
            long selectedId = 0;
            foreach (object item in ReleaseJson.Array(value, "assets", 1, 4096))
            {
                var candidate = ReleaseJson.Object(item);
                string name = ReleaseJson.String(candidate, "name", 180);
                var match = Regex.Match(name, @"\Aq2pro_race-windows-x64-r([0-9]{1,10})\.zip\z");
                if (!match.Success) continue;
                string created = ReleaseJson.Timestamp(ReleaseJson.String(candidate, "created_at", 20));
                long id = ReleaseJson.Integer(candidate, "id", 1, 9007199254740991L);
                int order = String.CompareOrdinal(created, selectedTime);
                if (selected != null && (order < 0 || (order == 0 && id <= selectedId))) continue;
                selected = candidate; selectedTime = created; selectedId = id;
                selectedVersion = "r" + match.Groups[1].Value;
            }
            if (selected == null) throw new InvalidDataException("The latest release has no Windows x64 client package.");
            // Revisioned assets are retained between publications. Choose by upload
            // time, not revision count (another branch may publish a lower count).
            // Validate the chosen asset without silently falling back to an old one.
            string selectedName = ReleaseJson.String(selected, "name", 180);
            string digest = ReleaseJson.String(selected, "digest", 80);
            string url = ReleaseJson.String(selected, "browser_download_url", 2048);
            if (ReleaseJson.String(selected, "state") != "uploaded" || !Regex.IsMatch(digest, @"\Asha256:[0-9a-f]{64}\z") ||
                url != ReleaseVerifier.Origin + "/releases/download/latest/" + selectedName)
                throw new InvalidDataException("The latest Windows x64 package has no valid GitHub download identity.");
            var asset = new Asset { Id = selectedId, Size = ReleaseJson.Integer(selected, "size", 1, MaximumArchiveBytes),
                Name = selectedName, Url = url, Hash = digest.Substring(7), Version = selectedVersion };
            object body;
            value.TryGetValue("body", out body);
            string notes = body as string;
            if (body != null && (notes == null || notes.Length > 16384 || notes.Any(c => Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')))
                throw new InvalidDataException("Invalid latest-release notes.");
            asset.Changes = String.IsNullOrWhiteSpace(notes) ? "Latest Q2PRO Race release." : notes;
            return asset;
        }
        internal static GitHubGameRelease Fetch(ReleaseDownloadClient downloads, CancellationToken token, Action<string> progress = null)
        {
            if (progress != null) progress("Checking the latest Q2PRO Race release");
            byte[] metadata = downloads.GitHubMetadata(ApiUrl, ReleaseVerifier.MaximumMetadataBytes, token);
            Asset asset = ReadAsset(metadata);
            byte[] source = downloads.GitHubMetadata(SourceApiUrl, 16384, token);
            using (var buffer = new MemoryStream())
            {
                if (progress != null) progress("Downloading " + asset.Version + " for installation preview");
                downloads.Download(asset.Url, buffer, asset.Size, token, bytes => {
                    if (progress != null) progress("Downloading " + bytes + " / " + asset.Size + " bytes");
                });
                return Parse(metadata, source, buffer.ToArray(), token);
            }
        }
        internal static GitHubGameRelease Parse(byte[] metadata, byte[] source, byte[] archive, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            metadata = ReleaseJson.Snapshot(metadata, ReleaseVerifier.MaximumMetadataBytes);
            source = ReleaseJson.Snapshot(source, 16384);
            archive = ReleaseJson.Snapshot(archive, MaximumArchiveBytes);
            Asset asset = ReadAsset(metadata);
            if (archive == null || archive.LongLength != asset.Size || ReleaseJson.Digest(archive) != asset.Hash)
                throw new InvalidDataException("The downloaded client does not match the GitHub release SHA-256 and size. Check the release again.");
            var reference = ReleaseJson.Parse(source, 16384);
            var target = ReleaseJson.Object(reference, "object");
            string revision = ReleaseJson.String(target, "sha", 40);
            if (ReleaseJson.String(reference, "ref") != "refs/tags/latest" || ReleaseJson.String(target, "type") != "commit" ||
                !Regex.IsMatch(revision, @"\A[0-9a-f]{40}\z")) throw new InvalidDataException("The latest source tag does not identify a supported commit.");
            var files = new List<PackageFile>();
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new List<string>(); long expanded = 0;
            using (var input = new MemoryStream(archive, false))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, false))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > 4096) throw new InvalidDataException("Invalid latest client archive inventory.");
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                    string path = directory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName;
                    InstallationPaths.Relative(path);
                    if (!names.Add(path)) throw new InvalidDataException("Duplicate or case-colliding latest package entry.");
                    int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                    if ((entry.ExternalAttributes & 0x400) != 0 || (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)))
                        throw new InvalidDataException("The latest package contains a link or unsupported file type.");
                    if (directory)
                    {
                        if (entry.Length != 0) throw new InvalidDataException("A package directory has content.");
                        directories.Add(path); continue;
                    }
                    if (entry.Length <= 0 || entry.Length > MaximumArchiveBytes || (expanded += entry.Length) > MaximumExpandedBytes)
                        throw new InvalidDataException("The expanded latest package exceeds its size limit.");
                    string role = path == "q2pro_race.exe" ? "client" : !path.Contains('/') && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "runtime" :
                        path.StartsWith("jump/", StringComparison.Ordinal) ? "resource" : path.StartsWith("licenses/", StringComparison.Ordinal) ? "notice" :
                        path.StartsWith("source/", StringComparison.Ordinal) ? "source" : null;
                    if (role == null) throw new InvalidDataException("Unexpected file in latest client package: " + path);
                    using (var bytes = new MemoryStream())
                    {
                        using (var stream = entry.Open()) ReleaseDownloadClient.CopyBounded(stream, bytes, entry.Length, token);
                        byte[] data = bytes.ToArray();
                        if (data.LongLength != entry.Length) throw new InvalidDataException("Truncated latest package entry.");
                        var file = new PackageFile(path, data.LongLength, ReleaseJson.Digest(data), role);
                        InstallationPaths.ProgramFile("game", file);
                        if (role == "client" || role == "runtime") RequireX64(data);
                        files.Add(file); contents.Add(path, data);
                    }
                }
            }
            foreach (string directory in directories)
                if (!files.Any(file => file.Path.StartsWith(directory + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("Unexpected empty directory in latest package.");
            return new GitHubGameRelease(metadata, source, archive, asset, revision, files, contents);
        }
        private static void RequireX64(byte[] data)
        {
            if (data.Length < 64 || data[0] != 'M' || data[1] != 'Z') throw new InvalidDataException("The latest package program is not a Windows x64 PE file.");
            int offset = BitConverter.ToInt32(data, 60);
            if (offset < 64 || offset > data.Length - 26 || BitConverter.ToUInt32(data, offset) != 0x4550 ||
                BitConverter.ToUInt16(data, offset + 4) != 0x8664 || BitConverter.ToUInt16(data, offset + 24) != 0x20b)
                throw new InvalidDataException("The latest package program is not a Windows x64 PE file.");
        }
        private GitHubGameRelease(byte[] metadata, byte[] source, byte[] archive, Asset asset, string revision,
            List<PackageFile> files, Dictionary<string, byte[]> contents)
        {
            this.metadata = metadata; this.source = source; this.archive = archive; this.contents = contents;
            PackageUrl = asset.Url; PackageSize = asset.Size; PackageSha256 = asset.Hash; SourceRevision = revision;
            Version = asset.Version; Changes = asset.Changes; BuildUrl = ReleaseVerifier.Origin + "/tree/" + revision;
            var serializer = new JavaScriptSerializer { MaxJsonLength = ReleaseVerifier.MaximumMetadataBytes };
            // Capabilities are the helper's explicit latest-build compatibility policy.
            // The moving source tag is an observation, not a binary build attestation.
            descriptor = Encoding.UTF8.GetBytes(serializer.Serialize(new Dictionary<string, object> {
                { "schema", 1 }, { "kind", "game-data-contract" }, { "id", GameDataContract.Version },
                { "sourceRevision", revision }, { "png", true }, { "jpg", true }, { "tga", true }, { "pkz", true }
            }));
            files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
            byte[] manifest = Encoding.UTF8.GetBytes(serializer.Serialize(new Dictionary<string, object> {
                { "schema", 1 }, { "kind", "github-latest-game" }, { "repository", ReleaseVerifier.Repository },
                { "assetId", asset.Id }, { "assetName", asset.Name }, { "size", asset.Size }, { "sha256", asset.Hash },
                { "metadataSha256", ReleaseJson.Digest(metadata) }, { "sourceObservationSha256", ReleaseJson.Digest(source) },
                { "files", files.Select(file => new Dictionary<string, object> { { "path", file.Path }, { "size", file.Size },
                    { "sha256", file.Sha256 }, { "role", file.Role } }).ToArray() }
            }));
            Package = ManagedPackage.FromGithubLatest("github-" + asset.Id + "-" + asset.Hash.Substring(0, 16), files, manifest);
        }
        internal GameDataContract BindGameData(byte[] bytes)
        {
            if (bytes == null || !descriptor.SequenceEqual(bytes)) throw new InvalidDataException("The retained latest-client compatibility descriptor changed.");
            return new GameDataContract(SourceRevision, true, true, true, true);
        }
        internal void Extract(string directory, CancellationToken token)
        {
            if (File.Exists(directory) || Directory.Exists(directory)) throw new IOException("Latest package staging must use a new directory.");
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory); InstallationPaths.Root(directory);
            foreach (var file in Package.Files)
            {
                token.ThrowIfCancellationRequested();
                string path = InstallationPaths.Under(directory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var input = new MemoryStream(contents[file.Path], false))
                { ReleaseDownloadClient.CopyBounded(input, output, file.Size, token); output.Flush(true); }
                if (!FileObservation.Read(path, token).Matches(file)) throw new InvalidDataException("Staged latest-client file changed.");
            }
        }
    }
}
