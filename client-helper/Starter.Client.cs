// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Q2JumpStarter
{
    // Temporary helper embedded in Starter, not installed into the game root.
    internal static class StarterClientProgram
    {
        internal static ManagedReadiness Install(string root, ManagedGameService service, CancellationToken token, Action<string> progress,
            Func<GameSetupPlan, bool> review = null)
        {
            root = InstallationPaths.Root(root);
            if (ManagedGameService.HasPending(root, service.Client.Component)) {
                progress("Recovering interrupted client installation"); service.Recover(root, token);
            }
            var plan = service.Preview(root, false, token, progress);
            if (!plan.CanInstall) throw new InvalidDataException(plan.DataMessage);
            bool replacements = plan.Files.Files.Any(file => file.Disposition == FileDisposition.Replace);
            if (replacements && review == null)
                throw new IOException("Existing client files differ. Review their replacements before installing.");
            if (review != null && !review(plan)) throw new OperationCanceledException("Client installation was not approved.");
            token.ThrowIfCancellationRequested();
            service.Install(plan, replacements, true, token, progress);
            if (progress != null) progress("Checking installed client");
            var result = service.Check(root, token);
            if (!result.Installation.CanPlay) throw new IOException(result.Installation.Message);
            progress(service.Client.Name + " " + result.Installation.ClientIdentity + " is installed and ready.");
            return result;
        }

        internal static void InstallClients(string root, ManagedGameService[] services, CancellationToken token,
            Action<string> progress, Func<GameSetupPlan, bool> review = null, Action<GameClient> started = null,
            Action<GameClient, string> clientProgress = null, Action<GameClient, ManagedReadiness> finished = null)
        {
            root = InstallationPaths.Root(root);
            using (new OperationLock(root)) {
                Exception failure = null;
                try {
                    // Recover both before previewing either: the two clients share assets.
                    foreach (var service in services)
                        if (ManagedGameService.HasPending(root, service.Client.Component)) service.Recover(root, token);
                    foreach (var service in services) {
                        if (started != null) started(service.Client);
                        Install(root, service, token, message => {
                            if (progress != null) progress(message);
                            if (clientProgress != null) clientProgress(service.Client, message);
                        }, review);
                    }
                } catch (Exception error) {
                    failure = error;
                    throw;
                } finally {
                    // A failed second client must not hide a verified first client.
                    // Shared assets require checking both again after all file changes.
                    Exception verificationFailure = null;
                    foreach (var service in services) {
                        ManagedReadiness result;
                        try {
                            if (progress != null) progress("Checking installed client");
                            if (clientProgress != null) clientProgress(service.Client, "Checking installed client");
                        } catch (Exception error) { if (verificationFailure == null) verificationFailure = error; }
                        try { result = service.Check(root, CancellationToken.None); }
                        catch (Exception error) { result = new ManagedReadiness(ManagedGameState.RepairNeeded, error.Message); }
                        if (!result.Installation.CanPlay && verificationFailure == null)
                            verificationFailure = new IOException(result.Installation.Message);
                        try { if (finished != null) finished(service.Client, result); }
                        catch (Exception error) { if (verificationFailure == null) verificationFailure = error; }
                    }
                    if (failure == null && verificationFailure != null) throw verificationFailure;
                }
            }
        }

        private static int Main(string[] args)
        {
            int parentId;
            if (args.Length != 5 || (args[1] != "new" && args[1] != "existing") ||
                (args[3] != "wizard" && args[3] != "silent") || !Int32.TryParse(args[4], out parentId) || parentId <= 0) {
                Console.Error.WriteLine("Usage: Q2JUMP-Starter-Client <game-folder> <new|existing> <q2pro|r1q2|both> <wizard|silent> <parent-pid>");
                return 1;
            }
            try {
                var clients = GameClient.Selection(args[2]);
                var protocol = new WizardProtocol(Console.Out, AppDomain.CurrentDomain.BaseDirectory, args[3] == "silent");
                using (var cancellation = new CancellationTokenSource())
                using (var parent = Process.GetProcessById(parentId))
                using (var timer = new System.Threading.Timer(state => {
                    try {
                        if (parent.HasExited || File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "q2jump-client.cancel")))
                            cancellation.Cancel();
                    } catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
                    catch (System.ComponentModel.Win32Exception) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
                }, null, 0, 100)) {
                    // Enable the wizard's cancellation controls before recovery begins.
                    protocol.Start(clients[0]);
                    InstallClients(args[0], clients.Select(client => new ManagedGameService(client: client)).ToArray(),
                        cancellation.Token, null, plan => {
                            if (args[1] == "new" && !plan.Files.Files.Any(file => file.Disposition == FileDisposition.Replace)) return true;
                            return protocol.Review(plan, cancellation.Token);
                        }, protocol.Start, protocol.Progress, protocol.Result);
                    return 0;
                }
            } catch (OperationCanceledException) {
                Console.Error.WriteLine("Client installation cancelled. Rerun Starter to continue.");
                return 2;
            } catch (Exception error) {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
    }

    // Only the Inno Setup wizard owns UI. A unique request binds each approval
    // to the frozen preview, so an old approval cannot authorize new changes.
    internal sealed class WizardProtocol
    {
        private readonly TextWriter output;
        private readonly string approvalPath;
        private readonly bool silent;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private long lastProgress = -200;
        private string lastStatus;

        internal WizardProtocol(TextWriter output, string directory, bool silent)
        { this.output = output; approvalPath = Path.Combine(directory, "q2jump-client.approve"); this.silent = silent; }

        internal static string Field(string value)
        { return (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '); }

        private void Write(string type, GameClient client, params string[] fields)
        {
            output.WriteLine("Q2JUMP\t" + type + "\t" + client.Id + "\t" + String.Join("\t", fields.Select(Field)));
            output.Flush();
        }

        internal void Start(GameClient client)
        {
            lastProgress = -200; lastStatus = null;
            Progress(client, "Preparing " + client.Name);
        }

        internal void Progress(GameClient client, string message)
        {
            long received, total;
            if (DownloadProgress(message, out received, out total)) {
                long now = elapsed.ElapsedMilliseconds;
                if (received != total && now - lastProgress < 150) return;
                lastProgress = now;
                int percent = (int)(100.0 * received / total);
                Write("progress", client, percent.ToString(CultureInfo.InvariantCulture),
                    String.Format(CultureInfo.InvariantCulture, "Downloading {0:0.0} / {1:0.0} MiB", received / 1048576.0, total / 1048576.0));
            } else if (FileProgress(message, out received, out total)) {
                int percent = (int)(100.0 * received / total);
                Write("progress", client, percent.ToString(CultureInfo.InvariantCulture), message);
            } else if (message != lastStatus) {
                lastStatus = message;
                Write("status", client, message);
            }
        }

        internal static bool DownloadProgress(string message, out long received, out long total)
        {
            received = 0; total = 0;
            if (message == null || message.Length <= 18 || !message.StartsWith("Downloading ", StringComparison.Ordinal) ||
                !message.EndsWith(" bytes", StringComparison.Ordinal)) return false;
            string[] counts = message.Substring(12, message.Length - 18).Split(new[] { " / " }, StringSplitOptions.None);
            return counts.Length == 2 && Int64.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out received) &&
                Int64.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out total) && total > 0 && received <= total;
        }

        internal static bool FileProgress(string message, out long completed, out long total)
        {
            completed = 0; total = 0;
            if (message == null) return false;
            foreach (string prefix in new[] { "Staging approved files: ", "Backing up existing files: ", "Applying approved files: " }) {
                if (!message.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string[] counts = message.Substring(prefix.Length).Split(new[] { " / " }, StringSplitOptions.None);
                return counts.Length == 2 && Int64.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out completed) &&
                    Int64.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out total) && total > 0 && completed <= total;
            }
            return false;
        }

        internal bool Review(GameSetupPlan plan, CancellationToken token)
        {
            var client = GameClient.ForComponent(plan.Files.Package.Component);
            if (silent) throw new IOException(client.Name + " requires review before installation. Run Starter interactively to approve the file changes.");
            token.ThrowIfCancellationRequested();
            var files = plan.Files.Files;
            int added = files.Count(file => file.Disposition == FileDisposition.Add);
            int replaced = files.Count(file => file.Disposition == FileDisposition.Replace);
            int kept = files.Count(file => file.Disposition == FileDisposition.Register);
            string request = Guid.NewGuid().ToString("N");
            Write("review-begin", client, client.Name + " " + plan.Resolved.Release.Version + ": " + added + " new, " + replaced +
                " replaced, " + kept + " matching files. Replaced files are backed up. Custom PAKs, settings and unrelated files are kept.");
            foreach (var file in files.Where(file => file.Disposition != FileDisposition.Register))
                Write("review-file", client, (file.Disposition == FileDisposition.Replace ? "Replace  " : "Add      ") + file.File.Path);
            if (added + replaced == 0) Write("review-file", client, "All client files already match this release.");
            Write("review-ready", client, request);
            WaitForApproval(approvalPath, request, token);
            return true;
        }

        internal static void WaitForApproval(string path, string request, CancellationToken token)
        {
            while (true) {
                token.ThrowIfCancellationRequested();
                try {
                    using (var input = new StreamReader(path)) {
                        var approval = new char[33];
                        if (input.ReadBlock(approval, 0, approval.Length) == 32 && new String(approval, 0, 32) == request) {
                            token.ThrowIfCancellationRequested();
                            return;
                        }
                    }
                } catch (IOException) { } // The wizard may still be writing the request.
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
        }

        internal void Result(GameClient client, ManagedReadiness result)
        {
            Write("result", client, result.Installation.CanPlay ? "ready" : "unavailable",
                result.Installation.ClientIdentity, result.Installation.Message);
        }
    }
}
