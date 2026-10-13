// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Q2JumpStarter
{
    public enum GameDataState { Verified, Missing, Invalid, Unverified }

    // This contract describes source/build capabilities, never an arbitrary discovered EXE.

    public sealed class GameDataContract
    {
        public const string Version = "q2pro-jump-native-v1";
        public string SourceRevision { get; private set; }
        public bool SupportsPkz { get; private set; }
        private readonly string[] imageFormats;
        public string[] ImageFormats { get { return (string[])imageFormats.Clone(); } }

        public GameDataContract(string sourceRevision, bool png, bool jpg, bool tga, bool pkz)
        {
            if (!Regex.IsMatch(sourceRevision ?? "", "\\A[0-9a-fA-F]{40}\\z"))
                throw new ArgumentException("The asset contract requires a full source revision.", "sourceRevision");
            SourceRevision = sourceRevision;
            SupportsPkz = pkz;
            var formats = new List<string>();
            if (png) formats.Add("png");
            if (jpg) formats.Add("jpg");
            if (tga) formats.Add("tga");
            imageFormats = formats.ToArray();
        }
    }

    public sealed class GameDataAsset
    {
        public string Path { get; internal set; }
        public string Source { get; internal set; }
        public GameDataState State { get; internal set; }
        public string Detail { get; internal set; }
        public bool Required { get; internal set; }
    }

    public sealed class GameDataReport
    {
        private readonly List<GameDataAsset> assets = new List<GameDataAsset>();
        public IList<GameDataAsset> Assets { get { return assets.AsReadOnly(); } }
        public bool Verified { get; internal set; }
        public string ContractVersion { get { return GameDataContract.Version; } }
        public string SourceRevision { get; internal set; }
        public bool BaselineDownloadEnabled { get { return false; } }
        internal void Add(string path, string source, GameDataState state, string detail, bool required = true)
        {
            assets.Add(new GameDataAsset { Path = path, Source = source, State = state, Detail = detail, Required = required });
        }
    }

    // All observations are bounded and read-only. No config, client or original installer runs.

    public sealed class GameDataInspector
    {
        public const int MaximumEntries = 65536;
        public const int MaximumArchives = 256;
        public const int MaximumAssetBytes = 32 * 1024 * 1024;
        public const int MaximumConfigBytes = 1024 * 1024;
        private const long MaximumReadBytes = 256L * 1024 * 1024;
        private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
        private readonly List<Source> sources = new List<Source>();
        private readonly List<Source> looseSnapshots = new List<Source>();
        private readonly Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StagedResource> stagedResources = new Dictionary<string, StagedResource>(StringComparer.OrdinalIgnoreCase);
        private string stagedRoot;
        private IEnumerable<PackageFile> stagedFiles;
        private CancellationToken cancellation;
        private GameDataContract contract;
        private GameDataReport report;
        private long readBytes;
        private int entries;
        private int archives;

        private sealed class UnverifiedException : Exception
        {
            internal UnverifiedException(string message) : base(message) { }
        }

        private sealed class Entry
        {
            internal string Name;
            internal long Offset;
            internal int Size;
            internal int CompressedSize;
            internal int Compression;
            internal uint Crc;
        }

        private sealed class Source
        {
            internal string Path;
            internal string Game;
            internal bool Loose;
            internal long Length;
            internal DateTime Modified;
            internal Dictionary<string, Entry> Entries;
        }

        private sealed class StagedResource
        {
            internal PackageFile File;
            internal Source Snapshot;
        }

        private sealed class Found
        {
            internal byte[] Data;
            internal string Source;
        }

        // Product readiness only checks for a PAK filename. Never open custom PAKs,
        // interpret their entries, or read user configurations to admit game data.
        internal static GameDataReport CheckPakPresence(string root, GameDataContract contract, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = new GameDataReport { SourceRevision = contract.SourceRevision };
            root = InstallationPaths.Root(root); CheckPath(root);
            foreach (string game in new[] { "baseq2", "jump" }) {
                string folder = System.IO.Path.Combine(root, game); CheckPath(folder);
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)) {
                    cancellation.ThrowIfCancellationRequested();
                    if (!System.IO.Path.GetExtension(file).Equals(".pak", StringComparison.OrdinalIgnoreCase)) continue;
                    CheckPath(file);
                    result.Add(game + "/" + System.IO.Path.GetFileName(file), game, GameDataState.Verified,
                        "PAK file present. Contents were not inspected.");
                    result.Verified = true; return result;
                }
            }
            result.Add("baseq2 or jump", null, GameDataState.Missing, "Place a .pak file in baseq2 or jump, or run Starter to download demo data.");
            return result;
        }

        public static GameDataReport Inspect(string root, GameDataContract contract, CancellationToken cancellation)
        {
            if (contract == null) throw new ArgumentNullException("contract");
            var inspector = new GameDataInspector { contract = contract, cancellation = cancellation,
                report = new GameDataReport { SourceRevision = contract.SourceRevision } };
            inspector.Run(root);
            return inspector.report;
        }

        // The caller authenticates the release; only its exact resource inventory is overlaid.
        internal static GameDataReport Inspect(string root, GameDataContract contract, CancellationToken cancellation,
            string stagedRoot, IEnumerable<PackageFile> stagedFiles)
        {
            if (contract == null) throw new ArgumentNullException("contract");
            if (stagedRoot == null) throw new ArgumentNullException("stagedRoot");
            if (stagedFiles == null) throw new ArgumentNullException("stagedFiles");
            var inspector = new GameDataInspector { contract = contract, cancellation = cancellation,
                stagedRoot = stagedRoot, stagedFiles = stagedFiles,
                report = new GameDataReport { SourceRevision = contract.SourceRevision } };
            inspector.Run(root);
            return inspector.report;
        }

        private void PrepareOverlay()
        {
            if (stagedRoot == null) return;
            if (!System.IO.Path.IsPathRooted(stagedRoot) || stagedRoot.StartsWith("\\", StringComparison.Ordinal))
                throw new UnverifiedException("Staging must be an ordinary local absolute directory.");
            stagedRoot = System.IO.Path.GetFullPath(stagedRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            CheckPath(stagedRoot);
            if (!Directory.Exists(stagedRoot)) throw new UnverifiedException("The resource staging directory does not exist.");
            int count = 0;
            foreach (PackageFile file in stagedFiles) {
                cancellation.ThrowIfCancellationRequested();
                if (++count > 4096) throw new UnverifiedException("Staged inventory entry limit exceeded.");
                if (file == null) throw new UnverifiedException("The staged inventory contains a null file.");
                if (file.Role != "resource") continue;
                InstallationPaths.Relative(file.Path);
                InstallationPaths.ProgramFile("game", file);
                if (!file.Path.StartsWith("jump/", StringComparison.OrdinalIgnoreCase) || !ValidPath(file.Path))
                    throw new UnverifiedException("Only exact Jump resource paths may be overlaid.");
                if (file.Size > MaximumAssetBytes) throw new UnverifiedException("Staged resource exceeds the asset read limit.");
                string relative = file.Path.Substring(5);
                if (stagedResources.ContainsKey(relative)) throw new UnverifiedException("Duplicate or case-colliding staged resource path.");
                string full = InstallationPaths.Under(stagedRoot, file.Path);
                CheckPath(full);
                var info = new FileInfo(full);
                if (!info.Exists) throw new UnverifiedException("A declared staged resource is missing: " + file.Path);
                var resource = new StagedResource { File = file,
                    Snapshot = new Source { Path = full, Length = info.Length, Modified = info.LastWriteTimeUtc } };
                ReadStagedResource(resource);
                stagedResources.Add(relative, resource);
            }
        }

        private Found ReadStagedResource(StagedResource resource)
        {
            cancellation.ThrowIfCancellationRequested();
            CheckSnapshot(resource.Snapshot);
            byte[] data;
            using (var input = new FileStream(resource.Snapshot.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (input.Length != resource.File.Size) throw new UnverifiedException("Staged resource size changed: " + resource.File.Path);
                data = Read(input, (int)input.Length);
            }
            using (var input = new MemoryStream(data, false))
                if (ContentService.Hash(input, cancellation) != resource.File.Sha256)
                    throw new UnverifiedException("Staged resource hash does not match the package: " + resource.File.Path);
            CheckSnapshot(resource.Snapshot);
            return new Found { Data = data, Source = "staged:" + resource.File.Path };
        }

        private void Run(string root)
        {
            cancellation.ThrowIfCancellationRequested();
            try {
                root = System.IO.Path.GetFullPath(root);
                CheckPath(root);
                if (!Directory.Exists(root)) throw new UnverifiedException("The selected directory does not exist.");
                PrepareOverlay();
                // Native Windows launch: empty homedir; Jump overlays baseq2.
                AddDirectory(root, "jump");
                AddDirectory(root, "baseq2");
                if (File.Exists(System.IO.Path.Combine(root, "Q2Game.kpf")))
                    throw new UnverifiedException("Q2Game.kpf search paths are outside this classic-data contract.");
                ReadConfiguration();
                if (Setting("ui_external_menu", "0") != "0")
                    throw new UnverifiedException("External menu mode can change startup assets and requires separate validation.");
                CheckAsset("pics/colormap.pcx", false, true, 0);
                CheckAsset("pics/prochars.pcx", true, false, 1);
                CheckFont(Setting("con_font", "conchars"), "conchars");
                CheckFont(Setting("scr_font", "prochars"), "prochars");
                CheckAsset(Picture(Setting("con_background", "conback")), true, false, 0, false);
                string background = Setting("ui_menu_bar_image", "q2jump_background");
                if (background.Length != 0) CheckAsset(Picture(background), true, false, 0, false);
                string cursor = Setting("cl_menu_cursor", "ch1");
                if (cursor != "ch1") throw new UnverifiedException("A non-default menu cursor requires separate validation.");
                CheckAsset("pics/ch1.pcx", true, false, 0, false);
                for (int i = 0; i < 15; i++) CheckAsset("pics/m_cursor" + i.ToString(CultureInfo.InvariantCulture) + ".pcx", true, false, 0, false);
                foreach (string sound in new[] { "menu1", "menu2", "menu3", "talk1" })
                    CheckAsset("sound/misc/" + sound + ".wav", false, false, 0, false);
                if (Setting("ui_menu_model", "1") != "0") {
                    string skin = Setting("skin", "male/grunt").Replace('\\', '/');
                    string[] parts = skin.Split('/');
                    if (parts.Length != 2 || !ValidPath(skin)) throw new UnverifiedException("Unsupported player-preview skin path.");
                    CheckAsset("players/" + parts[0] + "/tris.md2", false, false, 0, false);
                    CheckAsset("players/" + skin + ".pcx", true, false, 2, false);
                }
                VerifySnapshots();
            } catch (OperationCanceledException) { throw; }
            catch (UnverifiedException error) { report.Add("inspection", null, GameDataState.Unverified, error.Message); }
            catch (Exception error) {
                if (!(error is IOException) && !(error is InvalidDataException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is System.Security.SecurityException)) throw;
                report.Add("inspection", null, GameDataState.Unverified, error.Message);
            }
            report.Verified = report.Assets.Count > 0;
            foreach (GameDataAsset asset in report.Assets) if (asset.Required && asset.State != GameDataState.Verified) report.Verified = false;
        }

        private static void CheckPath(string path)
        {
            for (string current = path; !String.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnverifiedException("Linked/reparse paths are not inspected: " + path);
            }
        }

        private void AddDirectory(string root, string game)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = System.IO.Path.Combine(root, game);
            CheckPath(path);
            if (!Directory.Exists(path)) {
                if (game == "jump" && stagedResources.Count != 0) sources.Add(new Source { Path = path, Game = game, Loose = true });
                return;
            }
            var packs = new List<string>();
            int count = 0;
            foreach (string file in Directory.EnumerateFileSystemEntries(path)) {
                cancellation.ThrowIfCancellationRequested();
                if (++count > MaximumEntries) throw new UnverifiedException("Directory entry limit exceeded.");
                string extension = System.IO.Path.GetExtension(file);
                if (!extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".pkz", StringComparison.OrdinalIgnoreCase)) continue;
                CheckPath(file);
                if (!File.Exists(file)) continue;
                if (extension.Equals(".pkz", StringComparison.OrdinalIgnoreCase) && !contract.SupportsPkz) continue;
                if (++archives > MaximumArchives) throw new UnverifiedException("Archive count limit exceeded.");
                string packName = System.IO.Path.GetFileName(file);
                if (packName.StartsWith("pak", StringComparison.OrdinalIgnoreCase)) { int ignored; PackNumber(packName, out ignored); }
                packs.Add(file);
            }
            packs.Sort(ComparePacks);
            for (int i = 1; i < packs.Count; i++) if (ComparePacks(packs[i - 1], packs[i]) == 0)
                throw new UnverifiedException("Ambiguous archive ordering requires separate validation.");
            for (int i = packs.Count - 1; i >= 0; i--) {
                cancellation.ThrowIfCancellationRequested();
                var info = new FileInfo(packs[i]);
                if (info.Length > MaximumArchiveBytes) throw new UnverifiedException("Archive size limit exceeded: " + info.Name);
                var source = new Source { Path = packs[i], Game = game, Length = info.Length, Modified = info.LastWriteTimeUtc,
                    Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase) };
                using (var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    try {
                        if (System.IO.Path.GetExtension(source.Path).Equals(".pkz", StringComparison.OrdinalIgnoreCase)) ReadZip(source, stream);
                        else ReadPak(source, stream);
                    } catch (InvalidDataException error) {
                        throw new UnverifiedException("Archive could not be verified: " + info.Name + ": " + error.Message);
                    }
                }
                sources.Add(source);
            }
            sources.Add(new Source { Path = path, Game = game, Loose = true });
        }

        // Match Windows strtoul-based pakcmp, then reverse the sorted load order.
        private static int ComparePacks(string left, string right)
        {
            string a = System.IO.Path.GetFileName(left), b = System.IO.Path.GetFileName(right);
            bool pa = a.StartsWith("pak", StringComparison.OrdinalIgnoreCase), pb = b.StartsWith("pak", StringComparison.OrdinalIgnoreCase);
            if (pa != pb) return pa ? -1 : 1;
            if (pa) {
                int ia, ib;
                uint na = PackNumber(a, out ia), nb = PackNumber(b, out ib);
                if (na != nb) return na < nb ? -1 : 1;
                return StringComparer.OrdinalIgnoreCase.Compare(a.Substring(ia), b.Substring(ib));
            }
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }

        private static uint PackNumber(string name, out int end)
        {
            end = 3;
            if (end < name.Length && (name[end] == '+' || name[end] == '-' || Char.IsWhiteSpace(name[end])))
                throw new UnverifiedException("Unusual numeric pack name requires separate validation.");
            ulong value = 0;
            while (end < name.Length && name[end] >= '0' && name[end] <= '9') {
                value = value * 10 + (uint)(name[end++] - '0');
                if (value > UInt32.MaxValue) throw new UnverifiedException("Numeric pack name exceeds supported ordering.");
            }
            return (uint)value;
        }

        private void ReadPak(Source source, Stream stream)
        {
            byte[] header = Read(stream, 12);
            Require(Text(header, 0, 4) == "PACK", "Bad PAK signature.");
            long offset = U32(header, 4), length = U32(header, 8);
            Require(length > 0 && length % 64 == 0 && offset >= 12 && offset + length <= stream.Length, "Bad PAK directory bounds.");
            if (length / 64 > MaximumEntries - entries) throw new UnverifiedException("Archive entry limit exceeded.");
            stream.Position = offset;
            for (int i = 0; i < length / 64; i++) {
                byte[] row = Read(stream, 64);
                int end = Array.IndexOf(row, (byte)0, 0, 56);
                if (end < 0) end = 56;
                string name = Text(row, 0, end).Replace('\\', '/');
                long start = U32(row, 56), size = U32(row, 60);
                Require(start <= Int32.MaxValue && size <= Int32.MaxValue && start + size <= stream.Length, "PAK entry exceeds archive bounds.");
                AddEntry(source, new Entry { Name = name, Offset = start, Size = (int)size, CompressedSize = (int)size, Compression = -1 });
            }
        }

        private void ReadZip(Source source, Stream stream)
        {
            int tailLength = (int)Math.Min(stream.Length, 65557);
            stream.Position = stream.Length - tailLength;
            byte[] tail = Read(stream, tailLength);
            int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--) {
                cancellation.ThrowIfCancellationRequested();
                if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
            }
            Require(end >= 0, "ZIP end record missing.");
            long endOffset = stream.Length - tailLength + end;
            int count = U16(tail, end + 10);
            long centralSize = U32(tail, end + 12), centralOffset = U32(tail, end + 16);
            Require(U16(tail, end + 4) == 0 && U16(tail, end + 6) == 0 && U16(tail, end + 8) == count, "Split ZIP is unsupported.");
            if (count == 65535 || centralSize == UInt32.MaxValue || centralOffset == UInt32.MaxValue)
                throw new UnverifiedException("ZIP64 is outside the bounded PKZ contract.");
            Require(count > 0 && centralOffset + centralSize == endOffset, "ZIP directory bounds or SFX prefix are unsupported.");
            if (count > MaximumEntries - entries) throw new UnverifiedException("Archive entry limit exceeded.");
            stream.Position = centralOffset;
            for (int i = 0; i < count; i++) {
                byte[] row = Read(stream, 46);
                Require(U32(row, 0) == 0x02014b50, "Bad ZIP central entry.");
                int flags = U16(row, 8), method = U16(row, 10), nameSize = U16(row, 28), extra = U16(row, 30), comment = U16(row, 32);
                Require((flags & ~0x080e) == 0 && (method == 0 || method == 8), "Encrypted or unsupported ZIP compression.");
                Require(nameSize > 0 && nameSize < 256 && U16(row, 34) == 0, "Unsupported ZIP entry name or disk.");
                byte[] nameBytes = Read(stream, nameSize);
                foreach (byte value in nameBytes) Require(value >= 32 && value < 127, "Non-ASCII ZIP entry names are unverified.");
                string name = Encoding.ASCII.GetString(nameBytes).Replace('\\', '/');
                Require(stream.Position + extra + comment <= endOffset, "Truncated ZIP entry metadata.");
                stream.Position += extra + comment;
                long size = U32(row, 24), compressed = U32(row, 20), offset = U32(row, 42);
                Require(size <= Int32.MaxValue && compressed <= Int32.MaxValue && offset + 30 <= centralOffset, "Unsupported ZIP entry bounds.");
                if (name.EndsWith("/", StringComparison.Ordinal)) {
                    Require(ValidPath(name.TrimEnd('/')), "Unsafe ZIP directory path.");
                    continue;
                }
                AddEntry(source, new Entry { Name = name, Offset = offset, Size = (int)size, CompressedSize = (int)compressed,
                    Compression = method, Crc = U32(row, 16) });
            }
            Require(stream.Position == endOffset, "ZIP directory size mismatch.");
        }

        private void AddEntry(Source source, Entry entry)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++entries > MaximumEntries) throw new UnverifiedException("Archive entry limit exceeded.");
            // Pack names are virtual paths. Like the engine's FS_NormalizePath,
            // resolve interior dot segments before lookup (the original demo uses
            // tank/../ctank). Keep Entry.Name unchanged for ZIP local-header checks.
            string name = NormalizeArchivePath(entry.Name);
            Require(ValidPath(name), "Unsupported archive path: " + entry.Name);
            Require(!source.Entries.ContainsKey(name), "Duplicate/case-colliding archive path: " + entry.Name);
            source.Entries.Add(name, entry);
        }

        private static string NormalizeArchivePath(string path)
        {
            Require(!String.IsNullOrEmpty(path) && path.Length < 256 && path[0] != '/', "Unsupported archive path: " + path);
            var parts = new List<string>();
            foreach (string part in path.Split('/')) {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") {
                    Require(parts.Count > 0, "Archive path escapes its root: " + path);
                    parts.RemoveAt(parts.Count - 1);
                } else parts.Add(part);
            }
            return String.Join("/", parts);
        }

        private static bool ValidPath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length >= 256 || path[0] == '/' || path.IndexOfAny(new[] { ':', '\\', '\0', '*', '?', '"', '<', '>', '|' }) >= 0) return false;
            foreach (string part in path.Split('/')) {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)) return false;
                foreach (char value in part) if (value < 32 || value >= 127) return false;
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || Regex.IsMatch(stem, "\\A(?:COM|LPT)[0-9]\\z")) return false;
            }
            return true;
        }

        private Found Find(string path, bool realOnly, string game, int maximum)
        {
            if (!ValidPath(path)) throw new UnverifiedException("Unsupported asset path: " + path);
            foreach (Source source in sources) {
                cancellation.ThrowIfCancellationRequested();
                if (game != null && source.Game != game) continue;
                if (source.Loose) {
                    string full = System.IO.Path.Combine(source.Path, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    CheckPath(full);
                    StagedResource staged;
                    if (!realOnly && source.Game == "jump" && stagedResources.TryGetValue(path, out staged))
                        return ReadStagedResource(staged);
                    if (!File.Exists(full)) continue;
                    var info = new FileInfo(full);
                    var snapshot = new Source { Path = full, Length = info.Length, Modified = info.LastWriteTimeUtc };
                    using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        if (stream.Length > maximum) throw new UnverifiedException("Asset exceeds inspection size limit: " + path);
                        byte[] data = Read(stream, (int)stream.Length);
                        CheckSnapshot(snapshot); looseSnapshots.Add(snapshot);
                        return new Found { Data = data, Source = source.Game + "/" + path };
                    }
                }
                if (realOnly) continue;
                Entry entry;
                if (!source.Entries.TryGetValue(path, out entry)) continue;
                if (entry.Size > maximum || entry.CompressedSize > MaximumAssetBytes)
                    throw new UnverifiedException("Asset exceeds inspection size limit: " + path);
                CheckSnapshot(source);
                using (var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    stream.Position = entry.Offset;
                    byte[] data;
                    if (entry.Compression < 0) data = Read(stream, entry.Size);
                    else {
                        byte[] header = Read(stream, 30);
                        int nameLength = U16(header, 26), extra = U16(header, 28);
                        Require(U32(header, 0) == 0x04034b50 && U16(header, 8) == entry.Compression && (U16(header, 6) & ~0x080e) == 0, "ZIP local header mismatch.");
                        Require(nameLength > 0 && nameLength < 256, "ZIP local name exceeds limit.");
                        string localName = Encoding.ASCII.GetString(Read(stream, nameLength)).Replace('\\', '/');
                        Require(localName == entry.Name && stream.Position + extra + entry.CompressedSize <= stream.Length, "ZIP local entry mismatch.");
                        stream.Position += extra;
                        byte[] compressed = Read(stream, entry.CompressedSize);
                        if (entry.Compression == 0) {
                            Require(entry.CompressedSize == entry.Size, "Stored ZIP size mismatch.");
                            data = compressed;
                        } else {
                            using (var input = new MemoryStream(compressed, false))
                            using (var deflate = new DeflateStream(input, CompressionMode.Decompress)) {
                                data = Read(deflate, entry.Size);
                                Require(deflate.ReadByte() == -1, "ZIP expansion exceeds declared size.");
                            }
                        }
                        Require(Crc32(data) == entry.Crc, "ZIP CRC mismatch.");
                    }
                    return new Found { Data = data, Source = source.Game + "/" + System.IO.Path.GetFileName(source.Path) + ":" + path };
                }
            }
            return null;
        }

        private void CheckSnapshot(Source source)
        {
            CheckPath(source.Path);
            var info = new FileInfo(source.Path);
            if (!info.Exists || info.Length != source.Length || info.LastWriteTimeUtc != source.Modified)
                throw new UnverifiedException("An archive changed during inspection: " + source.Path);
        }

        private void VerifySnapshots()
        {
            foreach (Source source in sources) { cancellation.ThrowIfCancellationRequested(); if (!source.Loose) CheckSnapshot(source); }
            foreach (Source source in looseSnapshots) { cancellation.ThrowIfCancellationRequested(); CheckSnapshot(source); }
            foreach (StagedResource resource in stagedResources.Values) ReadStagedResource(resource);
        }

        private void ReadConfiguration()
        {
            ApplyConfig(Find("default.cfg", false, null, MaximumConfigBytes));
            ApplyConfig(Find("q2config.cfg", true, null, MaximumConfigBytes));
            ApplyConfig(Find("autoexec.cfg", true, "baseq2", MaximumConfigBytes));
            ApplyConfig(Find("autoexec.cfg", true, "jump", MaximumConfigBytes));
            ApplyConfig(Find("postexec.cfg", true, null, MaximumConfigBytes));
            ApplyConfig(Find("postinit.cfg", true, null, MaximumConfigBytes));
        }

        // Interpret literal assignments only. Binds and alias definitions are inert here.
        private void ApplyConfig(Found file)
        {
            if (file == null) return;
            string content = new UTF8Encoding(false, true).GetString(file.Data);
            var command = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i <= content.Length; i++) {
                cancellation.ThrowIfCancellationRequested();
                char c = i == content.Length ? '\n' : content[i];
                if (c == '"') quoted = !quoted;
                if (!quoted && c == '/' && i + 1 < content.Length && content[i + 1] == '/') {
                    while (i < content.Length && content[i] != '\n') i++;
                    c = '\n';
                }
                if ((c == '\n' || c == '\r' || c == ';') && !quoted) {
                    ApplyCommand(command.ToString(), file.Source);
                    command.Length = 0;
                } else command.Append(c);
            }
            if (quoted) throw new UnverifiedException("Unclosed config quoting: " + file.Source);
        }

        private void ApplyCommand(string text, string source)
        {
            var tokens = new List<string>();
            foreach (Match match in Regex.Matches(text.TrimStart('\uFEFF'), "\"([^\"]*)\"|([^\\s]+)"))
                tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            if (tokens.Count == 0) return;
            string name = tokens[0].ToLowerInvariant();
            if (aliases.Contains(name)) throw new UnverifiedException("Config invokes an alias: " + source);
            if (name == "alias") { if (tokens.Count >= 2) aliases.Add(tokens[1]); return; }
            if (name == "bind" || name == "unbind" || name == "unbindall" || name == "echo") return;
            if (text.IndexOf('$') >= 0 || text.IndexOf("/*", StringComparison.Ordinal) >= 0)
                throw new UnverifiedException("Dynamic or unsupported config syntax: " + source);
            string value;
            if (name == "set" || name == "seta" || name == "sets" || name == "setu") {
                if (tokens.Count < 3 || tokens.Count > 4) throw new UnverifiedException("Unsupported config assignment: " + source);
                name = tokens[1].ToLowerInvariant(); value = tokens[2];
            } else {
                if (tokens.Count != 2 || !(name.Contains("_") || name == "skin" || name == "name" || name == "sensitivity" || name == "crosshair" || name == "hand" || name == "viewsize" || name == "fov" || name == "gender" || name == "rate"))
                    throw new UnverifiedException("Config command requires execution to resolve: " + name + " in " + source);
                value = tokens[1];
            }
            if (name == "game" || name == "basedir" || name == "homedir" || name.StartsWith("fs_", StringComparison.Ordinal) || name == "gl_md5_load")
                throw new UnverifiedException("Config changes a filesystem/model lookup setting: " + name);
            settings[name] = value;
        }

        private string Setting(string name, string fallback)
        {
            string value; return settings.TryGetValue(name, out value) ? value : fallback;
        }

        private static string Picture(string name)
        {
            if (name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("\\", StringComparison.Ordinal)) name = name.Substring(1);
            else name = "pics/" + name;
            if (System.IO.Path.GetExtension(name).Length == 0) name += ".pcx";
            if (!ValidPath(name)) throw new UnverifiedException("Unsupported image setting path.");
            return name;
        }

        private List<string> ImageCandidates(string path, int type)
        {
            int overrides, mask;
            if (!Int32.TryParse(Setting("r_override_textures", "1"), out overrides) || !Int32.TryParse(Setting("r_texture_overrides", "-1"), out mask))
                throw new UnverifiedException("Non-integral texture override setting.");
            var formats = new List<string>();
            string value = Setting("r_texture_formats", String.Join(" ", contract.ImageFormats));
            foreach (string word in value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) {
                string lower = word.ToLowerInvariant();
                if (Array.IndexOf(contract.ImageFormats, lower) >= 0) { if (!formats.Contains(lower)) formats.Add(lower); continue; }
                foreach (char c in lower) foreach (string format in contract.ImageFormats)
                    if (format[0] == c && !formats.Contains(format)) formats.Add(format);
            }
            string extension = System.IO.Path.GetExtension(path).Substring(1).ToLowerInvariant();
            string stem = path.Substring(0, path.Length - extension.Length);
            bool originalSupported = extension == "pcx" || Array.IndexOf(contract.ImageFormats, extension) >= 0;
            bool replace = overrides > 0 && (overrides != 1 || extension == "pcx") && (mask & (1 << type)) != 0;
            var paths = new List<string>();
            if (originalSupported && !replace) paths.Add(path);
            foreach (string format in formats) if (!paths.Contains(stem + format)) paths.Add(stem + format);
            if (!paths.Contains(stem + "pcx")) paths.Add(stem + "pcx");
            return paths;
        }

        private void CheckFont(string name, string fallback)
        {
            string path = Picture(name);
            CheckAsset(path, true, false, 1, false);
            foreach (GameDataAsset asset in report.Assets) {
                if (asset.Path != path) continue;
                if (asset.State == GameDataState.Verified) { asset.Required = true; return; }
                break;
            }
            string fallbackPath = Picture(fallback);
            CheckAsset(fallbackPath, true, false, 1);
            foreach (GameDataAsset asset in report.Assets) if (asset.Path == fallbackPath) asset.Required = true;
        }

        private void CheckAsset(string path, bool image, bool palette, int imageType, bool required = true)
        {
            foreach (GameDataAsset prior in report.Assets) if (prior.Path == path) return;
            string origin = null;
            try {
                var candidates = image ? ImageCandidates(path, imageType) : new List<string> { path };
                foreach (string candidate in candidates) {
                    Found found = Find(candidate, false, null, MaximumAssetBytes);
                    if (found == null) continue;
                    origin = found.Source;
                    Validate(candidate, found.Data, palette);
                    report.Add(path, origin, GameDataState.Verified, "Effective asset structure verified; runtime rendering/audio is not tested.", required);
                    return;
                }
                report.Add(path, null, GameDataState.Missing, "No supported asset was found in the selected search paths.", required);
            } catch (UnverifiedException error) { report.Add(path, origin, GameDataState.Unverified, error.Message, required); }
            catch (InvalidDataException error) { report.Add(path, origin, GameDataState.Invalid, error.Message, required); }
        }

        private void Validate(string path, byte[] data, bool palette)
        {
            cancellation.ThrowIfCancellationRequested();
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pcx") ValidatePcx(data, palette);
            else if (extension == ".png" || extension == ".jpg") ValidateImage(data, extension);
            else if (extension == ".tga") ValidateTga(data);
            else if (extension == ".md2") ValidateMd2(data);
            else if (extension == ".wav") ValidateWav(data);
            else throw new UnverifiedException("Unsupported effective asset format.");
        }

        private static void Dimensions(long width, long height)
        {
            Require(width > 0 && height > 0 && width <= 4096 && height <= 4096 && width * height <= 16777216, "Invalid or oversized image dimensions.");
        }

        private void ValidatePcx(byte[] data, bool palette)
        {
            Require(data.Length >= 128 && data[0] == 10 && data[1] == 5 && data[2] == 1 && data[3] == 8, "Invalid PCX header.");
            int width = U16(data, 8) - U16(data, 4) + 1, height = U16(data, 10) - U16(data, 6) + 1;
            Dimensions(width, height);
            int planes = data[65], stride = U16(data, 66);
            Require((planes == 1 || (!palette && planes == 3)) && stride >= width, "Unsupported PCX planes/stride.");
            if (palette) { Require(data.Length >= 128 + 768, "PCX palette is truncated."); return; }
            int offset = 128;
            for (int y = 0; y < height; y++) {
                cancellation.ThrowIfCancellationRequested();
                int decoded = 0;
                while (decoded < stride * planes) {
                    Require(offset < data.Length, "Truncated PCX pixel data.");
                    int value = data[offset++], run = 1;
                    if ((value & 0xc0) == 0xc0) { run = value & 63; Require(run > 0 && offset < data.Length, "Truncated PCX run."); offset++; }
                    decoded += run;
                    Require(decoded <= stride * planes, "PCX run crosses the scanline.");
                }
            }
        }

        private void ValidateImage(byte[] data, string extension)
        {
            if (extension == ".png") {
                Require(data.Length >= 33 && BitConverter.ToString(data, 0, 8) == "89-50-4E-47-0D-0A-1A-0A" && Text(data, 12, 4) == "IHDR", "Invalid PNG header.");
                Dimensions(Big32(data, 16), Big32(data, 20));
                int position = 8; bool ended = false, pixels = false;
                while (position < data.Length) {
                    cancellation.ThrowIfCancellationRequested();
                    Require(position + 12 <= data.Length, "Truncated PNG chunk.");
                    uint length = Big32(data, position);
                    Require(length <= data.Length - position - 12, "PNG chunk exceeds file.");
                    string kind = Text(data, position + 4, 4);
                    Require(Crc32(data, position + 4, (int)length + 4) == Big32(data, position + 8 + (int)length), "PNG CRC mismatch.");
                    if (kind == "IDAT") pixels = true;
                    position += (int)length + 12;
                    if (kind == "IEND") { Require(length == 0 && position == data.Length, "Invalid PNG end chunk."); ended = true; break; }
                }
                Require(ended && pixels, "PNG image data/end missing.");
            } else {
                Require(data.Length >= 4 && data[0] == 255 && data[1] == 216, "Invalid JPEG header.");
                int position = 2; bool frame = false;
                while (position + 4 <= data.Length) {
                    cancellation.ThrowIfCancellationRequested();
                    Require(data[position++] == 255, "Invalid JPEG marker.");
                    while (position < data.Length && data[position] == 255) position++;
                    Require(position + 2 < data.Length, "Truncated JPEG marker.");
                    int marker = data[position++], size = data[position] * 256 + data[position + 1];
                    Require(size >= 2 && position + size <= data.Length, "Truncated JPEG segment.");
                    if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc) {
                        Require(size >= 8, "Short JPEG frame.");
                        Dimensions(data[position + 5] * 256 + data[position + 6], data[position + 3] * 256 + data[position + 4]); frame = true; break;
                    }
                    position += size;
                }
                Require(frame, "JPEG dimensions missing.");
            }
            cancellation.ThrowIfCancellationRequested();
            try {
                using (var input = new MemoryStream(data, false))
                using (var decoded = Image.FromStream(input, false, true))
                using (var bitmap = new Bitmap(decoded)) { bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1); }
            } catch (ArgumentException) { throw new InvalidDataException("Image decoding failed."); }
            catch (ExternalException) { throw new InvalidDataException("Image decoding failed."); }
            catch (OutOfMemoryException) { throw new UnverifiedException("Image decoder could not validate the bounded image."); }
            cancellation.ThrowIfCancellationRequested();
        }

        private void ValidateTga(byte[] data)
        {
            Require(data.Length >= 18, "Truncated TGA header.");
            int type = data[2], width = U16(data, 12), height = U16(data, 14), bits = data[16];
            Dimensions(width, height);
            // Keep unfamiliar colormapped variants unverified instead of accepting header-only data.
            if (data[1] != 0 || (type != 2 && type != 3 && type != 10 && type != 11)) throw new UnverifiedException("This TGA variant requires separate validation.");
            Require(((type == 2 || type == 10) && (bits == 24 || bits == 32)) || ((type == 3 || type == 11) && bits == 8), "Unsupported TGA pixel size.");
            int offset = 18 + data[0], pixels = width * height, pixelBytes = bits / 8;
            if (type < 8) { Require((long)offset + (long)pixels * pixelBytes <= data.Length, "Truncated TGA pixels."); return; }
            int decoded = 0;
            while (decoded < pixels) {
                cancellation.ThrowIfCancellationRequested();
                Require(offset < data.Length, "Truncated TGA run.");
                int run = data[offset++], count = (run & 127) + 1;
                decoded += count; offset += ((run & 128) != 0 ? 1 : count) * pixelBytes;
                Require(decoded <= pixels && offset <= data.Length, "TGA run exceeds image.");
            }
        }

        private void ValidateMd2(byte[] data)
        {
            Require(data.Length >= 68 && Text(data, 0, 4) == "IDP2" && U32(data, 4) == 8, "Invalid MD2 header/version.");
            uint vertices = U32(data, 24), st = U32(data, 28), triangles = U32(data, 32), frames = U32(data, 40), skins = U32(data, 20), frameSize = U32(data, 16);
            Dimensions(U32(data, 8), U32(data, 12));
            Require(vertices > 0 && vertices <= 2048 && frames > 0 && frames <= 1024 && skins <= 32 && st > 0 && triangles > 0 && triangles <= 4096, "Unsupported MD2 counts.");
            Require(frameSize >= 40 + vertices * 4 && frameSize <= 8232 && frameSize % 4 == 0, "Invalid MD2 frame size.");
            Range(data, U32(data, 44), (long)skins * 64); Range(data, U32(data, 48), (long)st * 4);
            Range(data, U32(data, 52), (long)triangles * 12); Range(data, U32(data, 56), (long)frames * frameSize);
            Require(U32(data, 48) % 2 == 0 && U32(data, 52) % 2 == 0 && U32(data, 56) % 4 == 0, "Unaligned MD2 data.");
            for (uint i = 0; i < triangles; i++) {
                cancellation.ThrowIfCancellationRequested();
                int offset = (int)(U32(data, 52) + i * 12);
                for (int n = 0; n < 3; n++) Require(U16(data, offset + n * 2) < vertices && U16(data, offset + 6 + n * 2) < st, "MD2 triangle index exceeds model.");
            }
            for (uint i = 0; i < frames; i++) {
                cancellation.ThrowIfCancellationRequested();
                int offset = (int)(U32(data, 56) + i * frameSize);
                for (int n = 0; n < 6; n++) { float value = BitConverter.ToSingle(data, offset + n * 4); Require(!Single.IsNaN(value) && !Single.IsInfinity(value), "Non-finite MD2 frame transform."); }
            }
        }

        private void ValidateWav(byte[] data)
        {
            Require(data.Length >= 12 && Text(data, 0, 4) == "RIFF" && Text(data, 8, 4) == "WAVE" && U32(data, 4) + 8L <= data.Length, "Invalid WAV header.");
            int offset = 12, channels = 0, width = 0, samples = 0; long end = U32(data, 4) + 8L;
            while (offset + 8 <= end) {
                cancellation.ThrowIfCancellationRequested();
                string kind = Text(data, offset, 4); uint length = U32(data, offset + 4); offset += 8;
                Require(offset + (long)length <= end, "WAV chunk exceeds file.");
                if (kind == "fmt ") {
                    Require(length >= 16 && U16(data, offset) == 1, "WAV must contain PCM samples.");
                    channels = U16(data, offset + 2); uint rate = U32(data, offset + 4); width = U16(data, offset + 14) / 8;
                    Require((channels == 1 || channels == 2) && rate >= 6000 && rate <= 48000 && (U16(data, offset + 14) == 8 || U16(data, offset + 14) == 16 || U16(data, offset + 14) == 24), "Unsupported WAV sample layout.");
                }
                if (kind == "data") samples = (int)length;
                offset += (int)length + ((int)length & 1);
            }
            Require(channels != 0 && width != 0 && samples >= channels * width && samples % (channels * width) == 0, "WAV sample data missing or truncated.");
        }

        private byte[] Read(Stream stream, int length)
        {
            if (length < 0 || (readBytes += length) > MaximumReadBytes) throw new UnverifiedException("Inspection read budget exceeded.");
            byte[] data = new byte[length]; int done = 0;
            while (done < length) {
                cancellation.ThrowIfCancellationRequested();
                int count = stream.Read(data, done, Math.Min(8192, length - done));
                Require(count > 0, "Unexpected end of asset/archive."); done += count;
            }
            return data;
        }

        private uint Crc32(byte[] data) { return Crc32(data, 0, data.Length); }
        private uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < length; i++) {
                if ((i & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                crc ^= data[offset + i];
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320 : 0);
            }
            return ~crc;
        }

        private static void Range(byte[] data, long offset, long length) { Require(offset >= 68 && length >= 0 && offset + length <= data.Length, "MD2 section exceeds file."); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        private static string Text(byte[] data, int offset, int length) { return Encoding.ASCII.GetString(data, offset, length); }
        private static int U16(byte[] data, int offset) { return data[offset] | data[offset + 1] << 8; }
        private static uint U32(byte[] data, int offset) { return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24); }
        private static uint Big32(byte[] data, int offset) { return (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]); }
    }
}
