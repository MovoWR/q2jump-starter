// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    // These data-only contracts are internal. The release verifier must authenticate a
    // package before exposing its plan in the UI; local ledgers never establish trust.
    internal sealed class PackageFile
    {
        internal readonly string Path, Sha256, Role;
        internal readonly long Size;
        internal PackageFile(string path, long size, string sha256, string role)
        {
            InstallationPaths.Relative(path);
            if (size <= 0 || size > 512L * 1024 * 1024 || !InstallationPaths.IsHash(sha256))
                throw new InvalidDataException("Invalid package file identity.");
            if (!new[] { "client", "launcher", "helper", "runtime", "resource", "notice", "source" }.Contains(role))
                throw new InvalidDataException("Unsupported package file role.");
            Path = path; Size = size; Sha256 = sha256; Role = role;
        }
    }

    internal enum ManagedPackageAuthority { PublisherSigned, GithubLatest }

    internal sealed class ManagedPackage
    {
        internal readonly string ReleaseId, Component, ManifestSha256;
        internal readonly ManagedPackageAuthority Authority;
        internal readonly ReadOnlyCollection<PackageFile> Files;
        private readonly byte[] manifest, signature;
        internal byte[] Manifest { get { return (byte[])manifest.Clone(); } }
        internal byte[] Signature { get { return (byte[])signature.Clone(); } }
        internal ManagedPackage(string releaseId, string component, IEnumerable<PackageFile> files,
            byte[] manifestBytes, byte[] signatureBytes)
            : this(releaseId, component, files, manifestBytes, signatureBytes, ManagedPackageAuthority.PublisherSigned) { }
        internal static ManagedPackage FromGithubLatest(string releaseId, IEnumerable<PackageFile> files, byte[] manifestBytes)
        { return new ManagedPackage(releaseId, "game", files, manifestBytes, new byte[0], ManagedPackageAuthority.GithubLatest); }
        private ManagedPackage(string releaseId, string component, IEnumerable<PackageFile> files,
            byte[] manifestBytes, byte[] signatureBytes, ManagedPackageAuthority authority)
        {
            if (String.IsNullOrEmpty(releaseId) || !Regex.IsMatch(releaseId, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}\z") ||
                (component != "game" && component != "launcher") || new[] { "latest", "stable", "dev", "q2pro_race" }.Contains(releaseId.ToLowerInvariant())) throw new InvalidDataException("Invalid package identity.");
            if (manifestBytes == null || manifestBytes.Length == 0 || manifestBytes.Length > 1024 * 1024 || signatureBytes == null ||
                (authority == ManagedPackageAuthority.PublisherSigned ? signatureBytes.Length == 0 || signatureBytes.Length > 8192 :
                    component != "game" || signatureBytes.Length != 0))
                throw new InvalidDataException("Missing, oversized or inconsistent release evidence.");
            var inventory = files.ToList();
            if (inventory.Count == 0 || inventory.Count > 4096 || inventory.Sum(f => f.Size) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Invalid package inventory size.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in inventory)
            {
                InstallationPaths.ProgramFile(component, file);
                if (!names.Add(file.Path)) throw new InvalidDataException("Duplicate or case-colliding package path.");
            }
            foreach (string name in names)
            {
                string parent = name;
                while (parent.Contains("/"))
                { parent = parent.Substring(0, parent.LastIndexOf('/')); if (names.Contains(parent)) throw new InvalidDataException("File/directory collision."); }
            }
            string executable = component == "game" ? "q2pro_race.exe" : "Q2JUMP-Launcher.exe";
            if (!inventory.Any(f => f.Path == executable && f.Role == (component == "game" ? "client" : "launcher"))) throw new InvalidDataException("Missing component executable.");
            string[] requiredRoles = authority == ManagedPackageAuthority.GithubLatest ? new[] { "client", "resource" } :
                component == "game" ? new[] { "client", "resource", "notice", "source" } : new[] { "launcher", "helper", "notice", "source" };
            if (requiredRoles.Any(role => !inventory.Any(file => file.Role == role))) throw new InvalidDataException("Incomplete program component roles.");
            ReleaseId = releaseId; Component = component; Authority = authority; Files = inventory.AsReadOnly();
            manifest = (byte[])manifestBytes.Clone(); signature = (byte[])signatureBytes.Clone();
            using (var input = new MemoryStream(manifest)) ManifestSha256 = ContentService.Hash(input, CancellationToken.None);
        }
    }

    // Only these fixed namespaces can hold managed transaction evidence. The
    // authenticated package chooses its component; paths never come from a journal.

    internal sealed class MetadataScope
    {
        internal readonly string Component, Directory, OwnerText;
        private MetadataScope(string component, string directory, string owner)
        { Component = component; Directory = directory; OwnerText = owner; }
        internal static MetadataScope For(string component)
        {
            if (component == "game") return new MetadataScope(component, ".q2jump", "Q2JUMP_MANAGED_FILES_V1\n");
            if (component == "launcher") return new MetadataScope(component, ".q2jump-launcher", "Q2JUMP_LAUNCHER_MANAGED_FILES_V1\n");
            throw new InvalidDataException("Unknown managed component.");
        }
    }

    internal static class InstallationPaths
    {
        internal const string MetadataDirectory = ".q2jump";
        internal static bool IsHash(string value) { return value != null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z"); }
        internal static void Relative(string relative, int maximum = 180)
        {
            if (String.IsNullOrEmpty(relative) || relative.Length > maximum || !Regex.IsMatch(relative, @"\A[A-Za-z0-9_./ -]+\z") ||
                relative.Contains('\\') || relative.Contains(':') || relative.StartsWith("/")) throw new InvalidDataException("Unsupported package path.");
            foreach (var part in relative.Split('/'))
                if (part == "" || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") ||
                    part.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 ||
                    Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Unsafe package path.");
        }
        internal static void ProgramFile(string component, PackageFile file)
        {
            MetadataScope.For(component);
            string path = file.Path, lower = path.ToLowerInvariant();
            bool launcherNamespace = lower == "licenses/launcher" || lower.StartsWith("licenses/launcher/") || lower == "source/launcher" || lower.StartsWith("source/launcher/");
            bool valid = file.Role == "notice" ? lower.StartsWith("licenses/") && Regex.IsMatch(lower, @"\.(txt|md|html)$") : file.Role == "source" ? lower.StartsWith("source/") && Regex.IsMatch(lower, @"\.(txt|md|json|c|h|cs|ps1|py|iss|yml|yaml|in|build)$") :
                file.Role == "client" ? component == "game" && path == "q2pro_race.exe" :
                file.Role == "launcher" ? component == "launcher" && path == "Q2JUMP-Launcher.exe" :
                file.Role == "helper" ? component == "launcher" && path == "Q2JUMP-Update.exe" :
                file.Role == "runtime" ? component == "game" && !path.Contains('/') && lower.EndsWith(".dll") :
                file.Role == "resource" && lower.StartsWith(component == "game" ? "jump/" : "assets/") &&
                    Regex.IsMatch(lower, @"\.(png|pcx|tga|jpg|jpeg|wal|wav|ogg|md2|md3|sp2|font|ttf|txt)$");
            if (!valid || (component == "game" && launcherNamespace) ||
                (component == "launcher" && (file.Role == "notice" || file.Role == "source") && !launcherNamespace) ||
                lower.Split('/').Any(part => part == MetadataDirectory || part == ".q2jump-launcher") || lower.StartsWith("baseq2/") ||
                (file.Role != "source" && Regex.IsMatch(lower, @"\.(cfg|bat|cmd|ps1|pak|pkz|zip)$")) ||
                (file.Role == "resource" && (Regex.IsMatch(lower, @"\.(exe|dll|com|scr|msi|vbs|js|lnk)$") ||
                    lower.Split('/').Any(p => new[] { "maps", "textures", "demos", "screenshots", "save" }.Contains(p)))))
                throw new InvalidDataException("Package cannot manage this player or program path: " + path);
        }
        internal static string Root(string root)
        {
            if (String.IsNullOrWhiteSpace(root) || root.StartsWith("\\") || root.IndexOfAny(new[] { '"', '\0', '\r', '\n' }) >= 0 || !Path.IsPathRooted(root))
                throw new InvalidDataException("Choose an ordinary local game folder.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (root.Length < 4 || root.Length > 180 || !Directory.Exists(root)) throw new InvalidDataException("Choose an existing local folder with a supported path length.");
            ContentService.SafePath(root, "q2jump-path-check");
            return root;
        }
        internal static string Under(string root, string relative)
        {
            Relative(relative, 240);
            string path = ContentService.SafePath(root, relative);
            if (path.Length > 240) throw new InvalidDataException("The selected folder makes a required path too long.");
            string parent = Path.GetDirectoryName(path);
            while (parent != null && parent.Length > root.Length)
            { if (File.Exists(parent)) throw new InvalidDataException("A file occupies a required directory."); parent = Path.GetDirectoryName(parent); }
            if (Directory.Exists(path)) throw new InvalidDataException("A directory occupies a required file path.");
            return path;
        }
        internal static void Probe(string root)
        {
            root = Root(root);
            string probe = Under(root, ".q2jump-write-test-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            { stream.WriteByte(0); stream.Flush(true); }
        }
    }

    internal sealed class FileObservation
    {
        internal readonly bool Exists;
        internal readonly long Size;
        internal readonly string Sha256;
        private FileObservation(bool exists, long size, string hash) { Exists = exists; Size = size; Sha256 = hash; }
        internal static FileObservation Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return new FileObservation(false, 0, null);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { return new FileObservation(true, stream.Length, ContentService.Hash(stream, token)); }
        }
        internal bool Matches(FileObservation other) { return Exists == other.Exists && Size == other.Size && Sha256 == other.Sha256; }
        internal bool Matches(PackageFile file) { return Exists && Size == file.Size && Sha256 == file.Sha256; }
    }

    internal enum FileDisposition { Add, Replace, Register }

    internal sealed class PlannedFile
    {
        internal readonly PackageFile File;
        internal readonly FileObservation Before;
        internal readonly FileDisposition Disposition;
        internal PlannedFile(PackageFile file, FileObservation before)
        { File = file; Before = before; Disposition = !before.Exists ? FileDisposition.Add : before.Matches(file) ? FileDisposition.Register : FileDisposition.Replace; }
    }

    internal sealed class InstallationPlan
    {
        internal readonly string Root;
        internal readonly ManagedPackage Package;
        internal readonly ReadOnlyCollection<PlannedFile> Files;
        internal readonly long RequiredBytes;
        private InstallationPlan(string root, ManagedPackage package, List<PlannedFile> files)
        { Root = root; Package = package; Files = files.AsReadOnly(); RequiredBytes = checked(files.Sum(f => f.File.Size + (f.Disposition == FileDisposition.Replace ? f.Before.Size : 0)) + 4 * 1024 * 1024); }
        internal static InstallationPlan Inspect(string root, ManagedPackage package, CancellationToken token)
        {
            root = InstallationPaths.Root(root);
            var scope = MetadataScope.For(package.Component);
            InstallationTransaction.CheckMetadata(root, package.Component);
            var files = new List<PlannedFile>();
            foreach (var file in package.Files)
            {
                token.ThrowIfCancellationRequested();
                // Reserve enough path length for the longest internal backup path
                // before any download or metadata creation takes place.
                InstallationPaths.Under(root, scope.Directory + "/operations/" + new string('0', 32) + "/displaced/" + file.Path);
                files.Add(new PlannedFile(file, FileObservation.Read(InstallationPaths.Under(root, file.Path), token)));
            }
            return new InstallationPlan(root, package, files);
        }
        internal ApprovedInstallation Approve(bool replacements, bool registrations)
        {
            if ((!replacements && Files.Any(f => f.Disposition == FileDisposition.Replace)) ||
                (!registrations && Files.Any(f => f.Disposition == FileDisposition.Register)))
                throw new InvalidOperationException("Explicit approval is required for replacement and registration of existing files.");
            return new ApprovedInstallation(this);
        }
    }

    internal sealed class ApprovedInstallation
    {
        internal readonly InstallationPlan Plan;
        internal ApprovedInstallation(InstallationPlan plan) { Plan = plan; }
    }
    // Serializable records describe only files in the authenticated package supplied
    // by the caller. Recovery must never infer permission from a journal path alone.

    public sealed class OperationFileRecord
    {
        public string Path { get; set; }
        public long BeforeSize { get; set; }
        public string BeforeHash { get; set; }
        public bool Existed { get; set; }
        public bool Register { get; set; }
    }

    public sealed class OperationRecord
    {
        public int Schema { get; set; }
        public string Id { get; set; }
        public string ManifestSha256 { get; set; }
        public string State { get; set; }
        public List<OperationFileRecord> Files { get; set; }
    }

    internal sealed class OperationLock : IDisposable
    {
        private sealed class HeldLock
        {
            internal FileStream Stream;
            internal int Count;
        }
        [ThreadStatic] private static Dictionary<string, HeldLock> heldLocks;
        private readonly string root;
        private readonly Thread thread;
        private HeldLock held;
        internal OperationLock(string root)
        {
            this.root = InstallationPaths.Root(root); thread = Thread.CurrentThread;
            if (heldLocks == null) heldLocks = new Dictionary<string, HeldLock>(StringComparer.OrdinalIgnoreCase);
            if (heldLocks.TryGetValue(this.root, out held)) { held.Count++; return; }
            // Only a writer of this installation may reserve its lock. Keep nested
            // state verification/recovery on this thread within the same lease.
            string path = InstallationPaths.Under(this.root, ".q2jump-operation.lock");
            try {
                held = new HeldLock { Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1, FileOptions.DeleteOnClose), Count = 1 };
            } catch (IOException error) {
                throw new IOException("Another Q2JUMP operation is using this folder, or the reserved .q2jump-operation.lock path is occupied.", error);
            }
            heldLocks.Add(this.root, held);
        }
        public void Dispose()
        {
            if (held == null) return;
            if (Thread.CurrentThread != thread) throw new InvalidOperationException("Release the operation lock on its acquiring thread.");
            var released = held; held = null;
            if (--released.Count == 0) { heldLocks.Remove(root); released.Stream.Dispose(); }
        }
    }

    internal static class InstallationTransaction
    {
        private static bool ValidateMetadata(string root, MetadataScope scope, bool allowPending)
        {
            string directory = ContentService.SafePath(root, scope.Directory);
            if (File.Exists(directory)) throw new InvalidDataException("The reserved " + scope.Directory + " path is occupied.");
            if (!Directory.Exists(directory)) return false;
            string owner = InstallationPaths.Under(root, scope.Directory + "/owner");
            if (!File.Exists(owner) || new FileInfo(owner).Length != Encoding.UTF8.GetByteCount(scope.OwnerText) || File.ReadAllText(owner) != scope.OwnerText)
                throw new InvalidDataException("The " + scope.Directory + " folder is not recognized. Resolve the collision before installing.");
            if (!allowPending && File.Exists(InstallationPaths.Under(root, scope.Directory + "/active.json"))) throw new InvalidDataException("An unfinished operation requires recovery.");
            return true;
        }
        internal static void CheckMetadata(string root, string component = "game")
        { ValidateMetadata(InstallationPaths.Root(root), MetadataScope.For(component), false); }
        // Caller holds the same root OperationLock used by all components. Publish
        // ownership atomically, without adopting a concurrently created directory.
        internal static void EnsureMetadata(string root, string component = "game")
        {
            root = InstallationPaths.Root(root); var scope = MetadataScope.For(component);
            if (ValidateMetadata(root, scope, false)) return;
            string temporary = scope.Directory + ".initialize-" + Guid.NewGuid().ToString("N");
            WriteNew(InstallationPaths.Under(root, temporary + "/owner"), Encoding.UTF8.GetBytes(scope.OwnerText));
            Directory.Move(ContentService.SafePath(root, temporary), ContentService.SafePath(root, scope.Directory));
            ValidateMetadata(root, scope, false);
        }
        private static void WriteNew(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        private static void PublishRecord(string root, MetadataScope scope, OperationRecord record)
        {
            string path = InstallationPaths.Under(root, scope.Directory + "/active.json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            WriteNew(temporary, Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(record)));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        private static void RequireMatch(string path, FileObservation expected, CancellationToken token)
        { if (!expected.Matches(FileObservation.Read(path, token))) throw new IOException("A file changed after preview: " + path); }
        private static void CopyVerified(string source, string target, long size, string hash, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; long copied = 0; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                { token.ThrowIfCancellationRequested(); copied += count; if (copied > size) throw new InvalidDataException("Staged file exceeds its declared size."); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            var actual = FileObservation.Read(target, token);
            if (actual.Size != size || actual.Sha256 != hash) throw new InvalidDataException("Staged file identity mismatch: " + target);
        }
        private static void MoveAside(string target, string retained, long size, string hash, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(retained));
            File.Move(target, retained); // Atomic capture, never overwrite another file.
            var captured = FileObservation.Read(retained, token);
            if (captured.Exists && captured.Size == size && captured.Sha256 == hash) return;
            // The path changed after the last observation. Restore only into an empty
            // name; if another writer has recreated it, preserve both copies.
            if (!File.Exists(target) && !Directory.Exists(target)) File.Move(retained, target);
            throw new IOException("A concurrent change stopped publication. Its bytes remain at the destination or " + retained);
        }
        private static void RequireSpace(InstallationPlan plan)
        { if (new DriveInfo(Path.GetPathRoot(plan.Root)).AvailableFreeSpace < plan.RequiredBytes) throw new IOException("Not enough space for verified staging and backups."); }
        private static void RequireCommitted(string root, ManagedPackage package, byte[] expected, CancellationToken token)
        {
            string pointer = InstallationPaths.Under(root, MetadataScope.For(package.Component).Directory + "/installed.json");
            using (var stream = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
                if (stream.Length != expected.Length || !reader.ReadBytes(expected.Length + 1).SequenceEqual(expected))
                    throw new IOException("The committed installation record changed during finalization.");
            foreach (var file in package.Files)
                if (!FileObservation.Read(InstallationPaths.Under(root, file.Path), token).Matches(file))
                    throw new IOException("Committed files changed during finalization: " + file.Path);
        }
        // The callback proves final game assets/config backups and process closure.
        // A failure preserves the journal. Recovery is an explicit separate action.
        internal static string Apply(ApprovedInstallation approval, string payloadRoot, CancellationToken token,
            Action verifyReady, Action requireProgramsClosed, Action<string> boundary = null, Action<string> prepareMetadata = null,
            Action<string, bool> finalizeMetadata = null)
        {
            var plan = approval.Plan; string root = InstallationPaths.Root(plan.Root); var scope = MetadataScope.For(plan.Package.Component);
            using (new OperationLock(root))
            {
                CheckMetadata(root, scope.Component); InstallationPaths.Probe(root); RequireSpace(plan);
                requireProgramsClosed(); token.ThrowIfCancellationRequested();
                foreach (var item in plan.Files) RequireMatch(InstallationPaths.Under(root, item.File.Path), item.Before, token);
                string id = Guid.NewGuid().ToString("N"), operation = scope.Directory + "/operations/" + id;
                EnsureMetadata(root, scope.Component);
                var record = new OperationRecord { Schema = 1, Id = id, ManifestSha256 = plan.Package.ManifestSha256, State = "preparing",
                    Files = plan.Files.Select(f => new OperationFileRecord { Path = f.File.Path, BeforeSize = f.Before.Size, BeforeHash = f.Before.Sha256, Existed = f.Before.Exists, Register = f.Disposition == FileDisposition.Register }).ToList() };
                WriteNew(InstallationPaths.Under(root, operation + "/manifest.json"), plan.Package.Manifest);
                if (plan.Package.Authority == ManagedPackageAuthority.PublisherSigned)
                    WriteNew(InstallationPaths.Under(root, operation + "/manifest.sig"), plan.Package.Signature);
                if (prepareMetadata != null) prepareMetadata(Path.Combine(root, operation.Replace('/', Path.DirectorySeparatorChar)));
                PublishRecord(root, scope, record); if (boundary != null) boundary("journal");
                foreach (var item in plan.Files.Where(f => f.Disposition != FileDisposition.Register))
                {
                    CopyVerified(InstallationPaths.Under(payloadRoot, item.File.Path), InstallationPaths.Under(root, operation + "/staged/" + item.File.Path), item.File.Size, item.File.Sha256, token);
                    if (boundary != null) boundary("staged:" + item.File.Path);
                }
                foreach (var item in plan.Files.Where(f => f.Disposition == FileDisposition.Replace))
                {
                    RequireMatch(InstallationPaths.Under(root, item.File.Path), item.Before, token);
                    CopyVerified(InstallationPaths.Under(root, item.File.Path), InstallationPaths.Under(root, operation + "/backups/" + item.File.Path), item.Before.Size, item.Before.Sha256, token);
                    if (boundary != null) boundary("backup:" + item.File.Path);
                }
                requireProgramsClosed();
                if (new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace < 4 * 1024 * 1024) throw new IOException("Not enough space to publish recovery metadata.");
                foreach (var item in plan.Files) RequireMatch(InstallationPaths.Under(root, item.File.Path), item.Before, token);
                record.State = "applying"; PublishRecord(root, scope, record); if (boundary != null) boundary("applying");
                foreach (var item in plan.Files.Where(f => f.Disposition != FileDisposition.Register).OrderBy(f => new[] { "client", "launcher", "helper" }.Contains(f.File.Role) ? 1 : 0))
                {
                    token.ThrowIfCancellationRequested(); requireProgramsClosed();
                    string target = InstallationPaths.Under(root, item.File.Path), staged = InstallationPaths.Under(root, operation + "/staged/" + item.File.Path);
                    RequireMatch(target, item.Before, token);
                    if (!FileObservation.Read(staged, token).Matches(item.File)) throw new InvalidDataException("Staging changed before replacement.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (boundary != null) boundary("publishing:" + item.File.Path);
                    if (item.Before.Exists)
                    {
                        MoveAside(target, InstallationPaths.Under(root, operation + "/displaced/" + item.File.Path), item.Before.Size, item.Before.Sha256, token);
                        if (boundary != null) boundary("displaced:" + item.File.Path);
                    }
                    File.Move(staged, target); // Fail rather than replace a concurrently created path.
                    if (boundary != null) boundary("written:" + item.File.Path);
                }
                foreach (var item in plan.Files)
                    if (!FileObservation.Read(InstallationPaths.Under(root, item.File.Path), token).Matches(item.File)) throw new IOException("Final file verification failed: " + item.File.Path);
                verifyReady(); token.ThrowIfCancellationRequested();
                record.State = "verified"; PublishRecord(root, scope, record); if (boundary != null) boundary("verified");
                // A versioned ledger preserves previous provenance. The pointer is only
                // advisory; future Play/repair must authenticate its referenced manifest.
                byte[] committedRecord = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(record));
                WriteNew(InstallationPaths.Under(root, operation + "/ledger.json"), committedRecord);
                string pointer = InstallationPaths.Under(root, scope.Directory + "/installed.json"), next = pointer + "." + id + ".tmp";
                WriteNew(next, committedRecord);
                if (File.Exists(pointer)) File.Replace(next, pointer, InstallationPaths.Under(root, operation + "/previous-ledger.json")); else File.Move(next, pointer);
                if (boundary != null) boundary("committed");
                if (finalizeMetadata != null) {
                    ReadRecovery(root, plan.Package, committedRecord);
                    RequireCommitted(root, plan.Package, committedRecord, CancellationToken.None);
                    finalizeMetadata(Path.Combine(root, operation.Replace('/', Path.DirectorySeparatorChar)), true);
                    ReadRecovery(root, plan.Package, committedRecord);
                    RequireCommitted(root, plan.Package, committedRecord, CancellationToken.None);
                }
                File.Delete(InstallationPaths.Under(root, scope.Directory + "/active.json"));
                return id;
            }
        }

        internal static OperationRecord ReadRecovery(string root, ManagedPackage package, byte[] expectedJournalBytes = null)
        { byte[] bytes; return ReadRecovery(root, package, expectedJournalBytes, out bytes); }
        private static OperationRecord ReadRecovery(string root, ManagedPackage package, byte[] expectedJournalBytes, out byte[] bytes)
        {
            root = InstallationPaths.Root(root); var scope = MetadataScope.For(package.Component);
            if (!ValidateMetadata(root, scope, true)) throw new InvalidDataException("Managed recovery metadata is missing.");
            string path = InstallationPaths.Under(root, scope.Directory + "/active.json");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream)) bytes = reader.ReadBytes(1024 * 1024 + 1);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Operation journal exceeds its limit.");
            if (expectedJournalBytes != null && !bytes.SequenceEqual(expectedJournalBytes)) throw new IOException("The authenticated recovery journal changed. Inspect the operation again before recovery.");
            var record = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 12 }.Deserialize<OperationRecord>(new UTF8Encoding(false, true).GetString(bytes));
            if (record == null || record.Schema != 1 || record.ManifestSha256 != package.ManifestSha256 || record.Id == null ||
                !Regex.IsMatch(record.Id, @"\A[0-9a-f]{32}\z") || !new[] { "preparing", "applying", "verified" }.Contains(record.State) ||
                record.Files == null || record.Files.Count != package.Files.Count) throw new InvalidDataException("Invalid operation journal.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in record.Files)
                if (item == null || !paths.Add(item.Path) || !package.Files.Any(f => f.Path == item.Path) || item.BeforeSize < 0 ||
                    (item.Existed ? !InstallationPaths.IsHash(item.BeforeHash) : item.BeforeHash != null || item.BeforeSize != 0) ||
                    (item.Register && (!item.Existed || !package.Files.Any(f => f.Path == item.Path && f.Size == item.BeforeSize && f.Sha256 == item.BeforeHash))))
                    throw new InvalidDataException("Journal entries do not match the authenticated package.");
            return record;
        }
        internal static void Recover(string root, ManagedPackage package, CancellationToken token, Action requireProgramsClosed, byte[] expectedJournalBytes = null,
            Action<string, bool> finalizeMetadata = null)
        {
            root = InstallationPaths.Root(root); var scope = MetadataScope.For(package.Component);
            using (new OperationLock(root))
            {
                requireProgramsClosed(); byte[] journalBytes; var record = ReadRecovery(root, package, expectedJournalBytes, out journalBytes); string operation = scope.Directory + "/operations/" + record.Id;
                // A committed pointer is conclusive only after signed payload verification.
                string pointer = InstallationPaths.Under(root, scope.Directory + "/installed.json");
                bool committed = File.Exists(pointer) && new FileInfo(pointer).Length == journalBytes.Length && File.ReadAllBytes(pointer).SequenceEqual(journalBytes) && record.State == "verified";
                if (committed)
                {
                    foreach (var file in package.Files)
                        if (!FileObservation.Read(InstallationPaths.Under(root, file.Path), token).Matches(file)) throw new IOException("Committed files changed; explicit repair is required.");
                }
                else
                {
                    // Preflight every path before restoring any of them. Never destroy
                    // edits made by a different writer since the operation stopped.
                    foreach (var item in record.Files.Where(f => !f.Register))
                    {
                        var current = FileObservation.Read(InstallationPaths.Under(root, item.Path), token);
                        var displaced = FileObservation.Read(InstallationPaths.Under(root, operation + "/displaced/" + item.Path), token);
                        if (displaced.Exists && (!item.Existed || displaced.Size != item.BeforeSize || displaced.Sha256 != item.BeforeHash))
                            throw new IOException("Displaced bytes contain an external change; preserve them for manual recovery: " + item.Path);
                        if (!current.Exists && item.Existed && !displaced.Exists)
                            throw new IOException("An original file disappeared without an operation capture: " + item.Path);
                        bool original = current.Exists == item.Existed && current.Size == item.BeforeSize && current.Sha256 == item.BeforeHash;
                        if (original) continue;
                        if (item.Existed && !displaced.Exists) throw new IOException("A required displaced original is missing: " + item.Path);
                        if (current.Exists && !current.Matches(package.Files.Single(f => f.Path == item.Path))) throw new IOException("Recovery stopped for an external change: " + item.Path);
                        if (item.Existed)
                        {
                            var backup = FileObservation.Read(InstallationPaths.Under(root, operation + "/backups/" + item.Path), token);
                            if (!backup.Exists || backup.Size != item.BeforeSize || backup.Sha256 != item.BeforeHash) throw new IOException("Required recovery backup is missing or changed: " + item.Path);
                        }
                    }
                    foreach (var item in record.Files.Where(f => !f.Register).Reverse())
                    {
                        token.ThrowIfCancellationRequested(); requireProgramsClosed();
                        string target = InstallationPaths.Under(root, item.Path); var current = FileObservation.Read(target, token);
                        if (current.Exists == item.Existed && current.Size == item.BeforeSize && current.Sha256 == item.BeforeHash) continue;
                        var expected = package.Files.Single(f => f.Path == item.Path);
                        if (current.Exists && !current.Matches(expected)) throw new IOException("File changed during recovery: " + item.Path);
                        if (current.Exists)
                            MoveAside(target, InstallationPaths.Under(root, operation + "/recovery-capture-" + Guid.NewGuid().ToString("N")), expected.Size, expected.Sha256, token);
                        if (item.Existed)
                        {
                            string restore = InstallationPaths.Under(root, operation + "/displaced/" + item.Path);
                            var original = FileObservation.Read(restore, token);
                            if (!original.Exists || original.Size != item.BeforeSize || original.Sha256 != item.BeforeHash)
                                throw new IOException("The displaced original changed during recovery: " + item.Path);
                            File.Move(restore, target); // Keep the independent backup; no extra disk reservation or overwrite.
                        }
                    }
                }
                if (!committed)
                    foreach (var item in record.Files)
                    {
                        var final = FileObservation.Read(InstallationPaths.Under(root, item.Path), token);
                        if (final.Exists != item.Existed || final.Size != item.BeforeSize || final.Sha256 != item.BeforeHash)
                            throw new IOException("A file changed before recovery completed: " + item.Path);
                    }
                if (finalizeMetadata != null) {
                    ReadRecovery(root, package, journalBytes);
                    if (committed) RequireCommitted(root, package, journalBytes, token);
                    finalizeMetadata(Path.Combine(root, operation.Replace('/', Path.DirectorySeparatorChar)), committed);
                    ReadRecovery(root, package, journalBytes);
                    if (committed) RequireCommitted(root, package, journalBytes, token);
                    else foreach (var item in record.Files) {
                        var final = FileObservation.Read(InstallationPaths.Under(root, item.Path), token);
                        if (final.Exists != item.Existed || final.Size != item.BeforeSize || final.Sha256 != item.BeforeHash)
                            throw new IOException("An original file changed during recovery finalization: " + item.Path);
                    }
                }
                string archived = InstallationPaths.Under(root, operation + "/closed-" + Guid.NewGuid().ToString("N") + ".json");
                File.Move(InstallationPaths.Under(root, scope.Directory + "/active.json"), archived);
            }
        }
    }
}
