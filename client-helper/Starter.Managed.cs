// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Q2JumpStarter
{
    internal enum ManagedGameState { NoSelection, NeedsInstallation, NeedsData, Ready, RepairNeeded, RecoveryRequired }

    internal sealed class ManagedReadiness
    {
        internal readonly ManagedGameState State;
        internal readonly InstallationStatus Installation;
        internal readonly GameDataReport Data;
        internal ManagedReadiness(ManagedGameState state, string message, IManagedRelease release = null, GameDataReport data = null)
        {
            State = state; Data = data;
            Installation = new InstallationStatus { CanPlay = state == ManagedGameState.Ready, Message = message,
                ClientIdentity = release == null ? null : release.Version, StarterVersion = null };
        }
    }

    internal sealed class ResolvedGameRelease
    {
        internal readonly StableChannel Channel;
        internal readonly IManagedRelease Release;
        internal readonly GameDataContract Contract;
        private readonly byte[] descriptor;
        internal byte[] DescriptorBytes { get { return (byte[])descriptor.Clone(); } }
        internal ResolvedGameRelease(StableChannel channel, IManagedRelease release, byte[] descriptor)
        { Channel = channel; Release = release; this.descriptor = (byte[])descriptor.Clone(); Contract = release.BindGameData(this.descriptor); }
    }

    internal sealed class GameSetupPlan
    {
        internal readonly ResolvedGameRelease Resolved;
        internal readonly InstallationPlan Files;
        internal readonly GameDataReport Data;
        internal readonly bool Repair, CanInstall;
        private readonly ManagedInstallation previous;
        internal readonly string DataMessage;
        internal GameSetupPlan(ResolvedGameRelease release, InstallationPlan files, GameDataReport data, bool repair, ManagedInstallation previous = null)
        {
            Resolved = release; Files = files; Data = data; Repair = repair; this.previous = previous;
            CanInstall = data.Verified;
            DataMessage = data.Verified ? "PAK file found in baseq2 or jump. Its contents will not be inspected." :
                ManagedGameService.DescribeData(data) + " " + ManagedGameService.BaselineUnavailable;
        }
        internal void CheckPreviousInstallation(CancellationToken token)
        {
            if (previous != null) previous.RecheckMetadata(token);
            else if (File.Exists(InstallationPaths.Under(Files.Root, ".q2jump/installed.json")))
                throw new IOException("The selected folder was managed after this preview. Inspect it again before installing.");
        }
    }

    internal sealed class ManagedGameService
    {
        internal const string BaselineUnavailable = "Use Settings > Run installer to prepare game data, or select an existing Quake II folder.";
        private readonly ReleaseVerifier verifier;
        private readonly ReleaseDownloadClient downloads;
        private readonly bool useGitHubLatest;
        private readonly Action<string, IEnumerable<PackageFile>> requireClosed;
        internal ManagedGameService(ReleaseVerifier verifier = null, ReleaseDownloadClient downloads = null,
            Action<string, IEnumerable<PackageFile>> requireClosed = null, bool useGitHubLatest = true)
        {
            this.verifier = verifier ?? new ReleaseVerifier(); this.downloads = downloads ?? new ReleaseDownloadClient();
            this.requireClosed = requireClosed ?? ManagedProcesses.RequireGameClosed;
            this.useGitHubLatest = useGitHubLatest;
        }
        internal bool TrustConfigured { get { return useGitHubLatest || verifier.IsConfigured; } }
        internal static string DescribeData(GameDataReport report)
        {
            return String.Join("\r\n", report.Assets.Where(asset => asset.Required && asset.State != GameDataState.Verified)
                .Select(asset => asset.Path + ": " + asset.State + " - " + asset.Detail));
        }
        internal static bool HasPending(string root)
        {
            try { return File.Exists(InstallationPaths.Under(InstallationPaths.Root(root), ".q2jump/active.json")); }
            catch (Exception error) { if (!Expected(error)) throw; return false; }
        }
        private static bool Expected(Exception error)
        { return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is System.Security.SecurityException; }

        internal ManagedReadiness Check(string root, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(root)) return new ManagedReadiness(ManagedGameState.NoSelection, "Choose an existing Quake II folder or create a separate installation.");
            try
            {
                root = InstallationPaths.Root(root);
                using (new OperationLock(root))
                {
                    if (HasPending(root)) return new ManagedReadiness(ManagedGameState.RecoveryRequired, "An interrupted file operation requires recovery before Play.");
                    var installed = ManagedInstallationStore.ReadInstalled(root, verifier, token);
                    if (installed == null) return new ManagedReadiness(ManagedGameState.NeedsInstallation, "Inspect the selected folder and preview the latest Q2PRO Race installation.");
                    installed.VerifyProgramFiles(token);
                    if (installed.Release is GitHubGameRelease && !ClientRuntimePresent(root, Environment.SystemDirectory))
                        return new ManagedReadiness(ManagedGameState.NeedsData,
                            "Q2PRO Race requires the Microsoft Visual C++ x64 runtime (vcruntime140.dll). Install that runtime, then verify files again.", installed.Release);
                    var data = GameDataInspector.CheckPakPresence(root, installed.Contract, token);
                    if (!data.Verified) return new ManagedReadiness(ManagedGameState.NeedsData, DescribeData(data) + " " + BaselineUnavailable, installed.Release, data);
                    ManagedConfiguration.ValidateReadyBackup(new GamePaths(root), token);
                    return new ManagedReadiness(ManagedGameState.Ready, "Client verified and PAK file found. Settings are shared in jump/q2config.cfg.", installed.Release, data);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (!Expected(error)) throw;
                return new ManagedReadiness(HasPending(root) ? ManagedGameState.RecoveryRequired : ManagedGameState.RepairNeeded, error.Message);
            }
        }

        internal static bool ClientRuntimePresent(string root, string systemDirectory)
        { return File.Exists(InstallationPaths.Under(root, "vcruntime140.dll")) || File.Exists(Path.Combine(systemDirectory, "vcruntime140.dll")); }

        internal GameSetupPlan Preview(string root, bool repair, CancellationToken token, Action<string> progress = null)
        {
            root = InstallationPaths.Root(root); InstallationPaths.Probe(root); InstallationTransaction.CheckMetadata(root);
            if (!useGitHubLatest && !verifier.IsConfigured) throw new InvalidDataException("Approved stable releases are not configured in this build. Publisher trust must be provisioned before managed installation or updates are available.");
            token.ThrowIfCancellationRequested(); if (progress != null) progress("Inspecting selected folder");
            var installed = ManagedInstallationStore.ReadInstalled(root, verifier, token);
            ResolvedGameRelease resolved;
            if (repair)
            {
                if (installed == null) throw new InvalidDataException("This folder has no recorded installed release to repair. Use installation preview first.");
                resolved = new ResolvedGameRelease(installed.Channel, installed.Release, installed.DescriptorBytes);
            }
            else if (useGitHubLatest)
            {
                var release = GitHubGameRelease.Fetch(downloads, token, progress);
                resolved = new ResolvedGameRelease(null, release, release.DescriptorBytes);
            }
            else
            {
                if (progress != null) progress("Resolving approved stable release");
                byte[] channelBytes = downloads.Metadata(ReleaseOrigins.ChannelUrl, ReleaseVerifier.MaximumMetadataBytes, token);
                byte[] signature = downloads.Metadata(ReleaseOrigins.ChannelUrl + ".sig.json", ReleaseVerifier.MaximumSignatureBytes, token);
                var channel = verifier.ParseChannel(channelBytes, signature, installed == null || installed.Channel == null ? 0 : installed.Channel.ChannelSequence,
                    installed == null || installed.Channel == null ? null : installed.Channel.ChannelHash);
                var reference = channel.Game;
                var release = verifier.ParseManifest(downloads.Metadata(reference.Url, checked((int)reference.Size), token),
                    downloads.Metadata(reference.SignatureUrl, ReleaseVerifier.MaximumSignatureBytes, token), reference, "game");
                resolved = new ResolvedGameRelease(channel, release, downloads.Metadata(release.AssetContractUrl, 16384, token));
            }
            token.ThrowIfCancellationRequested();
            var files = InstallationPlan.Inspect(root, resolved.Release.Package, token);
            if (new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace < RequiredSpace(files, resolved.Release)) throw new IOException("Not enough space for staging and replacement backups in the selected folder.");
            var data = GameDataInspector.CheckPakPresence(root, resolved.Contract, token);
            return new GameSetupPlan(resolved, files, data, repair, installed);
        }

        internal void Install(GameSetupPlan preview, bool replace, bool register, CancellationToken token, Action<string> progress = null)
        {
            if (preview == null || !preview.CanInstall) throw new InvalidDataException(preview == null ? "Inspect the selected folder first." : preview.DataMessage);
            var approval = preview.Files.Approve(replace, register); string root = preview.Files.Root;
            preview.CheckPreviousInstallation(token);
            InstallationPaths.Probe(root); InstallationTransaction.CheckMetadata(root); requireClosed(root, preview.Resolved.Release.Package.Files);
            if (new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace < RequiredSpace(preview.Files, preview.Resolved.Release)) throw new IOException("Not enough space for staging and backups. Free space and refresh the preview.");
            var release = preview.Resolved.Release;
            using (var cache = new PackageStaging(release))
            {
                var github = release as GitHubGameRelease;
                if (github != null) github.Extract(cache.Payload, token);
                else
                {
                    if (progress != null) progress("Downloading " + release.Version);
                    using (var output = new FileStream(cache.Archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        long copied = downloads.Download(release.PackageUrl, output, release.PackageSize, token,
                            bytes => { if (progress != null) progress("Downloading " + bytes + " / " + release.PackageSize + " bytes"); });
                        output.Flush(true); if (copied != release.PackageSize) throw new InvalidDataException("The package download is incomplete.");
                    }
                    PackageExtractor.Extract(cache.Archive, cache.Payload, release.Package, release.PackageSize, release.PackageSha256, token);
                }
                if (progress != null) progress("Verifying package and game data");
                var stagedData = GameDataInspector.CheckPakPresence(root, preview.Resolved.Contract, token);
                if (!stagedData.Verified) throw new InvalidDataException(DescribeData(stagedData) + " " + BaselineUnavailable);
                InstallationTransaction.Apply(approval, cache.Payload, token, () => {
                    var finalData = GameDataInspector.CheckPakPresence(root, preview.Resolved.Contract, token);
                    if (!finalData.Verified) throw new InvalidDataException(DescribeData(finalData));
                    ManagedConfiguration.EnsureReadyBackup(new GamePaths(root), token);
                }, () => requireClosed(root, release.Package.Files), boundary => {
                    if (progress != null) progress(boundary == "applying" || boundary.StartsWith("written:", StringComparison.Ordinal) ? "Applying approved files" :
                        boundary.StartsWith("backup:", StringComparison.Ordinal) ? "Backing up existing files" : "Verifying and staging approved files");
                }, operation => {
                    preview.CheckPreviousInstallation(token);
                    ManagedConfiguration.EnsureReadyBackup(new GamePaths(root), token);
                    if (github != null) {
                        WriteProof(operation, "github-release.json", github.MetadataBytes, token);
                        WriteProof(operation, "github-source.json", github.SourceBytes, token);
                        WriteProof(operation, "github-package.zip", github.ArchiveBytes, token);
                    } else {
                        WriteProof(operation, "channel.json", preview.Resolved.Channel.ManifestBytes, token);
                        WriteProof(operation, "channel.sig", preview.Resolved.Channel.SignatureBytes, token);
                    }
                    WriteProof(operation, "game-data-contract.json", preview.Resolved.DescriptorBytes, token);
                });
            }
        }
        private static long RequiredSpace(InstallationPlan plan, IManagedRelease release)
        { return checked(plan.RequiredBytes + (release is GitHubGameRelease ? release.PackageSize + 2L * 1024 * 1024 : 0)); }
        private static void WriteProof(string operation, string name, byte[] data, CancellationToken token)
        {
            using (var stream = new FileStream(InstallationPaths.Under(operation, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var input = new MemoryStream(data, false))
            { ReleaseDownloadClient.CopyBounded(input, stream, data.LongLength, token); stream.Flush(true); }
        }
        internal void Recover(string root, CancellationToken token)
        {
            root = InstallationPaths.Root(root);
            using (new OperationLock(root))
            {
                var pending = ManagedInstallationStore.ReadPending(root, verifier, token);
                if (pending == null) throw new InvalidDataException("There is no verified recorded operation to recover.");
                InstallationTransaction.Recover(root, pending.Release.Package, token, () => requireClosed(root, pending.Release.Package.Files), pending.RecordBytes);
            }
        }
        private sealed class PackageStaging : IDisposable
        {
            internal readonly string Root, Archive, Payload;
            private readonly IManagedRelease release;
            internal PackageStaging(IManagedRelease release)
            {
                this.release = release;
                Root = Path.Combine(Path.GetTempPath(), "Q2JL-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("The private staging location already exists.");
                Directory.CreateDirectory(Root); InstallationPaths.Root(Root);
                Archive = InstallationPaths.Under(Root, "package.zip"); Payload = InstallationPaths.Under(Root, "payload");
                long required = release.PackageSize + release.Package.Files.Sum(file => file.Size) + 4L * 1024 * 1024;
                if (new DriveInfo(Path.GetPathRoot(Root)).AvailableFreeSpace < required) throw new IOException("Not enough temporary space for package download and verification.");
            }
            public void Dispose()
            {
                // Only exact private staging files are removed. Unknown files or links
                // leave the directory intact; there is no recursive directory deletion.
                try
                {
                    InstallationPaths.Root(Root);
                    var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in release.Package.Files)
                    {
                        string path = InstallationPaths.Under(Root, "payload/" + file.Path);
                        if (File.Exists(path)) File.Delete(path);
                        string parent = Path.GetDirectoryName(path);
                        while (parent.Length > Root.Length) { directories.Add(parent); parent = Path.GetDirectoryName(parent); }
                    }
                    if (File.Exists(Archive)) File.Delete(InstallationPaths.Under(Root, "package.zip"));
                    foreach (string directory in directories.OrderByDescending(path => path.Length)) if (Directory.Exists(directory)) Directory.Delete(directory, false);
                    Directory.Delete(Root, false);
                }
                catch (IOException) { }
                catch (InvalidDataException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
