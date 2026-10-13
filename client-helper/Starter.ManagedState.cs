// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Q2JumpStarter
{
    internal sealed class ManagedInstallation
    {
        internal readonly string Root, OperationDirectory;
        internal readonly StableChannel Channel;
        internal readonly IManagedRelease Release;
        internal readonly ReleaseReference ComponentReference;
        internal readonly GameDataContract Contract;
        internal readonly bool IsPending;
        private readonly OperationRecord record;
        private readonly byte[] descriptor, recordBytes;
        private readonly List<ManagedMetadataSnapshot> snapshots;
        internal OperationRecord Record { get { return ManagedInstallationStore.CopyRecord(record); } }
        internal byte[] DescriptorBytes { get { return descriptor == null ? null : (byte[])descriptor.Clone(); } }
        internal byte[] RecordBytes { get { return (byte[])recordBytes.Clone(); } }
        internal ManagedInstallation(string root, string operationDirectory, OperationRecord record, StableChannel channel,
            IManagedRelease release, byte[] descriptor, byte[] recordBytes, bool pending, List<ManagedMetadataSnapshot> snapshots)
        {
            Root = root; OperationDirectory = operationDirectory; this.record = ManagedInstallationStore.CopyRecord(record);
            Channel = channel; Release = release; this.descriptor = descriptor == null ? null : (byte[])descriptor.Clone(); this.recordBytes = (byte[])recordBytes.Clone();
            ComponentReference = channel == null ? null : release.Package.Component == "game" ? channel.Game : channel.Launcher;
            Contract = release.Package.Component == "game" ? release.BindGameData(descriptor) : null; IsPending = pending;
            this.snapshots = snapshots.Select(item => new ManagedMetadataSnapshot(item.Path, item.Bytes)).ToList();
        }
        // A caller about to use this verified identity can detect intervening
        // metadata edits without hashing program files or admitting pending Play.
        internal void RecheckMetadata(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (new OperationLock(InstallationPaths.Root(Root))) {
                if (!IsPending) ManagedInstallationStore.RequireNoPending(Root, Release.Package.Component);
                ManagedInstallationStore.CheckSnapshots(Root, snapshots, token);
            }
        }
        // Success proves the recorded program inventory at this observation. Game
        // assets, config backups and process/launch checks remain caller obligations.
        internal void VerifyProgramFiles(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (IsPending) throw new InvalidDataException("An unfinished operation cannot become Ready.");
            using (new OperationLock(InstallationPaths.Root(Root))) {
                ManagedInstallationStore.RequireNoPending(Root, Release.Package.Component);
                ManagedInstallationStore.CheckSnapshots(Root, snapshots, token);
                foreach (var file in Release.Package.Files) {
                    token.ThrowIfCancellationRequested();
                    string path = ManagedInstallationStore.CheckedFilePath(Root, file.Path);
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        if (input.Length != file.Size || ContentService.Hash(input, token) != file.Sha256)
                            throw new InvalidDataException("Managed program file is missing or changed: " + file.Path);
                    }
                    ManagedInstallationStore.CheckedFilePath(Root, file.Path);
                }
                ManagedInstallationStore.RequireNoPending(Root, Release.Package.Component);
                ManagedInstallationStore.CheckSnapshots(Root, snapshots, token);
            }
        }
    }

    internal sealed class ManagedMetadataSnapshot
    {
        internal readonly string Path;
        internal readonly byte[] Bytes;
        internal ManagedMetadataSnapshot(string path, byte[] bytes) { Path = path; Bytes = (byte[])bytes.Clone(); }
    }

    // Local pointers locate signed publisher evidence or cached GitHub game receipts.
    // Neither mode can grant authority over paths outside its admitted package.

    internal static class ManagedInstallationStore
    {
        private const int MaximumRecordBytes = 1024 * 1024;
        internal static ManagedInstallation ReadInstalled(string root, ReleaseVerifier verifier, CancellationToken token, string component = "game")
        { return Read(root, verifier, token, false, component); }
        internal static ManagedInstallation ReadPending(string root, ReleaseVerifier verifier, CancellationToken token, string component = "game")
        { return Read(root, verifier, token, true, component); }

        private static ManagedInstallation Read(string root, ReleaseVerifier verifier, CancellationToken token, bool pending, string component)
        {
            if (verifier == null) throw new ArgumentNullException("verifier");
            token.ThrowIfCancellationRequested(); root = InstallationPaths.Root(root); var scope = MetadataScope.For(component);
            byte[] expectedOwner = Encoding.UTF8.GetBytes(scope.OwnerText);
            using (new OperationLock(root)) {
                string metadata = ContentService.SafePath(root, scope.Directory); CheckAncestors(metadata);
                FileAttributes? attributes = Attributes(metadata);
                if (attributes == null) return null;
                if ((attributes.Value & FileAttributes.Directory) == 0) throw new InvalidDataException("The reserved " + scope.Directory + " path is not a metadata directory.");
                var snapshots = new List<ManagedMetadataSnapshot>();
                byte[] owner = ReadSnapshot(root, scope.Directory + "/owner", expectedOwner.Length, snapshots, token);
                if (!owner.SequenceEqual(expectedOwner)) throw new InvalidDataException("The " + scope.Directory + " directory has an unknown owner marker.");
                if (!pending) RequireNoPending(root, component);
                string pointerPath = scope.Directory + (pending ? "/active.json" : "/installed.json");
                byte[] pointer = ReadBytes(root, pointerPath, MaximumRecordBytes, token, true);
                if (pointer == null) { CheckSnapshots(root, snapshots, token); return null; }
                snapshots.Add(new ManagedMetadataSnapshot(pointerPath, pointer));
                var rawRecord = ReleaseJson.Parse(pointer, MaximumRecordBytes);
                ReleaseJson.Fields(rawRecord, "Schema Id ManifestSha256 State Files");
                string id = ReleaseJson.String(rawRecord, "Id", 32);
                Guid guid;
                if (!Regex.IsMatch(id, @"\A[0-9a-f]{32}\z") || !Guid.TryParseExact(id, "N", out guid) || guid == Guid.Empty)
                    throw new InvalidDataException("Invalid managed operation identifier.");
                string operation = scope.Directory + "/operations/" + id;
                byte[] manifest = ReadSnapshot(root, operation + "/manifest.json", ReleaseVerifier.MaximumMetadataBytes, snapshots, token);
                byte[] githubMetadata = ReadBytes(root, operation + "/github-release.json", ReleaseVerifier.MaximumMetadataBytes, token, true);
                StableChannel channel = null;
                IManagedRelease release;
                if (githubMetadata != null) {
                    if (component != "game") throw new InvalidDataException("GitHub game evidence cannot authorize launcher files.");
                    foreach (string signedName in new[] { "channel.json", "channel.sig", "manifest.sig" })
                        if (Attributes(CheckedFilePath(root, operation + "/" + signedName)) != null)
                            throw new InvalidDataException("Managed operation mixes GitHub and publisher-signed evidence.");
                    snapshots.Add(new ManagedMetadataSnapshot(operation + "/github-release.json", githubMetadata));
                    byte[] source = ReadSnapshot(root, operation + "/github-source.json", 16384, snapshots, token);
                    byte[] archive = ReadSnapshot(root, operation + "/github-package.zip", GitHubGameRelease.MaximumArchiveBytes, snapshots, token);
                    release = GitHubGameRelease.Parse(githubMetadata, source, archive, token);
                    if (!release.ManifestBytes.SequenceEqual(manifest))
                        throw new InvalidDataException("The retained GitHub archive differs from its recorded inventory.");
                } else {
                    byte[] channelBytes = ReadSnapshot(root, operation + "/channel.json", ReleaseVerifier.MaximumMetadataBytes, snapshots, token);
                    byte[] channelSignature = ReadSnapshot(root, operation + "/channel.sig", ReleaseVerifier.MaximumSignatureBytes, snapshots, token);
                    channel = verifier.ParseChannel(channelBytes, channelSignature);
                    byte[] signature = ReadSnapshot(root, operation + "/manifest.sig", ReleaseVerifier.MaximumSignatureBytes, snapshots, token);
                    release = verifier.ParseManifest(manifest, signature, component == "game" ? channel.Game : channel.Launcher, component);
                }
                token.ThrowIfCancellationRequested();
                var record = ParseRecord(rawRecord, release.Package, pending);
                byte[] descriptor = null;
                if (component == "game") {
                    descriptor = ReadSnapshot(root, operation + "/game-data-contract.json", 16384, snapshots, token);
                    release.BindGameData(descriptor);
                }
                byte[] ledger = ReadBytes(root, operation + "/ledger.json", MaximumRecordBytes, token, pending);
                if (ledger != null) {
                    ParseRecord(ReleaseJson.Parse(ledger, MaximumRecordBytes), release.Package, false);
                    if (record.State != "verified" || !ledger.SequenceEqual(pointer)) throw new InvalidDataException("Installed ledger does not match its operation pointer.");
                    snapshots.Add(new ManagedMetadataSnapshot(operation + "/ledger.json", ledger));
                }
                foreach (var file in release.Package.Files) { token.ThrowIfCancellationRequested(); CheckedFilePath(root, file.Path); }
                if (!pending) RequireNoPending(root, component);
                CheckSnapshots(root, snapshots, token);
                return new ManagedInstallation(root, Path.Combine(root, operation.Replace('/', Path.DirectorySeparatorChar)),
                    record, channel, release, descriptor, pointer, pending, snapshots);
            }
        }

        private static OperationRecord ParseRecord(Dictionary<string, object> value, ManagedPackage package, bool pending)
        {
            ReleaseJson.Fields(value, "Schema Id ManifestSha256 State Files");
            ReleaseJson.Integer(value, "Schema", 1, 1);
            string state = ReleaseJson.String(value, "State", 16), id = ReleaseJson.String(value, "Id", 32);
            Guid guid;
            if (!Regex.IsMatch(id, @"\A[0-9a-f]{32}\z") || !Guid.TryParseExact(id, "N", out guid) || guid == Guid.Empty ||
                !(pending ? new[] { "preparing", "applying", "verified" } : new[] { "verified" }).Contains(state) ||
                ReleaseJson.Hash(value, "ManifestSha256") != package.ManifestSha256)
                throw new InvalidDataException("Invalid state or package identity in the operation record.");
            var files = ReleaseJson.Array(value, "Files", package.Files.Count, package.Files.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var record = new OperationRecord { Schema = 1, Id = id, State = state, ManifestSha256 = package.ManifestSha256, Files = new List<OperationFileRecord>() };
            foreach (object item in files) {
                var file = ReleaseJson.Object(item); ReleaseJson.Fields(file, "Path BeforeSize BeforeHash Existed Register");
                string path = ReleaseJson.String(file, "Path", 180); InstallationPaths.Relative(path);
                PackageFile expected = package.Files.SingleOrDefault(entry => entry.Path == path);
                if (expected == null || !seen.Add(path)) throw new InvalidDataException("Operation files differ from the verified inventory.");
                long size = ReleaseJson.Integer(file, "BeforeSize", 0, Int64.MaxValue);
                bool existed = ReleaseJson.Boolean(file, "Existed"), registered = ReleaseJson.Boolean(file, "Register");
                string hash = existed ? ReleaseJson.Hash(file, "BeforeHash") : null;
                if ((!existed && (file["BeforeHash"] != null || size != 0)) ||
                    (registered && (!existed || size != expected.Size || hash != expected.Sha256)))
                    throw new InvalidDataException("Invalid original-file or registration identity.");
                record.Files.Add(new OperationFileRecord { Path = path, BeforeSize = size, BeforeHash = hash, Existed = existed, Register = registered });
            }
            return record;
        }

        internal static OperationRecord CopyRecord(OperationRecord record)
        { return new OperationRecord { Schema = record.Schema, Id = record.Id, State = record.State, ManifestSha256 = record.ManifestSha256,
            Files = record.Files.Select(file => new OperationFileRecord { Path = file.Path, BeforeSize = file.BeforeSize, BeforeHash = file.BeforeHash, Existed = file.Existed, Register = file.Register }).ToList() }; }

        internal static string CheckedFilePath(string root, string relative)
        { string path = InstallationPaths.Under(root, relative); CheckAncestors(path); return path; }

        private static FileAttributes? Attributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        private static void CheckAncestors(string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current)) {
                FileAttributes? attributes = Attributes(current);
                if (attributes != null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Managed metadata cannot traverse a reparse point.");
            }
        }
        internal static void RequireNoPending(string root, string component = "game")
        {
            string path = CheckedFilePath(root, MetadataScope.For(component).Directory + "/active.json");
            if (Attributes(path) != null) throw new InvalidDataException("An unfinished operation requires recovery before Play.");
        }
        private static byte[] ReadSnapshot(string root, string relative, int maximum, List<ManagedMetadataSnapshot> snapshots, CancellationToken token)
        { byte[] bytes = ReadBytes(root, relative, maximum, token, false); snapshots.Add(new ManagedMetadataSnapshot(relative, bytes)); return bytes; }
        internal static byte[] ReadBytes(string root, string relative, int maximum, CancellationToken token, bool optional)
        {
            token.ThrowIfCancellationRequested(); string path = CheckedFilePath(root, relative);
            if (optional && Attributes(path) == null) return null;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (input.Length == 0 || input.Length > maximum) throw new InvalidDataException("Managed metadata exceeds its byte limit: " + relative);
                var bytes = new byte[(int)input.Length]; int position = 0;
                while (position < bytes.Length) {
                    token.ThrowIfCancellationRequested(); int count = input.Read(bytes, position, Math.Min(65536, bytes.Length - position));
                    if (count == 0) throw new IOException("Managed metadata changed while reading: " + relative); position += count;
                }
                if (input.ReadByte() != -1) throw new IOException("Managed metadata changed while reading: " + relative);
                CheckedFilePath(root, relative); return bytes;
            }
        }
        internal static void CheckSnapshots(string root, List<ManagedMetadataSnapshot> snapshots, CancellationToken token)
        {
            foreach (var item in snapshots) {
                byte[] current = ReadBytes(root, item.Path, item.Bytes.Length, token, false);
                if (!current.SequenceEqual(item.Bytes)) throw new IOException("Managed metadata changed during inspection: " + item.Path);
            }
        }
    }
}
