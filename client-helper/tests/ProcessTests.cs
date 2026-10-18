// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Q2JumpStarter
{
    internal static class ProcessTests
    {
        private static int assertions;
        private static string workspace, root, other;

        public static int Main()
        {
            workspace = Path.Combine(Path.GetTempPath(), "q2jump-process-fixtures-" + Guid.NewGuid().ToString("N"));
            root = Path.Combine(workspace, "selected"); other = Path.Combine(workspace, "other");
            Directory.CreateDirectory(root); Directory.CreateDirectory(other);
            try {
                TestMatchingPrograms();
                TestUnknownProgramsAndAccess();
                TestRacesAndDisposal();
                TestInventoryBounds();
                TestConfigurationClosure();
                TestGameCallerClosure();
                TestNativeModuleObservation();
                Console.WriteLine("Process closure fixtures passed: " + assertions + " assertions.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally {
                string full = Path.GetFullPath(workspace);
                if (Path.GetDirectoryName(full) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    && Path.GetFileName(full).StartsWith("q2jump-process-fixtures-", StringComparison.Ordinal)) Directory.Delete(full, true);
            }
        }

        private static PackageFile File(string path, string role)
        {
            return new PackageFile(path, 1, new string('a', 64), role);
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        private static void TestNativeModuleObservation()
        {
            // Load a copied Windows library, never a client or downloaded executable.
            // A new observation must see modules loaded since an earlier check.
            string library = Path.Combine(root, "fixture-runtime.dll");
            System.IO.File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll"), library);
            var observe = typeof(WindowsManagedProcessSource).GetMethod("Observe", BindingFlags.NonPublic | BindingFlags.Static);
            using (var current = System.Diagnostics.Process.GetCurrentProcess()) {
                string executable = current.MainModule.FileName;
                var before = (ManagedProcessObservation)observe.Invoke(null, new object[] { current });
                Assert(before.ModulesComplete && !before.Exited && before.Id == current.Id, "Native observer identifies its running process completely.");
                Assert(!new HashSet<string>(before.Modules, StringComparer.OrdinalIgnoreCase).Contains(library), "Fixture library must not be loaded before the test.");
                IntPtr module = LoadLibrary(library);
                if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try {
                    current.Refresh();
                    var loaded = (ManagedProcessObservation)observe.Invoke(null, new object[] { current });
                    Assert(loaded.ModulesComplete && new HashSet<string>(loaded.Modules, StringComparer.OrdinalIgnoreCase).Contains(library), "A freshly loaded affected library must be observed.");
                    var access = new FakeAccess();
                    bool blocked = false;
                    try { ManagedProcesses.RequireGameClosed(root, new[] { File("fixture-runtime.dll", "runtime") }, current.Id, executable, new FakeProcesses(loaded), access); }
                    catch (ProcessClosureException error) { blocked = error.ProcessId == current.Id && error.AffectedPath == library; }
                    Assert(blocked && access.Paths.Count == 0, "A native module observation blocks even the allowed caller before file access.");
                } finally { FreeLibrary(module); }
                current.Refresh();
                var unloaded = (ManagedProcessObservation)observe.Invoke(null, new object[] { current });
                Assert(unloaded.ModulesComplete && !new HashSet<string>(unloaded.Modules, StringComparer.OrdinalIgnoreCase).Contains(library), "Native module observations are fresh after unload, too.");
            }
        }

        private static PackageFile[] Game()
        {
            return new[] { File("q2pro_race.exe", "client"), File("vcruntime140.dll", "runtime"), File("jump/pics/prochars.png", "resource") };
        }

        private static ManagedProcessObservation Process(int id, string name, string executable,
            bool complete = true, bool exited = false, params string[] modules)
        {
            return new ManagedProcessObservation(id, name, executable, modules, complete, exited, complete ? null : "Access denied");
        }

        private static void TestMatchingPrograms()
        {
            var access = new FakeAccess();
            var running = Process(101, "q2pro_race", Path.Combine(root, "q2pro_race.exe"));
            ProcessClosureException error = Blocked(Game(), new FakeProcesses(running), access);
            Assert(error.ProcessId == 101 && error.AffectedPath.EndsWith("q2pro_race.exe", StringComparison.Ordinal), "Selected client is identified by executable path even if the file is missing.");
            Assert(access.Paths.Count == 0, "A visible running owner is reported before generic access probes.");
            error = Blocked(Game(), new FakeProcesses(Process(102, "q2pro_speed", Path.Combine(root, "q2pro_speed.exe"))), new FakeAccess());
            Assert(error.ProcessId == 102, "An independently started alternative client in the selected root must close.");
            var shared = Process(103, "renamed-custom-client", Path.Combine(other, "anything.exe"), true, false, Path.Combine(root, "vcruntime140.dll").ToUpperInvariant());
            error = Blocked(Game(), new FakeProcesses(shared), new FakeAccess());
            Assert(error.ProcessId == 103 && error.Message.Contains("loaded"), "All process names are checked for shared affected runtime modules.");
            var partial = Process(104, "unrelated-name", Path.Combine(other, "anything.exe"), false, false, Path.Combine(root, "vcruntime140.dll"));
            Assert(Blocked(Game(), new FakeProcesses(partial), new FakeAccess()).ProcessId == 104, "An identified affected module blocks even when enumeration is incomplete.");
            var extended = Process(105, "custom", Path.Combine(other, "anything.exe"), true, false, "\\\\?\\" + Path.Combine(root, "vcruntime140.dll"));
            Assert(Blocked(Game(), new FakeProcesses(extended), new FakeAccess()).ProcessId == 105, "Extended Win32 module paths match their ordinary affected paths.");
            var elsewhere = Process(106, "q2pro_race", Path.Combine(other, "q2pro_race.exe"), true, false, Path.Combine(other, "vcruntime140.dll"));
            access = new FakeAccess();
            ManagedProcesses.RequireClosed(root, Game(), new FakeProcesses(elsewhere), access);
            Assert(access.Paths.Count == 3 && access.Disposed == 3, "An inspectable independent installation is allowed when it shares no affected files.");
            var launcher = new[] { File("Q2JUMP-Launcher.exe", "launcher"), File("Q2JUMP-Update.exe", "helper") };
            error = Blocked(launcher, new FakeProcesses(Process(107, "Q2JUMP-Launcher", Path.Combine(root, "Q2JUMP-Launcher.exe"))), new FakeAccess());
            Assert(error.ProcessId == 107, "There is no exception for a running affected launcher.");
            var resources = new[] { File("jump/pics/prochars.png", "resource") };
            Assert(Blocked(resources, new FakeProcesses(running), new FakeAccess()).ProcessId == 101, "A resource-only repair still requires the selected running client to close.");
        }

        private static void TestUnknownProgramsAndAccess()
        {
            var unknownGame = Process(201, "q2pro_race", null, false);
            ProcessClosureException error = Blocked(Game(), new FakeProcesses(unknownGame), new FakeAccess());
            Assert(error.ProcessId == 201 && error.Message.Contains("Cannot verify"), "Inaccessible relevant executable identity fails with an actionable closure message.");
            var unknownModules = Process(202, "q2pro_speed", Path.Combine(other, "q2pro_speed.exe"), false);
            Assert(Blocked(Game(), new FakeProcesses(unknownModules), new FakeAccess()).ProcessId == 202, "Another Q2 client with inaccessible modules cannot be assumed unrelated.");
            var malformed = Process(203, "quake2", "relative.exe", true);
            Assert(Blocked(Game(), new FakeProcesses(malformed), new FakeAccess()).ProcessId == 203, "Unusable relevant image paths remain ambiguous.");
            var protectedSystem = Process(4, "System", null, false);
            var access = new FakeAccess(); var source = new FakeProcesses(protectedSystem);
            ManagedProcesses.RequireClosed(root, Game(), source, access);
            Assert(source.Calls == 2 && access.Paths.Count == 3, "Protected unrelated system processes do not block every operation; all affected files are still probed.");
            var opaqueRenamed = Process(204, "unknown-client-name", null, false);
            access = new FakeAccess { FailAt = 1, Failure = new IOException("sharing violation") };
            error = Blocked(Game(), new FakeProcesses(opaqueRenamed), access);
            Assert(error.ProcessId == null && error.AffectedPath == Path.Combine(root, "q2pro_race.exe"), "Opaque owners still block through mandatory affected-file access checks.");
            Assert(error.Message.Contains("Close programs") && error.Message.Contains("permissions"), "Access failures do not invent a process identity or claim all errors are locks.");
            access = new FakeAccess { FailAt = 2, Failure = new UnauthorizedAccessException("denied") };
            error = Blocked(Game(), new FakeProcesses(protectedSystem), access);
            Assert(error.AffectedPath.EndsWith("vcruntime140.dll", StringComparison.Ordinal) && access.Disposed == 1, "Permission failure reports the exact affected file and releases prior probes.");
            access = new FakeAccess { Missing = true };
            ManagedProcesses.RequireClosed(root, Game(), new FakeProcesses(protectedSystem), access);
            Assert(access.Paths.Count == 3 && access.Disposed == 0, "Absent files in a new installation require no open handle and do not fail.");
            source = new FakeProcesses(protectedSystem) { Failure = new Win32Exception(5), FailureCall = 1 };
            error = Blocked(Game(), source, new FakeAccess());
            Assert(error.ProcessId == null && error.Message.Contains("process list"), "Unavailable process enumeration stops the operation without fabricated closure proof.");
        }

        private static void TestRacesAndDisposal()
        {
            var exited = Process(301, "q2pro_race", Path.Combine(root, "q2pro_race.exe"), false, true);
            var access = new FakeAccess();
            ManagedProcesses.RequireClosed(root, Game(), new FakeProcesses(exited), access);
            Assert(access.Paths.Count == 3, "A confirmed exit during inspection is harmless, subject to file access checks.");
            var source = new FakeProcesses();
            source.Second = new[] { Process(302, "q2pro_race", Path.Combine(root, "q2pro_race.exe")) };
            access = new FakeAccess();
            source.BeforeSnapshot = delegate(int call) { if (call == 2) Assert(access.Open == 3, "All successful exclusive handles are held during the second process snapshot."); };
            Assert(Blocked(Game(), source, access).ProcessId == 302, "A program appearing during the file checks is detected by the second snapshot.");
            Assert(access.Open == 0 && access.Disposed == 3 && access.DisposalOrder[0] == 3, "Probe handles are released in reverse order on a late conflict.");
            access = new FakeAccess();
            source = new FakeProcesses { Failure = new Win32Exception(299), FailureCall = 2 };
            Blocked(Game(), source, access);
            Assert(access.Open == 0 && access.Disposed == 3, "A second snapshot failure also releases every file probe.");
            access = new FakeAccess(); source = new FakeProcesses();
            ManagedProcesses.RequireClosed(root, Game(), source, access);
            Assert(access.Open == 0 && source.Calls == 2, "Success releases all handles: this callback does not pretend to lease future process closure.");
        }

        private static void TestInventoryBounds()
        {
            bool rejected = false;
            try { ManagedProcesses.RequireClosed(root, new PackageFile[0], new FakeProcesses(), new FakeAccess()); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "An empty affected inventory is rejected.");
            rejected = false;
            try { ManagedProcesses.RequireClosed(root, new[] { File("q2pro_race.exe", "client"), File("q2pro_race.exe", "client") }, new FakeProcesses(), new FakeAccess()); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Duplicate affected paths are rejected.");
            rejected = false;
            try { ManagedProcesses.RequireClosed(root, new[] { File("baseq2/pak0.pak", "resource") }, new FakeProcesses(), new FakeAccess()); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Affected paths remain bound to the existing managed-file policy.");
            rejected = false;
            try { File("../outside.exe", "client"); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Outside paths cannot enter the package file contract.");
            var observations = new ManagedProcessObservation[ManagedProcesses.MaximumProcesses + 1];
            for (int i = 0; i < observations.Length; i++) observations[i] = Process(i, "System", null, false, true);
            ProcessClosureException error = Blocked(Game(), new FakeProcesses(observations), new FakeAccess());
            Assert(error.Message.Contains("Cannot verify"), "Oversized observations do not silently truncate process evidence.");
            Assert(!System.IO.File.Exists(Path.Combine(root, "q2pro_race.exe")), "All process and file-access tests use injected observations; no game executable was created or run.");
        }

        private static void TestConfigurationClosure()
        {
            const int caller = 501;
            string config = Path.Combine(root, "jump", "q2config.cfg");
            var ownLauncher = Process(caller, "Q2JUMP-Launcher", Path.Combine(root, "Q2JUMP-Launcher.exe"));
            var access = new FakeAccess(); var source = new FakeProcesses(ownLauncher);
            source.BeforeSnapshot = call => { if (call == 2) Assert(access.Open == 1, "The exact configuration handle remains held during the second snapshot."); };
            ManagedProcesses.RequireConfigurationClosed(root, caller, source, access);
            Assert(source.Calls == 2 && access.Paths.Count == 1 && access.Paths[0] == config, "Configuration closure probes only native jump/q2config.cfg and permits the co-located calling launcher.");
            Assert(access.Open == 0 && access.Disposed == 1, "Successful configuration closure releases its exclusive handle.");
            var package = new[] { File("Q2JUMP-Launcher.exe", "launcher") };
            Assert(Blocked(package, new FakeProcesses(ownLauncher), new FakeAccess()).ProcessId == caller, "The configuration caller exception never exempts an affected package executable.");
            foreach (var otherProcess in new[] {
                Process(502, "q2pro_race", Path.Combine(root, "q2pro_race.exe")),
                Process(503, "q2pro_speed", Path.Combine(root, "q2pro_speed.exe")),
                Process(504, "Q2JUMP-Launcher", Path.Combine(root, "Q2JUMP-Launcher.exe")),
                Process(505, "custom-client", Path.Combine(root, "renamed.exe")),
                Process(506, "quake2", null, false),
                Process(507, "q2pro_race", Path.Combine(other, "q2pro_race.exe"), false)
            }) {
                access = new FakeAccess();
                var error = ConfigurationBlocked(caller, new FakeProcesses(ownLauncher, otherProcess), access);
                Assert(error.ProcessId == otherProcess.Id && access.Paths.Count == 0, "Other selected-root or relevant uninspectable processes still block configuration writes: " + otherProcess.Id);
            }
            var protectedSystem = Process(4, "System", null, false);
            source = new FakeProcesses(ownLauncher, protectedSystem, Process(508, "q2pro_race", Path.Combine(other, "q2pro_race.exe")));
            access = new FakeAccess { Missing = true };
            ManagedProcesses.RequireConfigurationClosed(root, caller, source, access);
            Assert(source.Calls == 2 && access.Paths.Count == 1 && access.Disposed == 0, "Missing config requires no creation; unrelated inspected clients and protected system processes alone do not block.");
            access = new FakeAccess { FailAt = 1, Failure = new IOException("sharing violation") };
            var locked = ConfigurationBlocked(caller, new FakeProcesses(ownLauncher), access);
            Assert(locked.ProcessId == null && locked.AffectedPath == config && locked.Message.Contains("Close programs"), "Configuration sharing failures identify the exact path without inventing an owner.");
            access = new FakeAccess { FailAt = 1, Failure = new UnauthorizedAccessException("denied") };
            Assert(ConfigurationBlocked(caller, new FakeProcesses(ownLauncher), access).Message.Contains("permissions"), "Configuration permission failures remain actionable without elevation.");
            access = new FakeAccess(); source = new FakeProcesses(ownLauncher) {
                Second = new[] { ownLauncher, Process(509, "Q2JUMP-Launcher", Path.Combine(root, "Q2JUMP-Launcher.exe")) }
            };
            Assert(ConfigurationBlocked(caller, source, access).ProcessId == 509 && access.Open == 0 && access.Disposed == 1,
                "A second launcher appearing during configuration access stops publication and releases the handle.");
            access = new FakeAccess(); source = new FakeProcesses(ownLauncher) { Failure = new Win32Exception(299), FailureCall = 2 };
            ConfigurationBlocked(caller, source, access);
            Assert(access.Open == 0 && access.Disposed == 1, "Configuration probe is disposed if the second process snapshot fails.");
            bool rejected = false;
            try { ManagedProcesses.RequireConfigurationClosed(root, 0, new FakeProcesses(), new FakeAccess()); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Configuration closure cannot exempt an unspecified system process ID.");
            Assert(!System.IO.File.Exists(config), "Injected configuration closure checks never create or write the player configuration.");
        }

        private static void TestGameCallerClosure()
        {
            const int caller = 601;
            string launcherPath = Path.Combine(root, "Q2JUMP-Launcher.exe");
            var own = Process(caller, "Q2JUMP-Launcher", launcherPath, true, false, launcherPath, Path.Combine(other, "System.dll"));
            var access = new FakeAccess(); var source = new FakeProcesses(own);
            source.BeforeSnapshot = call => { if (call == 2) Assert(access.Open == Game().Length, "Game targets remain exclusively held during the second current-identity snapshot."); };
            ManagedProcesses.RequireGameClosed(root, Game(), caller, launcherPath, source, access);
            Assert(source.Calls == 2 && access.Paths.Count == 3 && access.Disposed == 3 && access.Open == 0, "Verified calling launcher may share the game root while every game target is checked.");
            Assert(Blocked(Game(), new FakeProcesses(own), new FakeAccess()).ProcessId == caller, "Strict helper/package closure retains its same-directory restriction.");
            foreach (var observation in new[] {
                Process(caller, "Q2JUMP-Launcher", launcherPath, false),
                Process(caller, "Q2JUMP-Launcher", null, true),
                Process(caller, "Q2JUMP-Launcher", "relative.exe", true),
                Process(caller, "Q2JUMP-Launcher", Path.Combine(other, "Q2JUMP-Launcher.exe")),
                Process(caller, "renamed-current", launcherPath, true, false, "relative.dll"),
                Process(caller, "Q2JUMP-Launcher", launcherPath, true, false, Path.Combine(root, "vcruntime140.dll"))
            }) {
                access = new FakeAccess();
                Assert(GameBlocked(caller, launcherPath, new FakeProcesses(observation), access).ProcessId == caller && access.Paths.Count == 0,
                    "Unverified/mismatched current identity or affected module cannot use the same-directory exception.");
            }
            var currentClient = Process(caller, "renamed-launcher", Path.Combine(root, "q2pro_race.exe"));
            Assert(GameBlocked(caller, currentClient.ExecutablePath, new FakeProcesses(currentClient), new FakeAccess()).ProcessId == caller,
                "An affected current executable remains blocked even with the expected PID/path.");
            foreach (var another in new[] {
                Process(602, "Q2JUMP-Launcher", launcherPath),
                Process(603, "q2pro_race", Path.Combine(root, "q2pro_race.exe")),
                Process(604, "custom", Path.Combine(root, "custom.exe")),
                Process(605, "q2pro_speed", Path.Combine(other, "q2pro_speed.exe"), false),
                Process(606, "anything", Path.Combine(other, "renamed.exe"), true, false, Path.Combine(root, "vcruntime140.dll"))
            }) Assert(GameBlocked(caller, launcherPath, new FakeProcesses(own, another), new FakeAccess()).ProcessId == another.Id,
                "The caller exception does not bypass other processes or affected modules.");
            Assert(GameBlocked(caller, launcherPath, new FakeProcesses(), new FakeAccess()).ProcessId == caller, "Omitting the current identity from a snapshot fails closed.");
            Assert(GameBlocked(caller, launcherPath, new FakeProcesses(own, own), new FakeAccess()).ProcessId == caller, "Duplicate current identities are not complete process evidence.");
            source = new FakeProcesses(own) { Second = new[] { Process(caller, "Q2JUMP-Launcher", launcherPath, false) } }; access = new FakeAccess();
            GameBlocked(caller, launcherPath, source, access);
            Assert(access.Disposed == 3 && access.Open == 0, "Losing current identity on the second snapshot releases all handles.");
            source = new FakeProcesses(own) { Second = new[] { own, Process(607, "q2pro_race", Path.Combine(root, "q2pro_race.exe")) } }; access = new FakeAccess();
            Assert(GameBlocked(caller, launcherPath, source, access).ProcessId == 607 && access.Disposed == 3, "A new client arriving during caller-aware file checks is still detected.");
            access = new FakeAccess { FailAt = 2, Failure = new IOException("sharing violation") };
            Assert(GameBlocked(caller, launcherPath, new FakeProcesses(own), access).AffectedPath.EndsWith("vcruntime140.dll", StringComparison.Ordinal) && access.Disposed == 1,
                "Co-location never bypasses mandatory target access checks.");
            bool rejected = false;
            try { ManagedProcesses.RequireGameClosed(root, new[] { File("Q2JUMP-Launcher.exe", "launcher") }, caller, launcherPath, new FakeProcesses(own), new FakeAccess()); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Game caller exception cannot be requested for launcher package files.");
            rejected = false;
            try { ManagedProcesses.RequireGameClosed(root, Game(), 0, launcherPath, new FakeProcesses(own), new FakeAccess()); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Unspecified current PID cannot receive an exception.");
            rejected = false;
            try { ManagedProcesses.RequireGameClosed(root, Game(), caller, "relative.exe", new FakeProcesses(own), new FakeAccess()); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "The expected caller executable must be an absolute local path.");
            var launcherFiles = new[] { File("Q2JUMP-Launcher.exe", "launcher"), File("Q2JUMP-Update.exe", "helper"), File("licenses/launcher/COPYING.txt", "notice"), File("source/launcher/SOURCE.txt", "source") };
            access = new FakeAccess(); ManagedProcesses.RequireClosed(root, launcherFiles, new FakeProcesses(), access);
            Assert(access.Paths.Count == 4 && access.Disposed == 4, "Strict launcher closure recognizes namespaced notices and source references.");
            Assert(!System.IO.File.Exists(launcherPath), "All caller identity cases use injected observations without creating or starting executables.");
        }

        private static ProcessClosureException GameBlocked(int caller, string executable, FakeProcesses source, FakeAccess access)
        {
            try { ManagedProcesses.RequireGameClosed(root, Game(), caller, executable, source, access); }
            catch (ProcessClosureException error) { return error; }
            throw new Exception("Expected an actionable game caller closure failure.");
        }

        private static ProcessClosureException ConfigurationBlocked(int caller, FakeProcesses source, FakeAccess access)
        {
            try { ManagedProcesses.RequireConfigurationClosed(root, caller, source, access); }
            catch (ProcessClosureException error) { return error; }
            throw new Exception("Expected an actionable configuration process closure failure.");
        }

        private static ProcessClosureException Blocked(IEnumerable<PackageFile> files, FakeProcesses source, FakeAccess access)
        {
            try { ManagedProcesses.RequireClosed(root, files, source, access); }
            catch (ProcessClosureException error) { return error; }
            throw new Exception("Expected an actionable process closure failure.");
        }

        private static void Assert(bool result, string detail) { assertions++; if (!result) throw new Exception("FAIL: " + detail); }

        private sealed class FakeProcesses : IManagedProcessSource
        {
            internal int Calls, FailureCall;
            internal Exception Failure;
            internal IEnumerable<ManagedProcessObservation> Second;
            internal Action<int> BeforeSnapshot;
            private readonly IEnumerable<ManagedProcessObservation> first;
            internal FakeProcesses(params ManagedProcessObservation[] observations) { first = observations; }
            public IEnumerable<ManagedProcessObservation> Snapshot()
            {
                Calls++;
                if (BeforeSnapshot != null) BeforeSnapshot(Calls);
                if (Failure != null && Calls == FailureCall) throw Failure;
                return Calls > 1 && Second != null ? Second : first;
            }
        }

        private sealed class FakeAccess : IManagedFileAccess
        {
            internal readonly List<string> Paths = new List<string>();
            internal readonly List<int> DisposalOrder = new List<int>();
            internal int Open, Disposed, FailAt;
            internal bool Missing;
            internal Exception Failure;
            public IDisposable OpenExclusive(string path)
            {
                Paths.Add(path);
                if (Paths.Count == FailAt) throw Failure;
                if (Missing) return null;
                Open++;
                return new FakeHandle(this, Paths.Count);
            }
            private sealed class FakeHandle : IDisposable
            {
                private FakeAccess access;
                private readonly int id;
                internal FakeHandle(FakeAccess access, int id) { this.access = access; this.id = id; }
                public void Dispose() { if (access != null) { access.Open--; access.Disposed++; access.DisposalOrder.Add(id); access = null; } }
            }
        }
    }
}
