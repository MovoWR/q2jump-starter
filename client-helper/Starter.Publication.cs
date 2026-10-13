// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    internal sealed class ConfigurationPublicationException : IOException
    {
        internal string BackupPath { get; private set; }
        internal ConfigurationPublicationException(string path, string backup, Exception cause)
            : base("Configuration publication occurred at " + path + ", but a concurrent change or verification failure was detected. " +
                (backup == null ? "No previous file existed in the preview, so no replacement backup was made. " : "The actual displaced configuration is retained at " + backup + ". ") +
                "Reload and review recovery before saving again. " + cause.Message, cause)
        { BackupPath = backup; }
    }

    internal static class ConfigurationPublication
    {
        private static bool Matches(string path, byte[] expected)
        {
            ContentService.SafePath(Path.GetDirectoryName(path), Path.GetFileName(path));
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (expected == null || stream.Length != expected.Length) return false;
                    var buffer = new byte[65536]; int offset = 0, count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (offset + count > expected.Length) return false;
                        for (int i = 0; i < count; i++) if (buffer[i] != expected[offset + i]) return false;
                        offset += count;
                    }
                    return offset == expected.Length;
                }
            }
            catch (FileNotFoundException) { return expected == null; }
            catch (DirectoryNotFoundException) { return expected == null; }
        }
        // Call under the existing per-config named mutex. Hooks are per-call offline
        // fault-injection seams; production callers leave them null.
        internal static string Publish(string path, byte[] expected, byte[] updated, Action<string> boundary = null)
        {
            path = ContentService.SafePath(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileName(path));
            if (updated == null || updated.Length > 4 * 1024 * 1024) throw new InvalidDataException("The configuration would exceed its size limit.");
            if (!Matches(path, expected)) throw new IOException("The configuration changed before publication. Reload before saving.");
            string temporary = path + ".starter-" + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = expected == null ? null : path + ".before-starter-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".bak";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(updated, 0, updated.Length); stream.Flush(true); }
                if (!Matches(path, expected)) throw new IOException("The configuration changed before publication. Reload before saving.");
                if (boundary != null) boundary("publishing");
                if (expected == null) File.Move(temporary, path); else File.Replace(temporary, path, backup);
                try
                {
                    if (boundary != null) boundary("published");
                    if (backup != null && !Matches(backup, expected)) throw new IOException("The captured backup differs from the approved preview.");
                    if (!Matches(path, updated)) throw new IOException("The published configuration changed before verification completed.");
                }
                catch (Exception error) { throw new ConfigurationPublicationException(path, backup, error); }
                return backup;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
