// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    internal sealed class GamePaths
    {
        internal readonly string Root, GameDirectory, Config, Client;
        internal static readonly string[] StartupConfigurations = (from directory in new[] { "baseq2", "jump" }
            from name in new[] { "default.cfg", "q2config.cfg", "autoexec.cfg", "postexec.cfg", "postinit.cfg" }
            select directory + "/" + name).ToArray();
        internal GamePaths(string root)
        {
            Root = Path.GetFullPath(root);
            GameDirectory = ContentService.SafePath(Root, "jump");
            Config = ContentService.SafePath(Root, "jump/q2config.cfg");
            Client = ContentService.SafePath(Root, "q2pro_race.exe");
        }
    }

    internal sealed class ConfigurationFileSnapshot
    {
        public string Path { get; set; }
        public bool Exists { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class ConfigurationSnapshot
    {
        public int Schema { get; set; }
        public string Root { get; set; }
        public string Directory { get; set; }
        public List<ConfigurationFileSnapshot> Files { get; set; }
    }

    internal static class ConfigurationBackups
    {
        internal const int MaximumConfigBytes = 4 * 1024 * 1024;
        internal static byte[] Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            path = ContentService.SafePath(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileName(path));
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var buffer = new MemoryStream())
                {
                    if (stream.Length > MaximumConfigBytes) throw new InvalidDataException("The configuration file is too large.");
                    var block = new byte[65536]; int length;
                    while ((length = stream.Read(block, 0, block.Length)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (buffer.Length + length > MaximumConfigBytes) throw new InvalidDataException("The configuration file is too large.");
                        buffer.Write(block, 0, length);
                    }
                    token.ThrowIfCancellationRequested(); return buffer.ToArray();
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        internal static string Hash(byte[] bytes)
        { using (var stream = new MemoryStream(bytes)) return ContentService.Hash(stream, CancellationToken.None); }

        // The caller owns and validates backupDirectory; this service never claims a game directory.
        internal static ConfigurationSnapshot Capture(GamePaths paths, string backupDirectory, CancellationToken token)
        {
            string directory = ContentService.SafePath(backupDirectory, "configuration-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            var snapshot = new ConfigurationSnapshot { Schema = 1, Root = paths.Root, Directory = directory, Files = new List<ConfigurationFileSnapshot>() };
            Directory.CreateDirectory(directory);
            foreach (string relative in GamePaths.StartupConfigurations)
            {
                byte[] bytes = Read(ContentService.SafePath(paths.Root, relative), token);
                var file = new ConfigurationFileSnapshot { Path = relative, Exists = bytes != null, Size = bytes == null ? 0 : bytes.Length, Sha256 = bytes == null ? null : Hash(bytes) };
                snapshot.Files.Add(file);
                if (bytes == null) continue;
                string destination = ContentService.SafePath(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
            }
            if (!IsCurrent(paths, snapshot, token)) throw new IOException("A startup configuration changed during backup. Retry before continuing.");
            byte[] record = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(snapshot));
            using (var stream = new FileStream(ContentService.SafePath(directory, "snapshot.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(record, 0, record.Length);
            return snapshot;
        }
        internal static bool IsCurrent(GamePaths paths, ConfigurationSnapshot snapshot, CancellationToken token)
        {
            if (snapshot == null) return false;
            if (snapshot.Schema != 1 || !String.Equals(Path.GetFullPath(snapshot.Root), paths.Root, StringComparison.OrdinalIgnoreCase) || snapshot.Files == null || snapshot.Files.Count != GamePaths.StartupConfigurations.Length)
                throw new InvalidDataException("The configuration backup record is invalid for this game folder.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool current = true;
            foreach (var file in snapshot.Files)
            {
                token.ThrowIfCancellationRequested();
                if (file == null || !GamePaths.StartupConfigurations.Contains(file.Path) || !seen.Add(file.Path) || file.Size < 0 || file.Size > MaximumConfigBytes ||
                    (file.Exists ? !Regex.IsMatch(file.Sha256 ?? "", @"\A[0-9a-f]{64}\z") : file.Size != 0 || file.Sha256 != null))
                    throw new InvalidDataException("The configuration backup entries are invalid.");
                if (file.Exists)
                {
                    byte[] backup = Read(ContentService.SafePath(snapshot.Directory, file.Path), token);
                    if (backup == null || backup.Length != file.Size || Hash(backup) != file.Sha256) throw new IOException("A configuration backup is missing or changed: " + file.Path);
                }
                byte[] bytes = Read(ContentService.SafePath(paths.Root, file.Path), token);
                if ((bytes != null) != file.Exists || (bytes != null && (bytes.Length != file.Size || Hash(bytes) != file.Sha256))) current = false;
            }
            return current;
        }
        internal static ConfigurationSnapshot EnsureCurrent(GamePaths paths, ConfigurationSnapshot previous, string backupDirectory, CancellationToken token)
        { return IsCurrent(paths, previous, token) ? previous : Capture(paths, backupDirectory, token); }
    }
}
