// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Q2JumpStarter
{
    // Trust is supplied by the compiled helper, never by downloaded metadata.
    internal sealed class ReleasePublicKey
    {
        internal readonly string KeyId;
        private readonly byte[] modulus, exponent;
        internal int SignatureSize { get { return modulus.Length; } }
        internal ReleasePublicKey(string keyId, string modulusBase64, string exponentBase64)
        {
            KeyId = ReleaseJson.Identifier(keyId);
            modulus = ReleaseJson.Base64(modulusBase64); exponent = ReleaseJson.Base64(exponentBase64);
            if (!(new[] { 256, 384, 512 }).Contains(modulus.Length) || (modulus[0] & 128) == 0 ||
                (modulus[modulus.Length - 1] & 1) == 0 || !exponent.SequenceEqual(new byte[] { 1, 0, 1 }))
                throw new InvalidDataException("Unsupported pinned RSA public key.");
        }
        internal RSAParameters Parameters()
        { return new RSAParameters { Modulus = (byte[])modulus.Clone(), Exponent = (byte[])exponent.Clone() }; }
    }

    internal sealed class ReleaseReference
    {
        internal readonly string Url, Sha256, SignatureUrl, ReleaseId, Version;
        internal readonly long Size;
        internal ReleaseReference(Dictionary<string, object> value)
        {
            ReleaseJson.Fields(value, "releaseId version url size sha256 signatureUrl");
            ReleaseId = ReleaseJson.Identifier(ReleaseJson.String(value, "releaseId", 96));
            Version = ReleaseJson.String(value, "version", 64);
            Url = ReleaseJson.AssetUrl(ReleaseJson.String(value, "url", 2048), ReleaseId);
            SignatureUrl = ReleaseJson.AssetUrl(ReleaseJson.String(value, "signatureUrl", 2048), ReleaseId);
            if (SignatureUrl != Url + ".sig.json") throw new InvalidDataException("Invalid signature companion URL.");
            Size = ReleaseJson.Integer(value, "size", 1, ReleaseVerifier.MaximumMetadataBytes);
            Sha256 = ReleaseJson.Hash(value, "sha256");
        }
    }

    internal sealed class StableChannel
    {
        internal readonly ReleaseReference Game, Launcher;
        internal readonly long ChannelSequence;
        internal readonly string ChannelHash, ApprovedAt, Approver, ApprovalUrl, TestRecordUrl, TestRecordHash;
        private readonly byte[] manifestBytes, signatureBytes;
        internal byte[] ManifestBytes { get { return (byte[])manifestBytes.Clone(); } }
        internal byte[] SignatureBytes { get { return (byte[])signatureBytes.Clone(); } }
        internal bool BaselineDownloadEnabled { get { return false; } }
        internal StableChannel(Dictionary<string, object> value, byte[] bytes, byte[] signature)
        {
            ReleaseJson.Fields(value, "schema kind product platform sequence approvedAt approval testRecord baselinePolicy game launcher");
            ReleaseJson.Common(value, "stable-channel");
            if (ReleaseJson.String(value, "platform") != "windows-x64" ||
                ReleaseJson.String(value, "baselinePolicy") != "disabled-pending-permissions")
                throw new InvalidDataException("Unsupported stable platform or baseline policy.");
            ChannelSequence = ReleaseJson.Integer(value, "sequence", 1, 9007199254740991L);
            ApprovedAt = ReleaseJson.Timestamp(ReleaseJson.String(value, "approvedAt", 20));
            var approval = ReleaseJson.Object(value, "approval"); ReleaseJson.Fields(approval, "approver url");
            Approver = ReleaseJson.String(approval, "approver", 39);
            if (!Regex.IsMatch(Approver, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\z"))
                throw new InvalidDataException("Approval requires a public GitHub login.");
            ApprovalUrl = ReleaseJson.RepositoryUrl(ReleaseJson.String(approval, "url", 2048));
            var tests = ReleaseJson.Object(value, "testRecord"); ReleaseJson.Fields(tests, "url sha256");
            TestRecordUrl = ReleaseJson.RepositoryUrl(ReleaseJson.String(tests, "url", 2048));
            TestRecordHash = ReleaseJson.Hash(tests, "sha256");
            Game = new ReleaseReference(ReleaseJson.Object(value, "game"));
            Launcher = new ReleaseReference(ReleaseJson.Object(value, "launcher"));
            manifestBytes = (byte[])bytes.Clone(); signatureBytes = (byte[])signature.Clone();
            ChannelHash = ReleaseJson.Digest(bytes);
        }
        internal ReleaseReference Component(string component)
        {
            if (component == "game") return Game;
            if (component == "launcher") return Launcher;
            throw new InvalidDataException("Unsupported release component.");
        }
    }

    internal sealed class ReleaseDependency
    {
        internal readonly string Name, Version, SourceUrl, License;
        internal readonly ReadOnlyCollection<string> Files;
        internal ReleaseDependency(Dictionary<string, object> value, HashSet<string> packageFiles)
        {
            ReleaseJson.Fields(value, "name version sourceUrl license files");
            Name = ReleaseJson.String(value, "name"); Version = ReleaseJson.String(value, "version");
            SourceUrl = ReleaseJson.HttpsUrl(ReleaseJson.String(value, "sourceUrl", 2048));
            License = ReleaseJson.String(value, "license", 1024);
            var names = ReleaseJson.StringArray(value, "files", 1, 4096);
            if (names.Any(name => !packageFiles.Contains(name))) throw new InvalidDataException("Dependency references a missing file.");
            Files = names.AsReadOnly();
        }
    }

    // These sources share immutable program identity, not approval policy.

    internal interface IManagedRelease
    {
        ManagedPackage Package { get; }
        string PackageUrl { get; }
        string PackageSha256 { get; }
        long PackageSize { get; }
        string SourceRevision { get; }
        string Version { get; }
        string Changes { get; }
        string BuildUrl { get; }
        byte[] ManifestBytes { get; }
        GameDataContract BindGameData(byte[] descriptorBytes);
    }

    internal sealed class SignedRelease : IManagedRelease
    {
        internal const string GameDataDescriptorPath = "source/GAME-DATA-CONTRACT.json";
        internal readonly ManagedPackage Package;
        internal readonly string PackageUrl, PackageSha256, SourceRevision, Version, Changes, BuildUrl;
        internal readonly string AssetContractId, AssetContractHash;
        internal string AssetContractUrl { get { return Package.Component == "game" ? ReleaseVerifier.Origin + "/releases/download/" + Package.ReleaseId + "/game-data-contract.json" : null; } }
        internal readonly long PackageSize;
        internal readonly ReadOnlyCollection<ReleaseDependency> Dependencies;
        internal readonly ReadOnlyCollection<string> Notices;
        internal byte[] ManifestBytes { get { return Package.Manifest; } }
        internal byte[] SignatureBytes { get { return Package.Signature; } }
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
        internal SignedRelease(Dictionary<string, object> value, byte[] bytes, byte[] signature, string component)
        {
            ReleaseJson.Fields(value, "schema kind product component platform releaseId version source package files dependencies assetContract changes notices");
            ReleaseJson.Common(value, "package");
            if ((component != "game" && component != "launcher") || ReleaseJson.String(value, "component") != component ||
                ReleaseJson.String(value, "platform") != "windows-x64") throw new InvalidDataException("Wrong package component or platform.");
            string releaseId = ReleaseJson.Identifier(ReleaseJson.String(value, "releaseId", 96));
            Version = ReleaseJson.String(value, "version", 64); Changes = ReleaseJson.String(value, "changes", 16384);
            var source = ReleaseJson.Object(value, "source"); ReleaseJson.Fields(source, "repository revision buildUrl");
            if (ReleaseJson.String(source, "repository") != ReleaseVerifier.Repository) throw new InvalidDataException("Wrong source repository.");
            SourceRevision = ReleaseJson.String(source, "revision", 40);
            if (!Regex.IsMatch(SourceRevision, @"\A[0-9a-f]{40}\z")) throw new InvalidDataException("Source revision is not exact.");
            BuildUrl = ReleaseJson.String(source, "buildUrl", 2048);
            if (!Regex.IsMatch(BuildUrl, "\\A" + Regex.Escape(ReleaseVerifier.Origin) + @"/actions/runs/[1-9][0-9]*(/attempts/[1-9][0-9]*)?\z"))
                throw new InvalidDataException("Invalid public build provenance.");
            var archive = ReleaseJson.Object(value, "package"); ReleaseJson.Fields(archive, "url size sha256");
            PackageUrl = ReleaseJson.AssetUrl(ReleaseJson.String(archive, "url", 2048), releaseId);
            if (!PackageUrl.EndsWith(".zip", StringComparison.Ordinal)) throw new InvalidDataException("Expected ZIP package.");
            PackageSize = ReleaseJson.Integer(archive, "size", 1, 2L * 1024 * 1024 * 1024);
            PackageSha256 = ReleaseJson.Hash(archive, "sha256");
            var files = new List<PackageFile>();
            foreach (object entry in ReleaseJson.Array(value, "files", 1, 4096)) {
                var file = ReleaseJson.Object(entry); ReleaseJson.Fields(file, "path size sha256 role");
                files.Add(new PackageFile(ReleaseJson.String(file, "path", 180), ReleaseJson.Integer(file, "size", 1, 512L * 1024 * 1024),
                    ReleaseJson.Hash(file, "sha256"), ReleaseJson.String(file, "role")));
            }
            Package = new ManagedPackage(releaseId, component, files, bytes, signature);
            var paths = new HashSet<string>(files.Select(file => file.Path), StringComparer.Ordinal);
            var dependencies = new List<ReleaseDependency>();
            foreach (object entry in ReleaseJson.Array(value, "dependencies", 0, 128)) dependencies.Add(new ReleaseDependency(ReleaseJson.Object(entry), paths));
            if (dependencies.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != dependencies.Count)
                throw new InvalidDataException("Duplicate dependency.");
            var dependencyPaths = new HashSet<string>(dependencies.SelectMany(item => item.Files), StringComparer.Ordinal);
            if (files.Any(file => file.Role == "runtime" && !dependencyPaths.Contains(file.Path))) throw new InvalidDataException("Missing runtime provenance.");
            Dependencies = dependencies.AsReadOnly();
            var notices = ReleaseJson.StringArray(value, "notices", 1, 4096);
            if (!new HashSet<string>(notices, StringComparer.Ordinal).SetEquals(files.Where(file => file.Role == "notice").Select(file => file.Path)))
                throw new InvalidDataException("Notice inventory mismatch.");
            Notices = notices.AsReadOnly();
            if (component == "game") {
                var contract = ReleaseJson.Object(value, "assetContract"); ReleaseJson.Fields(contract, "id sha256");
                AssetContractId = ReleaseJson.Identifier(ReleaseJson.String(contract, "id", 96)); AssetContractHash = ReleaseJson.Hash(contract, "sha256");
                if (!files.Any(file => file.Path == GameDataDescriptorPath && file.Role == "source" && file.Sha256 == AssetContractHash && file.Size <= 16384))
                    throw new InvalidDataException("Missing authenticated game-data descriptor.");
            } else if (value["assetContract"] != null) throw new InvalidDataException("Launcher cannot declare a game-data contract.");
        }
        internal GameDataContract BindGameData(byte[] descriptorBytes)
        {
            descriptorBytes = ReleaseJson.Snapshot(descriptorBytes, 16384);
            if (Package.Component != "game" || descriptorBytes == null || descriptorBytes.Length == 0 || descriptorBytes.Length > 16384 ||
                ReleaseJson.Digest(descriptorBytes) != AssetContractHash ||
                Package.Files.Single(file => file.Path == GameDataDescriptorPath).Size != descriptorBytes.Length) throw new InvalidDataException("Game-data descriptor identity mismatch.");
            var value = ReleaseJson.Parse(descriptorBytes, 16384);
            ReleaseJson.Fields(value, "schema kind id sourceRevision png jpg tga pkz");
            if (ReleaseJson.Integer(value, "schema", 1, 1) != 1 || ReleaseJson.String(value, "kind") != "game-data-contract" ||
                ReleaseJson.String(value, "id") != GameDataContract.Version || AssetContractId != GameDataContract.Version ||
                ReleaseJson.String(value, "sourceRevision", 40) != SourceRevision)
                throw new InvalidDataException("Unsupported or mismatched game-data contract.");
            return new GameDataContract(SourceRevision, ReleaseJson.Boolean(value, "png"), ReleaseJson.Boolean(value, "jpg"),
                ReleaseJson.Boolean(value, "tga"), ReleaseJson.Boolean(value, "pkz"));
        }
    }

    internal sealed class ReleaseVerifier
    {
        internal const string Repository = "MovoWR/q2pro_race";
        internal const string Origin = "https://github.com/" + Repository;
        internal const int MaximumMetadataBytes = 1024 * 1024, MaximumSignatureBytes = 8192;
        // Public publisher identity only. Packaging verifies this pin against release-public-keys.json.
        private static readonly ReleasePublicKey PublisherKey = new ReleasePublicKey(
            "q2jump-release-20260921-01",
            "uZm4OkwbHgOQHGiW4zxSj/rsNKeCMJg2ZIwUz7DcH4PLKTGeO/GaTFlW1KtCfPhe7okCxVq5RBlpRYsQBzR8NfhXr/KYeC+l8rBWvR+nAmNdmrpST8ih/K19Lw6Ojn+jAZvUqcZFp5Pf3IGm+oBXs5t0ZIA06nP8nU9X4vmsAoY65Kn2AErAIjJ2utrzC3cyOQJKbG7CNYvxJyj12gQdmvlTDmTh8aL9r8kbyd6RC9sFjoLvUDs0TctNG9/s+ULU1Wno4jJfqVFJ02kmvh4G/KNeZx28HTDLD2FBlfBFJ/IAhqPnsC0i95wxPQLKXhIPmfF1QnvubX88Cv5S6Pd51rP81GlHr2Hde7ehP6Vdm26OV2UfJAf+tsLDFU33GjlE4g5neS0CRk39Odfsy9whJPE9iioXXsnzOCL+5fAxTap53rNaUakM9nCjQyXqvl6cShpo4WUYqL0pXshv1nMK7W0+jOAITw0WWNOBn0q5tdkDrx6mDrQBWrmv1vth5yPZ",
            "AQAB");
        private readonly Dictionary<string, ReleasePublicKey> trusted = new Dictionary<string, ReleasePublicKey>(StringComparer.Ordinal);
        internal bool IsConfigured { get { return trusted.Count != 0; } }
        internal ReleaseVerifier() : this(new[] { PublisherKey }) { }
        internal ReleaseVerifier(IEnumerable<ReleasePublicKey> keys)
        {
            if (keys == null) throw new ArgumentNullException("keys");
            foreach (var key in keys) {
                if (key == null || trusted.ContainsKey(key.KeyId)) throw new InvalidDataException("Duplicate or missing pinned public key.");
                trusted.Add(key.KeyId, key);
            }
        }
        internal StableChannel ParseChannel(byte[] bytes, byte[] signature, long minimumSequence = 0, string priorDigest = null)
        {
            if (minimumSequence < 0 || minimumSequence > 9007199254740991L ||
                (priorDigest != null && !InstallationPaths.IsHash(priorDigest))) throw new InvalidDataException("Invalid prior channel identity.");
            bytes = ReleaseJson.Snapshot(bytes, MaximumMetadataBytes); signature = ReleaseJson.Snapshot(signature, MaximumSignatureBytes);
            var channel = new StableChannel(Authenticate(bytes, signature), bytes, signature);
            if (channel.ChannelSequence < minimumSequence || (channel.ChannelSequence == minimumSequence && minimumSequence != 0 &&
                (priorDigest == null || priorDigest != channel.ChannelHash))) throw new InvalidDataException("Stable approval rollback or conflicting sequence.");
            return channel;
        }
        internal SignedRelease ParseManifest(byte[] bytes, byte[] signature, ReleaseReference expectedReference, string component)
        {
            if (expectedReference == null) throw new ArgumentNullException("expectedReference");
            bytes = ReleaseJson.Snapshot(bytes, MaximumMetadataBytes); signature = ReleaseJson.Snapshot(signature, MaximumSignatureBytes);
            if (bytes == null || bytes.LongLength != expectedReference.Size || ReleaseJson.Digest(bytes) != expectedReference.Sha256)
                throw new InvalidDataException("Frozen manifest identity mismatch.");
            var release = ParseInstalledManifest(bytes, signature, component);
            if (release.Package.ReleaseId != expectedReference.ReleaseId || release.Version != expectedReference.Version)
                throw new InvalidDataException("Frozen release identity mismatch.");
            return release;
        }
        // This authenticates a recorded release; it does not authorize adoption or
        // ordinary updates to an older release merely because a local ledger names it.
        internal SignedRelease ParseInstalledManifest(byte[] bytes, byte[] signature, string component)
        {
            bytes = ReleaseJson.Snapshot(bytes, MaximumMetadataBytes); signature = ReleaseJson.Snapshot(signature, MaximumSignatureBytes);
            return new SignedRelease(Authenticate(bytes, signature), bytes, signature, component);
        }
        private Dictionary<string, object> Authenticate(byte[] bytes, byte[] signatureBytes)
        {
            if (!IsConfigured) throw new InvalidDataException("Official release trust has not been configured.");
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("Signed metadata exceeds its size bound.");
            var envelope = ReleaseJson.Parse(signatureBytes, MaximumSignatureBytes);
            ReleaseJson.Fields(envelope, "schema algorithm keyId signature");
            if (ReleaseJson.Integer(envelope, "schema", 1, 1) != 1 || ReleaseJson.String(envelope, "algorithm") != "RSA-SHA256")
                throw new InvalidDataException("Unsupported release signature format.");
            string keyId = ReleaseJson.Identifier(ReleaseJson.String(envelope, "keyId", 96));
            ReleasePublicKey key;
            if (!trusted.TryGetValue(keyId, out key)) throw new InvalidDataException("Release signing key is not trusted.");
            byte[] signature = ReleaseJson.Base64(ReleaseJson.String(envelope, "signature", 1024));
            if (signature.Length != key.SignatureSize) throw new InvalidDataException("Invalid RSA signature length.");
            try {
                using (var rsa = new RSACryptoServiceProvider(new CspParameters(24))) {
                    rsa.PersistKeyInCsp = false; rsa.ImportParameters(key.Parameters());
                    if (!rsa.VerifyData(bytes, CryptoConfig.MapNameToOID("SHA256"), signature)) throw new InvalidDataException("Release signature verification failed.");
                }
            } catch (CryptographicException error) { throw new InvalidDataException("Release signature verification failed.", error); }
            return ReleaseJson.Parse(bytes, MaximumMetadataBytes);
        }
    }

    // A deliberately small JSON reader: no permissive serializer, numeric coercion,
    // duplicate-key loss, comments, BOM, or implicit casing changes at the trust edge.

    internal static class ReleaseJson
    {
        internal static Dictionary<string, object> Parse(byte[] bytes, int maximum = ReleaseVerifier.MaximumMetadataBytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > maximum ||
                (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191)) throw new InvalidDataException("Invalid bounded metadata encoding.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Metadata is not valid UTF-8.", error); }
            return Object(new Reader(text).Read());
        }
        internal static byte[] Snapshot(byte[] bytes, int maximum)
        { if (bytes == null || bytes.Length == 0 || bytes.Length > maximum) throw new InvalidDataException("Invalid bounded metadata size."); return (byte[])bytes.Clone(); }
        internal static string Digest(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static void Fields(Dictionary<string, object> value, string expected)
        { if (value.Count != expected.Split(' ').Length || expected.Split(' ').Any(key => !value.ContainsKey(key))) throw new InvalidDataException("Unexpected or missing metadata field."); }
        internal static Dictionary<string, object> Object(object value)
        { var result = value as Dictionary<string, object>; if (result == null) throw new InvalidDataException("Expected metadata object."); return result; }
        internal static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        { return Object(Get(value, key)); }
        private static object Get(Dictionary<string, object> value, string key)
        { object result; if (!value.TryGetValue(key, out result)) throw new InvalidDataException("Missing metadata field: " + key); return result; }
        internal static string String(Dictionary<string, object> value, string key, int maximum = 256)
        { return Text(Get(value, key), maximum); }
        private static string Text(object value, int maximum)
        {
            var text = value as string;
            if (System.String.IsNullOrEmpty(text) || text.Length - text.Count(Char.IsHighSurrogate) > maximum || text.Any(c => c < 32)) throw new InvalidDataException("Invalid metadata text.");
            return text;
        }
        internal static long Integer(Dictionary<string, object> value, string key, long minimum, long maximum)
        { object number = Get(value, key); if (!(number is long) || (long)number < minimum || (long)number > maximum) throw new InvalidDataException("Invalid metadata integer."); return (long)number; }
        internal static bool Boolean(Dictionary<string, object> value, string key)
        { object flag = Get(value, key); if (!(flag is bool)) throw new InvalidDataException("Invalid metadata boolean."); return (bool)flag; }
        internal static List<object> Array(Dictionary<string, object> value, string key, int minimum, int maximum)
        { var result = Get(value, key) as List<object>; if (result == null || result.Count < minimum || result.Count > maximum) throw new InvalidDataException("Invalid metadata array."); return result; }
        internal static List<string> StringArray(Dictionary<string, object> value, string key, int minimum, int maximum)
        {
            var result = Array(value, key, minimum, maximum).Select(item => Text(item, 180)).ToList();
            if (result.Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidDataException("Duplicate metadata array entry.");
            return result;
        }
        internal static string Hash(Dictionary<string, object> value, string key)
        { string hash = String(value, key, 64); if (!InstallationPaths.IsHash(hash)) throw new InvalidDataException("Invalid SHA256."); return hash; }
        internal static string Identifier(string value)
        {
            if (!Regex.IsMatch(value ?? "", @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,95}\z") ||
                new[] { "latest", "stable", "dev", "q2pro_race" }.Contains(value.ToLowerInvariant())) throw new InvalidDataException("Expected an immutable release identifier.");
            return value;
        }
        internal static string HttpsUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Port != 443 || value.Contains('\\') || value.Any(Char.IsWhiteSpace))
                throw new InvalidDataException("Expected a plain HTTPS URL.");
            return value;
        }
        internal static string RepositoryUrl(string value)
        { HttpsUrl(value); if (!value.StartsWith(ReleaseVerifier.Origin + "/", StringComparison.Ordinal)) throw new InvalidDataException("Wrong public repository URL."); return value; }
        internal static string AssetUrl(string value, string releaseId)
        {
            HttpsUrl(value);
            string prefix = ReleaseVerifier.Origin + "/releases/download/" + Identifier(releaseId) + "/";
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || !Regex.IsMatch(value.Substring(prefix.Length), @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z"))
                throw new InvalidDataException("Asset must belong to the designated release.");
            return value;
        }
        internal static string Timestamp(string value)
        {
            DateTime timestamp;
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
                throw new InvalidDataException("Expected a UTC timestamp.");
            return value;
        }
        internal static byte[] Base64(string value)
        {
            byte[] result;
            try { result = Convert.FromBase64String(value); }
            catch (FormatException error) { throw new InvalidDataException("Invalid signature/key base64.", error); }
            if (Convert.ToBase64String(result) != value) throw new InvalidDataException("Noncanonical signature/key base64.");
            return result;
        }
        internal static void Common(Dictionary<string, object> value, string kind)
        { if (Integer(value, "schema", 1, 1) != 1 || String(value, "kind") != kind || String(value, "product") != "Q2JUMP") throw new InvalidDataException("Unsupported release contract."); }

        private sealed class Reader
        {
            private readonly string text;
            private int position, nodes;
            internal Reader(string text) { this.text = text; }
            internal object Read()
            { object value = Value(0); White(); if (position != text.Length) Fail(); return value; }
            private void Fail() { throw new InvalidDataException("Malformed or excessive release JSON."); }
            private void White() { while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++; }
            private bool Take(char c) { if (position < text.Length && text[position] == c) { position++; return true; } return false; }
            private void Need(char c) { if (!Take(c)) Fail(); }
            private object Value(int depth)
            {
                if (depth > 32 || ++nodes > 65536) Fail(); White(); if (position == text.Length) Fail();
                char c = text[position];
                if (c == '"') return Quoted();
                if (Take('{')) {
                    var result = new Dictionary<string, object>(StringComparer.Ordinal); White(); if (Take('}')) return result;
                    do { White(); string key = Quoted(); White(); Need(':'); object value = Value(depth + 1); if (result.ContainsKey(key)) Fail(); result.Add(key, value); White(); if (Take('}')) return result; Need(','); } while (true);
                }
                if (Take('[')) {
                    var result = new List<object>(); White(); if (Take(']')) return result;
                    do { result.Add(Value(depth + 1)); White(); if (Take(']')) return result; Need(','); } while (true);
                }
                foreach (string literal in new[] { "true", "false", "null" }) {
                    if (text.Length - position >= literal.Length && System.String.CompareOrdinal(text, position, literal, 0, literal.Length) == 0) {
                        position += literal.Length; if (literal == "null") return null; return literal == "true";
                    }
                }
                int start = position; Take('-'); if (position == text.Length) Fail();
                if (!Take('0')) { if (text[position] < '1' || text[position] > '9') Fail(); while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++; }
                long number;
                if (!Int64.TryParse(text.Substring(start, position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)) Fail();
                return number;
            }
            private string Quoted()
            {
                Need('"'); var result = new StringBuilder();
                while (position < text.Length) {
                    char c = text[position++];
                    if (c == '"') {
                        string value = result.ToString();
                        for (int i = 0; i < value.Length; i++) {
                            if (Char.IsHighSurrogate(value[i])) { if (++i == value.Length || !Char.IsLowSurrogate(value[i])) Fail(); }
                            else if (Char.IsLowSurrogate(value[i])) Fail();
                        }
                        return value;
                    }
                    if (c < 32) Fail();
                    if (c == '\\') {
                        if (position == text.Length) Fail(); c = text[position++];
                        switch (c) {
                            case '"': case '\\': case '/': break;
                            case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break;
                            case 'u':
                                int code;
                                if (text.Length - position < 4 || !Int32.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) { Fail(); return null; }
                                c = (char)code; position += 4; break;
                            default: Fail(); break;
                        }
                    }
                    result.Append(c);
                }
                Fail(); return null;
            }
        }
    }
}
