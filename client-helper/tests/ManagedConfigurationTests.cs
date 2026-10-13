// SPDX-License-Identifier: GPL-2.0-or-later
// Synthetic configuration files only. No game, installer or process execution.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    internal static class ManagedConfigurationTests
    {
        private static int passed;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Reject<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "q2jump-mcfg-" + Guid.NewGuid().ToString("N"));
            internal string Descriptor { get { return PathOf(".q2jump/configuration.json"); } }
            internal GamePaths Paths { get { return new GamePaths(Root); } }
            internal Fixture(bool owned = true)
            { Directory.CreateDirectory(Root); if (owned) Write(".q2jump/owner", "Q2JUMP_MANAGED_FILES_V1\n"); }
            internal string PathOf(string relative) { return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)); }
            internal void Write(string relative, string data)
            { string path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, data, new UTF8Encoding(false)); }
            internal ConfigurationSnapshot Ensure(Action<string> boundary = null)
            { using (new OperationLock(Root)) return ManagedConfiguration.EnsureReadyBackup(Paths, CancellationToken.None, boundary); }
            internal PreparedConfigurationLaunch Prepare()
            { using (new OperationLock(Root)) return ManagedConfiguration.PrepareFirstLaunch(Paths, CancellationToken.None); }
            internal void Mark(PreparedConfigurationLaunch preparation, Action<string> boundary = null)
            { using (new OperationLock(Root)) ManagedConfiguration.MarkLaunchStarted(preparation, CancellationToken.None, boundary); }
            internal Dictionary<string, object> Record() { return ReleaseJson.Parse(File.ReadAllBytes(Descriptor)); }
            internal void Rewrite(Action<Dictionary<string, object>> update)
            { var record = Record(); update(record); File.WriteAllText(Descriptor, new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false)); }
            internal int SnapshotCount()
            { return Directory.GetDirectories(PathOf(".q2jump/configurations"), "configuration-*").Length; }
            public void Dispose()
            {
                string path = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("q2jump-mcfg-", StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup.");
                Directory.Delete(path, true);
            }
        }
        private static void Run()
        {
            Test("unowned and foreign metadata are never claimed", delegate {
                using (var f = new Fixture(false))
                {
                    Reject<InvalidDataException>(() => f.Ensure()); Check(!Directory.Exists(f.PathOf(".q2jump")), "Unowned inspection created metadata.");
                    f.Write(".q2jump/owner", "foreign owner"); byte[] owner = File.ReadAllBytes(f.PathOf(".q2jump/owner"));
                    Reject<InvalidDataException>(() => f.Ensure()); Check(File.ReadAllBytes(f.PathOf(".q2jump/owner")).SequenceEqual(owner) && !File.Exists(f.Descriptor), "Foreign owner changed.");
                }
            });
            Test("read-only validation never prepares a missing backup", delegate {
                using (var f = new Fixture())
                { Reject<InvalidDataException>(() => ManagedConfiguration.ValidateReadyBackup(f.Paths, CancellationToken.None)); Check(!Directory.Exists(f.PathOf(".q2jump/configurations")) && !File.Exists(f.Descriptor), "Read-only validation wrote metadata."); }
            });
            Test("empty configurations are recorded as absent without seeding", delegate {
                using (var f = new Fixture())
                {
                    var snapshot = f.Ensure(); ManagedConfiguration.ValidateReadyBackup(f.Paths, CancellationToken.None);
                    Check(snapshot.Files.Count == 10 && snapshot.Files.All(file => !file.Exists) && !Directory.Exists(f.PathOf("jump")), "Missing configurations were seeded.");
                    Check(!ReleaseJson.Boolean(f.Record(), "firstManagedLaunchStarted"), "Readiness was recorded as a launch.");
                }
            });
            Test("an active installation journal permits owner-validated initial backups", delegate {
                using (var f = new Fixture())
                { f.Write(".q2jump/active.json", "synthetic transaction owned by caller"); f.Ensure(); Check(File.ReadAllText(f.PathOf(".q2jump/active.json")) == "synthetic transaction owned by caller", "Backup changed installation transaction."); }
            });
            Test("startup scripts and configuration bytes are backed up without execution", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "unbindall\nseta name Original\n"); f.Write("baseq2/autoexec.cfg", "exec hidden.cfg\nalias marker quit\n");
                    f.Write("jump/custom.cfg", "exec uninspected.cfg\n"); var snapshot = f.Ensure();
                    Check(File.ReadAllText(Path.Combine(snapshot.Directory, "baseq2/autoexec.cfg")) == "exec hidden.cfg\nalias marker quit\n", "Script backup changed.");
                    Check(!File.Exists(Path.Combine(snapshot.Directory, "jump/custom.cfg")) && File.ReadAllText(f.PathOf("jump/q2config.cfg")) == "unbindall\nseta name Original\n", "Backup executed, seeded or scanned unrelated scripts.");
                }
            });
            Test("unchanged readiness preserves the descriptor and snapshot", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Original\n"); var first = f.Ensure(); byte[] descriptor = File.ReadAllBytes(f.Descriptor);
                    var second = f.Ensure(); Check(first.Directory == second.Directory && f.SnapshotCount() == 1 && File.ReadAllBytes(f.Descriptor).SequenceEqual(descriptor), "Unchanged readiness made a new backup.");
                }
            });
            Test("first managed launch refreshes changed new and deleted startup files", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Before\n"); f.Write("baseq2/autoexec.cfg", "echo old\n"); var first = f.Ensure();
                    f.Write("jump/q2config.cfg", "seta name After\n"); f.Write("jump/postinit.cfg", "echo added\n"); File.Delete(f.PathOf("baseq2/autoexec.cfg"));
                    var preparation = f.Prepare(); var second = f.Ensure();
                    Check(preparation.RequiresMark && f.SnapshotCount() == 2 && first.Directory != second.Directory, "Changed first-launch settings did not refresh.");
                    Check(File.ReadAllText(Path.Combine(first.Directory, "jump/q2config.cfg")) == "seta name Before\n" && File.ReadAllText(Path.Combine(second.Directory, "jump/q2config.cfg")) == "seta name After\n", "Refresh discarded earlier originals.");
                    Check(!second.Files.Single(file => file.Path == "baseq2/autoexec.cfg").Exists && second.Files.Single(file => file.Path == "jump/postinit.cfg").Exists, "Added/deleted state not captured.");
                }
            });
            Test("failed process creation leaves first launch unmarked and refreshable", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Before\n"); f.Prepare(); // Caller deliberately does not mark a failed start.
                    f.Write("jump/q2config.cfg", "seta name Retry\n"); var retry = f.Prepare();
                    Check(retry.RequiresMark && !ReleaseJson.Boolean(f.Record(), "firstManagedLaunchStarted") && f.SnapshotCount() == 2, "Failed launch suppressed backup refresh.");
                }
            });
            Test("successful launch can mark after game writes without taking a new backup", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Before\n"); var preparation = f.Prepare();
                    f.Write("jump/q2config.cfg", "seta name WrittenByGame\n"); f.Mark(preparation); byte[] marked = File.ReadAllBytes(f.Descriptor);
                    var later = f.Prepare(); f.Mark(later); f.Ensure(); ManagedConfiguration.ValidateReadyBackup(f.Paths, CancellationToken.None);
                    Check(!later.RequiresMark && ReleaseJson.Boolean(f.Record(), "firstManagedLaunchStarted") && f.SnapshotCount() == 1 && File.ReadAllBytes(f.Descriptor).SequenceEqual(marked), "Later launches repeat initial backups.");
                }
            });
            Test("a stale preparation cannot mark a refreshed descriptor", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Before\n"); var stale = f.Prepare();
                    f.Write("jump/q2config.cfg", "seta name Refresh\n"); var current = f.Prepare();
                    Reject<IOException>(() => f.Mark(stale)); Check(!ReleaseJson.Boolean(f.Record(), "firstManagedLaunchStarted"), "Stale receipt marked launch.");
                    f.Mark(current); Reject<IOException>(() => f.Mark(current));
                }
            });
            Test("preparation descriptor bytes are not mutable by a caller", delegate {
                using (var f = new Fixture())
                { var preparation = f.Prepare(); byte[] copy = preparation.Descriptor; copy[0] ^= 1; f.Mark(preparation); Check(ReleaseJson.Boolean(f.Record(), "firstManagedLaunchStarted"), "Caller mutated receipt identity."); }
            });
            Test("malformed oversized and duplicate-field descriptors fail without changes", delegate {
                foreach (string invalid in new[] { "{", new string('x', 16385), "{\"schema\":1,\"schema\":1}", "\uFEFF{}" })
                using (var f = new Fixture())
                {
                    f.Ensure(); f.Write(".q2jump/configuration.json", invalid); byte[] before = File.ReadAllBytes(f.Descriptor);
                    Reject<InvalidDataException>(() => f.Ensure()); Reject<InvalidDataException>(() => ManagedConfiguration.ValidateReadyBackup(f.Paths, CancellationToken.None));
                    Check(File.ReadAllBytes(f.Descriptor).SequenceEqual(before) && f.SnapshotCount() == 1, "Corrupt descriptor was replaced.");
                }
            });
            Test("descriptor root traversal references sizes hashes and flags are strict", delegate {
                foreach (string mode in new[] { "root", "snapshot", "snapshotSize", "snapshotSha256", "firstManagedLaunchStarted", "unknown" })
                using (var f = new Fixture())
                {
                    f.Ensure(); f.Rewrite(record => {
                        if (mode == "root") record[mode] = Path.Combine(f.Root, "other");
                        else if (mode == "snapshot") record[mode] = "../elsewhere";
                        else if (mode == "snapshotSize") record[mode] = 0;
                        else if (mode == "snapshotSha256") record[mode] = new string('0', 64);
                        else if (mode == "firstManagedLaunchStarted") record[mode] = "true";
                        else record.Add(mode, true);
                    });
                    Reject<InvalidDataException>(() => f.Prepare()); Check(f.SnapshotCount() == 1, "Invalid descriptor created backups.");
                }
            });
            Test("snapshot metadata must retain its exact recorded bytes", delegate {
                using (var f = new Fixture())
                { var snapshot = f.Ensure(); File.AppendAllText(Path.Combine(snapshot.Directory, "snapshot.json"), " "); Reject<InvalidDataException>(() => f.Ensure()); }
            });
            Test("hash-updated snapshot still cannot select an outside root or unknown file", delegate {
                foreach (string mode in new[] { "Root", "Directory", "Files" })
                using (var f = new Fixture())
                {
                    var snapshot = f.Ensure(); string path = Path.Combine(snapshot.Directory, "snapshot.json"); var record = ReleaseJson.Parse(File.ReadAllBytes(path));
                    if (mode == "Files") ReleaseJson.Object(ReleaseJson.Array(record, "Files", 10, 10)[0])["Path"] = "jump/other.cfg";
                    else record[mode] = Path.Combine(f.Root, "elsewhere");
                    byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(record)); File.WriteAllBytes(path, bytes);
                    f.Rewrite(descriptor => { descriptor["snapshotSize"] = bytes.Length; descriptor["snapshotSha256"] = ConfigurationBackups.Hash(bytes); });
                    Reject<InvalidDataException>(() => f.Ensure());
                }
            });
            Test("missing changed and unexpectedly present snapshot bytes are rejected", delegate {
                foreach (string mode in new[] { "missing", "changed", "unexpected" })
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Original\n"); var snapshot = f.Ensure(); string path = Path.Combine(snapshot.Directory, "jump/q2config.cfg");
                    if (mode == "missing") File.Delete(path); else if (mode == "changed") File.AppendAllText(path, "changed"); else File.WriteAllText(Path.Combine(snapshot.Directory, "jump/autoexec.cfg"), "unexpected");
                    Reject<InvalidDataException>(() => f.Ensure()); Reject<InvalidDataException>(() => ManagedConfiguration.ValidateReadyBackup(f.Paths, CancellationToken.None));
                }
            });
            Test("interruption after capture preserves orphan snapshots and retries safely", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Before\n");
                    Reject<IOException>(() => f.Ensure(point => { if (point == "captured") throw new IOException("fixture interruption"); }));
                    Check(!File.Exists(f.Descriptor) && f.SnapshotCount() == 1, "Capture interruption published a descriptor.");
                    f.Ensure(); Check(f.SnapshotCount() == 2, "Retry overwrote retained snapshot.");
                }
            });
            Test("interruption after atomic descriptor publication leaves valid reusable evidence", delegate {
                using (var f = new Fixture())
                {
                    Reject<ConfigurationPublicationException>(() => f.Ensure(point => { if (point == "descriptor-published") throw new IOException("fixture interruption"); }));
                    ManagedConfiguration.ValidateReadyBackup(f.Paths, CancellationToken.None); f.Ensure(); Check(f.SnapshotCount() == 1, "Atomic published evidence was not reusable.");
                }
            });
            Test("startup edits during descriptor publication prevent false readiness", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Before\n");
                    Reject<IOException>(() => f.Ensure(point => { if (point == "descriptor-published") f.Write("jump/q2config.cfg", "seta name Racing\n"); }));
                    var refreshed = f.Ensure(); Check(f.SnapshotCount() == 2 && File.ReadAllText(Path.Combine(refreshed.Directory, "jump/q2config.cfg")) == "seta name Racing\n", "Retry did not refresh the racing startup change.");
                }
            });
            Test("a concurrent descriptor edit is never reported as successful readiness", delegate {
                using (var f = new Fixture())
                {
                    f.Write("jump/q2config.cfg", "seta name Before\n"); f.Ensure(); f.Write("jump/q2config.cfg", "seta name Refresh\n");
                    Reject<IOException>(() => f.Ensure(point => { if (point == "captured") f.Write(".q2jump/configuration.json", "external metadata edit"); }));
                    Check(File.ReadAllText(f.Descriptor) == "external metadata edit", "Concurrent descriptor edit was silently overwritten.");
                }
            });
            Test("cancellation before preparation never creates a backup descriptor", delegate {
                using (var f = new Fixture())
                { Reject<OperationCanceledException>(() => ManagedConfiguration.EnsureReadyBackup(f.Paths, new CancellationToken(true))); Check(!File.Exists(f.Descriptor) && !Directory.Exists(f.PathOf(".q2jump/configurations")), "Cancelled preparation wrote files."); }
            });
        }
        private static int Main()
        {
            try { Run(); Console.WriteLine("Managed configuration fixtures: " + passed + " passed."); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
