// SPDX-License-Identifier: GPL-2.0-or-later
// Synthetic offline files only; never start a game or run an installer.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Q2JumpStarter
{
    internal static class InstallationTests
    {
        private static int passed, failed;
        private static void Test(string name, Action body)
        {
            try { body(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Throws<T>(Action body) where T : Exception
        { try { body(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Parent, Root, Payload;
            internal readonly ManagedPackage Package;
            internal Fixture()
            {
                Parent = Path.Combine(Path.GetTempPath(), "q2jump-operation-tests-" + Guid.NewGuid().ToString("N"));
                Root = Path.Combine(Parent, "game with spaces"); Payload = Path.Combine(Parent, "payload");
                Directory.CreateDirectory(Root); Directory.CreateDirectory(Payload);
                Put(Payload, "q2pro_race.exe", "new synthetic client"); Put(Payload, "jump/pics/prochars.png", "new synthetic resource");
                Put(Root, "q2pro_race.exe", "old client"); Put(Root, "jump/q2config.cfg", "bind X custom; keep me\r\n");
                Put(Root, "baseq2/custom.pak", "existing external data"); Put(Root, "unrelated.txt", "sentinel");
                var files = new List<PackageFile>();
                foreach (string path in new[] { "q2pro_race.exe", "jump/pics/prochars.png" })
                {
                    string full = Path.Combine(Payload, path.Replace('/', Path.DirectorySeparatorChar));
                    files.Add(new PackageFile(path, new FileInfo(full).Length, ContentService.Hash(full), path.EndsWith(".exe") ? "client" : "resource"));
                }
                foreach (string path in new[] { "licenses/fixture.txt", "source/fixture.txt" })
                {
                    Put(Payload, path, "fixture source or notice");
                    string full = Path.Combine(Payload, path.Replace('/', Path.DirectorySeparatorChar));
                    files.Add(new PackageFile(path, new FileInfo(full).Length, ContentService.Hash(full), path.StartsWith("licenses/") ? "notice" : "source"));
                }
                Package = new ManagedPackage("fixture-v1", "game", files, Encoding.UTF8.GetBytes("synthetic manifest"), new byte[] { 1 });
            }
            internal InstallationPlan Plan() { return InstallationPlan.Inspect(Root, Package, CancellationToken.None); }
            internal string Apply(Action<string> boundary = null, Action ready = null)
            { return InstallationTransaction.Apply(Plan().Approve(true, true), Payload, CancellationToken.None, ready ?? (() => { }), () => { }, boundary); }
            internal void Recover() { InstallationTransaction.Recover(Root, Package, CancellationToken.None, () => { }); }
            internal void Preserved()
            {
                Assert(Get(Root, "jump/q2config.cfg") == "bind X custom; keep me\r\n", "player config changed");
                Assert(Get(Root, "baseq2/custom.pak") == "existing external data", "external data changed");
                Assert(Get(Root, "unrelated.txt") == "sentinel", "unknown file changed");
            }
            public void Dispose()
            {
                string target = Path.GetFullPath(Parent), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("q2jump-operation-tests-")) throw new Exception("Unsafe fixture cleanup.");
                Directory.Delete(target, true);
            }
        }
        private static void Put(string root, string relative, string data)
        { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, data, new UTF8Encoding(false)); }
        private static string Get(string root, string relative) { return File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        private static void Interrupted(string boundary)
        {
            using (var f = new Fixture())
            {
                Throws<IOException>(() => f.Apply(point => { if (point == boundary) throw new IOException("simulated interruption"); }));
                Assert(File.Exists(Path.Combine(f.Root, ".q2jump/active.json")), "journal missing");
                Throws<InvalidDataException>(() => f.Plan());
                f.Recover();
                bool committed = boundary == "committed";
                Assert(Get(f.Root, "q2pro_race.exe") == (committed ? "new synthetic client" : "old client"), "incorrect client after recovery");
                Assert(File.Exists(Path.Combine(f.Root, "jump/pics/prochars.png")) == committed, "incorrect added file after recovery");
                Assert(!File.Exists(Path.Combine(f.Root, ".q2jump/active.json")), "recovery did not close journal"); f.Preserved();
            }
        }
        private static int HoldLock(string root, string mode)
        {
            if (mode == "file") {
                using (new OperationLock(root)) { Console.WriteLine("ready"); Console.ReadLine(); }
            } else {
                string identity;
                using (var input = new MemoryStream(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))) identity = ContentService.Hash(input, CancellationToken.None);
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
                if (mode == "denied-mutex") security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Deny));
                bool created;
                using (var mutex = new Mutex(true, "Global\\Q2JUMP-Operation-" + identity, out created, security)) {
                    Assert(created, "legacy mutex unexpectedly exists");
                    try { Console.WriteLine("ready"); Console.ReadLine(); } finally { mutex.ReleaseMutex(); }
                }
            }
            return 0;
        }
        private static void WithLockHolder(string root, string mode, Action<Process> body)
        {
            var start = new ProcessStartInfo(typeof(InstallationTests).Assembly.Location, "--hold-lock \"" + root + "\" " + mode) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
            };
            using (var holder = Process.Start(start)) {
                try {
                    var ready = holder.StandardOutput.ReadLineAsync();
                    Assert(ready.Wait(10000) && ready.Result == "ready", "lock holder did not start");
                    body(holder);
                } finally {
                    if (!holder.HasExited) {
                        holder.StandardInput.WriteLine("release"); holder.StandardInput.Flush();
                        if (!holder.WaitForExit(10000)) { holder.Kill(); holder.WaitForExit(); throw new Exception("lock holder did not stop"); }
                        Assert(holder.ExitCode == 0, "lock holder failed");
                    }
                }
            }
        }
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--hold-lock") return HoldLock(args[1], args[2]);
            Test("preview is read-only and requires exact conflict approval", () => {
                using (var f = new Fixture())
                { var plan = f.Plan(); Assert(plan.Files.Count == 4 && plan.Files.Count(i => i.Disposition == FileDisposition.Replace) == 1, "wrong file plan"); Throws<InvalidOperationException>(() => plan.Approve(false, false)); Assert(!Directory.Exists(Path.Combine(f.Root, ".q2jump")), "preview wrote metadata"); f.Preserved(); }
            });
            Test("install backs up originals and preserves external files", () => {
                using (var f = new Fixture())
                { string id = f.Apply(); Assert(Get(f.Root, "q2pro_race.exe") == "new synthetic client", "client not replaced"); Assert(Get(f.Root, ".q2jump/operations/" + id + "/backups/q2pro_race.exe") == "old client", "backup wrong"); Assert(File.Exists(Path.Combine(f.Root, ".q2jump/installed.json")), "ledger absent"); f.Preserved(); }
            });
            Test("existing identical files require registration approval", () => {
                using (var f = new Fixture())
                { Put(f.Root, "q2pro_race.exe", "new synthetic client"); var plan = f.Plan(); Assert(plan.Files[0].Disposition == FileDisposition.Register, "not registered"); Throws<InvalidOperationException>(() => plan.Approve(true, false)); f.Apply(); f.Preserved(); }
            });
            Test("stale preview fails before metadata or payload mutation", () => {
                using (var f = new Fixture())
                { var approved = f.Plan().Approve(true, true); Put(f.Root, "q2pro_race.exe", "external edit"); Throws<IOException>(() => InstallationTransaction.Apply(approved, f.Payload, CancellationToken.None, () => { }, () => { })); Assert(Get(f.Root, "q2pro_race.exe") == "external edit", "external edit lost"); Assert(!Directory.Exists(Path.Combine(f.Root, ".q2jump")), "metadata written too soon"); f.Preserved(); }
            });
            Test("metadata collision is not adopted", () => {
                using (var f = new Fixture()) { Put(f.Root, ".q2jump/player.txt", "unrelated"); Throws<InvalidDataException>(() => f.Plan()); Assert(Get(f.Root, ".q2jump/player.txt") == "unrelated", "collision changed"); }
            });
            Test("corrupt staging cannot reach the destination", () => {
                using (var f = new Fixture())
                { Put(f.Payload, "q2pro_race.exe", "wrong"); Throws<InvalidDataException>(() => f.Apply()); Assert(Get(f.Root, "q2pro_race.exe") == "old client", "bad file published"); f.Recover(); f.Preserved(); }
            });
            Test("failed final readiness leaves recovery and restores originals", () => {
                using (var f = new Fixture())
                { Throws<InvalidDataException>(() => f.Apply(null, () => { throw new InvalidDataException("assets invalid"); })); f.Recover(); Assert(Get(f.Root, "q2pro_race.exe") == "old client", "old state not restored"); f.Preserved(); }
            });
            Test("cancellation before apply does not change the root", () => {
                using (var f = new Fixture())
                using (var cancel = new CancellationTokenSource())
                { cancel.Cancel(); Throws<OperationCanceledException>(() => InstallationTransaction.Apply(f.Plan().Approve(true, true), f.Payload, cancel.Token, () => { }, () => { })); Assert(!Directory.Exists(Path.Combine(f.Root, ".q2jump")), "cancel wrote metadata"); f.Preserved(); }
            });
            foreach (string step in new[] { "journal", "staged:q2pro_race.exe", "staged:jump/pics/prochars.png", "backup:q2pro_race.exe", "applying", "written:jump/pics/prochars.png", "displaced:q2pro_race.exe", "written:q2pro_race.exe", "verified", "committed" })
            { string boundary = step; Test("recover interruption at " + step, () => Interrupted(boundary)); }
            Test("recovery refuses an external post-failure edit", () => {
                using (var f = new Fixture())
                { Throws<IOException>(() => f.Apply(point => { if (point == "written:q2pro_race.exe") throw new IOException("stop"); })); Put(f.Root, "q2pro_race.exe", "external edit"); Throws<IOException>(() => f.Recover()); Assert(Get(f.Root, "q2pro_race.exe") == "external edit", "recovery destroyed edit"); Assert(File.Exists(Path.Combine(f.Root, ".q2jump/active.json")), "recovery evidence lost"); f.Preserved(); }
            });
            Test("edited journal state cannot hide published files from recovery", () => {
                using (var f = new Fixture())
                {
                    Throws<IOException>(() => f.Apply(point => { if (point == "written:q2pro_race.exe") throw new IOException("stop"); }));
                    string journal = Path.Combine(f.Root, ".q2jump/active.json");
                    File.WriteAllText(journal, File.ReadAllText(journal).Replace("\"applying\"", "\"preparing\""));
                    f.Recover();
                    Assert(Get(f.Root, "q2pro_race.exe") == "old client", "edited state skipped rollback");
                    Assert(!File.Exists(Path.Combine(f.Root, "jump/pics/prochars.png")), "edited state retained partial data");
                    f.Preserved();
                }
            });
            Test("publication captures and refuses an edit after the final snapshot", () => {
                using (var f = new Fixture())
                {
                    Throws<IOException>(() => f.Apply(point => { if (point == "publishing:q2pro_race.exe") Put(f.Root, "q2pro_race.exe", "last-moment edit"); }));
                    Assert(Get(f.Root, "q2pro_race.exe") == "last-moment edit", "racing edit lost");
                    Throws<IOException>(() => f.Recover()); f.Preserved();
                }
            });
            Test("publication never overwrites a path recreated after displacement", () => {
                using (var f = new Fixture())
                {
                    Throws<IOException>(() => f.Apply(point => { if (point == "displaced:q2pro_race.exe") Put(f.Root, "q2pro_race.exe", "new concurrent client"); }));
                    Assert(Get(f.Root, "q2pro_race.exe") == "new concurrent client", "concurrent creation overwritten");
                    Throws<IOException>(() => f.Recover()); f.Preserved();
                }
            });
            Test("recovery rechecks the complete original set before closing its journal", () => {
                using (var f = new Fixture())
                {
                    Throws<IOException>(() => f.Apply(point => { if (point == "written:q2pro_race.exe") throw new IOException("stop"); }));
                    int closures = 0;
                    Throws<IOException>(() => InstallationTransaction.Recover(f.Root, f.Package, CancellationToken.None, () => {
                        if (++closures == 3) Put(f.Root, "source/fixture.txt", "late concurrent creation");
                    }));
                    Assert(Get(f.Root, "source/fixture.txt") == "late concurrent creation", "late change destroyed");
                    Assert(File.Exists(Path.Combine(f.Root, ".q2jump/active.json")), "journal closed despite late change"); f.Preserved();
                }
            });
            Test("executable is published last", () => {
                using (var f = new Fixture())
                { var written = new List<string>(); f.Apply(point => { if (point.StartsWith("written:")) written.Add(point); }); Assert(written.Last() == "written:q2pro_race.exe", "executable published before resources"); }
            });
            Test("program inventory excludes configs, baseline and unknown executables", () => {
                foreach (string path in new[] { "jump/q2config.cfg", "jump/autoexec.cfg", "jump/a.exe", "baseq2/pak0.pak", "jump/maps/map.bsp", ".q2jump/owner" })
                    Throws<InvalidDataException>(() => InstallationPaths.ProgramFile("game", new PackageFile(path, 1, new string('a', 64), "resource")));
            });
            Test("path traversal, devices, alternate streams and case collisions rejected", () => {
                foreach (string path in new[] { "../x", "C:/x", "/x", "a\\x", "a//x", "a./x", "NUL.txt", "x:y", "x/../y" }) Throws<InvalidDataException>(() => InstallationPaths.Relative(path));
                using (var f = new Fixture())
                { var files = f.Package.Files.Concat(new[] { new PackageFile("jump/PICS/PROCHARS.png", 1, new string('a', 64), "resource") }); Throws<InvalidDataException>(() => new ManagedPackage("fixture", "game", files, new byte[] { 1 }, new byte[] { 1 })); }
            });
            Test("operation lock excludes another thread", () => {
                using (var f = new Fixture())
                using (new OperationLock(f.Root))
                { Exception failure = null; var thread = new Thread(() => { try { Throws<IOException>(() => { using (new OperationLock(f.Root)) { } }); } catch (Exception error) { failure = error; } }); thread.Start(); thread.Join(); if (failure != null) throw failure; }
            });
            foreach (string mode in new[] { "held-mutex", "denied-mutex" }) {
                string holderMode = mode;
                Test("setup and recovery ignore a hostile " + mode, () => {
                    using (var f = new Fixture()) WithLockHolder(f.Root, holderMode, holder => {
                        int requests = 0;
                        var service = new ManagedGameService(downloads: new ReleaseDownloadClient((uri, token) => {
                            requests++; throw new InvalidOperationException("offline release lookup reached");
                        }));
                        Assert(service.Check(f.Root, CancellationToken.None).State == ManagedGameState.NeedsInstallation, "readiness blocked by global mutex");
                        Throws<InvalidOperationException>(() => StarterClientProgram.Install(f.Root, service, CancellationToken.None, _ => { }));
                        Assert(requests == 1, "setup did not reach the offline release transport");
                        Throws<InvalidDataException>(() => service.Recover(f.Root, CancellationToken.None));
                        Assert(!File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "lock was not removed"); f.Preserved();
                    });
                });
            }
            Test("nested operation locks normalize paths and retain outer exclusion", () => {
                using (var f = new Fixture()) {
                    using (new OperationLock(f.Root)) {
                        using (new OperationLock(f.Root.ToUpperInvariant() + Path.DirectorySeparatorChar + ".")) { }
                        WithLockHolder(f.Payload, "file", holder => { });
                        Assert(File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "inner release removed the outer lock");
                        Exception failure = null;
                        var thread = new Thread(() => { try { Throws<IOException>(() => { using (new OperationLock(f.Root)) { } }); } catch (Exception error) { failure = error; } });
                        thread.Start(); thread.Join(); if (failure != null) throw failure;
                    }
                    Assert(!File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "last release left a lock file");
                }
            });
            Test("operation lock excludes another process and permits reacquisition", () => {
                using (var f = new Fixture()) {
                    WithLockHolder(f.Root, "file", holder => Throws<IOException>(() => { using (new OperationLock(f.Root)) { } }));
                    using (new OperationLock(f.Root)) { }
                    Assert(!File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "release left a lock file");
                }
            });
            Test("process termination releases the operation lock", () => {
                using (var f = new Fixture()) {
                    WithLockHolder(f.Root, "file", holder => { holder.Kill(); Assert(holder.WaitForExit(10000), "holder did not terminate"); });
                    using (new OperationLock(f.Root)) { }
                    Assert(!File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "terminated holder left a lock file");
                }
            });
            Test("operation lock preserves pre-existing file and directory collisions", () => {
                using (var f = new Fixture()) {
                    Put(f.Root, ".q2jump-operation.lock", "unrelated sentinel");
                    Throws<IOException>(() => { using (new OperationLock(f.Root)) { } });
                    Assert(Get(f.Root, ".q2jump-operation.lock") == "unrelated sentinel", "existing lock path changed");
                }
                using (var f = new Fixture()) {
                    Put(f.Root, ".q2jump-operation.lock/sentinel", "unrelated directory");
                    Throws<InvalidDataException>(() => { using (new OperationLock(f.Root)) { } });
                    Assert(Get(f.Root, ".q2jump-operation.lock/sentinel") == "unrelated directory", "existing directory changed");
                }
            });
            Test("operation lock refuses a linked path without touching its target", () => {
                using (var f = new Fixture()) {
                    string link = Path.Combine(f.Root, ".q2jump-operation.lock");
                    var start = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + f.Payload + "\"") {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                    };
                    using (var process = Process.Start(start)) { process.WaitForExit(); Assert(process.ExitCode == 0, "fixture junction creation failed"); }
                    try {
                        Throws<InvalidDataException>(() => { using (new OperationLock(f.Root)) { } });
                        Assert((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction was changed");
                        Assert(Get(f.Payload, "q2pro_race.exe") == "new synthetic client", "junction target changed");
                    } finally { Directory.Delete(link); }
                }
            });
            Test("operation lock requires permission to create files in the selected root", () => {
                using (var f = new Fixture()) {
                    var original = Directory.GetAccessControl(f.Root);
                    var denied = Directory.GetAccessControl(f.Root);
                    denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.CreateFiles, AccessControlType.Deny));
                    try {
                        Directory.SetAccessControl(f.Root, denied);
                        Throws<UnauthorizedAccessException>(() => { using (new OperationLock(f.Root)) { } });
                        Assert(!File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "denied lock created a file"); f.Preserved();
                    } finally { Directory.SetAccessControl(f.Root, original); }
                }
            });
            Test("operation lock retains ownership through disposal ordering and wrong-thread release", () => {
                using (var f = new Fixture())
                using (var outer = new OperationLock(f.Root))
                using (var inner = new OperationLock(f.Root)) {
                    Exception failure = null;
                    var thread = new Thread(() => { try { Throws<InvalidOperationException>(() => outer.Dispose()); } catch (Exception error) { failure = error; } });
                    thread.Start(); thread.Join(); if (failure != null) throw failure;
                    outer.Dispose(); outer.Dispose();
                    Assert(File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "outer release lost the nested lease");
                    inner.Dispose(); inner.Dispose();
                    Assert(!File.Exists(Path.Combine(f.Root, ".q2jump-operation.lock")), "final repeated release left a lock");
                }
            });
            Console.WriteLine("Installation fixtures: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1;
        }
    }
}
