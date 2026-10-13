// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Q2JumpStarter
{
    internal static class ReleaseOrigins
    {
        internal const string ChannelUrl = "https://github.com/MovoWR/q2pro_race/releases/download/launcher-stable-channel/stable-channel.json";
        internal static Uri Initial(string value)
        {
            var uri = Https(value);
            if (uri.Host != "github.com" || uri.Query != "" || !Regex.IsMatch(uri.AbsolutePath,
                @"\A/MovoWR/q2pro_race/releases/download/[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z"))
                throw new InvalidDataException("Release downloads must use the designated public repository.");
            return uri;
        }
        internal static Uri Redirect(Uri previous, string location)
        {
            Uri next;
            if (String.IsNullOrEmpty(location) || !Uri.TryCreate(previous, location, out next)) throw new InvalidDataException("Invalid release redirect.");
            Https(next.AbsoluteUri);
            if (next.Host == "github.com") return Initial(next.AbsoluteUri);
            if (next.Host != "release-assets.githubusercontent.com" && next.Host != "objects.githubusercontent.com")
                throw new InvalidDataException("Release redirect left the allowed GitHub asset origins.");
            return next;
        }
        private static Uri Https(string value)
        {
            Uri uri;
            if (String.IsNullOrEmpty(value) || value.Length > 8192 || value.Any(c => Char.IsControl(c) || Char.IsWhiteSpace(c)) ||
                value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                uri.Port != 443 || uri.UserInfo != "" || uri.Fragment != "") throw new InvalidDataException("A plain HTTPS release URL is required.");
            return uri;
        }
    }

    internal sealed class DownloadReply : IDisposable
    {
        internal readonly int Status;
        internal readonly string Location;
        internal readonly long Length;
        internal readonly Stream Body;
        private readonly Action close;
        internal DownloadReply(int status, string location, long length, Stream body, Action close = null)
        { Status = status; Location = location; Length = length; Body = body; this.close = close; }
        public void Dispose() { try { if (Body != null) Body.Dispose(); } finally { if (close != null) close(); } }
    }

    internal sealed class ReleaseDownloadClient
    {
        private readonly Func<Uri, CancellationToken, DownloadReply> open;
        internal ReleaseDownloadClient(Func<Uri, CancellationToken, DownloadReply> open = null) { this.open = open ?? Open; }
        private static DownloadReply Open(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "Q2JUMP-Starter/1"; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false; request.AutomaticDecompression = DecompressionMethods.None;
            request.UseDefaultCredentials = false; request.Credentials = null;
            if (uri.Host == "api.github.com") {
                request.Accept = "application/vnd.github+json";
                request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            }
            var registration = token.Register(request.Abort);
            try
            {
                var response = (HttpWebResponse)request.GetResponse();
                return new DownloadReply((int)response.StatusCode, response.Headers[HttpResponseHeader.Location], response.ContentLength,
                    response.GetResponseStream(), () => { response.Dispose(); registration.Dispose(); });
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                int status = response == null ? 0 : (int)response.StatusCode;
                if (error.Response != null) error.Response.Dispose();
                registration.Dispose(); token.ThrowIfCancellationRequested();
                // HTTP failures follow the same path as synthetic replies. Never
                // expose localized framework errors or read an error response body.
                if (status != 0) return new DownloadReply(status, null, 0, null);
                throw;
            }
            catch { registration.Dispose(); token.ThrowIfCancellationRequested(); throw; }
        }
        internal long Download(string url, Stream output, long maximum, CancellationToken token, Action<long> progress = null)
        { return Transfer(ReleaseOrigins.Initial(url), url, output, maximum, token, progress, false); }
        internal byte[] GitHubMetadata(string url, int maximum, CancellationToken token)
        {
            if (url != GitHubGameRelease.ApiUrl && url != GitHubGameRelease.SourceApiUrl)
                throw new InvalidDataException("Only the designated latest-release metadata endpoints are allowed.");
            using (var buffer = new MemoryStream()) {
                Transfer(new Uri(url), url, buffer, maximum, token, null, true); return buffer.ToArray();
            }
        }
        private long Transfer(Uri uri, string url, Stream output, long maximum, CancellationToken token, Action<long> progress, bool metadata)
        {
            if (maximum <= 0 || maximum > 2L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException("maximum");
            for (int redirects = 0; ; redirects++)
            {
                token.ThrowIfCancellationRequested();
                using (var response = open(uri, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (new[] { 301, 302, 303, 307, 308 }.Contains(response.Status))
                    {
                        if (metadata) throw new InvalidDataException("Latest-release metadata cannot redirect to another endpoint.");
                        if (redirects >= 5) throw new IOException("Too many release redirects.");
                        uri = ReleaseOrigins.Redirect(uri, response.Location); continue;
                    }
                    if (response.Status == 404)
                    {
                        if (metadata) throw new IOException("The latest Q2PRO Race release metadata is unavailable (HTTP 404). Try again later.");
                        if (url == ReleaseOrigins.ChannelUrl)
                            throw new IOException("Approved stable releases are unavailable (HTTP 404). Try again later.");
                        if (url.EndsWith(".sig.json", StringComparison.Ordinal))
                            throw new IOException("The release signature is unavailable (HTTP 404). This release cannot be verified.");
                        throw new IOException("A required release file is unavailable (HTTP 404). Try again later.");
                    }
                    if (response.Status != 200) throw new IOException("Release download returned HTTP " + response.Status + ".");
                    if (response.Length > maximum) throw new InvalidDataException("Release download exceeds its declared size.");
                    long copied = CopyBounded(response.Body, output, maximum, token, progress);
                    if (response.Length >= 0 && copied != response.Length) throw new EndOfStreamException("Release download ended before its advertised length.");
                    return copied;
                }
            }
        }
        internal byte[] Metadata(string url, int maximum, CancellationToken token)
        {
            using (var buffer = new MemoryStream()) { Download(url, buffer, maximum, token); return buffer.ToArray(); }
        }
        internal static long CopyBounded(Stream input, Stream output, long maximum, CancellationToken token, Action<long> progress = null)
        {
            if (input == null) throw new IOException("The release response has no body.");
            var buffer = new byte[65536]; long total = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested(); int count;
                try { count = input.Read(buffer, 0, buffer.Length); }
                catch (IOException) { token.ThrowIfCancellationRequested(); throw; }
                catch (WebException) { token.ThrowIfCancellationRequested(); throw; }
                token.ThrowIfCancellationRequested();
                if (count == 0) break;
                if (count > maximum - total) throw new InvalidDataException("Release download exceeds its declared size.");
                output.Write(buffer, 0, count); total += count; if (progress != null) progress(total);
            }
            token.ThrowIfCancellationRequested(); return total;
        }
    }

    // ZIP framing is checked before any extraction. Framework decompression handles
    // only the allowlisted stored/DEFLATE entries; output is independently hashed.

    internal static class PackageExtractor
    {
        private sealed class ZipFileRecord
        {
            internal string Name;
            internal long Offset, End, Compressed, Size;
            internal ushort Method, Flags;
            internal uint Crc;
        }
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            { uint value = i; for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u); table[i] = value; }
            return table;
        }
        private static byte[] Exact(BinaryReader reader, int size)
        { byte[] bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException("Truncated package ZIP."); return bytes; }
        private static string Name(BinaryReader reader, int length)
        {
            if (length <= 0 || length > 180) throw new InvalidDataException("Invalid package ZIP path length.");
            byte[] bytes = Exact(reader, length);
            if (bytes.Any(value => value > 127)) throw new InvalidDataException("Package ZIP names must use portable ASCII.");
            string name = Encoding.ASCII.GetString(bytes); InstallationPaths.Relative(name); return name;
        }
        private static Dictionary<string, ZipFileRecord> Inventory(Stream stream, ManagedPackage package, CancellationToken token)
        {
            if (stream.Length < 22) throw new InvalidDataException("Truncated package ZIP.");
            var reader = new BinaryReader(stream, Encoding.UTF8, true);
            int tailLength = (int)Math.Min(stream.Length, 65557); stream.Position = stream.Length - tailLength;
            byte[] tail = Exact(reader, tailLength); int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                if (BitConverter.ToUInt32(tail, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(tail, i + 20) == tail.Length) { end = i; break; }
            }
            if (end < 0 || BitConverter.ToUInt16(tail, end + 4) != 0 || BitConverter.ToUInt16(tail, end + 6) != 0)
                throw new InvalidDataException("Missing or multi-disk package directory.");
            int count = BitConverter.ToUInt16(tail, end + 10);
            long directorySize = BitConverter.ToUInt32(tail, end + 12), directoryOffset = BitConverter.ToUInt32(tail, end + 16);
            long endOffset = stream.Length - tailLength + end;
            if (count != package.Files.Count || BitConverter.ToUInt16(tail, end + 8) != count ||
                directorySize > 16 * 1024 * 1024 || directoryOffset + directorySize != endOffset)
                throw new InvalidDataException("Unsupported or inconsistent package directory.");
            stream.Position = directoryOffset;
            var records = new Dictionary<string, ZipFileRecord>(StringComparer.Ordinal);
            foreach (var ignored in package.Files)
            {
                token.ThrowIfCancellationRequested();
                if (reader.ReadUInt32() != 0x02014b50) throw new InvalidDataException("Invalid package directory entry.");
                reader.ReadUInt16(); ushort needed = reader.ReadUInt16(), flags = reader.ReadUInt16(), method = reader.ReadUInt16();
                reader.ReadUInt32(); uint crc = reader.ReadUInt32(), compressed = reader.ReadUInt32(), size = reader.ReadUInt32();
                ushort nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16(), commentLength = reader.ReadUInt16(), disk = reader.ReadUInt16();
                reader.ReadUInt16(); uint attributes = reader.ReadUInt32(), offset = reader.ReadUInt32();
                if (needed > 20 || (flags & ~0x0808) != 0 || (method != 0 && method != 8) || disk != 0 || extraLength > 4096 || commentLength > 4096 ||
                    (attributes & 0x410) != 0 || ((attributes >> 16) & 0xf000) != 0 && ((attributes >> 16) & 0xf000) != 0x8000)
                    throw new InvalidDataException("Unsupported package entry type, encryption or compression.");
                string name = Name(reader, nameLength); Exact(reader, extraLength); Exact(reader, commentLength);
                PackageFile expected = package.Files.SingleOrDefault(file => file.Path == name);
                if (expected == null || records.ContainsKey(name) || size != expected.Size || compressed > 2L * 1024 * 1024 * 1024 || (method == 0 && compressed != size))
                    throw new InvalidDataException("Package entry is not the exact declared regular file: " + name);
                records.Add(name, new ZipFileRecord { Name = name, Offset = offset, Compressed = compressed, Size = size, Method = method, Flags = flags, Crc = crc });
            }
            if (stream.Position != directoryOffset + directorySize) throw new InvalidDataException("Unexpected package directory data.");
            long previousEnd = 0;
            foreach (var item in records.Values.OrderBy(value => value.Offset))
            {
                token.ThrowIfCancellationRequested();
                if (item.Offset != previousEnd || item.Offset >= directoryOffset) throw new InvalidDataException("Overlapping entries or unexpected bytes in package.");
                stream.Position = item.Offset;
                if (reader.ReadUInt32() != 0x04034b50 || reader.ReadUInt16() > 20 || reader.ReadUInt16() != item.Flags || reader.ReadUInt16() != item.Method)
                    throw new InvalidDataException("Local package header disagrees with its directory.");
                reader.ReadUInt32(); uint crc = reader.ReadUInt32(), compressed = reader.ReadUInt32(), size = reader.ReadUInt32();
                ushort nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16();
                if (extraLength > 4096 || Name(reader, nameLength) != item.Name) throw new InvalidDataException("Local package name disagrees with its directory.");
                Exact(reader, extraLength);
                bool descriptor = (item.Flags & 8) != 0;
                if ((!descriptor && (crc != item.Crc || compressed != item.Compressed || size != item.Size)) ||
                    (descriptor && (crc != 0 && crc != item.Crc || compressed != 0 && compressed != item.Compressed || size != 0 && size != item.Size)))
                    throw new InvalidDataException("Local package lengths or CRC disagree.");
                item.End = stream.Position + item.Compressed;
                if (item.End > directoryOffset) throw new InvalidDataException("Package entry exceeds its data area.");
                if (descriptor)
                {
                    stream.Position = item.End; uint first = reader.ReadUInt32();
                    if (first == 0x08074b50) first = reader.ReadUInt32();
                    if (first != item.Crc || reader.ReadUInt32() != item.Compressed || reader.ReadUInt32() != item.Size)
                        throw new InvalidDataException("Invalid package data descriptor.");
                    item.End = stream.Position;
                }
                previousEnd = item.End;
            }
            if (previousEnd != directoryOffset) throw new InvalidDataException("Unexpected data before package directory.");
            return records;
        }
        internal static void Extract(string archivePath, string outputDirectory, ManagedPackage package,
            long archiveSize, string archiveHash, CancellationToken token)
        {
            if (archiveSize <= 0 || archiveSize > 2L * 1024 * 1024 * 1024 || !InstallationPaths.IsHash(archiveHash)) throw new InvalidDataException("Invalid frozen package identity.");
            archivePath = ContentService.SafePath(Path.GetDirectoryName(Path.GetFullPath(archivePath)), Path.GetFileName(archivePath));
            outputDirectory = ContentService.SafePath(Path.GetDirectoryName(Path.GetFullPath(outputDirectory)), Path.GetFileName(outputDirectory));
            if (File.Exists(outputDirectory) || Directory.Exists(outputDirectory)) throw new IOException("Choose a new private staging directory.");
            using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != archiveSize || ContentService.Hash(input, token) != archiveHash) throw new InvalidDataException("Downloaded package identity does not match the frozen release.");
                var records = Inventory(input, package, token); input.Position = 0;
                using (var zip = new ZipArchive(input, ZipArchiveMode.Read, true, Encoding.UTF8))
                {
                    // No owned directory is created until metadata and archive identity
                    // have both passed. Failed extraction keeps only private staging.
                    Directory.CreateDirectory(outputDirectory);
                    foreach (var file in package.Files)
                    {
                        token.ThrowIfCancellationRequested(); string target = InstallationPaths.Under(outputDirectory, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)); uint crc = 0xffffffff;
                        using (var contents = zip.GetEntry(file.Path).Open())
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[65536]; long copied = 0; int length;
                            while ((length = contents.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                token.ThrowIfCancellationRequested(); copied += length;
                                if (copied > file.Size) throw new InvalidDataException("Expanded package entry exceeds its declared size.");
                                for (int i = 0; i < length; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[i]) & 255];
                                output.Write(buffer, 0, length);
                            }
                            output.Flush(true);
                            if (copied != file.Size || (crc ^ 0xffffffff) != records[file.Path].Crc) throw new InvalidDataException("Expanded package entry length or CRC mismatch.");
                        }
                        if (!FileObservation.Read(target, token).Matches(file)) throw new InvalidDataException("Expanded package file SHA-256 mismatch.");
                    }
                    token.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
