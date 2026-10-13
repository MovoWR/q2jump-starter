// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    internal sealed class PreparedConfigurationLaunch
    {
        internal readonly string Root;
        internal readonly bool RequiresMark;
        private readonly byte[] descriptor;
        internal byte[] Descriptor { get { return (byte[])descriptor.Clone(); } }
        internal PreparedConfigurationLaunch(string root, byte[] bytes, bool requiresMark)
        { Root = root; descriptor = (byte[])bytes.Clone(); RequiresMark = requiresMark; }
    }

    // Call while holding the selected root's OperationLock. This service validates
    // ownership but never creates an owner marker or interprets startup commands.

    internal static class ManagedConfiguration
    {
        private const string DescriptorPath = ".q2jump/configuration.json";
        private const string BackupDirectory = ".q2jump/configurations";
        private const string Owner = "Q2JUMP_MANAGED_FILES_V1\n";
        private const int MaximumDescriptorBytes = 16384, MaximumSnapshotBytes = 65536;
        private const string SnapshotPattern = @"\Aconfiguration-[0-9]{8}-[0-9]{6}-[0-9]{7}-[0-9a-f]{32}\z";

        private sealed class State
        {
            internal byte[] Bytes;
            internal string SnapshotName, SnapshotHash;
            internal int SnapshotSize;
            internal bool Started;
            internal ConfigurationSnapshot Snapshot;
        }

        private static GamePaths CheckOwner(GamePaths paths, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var checkedPaths = new GamePaths(InstallationPaths.Root(paths.Root));
            byte[] owner = Read(checkedPaths.Root, ".q2jump/owner", 64, token);
            if (owner == null || !owner.SequenceEqual(Encoding.UTF8.GetBytes(Owner)))
                throw new InvalidDataException("Initial configuration backups require an already-owned .q2jump folder. Finish or recover the managed installation first.");
            string directory = ContentService.SafePath(checkedPaths.Root, BackupDirectory);
            if (File.Exists(directory)) throw new InvalidDataException("The configuration backup folder is occupied by a file. Preserve it and resolve the collision.");
            // Account for Capture's longest generated name and publication backups
            // before writing anything, rather than discovering a path limit halfway.
            string sample = "configuration-20000101-000000-0000000-" + new string('0', 32);
            foreach (string relative in GamePaths.StartupConfigurations)
                InstallationPaths.Under(checkedPaths.Root, BackupDirectory + "/" + sample + "/" + relative);
            InstallationPaths.Under(checkedPaths.Root, DescriptorPath + ".before-starter-20000101-000000-0000000-" + new string('0', 32) + ".bak");
            return checkedPaths;
        }

        private static byte[] Read(string root, string relative, int maximum, CancellationToken token)
        {
            string path = InstallationPaths.Under(root, relative);
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var result = new MemoryStream())
                {
                    if (stream.Length > maximum) throw new InvalidDataException("Initial configuration metadata exceeds its size limit: " + relative);
                    var buffer = new byte[4096]; int count;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested(); count = stream.Read(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        if (count > maximum - result.Length) throw new InvalidDataException("Initial configuration metadata exceeds its size limit: " + relative);
                        result.Write(buffer, 0, count);
                    }
                    token.ThrowIfCancellationRequested(); return result.ToArray();
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static ConfigurationSnapshot Snapshot(GamePaths paths, string name, int size, string hash, CancellationToken token)
        {
            string relativeDirectory = BackupDirectory + "/" + name;
            string directory = ContentService.SafePath(paths.Root, relativeDirectory);
            byte[] bytes = Read(paths.Root, relativeDirectory + "/snapshot.json", MaximumSnapshotBytes, token);
            if (bytes == null || bytes.Length != size || ConfigurationBackups.Hash(bytes) != hash)
                throw new InvalidDataException("The initial configuration snapshot record is missing or changed.");
            var record = ReleaseJson.Parse(bytes, MaximumSnapshotBytes);
            ReleaseJson.Fields(record, "Schema Root Directory Files");
            if (ReleaseJson.Integer(record, "Schema", 1, 1) != 1 ||
                !String.Equals(ReleaseJson.String(record, "Root", 240), paths.Root, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(ReleaseJson.String(record, "Directory", 240), directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The initial configuration snapshot points outside this game folder.");
            var files = new List<ConfigurationFileSnapshot>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in ReleaseJson.Array(record, "Files", GamePaths.StartupConfigurations.Length, GamePaths.StartupConfigurations.Length))
            {
                token.ThrowIfCancellationRequested(); var row = ReleaseJson.Object(value);
                ReleaseJson.Fields(row, "Path Exists Size Sha256");
                string path = ReleaseJson.String(row, "Path", 180);
                if (!GamePaths.StartupConfigurations.Contains(path) || !seen.Add(path)) throw new InvalidDataException("The initial configuration snapshot has unexpected or duplicate paths.");
                bool exists = ReleaseJson.Boolean(row, "Exists");
                long length = ReleaseJson.Integer(row, "Size", 0, ConfigurationBackups.MaximumConfigBytes);
                string digest = exists ? ReleaseJson.Hash(row, "Sha256") : null;
                if (!exists && (length != 0 || row["Sha256"] != null)) throw new InvalidDataException("An absent configuration has unexpected file metadata.");
                byte[] backup = ConfigurationBackups.Read(InstallationPaths.Under(paths.Root, relativeDirectory + "/" + path), token);
                if ((backup != null) != exists || (exists && (backup.Length != length || ConfigurationBackups.Hash(backup) != digest)))
                    throw new InvalidDataException("An initial configuration backup is missing or changed: " + path);
                files.Add(new ConfigurationFileSnapshot { Path = path, Exists = exists, Size = length, Sha256 = digest });
            }
            return new ConfigurationSnapshot { Schema = 1, Root = paths.Root, Directory = directory, Files = files };
        }

        private static State Load(GamePaths paths, bool required, CancellationToken token)
        {
            try
            {
                byte[] bytes = Read(paths.Root, DescriptorPath, MaximumDescriptorBytes, token);
                if (bytes == null)
                {
                    if (!required) return null;
                    throw new InvalidDataException("The initial configuration backup record is missing.");
                }
                var record = ReleaseJson.Parse(bytes, MaximumDescriptorBytes);
                ReleaseJson.Fields(record, "schema kind root snapshot snapshotSize snapshotSha256 firstManagedLaunchStarted");
                if (ReleaseJson.Integer(record, "schema", 1, 1) != 1 || ReleaseJson.String(record, "kind") != "initial-configuration" ||
                    !String.Equals(ReleaseJson.String(record, "root", 240), paths.Root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The initial configuration record belongs to a different game folder or schema.");
                string name = ReleaseJson.String(record, "snapshot", 96);
                if (!Regex.IsMatch(name, SnapshotPattern)) throw new InvalidDataException("The initial configuration snapshot reference is invalid.");
                int size = (int)ReleaseJson.Integer(record, "snapshotSize", 1, MaximumSnapshotBytes);
                string hash = ReleaseJson.Hash(record, "snapshotSha256");
                return new State { Bytes = bytes, SnapshotName = name, SnapshotSize = size, SnapshotHash = hash,
                    Started = ReleaseJson.Boolean(record, "firstManagedLaunchStarted"), Snapshot = Snapshot(paths, name, size, hash, token) };
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException error)
            { throw new InvalidDataException("Initial configuration backup validation failed. Preserve .q2jump/configurations and review " + DescriptorPath + " before continuing. " + error.Message, error); }
        }

        private static byte[] Encode(GamePaths paths, State state, bool started)
        {
            return new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "schema", 1 }, { "kind", "initial-configuration" }, { "root", paths.Root },
                { "snapshot", state.SnapshotName }, { "snapshotSize", state.SnapshotSize }, { "snapshotSha256", state.SnapshotHash },
                { "firstManagedLaunchStarted", started }
            }));
        }

        internal static void ValidateReadyBackup(GamePaths paths, CancellationToken token)
        { paths = CheckOwner(paths, token); Load(paths, true, token); }

        internal static ConfigurationSnapshot EnsureReadyBackup(GamePaths paths, CancellationToken token, Action<string> boundary = null)
        {
            paths = CheckOwner(paths, token); State previous = Load(paths, false, token);
            if (previous != null && previous.Started) return previous.Snapshot;
            ConfigurationSnapshot snapshot = ConfigurationBackups.EnsureCurrent(paths, previous == null ? null : previous.Snapshot,
                ContentService.SafePath(paths.Root, BackupDirectory), token);
            if (previous != null && Object.ReferenceEquals(snapshot, previous.Snapshot)) return snapshot;
            string name = Path.GetFileName(snapshot.Directory);
            if (!Regex.IsMatch(name, SnapshotPattern)) throw new InvalidDataException("The new configuration snapshot name is invalid.");
            byte[] record = Read(paths.Root, BackupDirectory + "/" + name + "/snapshot.json", MaximumSnapshotBytes, token);
            if (record == null) throw new IOException("The new configuration snapshot was not recorded.");
            var next = new State { Snapshot = snapshot, SnapshotName = name, SnapshotSize = record.Length, SnapshotHash = ConfigurationBackups.Hash(record) };
            Snapshot(paths, name, next.SnapshotSize, next.SnapshotHash, token);
            if (boundary != null) boundary("captured");
            token.ThrowIfCancellationRequested();
            ConfigurationPublication.Publish(InstallationPaths.Under(paths.Root, DescriptorPath), previous == null ? null : previous.Bytes,
                Encode(paths, next, false), boundary == null ? null : new Action<string>(point => boundary("descriptor-" + point)));
            State published = Load(paths, true, token);
            if (!ConfigurationBackups.IsCurrent(paths, published.Snapshot, token))
                throw new IOException("A startup configuration changed before its backup became ready. Retry backup preparation before launch.");
            return published.Snapshot;
        }

        internal static PreparedConfigurationLaunch PrepareFirstLaunch(GamePaths paths, CancellationToken token)
        {
            EnsureReadyBackup(paths, token); paths = CheckOwner(paths, token);
            State state = Load(paths, true, token);
            return new PreparedConfigurationLaunch(paths.Root, state.Bytes, !state.Started);
        }

        // Call only after successful Process.Start. Do not compare live configs here:
        // the newly started game may already have legitimately written its settings.
        internal static void MarkLaunchStarted(PreparedConfigurationLaunch preparation, CancellationToken token, Action<string> boundary = null)
        {
            if (preparation == null) throw new ArgumentNullException("preparation");
            var paths = CheckOwner(new GamePaths(preparation.Root), token); State state = Load(paths, true, token);
            if (!state.Bytes.SequenceEqual(preparation.Descriptor)) throw new IOException("Configuration launch preparation changed. Prepare the selected folder again before recording its launch.");
            if (state.Started) return;
            token.ThrowIfCancellationRequested();
            ConfigurationPublication.Publish(InstallationPaths.Under(paths.Root, DescriptorPath), state.Bytes, Encode(paths, state, true), boundary);
            Load(paths, true, token);
        }
    }
}
