// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Q2JumpStarter
{
    // Temporary helper embedded in Starter, not installed into the game root.
    internal static class StarterClientProgram
    {
        internal static ManagedReadiness Install(string root, ManagedGameService service, CancellationToken token, Action<string> progress,
            Func<GameSetupPlan, bool> review = null)
        {
            root = InstallationPaths.Root(root);
            if (ManagedGameService.HasPending(root)) {
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
            var result = service.Check(root, token);
            if (!result.Installation.CanPlay) throw new IOException(result.Installation.Message);
            progress("Q2PRO Race " + result.Installation.ClientIdentity + " is installed and ready.");
            return result;
        }

        private static bool ReviewChanges(Form owner, GameSetupPlan plan, CancellationToken token)
        {
            var files = plan.Files.Files;
            int added = files.Count(file => file.Disposition == FileDisposition.Add);
            int replaced = files.Count(file => file.Disposition == FileDisposition.Replace);
            int kept = files.Count(file => file.Disposition == FileDisposition.Register);
            using (var review = new Form())
            using (var timer = new System.Windows.Forms.Timer())
            {
                review.Text = "Review Q2PRO Race client changes";
                review.ClientSize = new Size(620, 410);
                review.FormBorderStyle = FormBorderStyle.FixedDialog;
                review.MaximizeBox = false;
                review.MinimizeBox = false;
                review.StartPosition = FormStartPosition.CenterParent;
                review.ShowInTaskbar = false;
                var heading = new Label { Left = 16, Top = 16, Width = 588, Height = 52,
                    Text = "Latest client " + plan.Resolved.Release.Version + ": " + added + " new, " + replaced +
                        " replaced, " + kept + " matching files. Existing files that change are backed up for recovery." };
                var list = new TextBox { Left = 16, Top = 74, Width = 588, Height = 255,
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
                    Text = (added + replaced) == 0 ? "All client files already match this release." :
                        String.Join(Environment.NewLine, files.Where(file => file.Disposition != FileDisposition.Register)
                            .Select(file => (file.Disposition == FileDisposition.Replace ? "Replace  " : "Add      ") + file.File.Path)) };
                var note = new Label { Left = 16, Top = 339, Width = 588, Height = 28,
                    Text = "Custom PAKs, configuration, and unrelated files are kept." };
                var install = new Button { Left = 402, Top = 375, Width = 118, Height = 26,
                    Text = "Install / Update", DialogResult = DialogResult.OK };
                var cancel = new Button { Left = 528, Top = 375, Width = 76, Height = 26,
                    Text = "Cancel", DialogResult = DialogResult.Cancel };
                review.Controls.AddRange(new Control[] { heading, list, note, install, cancel });
                review.AcceptButton = install;
                review.CancelButton = cancel;
                timer.Interval = 100;
                timer.Tick += (sender, e) => { if (token.IsCancellationRequested) review.Close(); };
                token.ThrowIfCancellationRequested();
                timer.Start();
                try { return review.ShowDialog(owner) == DialogResult.OK && !token.IsCancellationRequested; }
                finally { timer.Stop(); }
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 2 || (args[1] != "new" && args[1] != "existing")) {
                Console.Error.WriteLine("Usage: Q2JUMP-Starter-Client <game-folder> <new|existing>"); return 1;
            }
            bool existingGame = args[1] == "existing";
            Application.EnableVisualStyles();
            using (var cancellation = new CancellationTokenSource())
            using (var timer = new System.Threading.Timer(state => {
                if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "q2jump-client.cancel"))) cancellation.Cancel();
            }, null, 100, 100))
            using (var dialog = new Form())
            {
                dialog.Text = "Q2JUMP Starter - Client setup";
                dialog.ClientSize = new Size(440, 120);
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.StartPosition = FormStartPosition.CenterScreen;
                dialog.TopMost = true;
                var status = new Label { Left = 16, Top = 16, Width = 408, Height = 36, Text = "Preparing the latest Q2PRO Race client..." };
                var progressBar = new ProgressBar { Left = 16, Top = 57, Width = 408, Height = 18, Style = ProgressBarStyle.Marquee };
                var cancelButton = new Button { Left = 339, Top = 86, Width = 85, Height = 25, Text = "Cancel" };
                dialog.Controls.Add(status);
                dialog.Controls.Add(progressBar);
                dialog.Controls.Add(cancelButton);
                bool finished = false;
                int exitCode = 1;
                Action requestCancel = () => {
                    if (finished || cancellation.IsCancellationRequested) return;
                    cancellation.Cancel();
                    cancelButton.Enabled = false;
                    status.Text = "Cancelling client setup safely...";
                };
                cancelButton.Click += (sender, e) => requestCancel();
                dialog.FormClosing += (sender, e) => {
                    if (!finished) { e.Cancel = true; requestCancel(); }
                };
                dialog.Shown += (sender, e) => ThreadPool.QueueUserWorkItem(state => {
                    DateTime last = DateTime.MinValue;
                    try {
                        Install(args[0], new ManagedGameService(), cancellation.Token, message => {
                            if (message.StartsWith("Downloading ", StringComparison.Ordinal) && (DateTime.UtcNow - last).TotalMilliseconds < 200) return;
                            last = DateTime.UtcNow;
                            Console.WriteLine(message);
                            try { dialog.BeginInvoke((MethodInvoker)(() => { if (!cancellation.IsCancellationRequested) status.Text = message; })); }
                            catch (InvalidOperationException) { }
                        }, plan => {
                            if (!existingGame && !plan.Files.Files.Any(file => file.Disposition == FileDisposition.Replace))
                                return true;
                            bool approved = false;
                            dialog.Invoke((MethodInvoker)(() => approved = ReviewChanges(dialog, plan, cancellation.Token)));
                            return approved;
                        });
                        exitCode = 0;
                    } catch (OperationCanceledException) {
                        Console.Error.WriteLine("Client installation cancelled. Rerun Starter to continue.");
                        exitCode = 2;
                    } catch (Exception error) {
                        Console.Error.WriteLine(error.Message);
                        exitCode = 1;
                    } finally {
                        try { dialog.BeginInvoke((MethodInvoker)(() => { finished = true; dialog.Close(); })); }
                        catch (InvalidOperationException) { }
                    }
                });
                Application.Run(dialog);
                return exitCode;
            }
        }
    }
}
