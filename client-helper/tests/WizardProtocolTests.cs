// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Q2JumpStarter
{
    internal static class WizardProtocolTests
    {
        private static int passed, failed;

        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }

        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }

        private static void WithDirectory(Action<string> test)
        {
            string root = Path.Combine(Path.GetTempPath(), "q2wizard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); }
            finally { Directory.Delete(root, true); }
        }

        private sealed class FlushedWriter : StringWriter
        {
            internal int Flushes;
            public override void Flush() { Flushes++; base.Flush(); }
        }

        private static void MustCancel(string path, string request)
        {
            using (var cancellation = new CancellationTokenSource(150)) {
                try { WizardProtocol.WaitForApproval(path, request, cancellation.Token); }
                catch (OperationCanceledException) { return; }
                throw new Exception("Unapproved request was accepted.");
            }
        }

        private static int Main()
        {
            Test("helper announces readiness before folder validation or recovery", () => WithDirectory(root => {
                TextWriter previousOutput = Console.Out, previousError = Console.Error;
                using (var output = new FlushedWriter())
                using (var error = new StringWriter()) {
                    try {
                        Console.SetOut(output); Console.SetError(error);
                        var entry = typeof(StarterClientProgram).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic);
                        int result = (int)entry.Invoke(null, new object[] { new[] { Path.Combine(root, "missing"), "existing", "q2pro", "wizard",
                            Process.GetCurrentProcess().Id.ToString() } });
                        Check(result == 1, "Invalid fixture folder was accepted.");
                        Check(output.ToString() == "Q2JUMP\tstatus\tq2pro\tPreparing Q2PRO Race" + Environment.NewLine,
                            "The wizard did not receive an early status record.");
                        Check(output.Flushes == 1, "The initial status was buffered.");
                    } finally { Console.SetOut(previousOutput); Console.SetError(previousError); }
                }
            }));
            Test("protocol fields cannot inject extra records or columns", () => {
                using (var output = new FlushedWriter()) {
                    var protocol = new WizardProtocol(output, Path.GetTempPath(), false);
                    protocol.Progress(GameClient.Q2Pro, "Checking\r\nQ2JUMP\tresult\tq2pro\tready");
                    protocol.Result(GameClient.R1Q2, new ManagedReadiness(ManagedGameState.RepairNeeded, "Problem\r\nwith\tthe file"));
                    string[] lines = output.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                    Check(lines.Length == 2, "A field injected another record.");
                    Check(lines[0].Split('\t').Length == 4 && lines[1].Split('\t').Length == 6, "A field injected another column.");
                    Check(lines[1] == "Q2JUMP\tresult\tr1q2\tunavailable\t\tProblem  with the file", "Wrong result encoding.");
                    Check(output.Flushes == 2, "Protocol output was buffered.");
                }
            });
            Test("byte progress uses percentages and MiB and always sends completion", () => {
                using (var output = new FlushedWriter()) {
                    var protocol = new WizardProtocol(output, Path.GetTempPath(), false);
                    protocol.Progress(GameClient.Q2Pro, "Downloading 524288 / 1048576 bytes");
                    protocol.Progress(GameClient.Q2Pro, "Downloading 600000 / 1048576 bytes");
                    protocol.Progress(GameClient.Q2Pro, "Downloading 1048576 / 1048576 bytes");
                    var lines = output.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                    Check(lines[0] == "Q2JUMP\tprogress\tq2pro\t50\tDownloading 0.5 / 1.0 MiB", "Wrong download progress.");
                    Check(lines.Last() == "Q2JUMP\tprogress\tq2pro\t100\tDownloading 1.0 / 1.0 MiB", "Completion was throttled.");
                    Check(output.Flushes == lines.Length, "Progress was buffered.");
                }
            });
            Test("malformed or nonnumeric download messages remain status messages", () => {
                foreach (var message in new[] { "Downloading bytes", "Downloading  bytes", "Downloading r42 for installation preview",
                    "Downloading 2 / 1 bytes", "Downloading -1 / 1 bytes", "Downloading 0 / 0 bytes", "Downloading 1 / 999999999999999999999 bytes" }) {
                    long received, total;
                    Check(!WizardProtocol.DownloadProgress(message, out received, out total), "Invalid byte progress accepted: " + message);
                }
            });
            Test("file stages report completed counts and never throttle a phase transition", () => {
                using (var output = new FlushedWriter()) {
                    var protocol = new WizardProtocol(output, Path.GetTempPath(), false);
                    foreach (string stage in new[] { "Staging approved files", "Backing up existing files", "Applying approved files" }) {
                        protocol.Progress(GameClient.Q2Pro, stage + ": 0 / 2");
                        protocol.Progress(GameClient.Q2Pro, stage + ": 1 / 2");
                        protocol.Progress(GameClient.Q2Pro, stage + ": 2 / 2");
                    }
                    protocol.Progress(GameClient.Q2Pro, "Verifying installed files");
                    var lines = output.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                    Check(lines.Length == 10 && output.Flushes == 10, "A file stage or verification status was dropped.");
                    Check(lines[1] == "Q2JUMP\tprogress\tq2pro\t50\tStaging approved files: 1 / 2", "Wrong completed-file progress.");
                    Check(lines[3] == "Q2JUMP\tprogress\tq2pro\t0\tBacking up existing files: 0 / 2", "Backup phase did not start at zero.");
                    Check(lines[8] == "Q2JUMP\tprogress\tq2pro\t100\tApplying approved files: 2 / 2", "Final write completion was dropped.");
                    Check(lines[9] == "Q2JUMP\tstatus\tq2pro\tVerifying installed files", "Final writes were confused with verified completion.");
                }
            });
            Test("invalid file counts do not produce percentages", () => {
                foreach (var message in new[] { "Applying approved files: 2 / 1", "Applying approved files: -1 / 2",
                    "Staging approved files: 0 / 0", "Backing up existing files: 1 / 999999999999999999999",
                    "Applying approved files: 1 / 2 / 3", "Checking files: 1 / 2" }) {
                    long completed, total;
                    Check(!WizardProtocol.FileProgress(message, out completed, out total), "Invalid file progress accepted: " + message);
                }
            });
            Test("a new client resets progress and duplicate status is omitted", () => {
                using (var output = new FlushedWriter()) {
                    var protocol = new WizardProtocol(output, Path.GetTempPath(), false);
                    protocol.Start(GameClient.Q2Pro);
                    protocol.Progress(GameClient.Q2Pro, "Preparing Q2PRO Race");
                    protocol.Progress(GameClient.Q2Pro, "Downloading 1 / 2 bytes");
                    protocol.Start(GameClient.R1Q2);
                    protocol.Progress(GameClient.R1Q2, "Downloading 1 / 2 bytes");
                    Check(output.Flushes == 4, "Client progress did not reset or status was duplicated.");
                }
            });
            Test("missing approval cancels without creating any file", () => WithDirectory(root => {
                MustCancel(Path.Combine(root, "q2jump-client.approve"), Guid.NewGuid().ToString("N"));
                Check(Directory.GetFiles(root).Length == 0, "Waiting for approval changed files.");
            }));
            Test("stale and padded approvals cannot authorize a new preview", () => WithDirectory(root => {
                string request = Guid.NewGuid().ToString("N"), path = Path.Combine(root, "q2jump-client.approve");
                foreach (var stale in new[] { Guid.NewGuid().ToString("N"), request + Environment.NewLine, " " + request }) {
                    File.WriteAllText(path, stale);
                    MustCancel(path, request);
                    Check(File.ReadAllText(path) == stale, "Waiting changed the approval file.");
                }
            }));
            Test("an oversized approval beginning with the right GUID is rejected", () => WithDirectory(root => {
                string request = Guid.NewGuid().ToString("N"), path = Path.Combine(root, "q2jump-client.approve");
                File.WriteAllText(path, request);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(1024 * 1024);
                MustCancel(path, request);
                Check(new FileInfo(path).Length == 1024 * 1024, "Waiting changed the approval file.");
            }));
            Test("only the current exact approval releases the wait", () => WithDirectory(root => {
                string request = Guid.NewGuid().ToString("N"), path = Path.Combine(root, "q2jump-client.approve");
                File.WriteAllText(path, Guid.NewGuid().ToString("N"));
                using (var cancellation = new CancellationTokenSource(3000))
                using (var entered = new ManualResetEvent(false))
                using (var completed = new ManualResetEvent(false)) {
                    Exception failure = null;
                    var thread = new Thread(() => {
                        entered.Set();
                        try { WizardProtocol.WaitForApproval(path, request, cancellation.Token); }
                        catch (Exception error) { failure = error; }
                        finally { completed.Set(); }
                    });
                    thread.Start();
                    try {
                        Check(entered.WaitOne(1000), "Approval thread did not start.");
                        Check(!completed.WaitOne(150), "Stale approval was accepted.");
                        File.WriteAllText(path, request);
                        Check(completed.WaitOne(2000), "Current approval was ignored.");
                        Check(failure == null, "Approved wait failed: " + failure);
                    } finally { cancellation.Cancel(); thread.Join(); }
                }
            }));
            Test("cancellation takes precedence over an already matching approval", () => WithDirectory(root => {
                string request = Guid.NewGuid().ToString("N"), path = Path.Combine(root, "q2jump-client.approve");
                File.WriteAllText(path, request);
                using (var cancellation = new CancellationTokenSource()) {
                    cancellation.Cancel();
                    try { WizardProtocol.WaitForApproval(path, request, cancellation.Token); }
                    catch (OperationCanceledException) { return; }
                    throw new Exception("Cancelled approval was accepted.");
                }
            }));
            Console.WriteLine(passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
