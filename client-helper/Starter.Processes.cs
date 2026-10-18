// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Q2JumpStarter
{
    internal sealed class ManagedProcessObservation
    {
        internal readonly int Id;
        internal readonly string Name, ExecutablePath, Detail;
        internal readonly ReadOnlyCollection<string> Modules;
        internal readonly bool ModulesComplete, Exited;

        internal ManagedProcessObservation(int id, string name, string executablePath,
            IEnumerable<string> modules, bool modulesComplete, bool exited, string detail)
        {
            Id = id; Name = name; ExecutablePath = executablePath;
            Modules = new List<string>(modules ?? new string[0]).AsReadOnly();
            ModulesComplete = modulesComplete; Exited = exited; Detail = detail;
        }
    }

    internal interface IManagedProcessSource
    {
        IEnumerable<ManagedProcessObservation> Snapshot();
    }

    internal interface IManagedFileAccess
    {
        // Null means the target is absent. A returned handle is held until the check finishes.
        IDisposable OpenExclusive(string path);
    }

    internal sealed class ProcessClosureException : IOException
    {
        internal readonly int? ProcessId;
        internal readonly string ProcessName, AffectedPath;

        internal ProcessClosureException(string message, int? processId, string processName,
            string affectedPath, Exception inner = null) : base(message, inner)
        {
            ProcessId = processId; ProcessName = processName; AffectedPath = affectedPath;
        }
    }

    internal static class ManagedProcesses
    {
        internal const int MaximumProcesses = 32768;
        private static readonly HashSet<string> KnownClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "r1q2_race", "r1q2", "q2pro_race", "q2pro", "q2pro_speed", "quake2", "q2jump-launcher", "q2jump-update"
        };

        internal static void RequireClosed(string root, IEnumerable<PackageFile> files)
        {
            RequireClosed(root, files, new WindowsManagedProcessSource(), new WindowsManagedFileAccess());
        }

        // A game update may be initiated by the launcher stored beside the client.
        // Its own identity is trusted only from the running process, never from a plan.
        internal static void RequireGameClosed(string root, IEnumerable<PackageFile> files)
        {
            using (var current = Process.GetCurrentProcess()) {
                string executable;
                try { executable = current.MainModule.FileName; }
                catch (Exception error) {
                    if (!ObservationFailure(error)) throw;
                    throw new ProcessClosureException("Cannot verify the calling launcher's executable. Retry before changing game files.", current.Id, null, null, error);
                }
                RequireGameClosed(root, files, current.Id, executable, new WindowsManagedProcessSource(), new WindowsManagedFileAccess());
            }
        }

        internal static void RequireGameClosed(string root, IEnumerable<PackageFile> files, int currentProcessId,
            string currentExecutable, IManagedProcessSource processes, IManagedFileAccess access)
        {
            if (currentProcessId <= 0) throw new ArgumentOutOfRangeException("currentProcessId");
            string executable = NormalizeObservedPath(currentExecutable);
            if (executable == null) throw new ArgumentException("The calling launcher requires an ordinary absolute executable path.", "currentExecutable");
            RequireClosed(root, files, processes, access, currentProcessId, executable);
        }

        internal static void RequireConfigurationClosed(string root)
        {
            using (var current = Process.GetCurrentProcess())
                RequireConfigurationClosed(root, current.Id, new WindowsManagedProcessSource(), new WindowsManagedFileAccess());
        }

        // Settings import changes exactly this player file. The calling launcher may
        // share the game root, but its exception never applies to package operations.
        internal static void RequireConfigurationClosed(string root, int currentProcessId,
            IManagedProcessSource processes, IManagedFileAccess access)
        {
            if (currentProcessId <= 0) throw new ArgumentOutOfRangeException("currentProcessId");
            if (processes == null) throw new ArgumentNullException("processes");
            if (access == null) throw new ArgumentNullException("access");
            root = InstallationPaths.Root(root);
            const string relative = "jump/q2config.cfg";
            string path = InstallationPaths.Under(root, relative);
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            var relevantNames = new HashSet<string>(KnownClients, StringComparer.OrdinalIgnoreCase);
            RequireSnapshotClosed(root, affected, relevantNames, processes, currentProcessId);
            IDisposable handle = null;
            try {
                try { handle = access.OpenExclusive(InstallationPaths.Under(root, relative)); }
                catch (Exception error) {
                    if (!ObservationFailure(error)) throw;
                    throw new ProcessClosureException("Cannot get exclusive access to " + relative +
                        ". Close programs using this file and retry. If it remains inaccessible, check its permissions. No program was terminated.",
                        null, null, path, error);
                }
                RequireSnapshotClosed(root, affected, relevantNames, processes, currentProcessId);
            } finally { if (handle != null) handle.Dispose(); }
        }

        // A point-in-time boundary check, not a lease preventing later independent launches.
        internal static void RequireClosed(string root, IEnumerable<PackageFile> files,
            IManagedProcessSource processes, IManagedFileAccess access)
        { RequireClosed(root, files, processes, access, null, null); }

        private static void RequireClosed(string root, IEnumerable<PackageFile> files,
            IManagedProcessSource processes, IManagedFileAccess access, int? currentProcessId, string currentExecutable)
        {
            if (files == null) throw new ArgumentNullException("files");
            if (processes == null) throw new ArgumentNullException("processes");
            if (access == null) throw new ArgumentNullException("access");
            root = InstallationPaths.Root(root);
            var inventory = new List<PackageFile>();
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relevantNames = new HashSet<string>(KnownClients, StringComparer.OrdinalIgnoreCase);
            foreach (PackageFile file in files) {
                if (file == null || inventory.Count >= 4096) throw new InvalidDataException("Invalid affected file inventory.");
                string component = file.Role == "launcher" || file.Role == "helper" ||
                    (file.Role == "resource" && file.Path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) ||
                    (file.Role == "notice" && file.Path.StartsWith("licenses/launcher/", StringComparison.OrdinalIgnoreCase)) ||
                    (file.Role == "source" && file.Path.StartsWith("source/launcher/", StringComparison.OrdinalIgnoreCase)) ? "launcher" : "game";
                // The current-process exception never authorizes a launcher package.
                InstallationPaths.ProgramFile(file.Path == GameClient.R1Q2.Executable ? "r1q2" : currentExecutable == null ? component : "game", file);
                string path = InstallationPaths.Under(root, file.Path);
                if (!affected.Add(path)) throw new InvalidDataException("Duplicate affected file path.");
                if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    relevantNames.Add(Path.GetFileNameWithoutExtension(path));
                inventory.Add(file);
            }
            if (inventory.Count == 0) throw new InvalidDataException("The affected file inventory is empty.");

            // Inspect first so a visible owner can be named before a generic file-lock error.
            RequireSnapshotClosed(root, affected, relevantNames, processes, currentProcessId, currentExecutable);
            var handles = new List<IDisposable>();
            try {
                foreach (PackageFile file in inventory) {
                    string path = InstallationPaths.Under(root, file.Path);
                    try {
                        IDisposable handle = access.OpenExclusive(path);
                        if (handle != null) handles.Add(handle);
                    } catch (Exception error) {
                        if (!ObservationFailure(error)) throw;
                        throw new ProcessClosureException("Cannot get exclusive access to " + file.Path +
                            ". Close programs using this file and retry. If it remains inaccessible, check its permissions. No program was terminated.",
                            null, null, path, error);
                    }
                }
                // Catch a process that appeared while existing affected files were being locked.
                RequireSnapshotClosed(root, affected, relevantNames, processes, currentProcessId, currentExecutable);
            } finally {
                for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            }
        }

        private static void RequireSnapshotClosed(string root, HashSet<string> affected,
            HashSet<string> relevantNames, IManagedProcessSource processes, int? allowedProcessId = null, string currentExecutable = null)
        {
            try {
                int count = 0; bool observedCurrent = false;
                IEnumerable<ManagedProcessObservation> snapshot = processes.Snapshot();
                if (snapshot == null) throw new InvalidOperationException("No process snapshot was returned.");
                foreach (ManagedProcessObservation process in snapshot) {
                    if (process == null || ++count > MaximumProcesses) throw new InvalidOperationException("Process snapshot is incomplete or exceeds the inspection limit.");
                    if (process.Exited) continue;
                    bool current = allowedProcessId.HasValue && process.Id == allowedProcessId.Value;
                    if (current && currentExecutable == null) continue; // Configuration-only exception.
                    string executable = NormalizeObservedPath(process.ExecutablePath);
                    string name = process.Name ?? "unknown program";
                    string label = name + " (PID " + process.Id + ")";
                    if (current) {
                        if (observedCurrent || executable == null || !process.ModulesComplete ||
                            !String.Equals(executable, currentExecutable, StringComparison.OrdinalIgnoreCase))
                            throw new ProcessClosureException("Cannot verify the calling launcher's executable and loaded files. Retry before changing game files.", process.Id, name, executable);
                        observedCurrent = true;
                    }
                    if (executable != null && (affected.Contains(executable) ||
                        (!current && String.Equals(Path.GetDirectoryName(executable), root, StringComparison.OrdinalIgnoreCase))))
                        throw new ProcessClosureException("Close " + label + " before changing this installation. It is running from " + executable + ".",
                            process.Id, name, executable);
                    bool malformedModule = false;
                    foreach (string module in process.Modules) {
                        string path = NormalizeObservedPath(module);
                        if (path == null) { malformedModule = true; continue; }
                        if (affected.Contains(path))
                            throw new ProcessClosureException("Close " + label + " before changing this installation. It has loaded " + path + ".",
                                process.Id, name, path);
                    }
                    string observedName = process.Name == null ? null : Path.GetFileNameWithoutExtension(process.Name);
                    bool relevant = observedName != null && relevantNames.Contains(observedName);
                    if ((current || relevant) && (executable == null || !process.ModulesComplete || malformedModule))
                        throw new ProcessClosureException("Cannot verify the executable and loaded files of " + label +
                            ". Close this program and retry; administrator access is not requested.", process.Id, name, null);
                    // Inaccessible unrelated system processes are not presumed to use the game.
                    // Mandatory exclusive-file checks still reject uninspectable owners/locks.
                }
                if (currentExecutable != null && !observedCurrent)
                    throw new ProcessClosureException("Cannot verify the calling launcher in the process snapshot. Retry before changing game files.", allowedProcessId, null, currentExecutable);
            } catch (ProcessClosureException) { throw; }
            catch (Exception error) {
                if (!ObservationFailure(error)) throw;
                throw new ProcessClosureException("Cannot verify which programs are using this installation. Retry after the process list is available.",
                    null, null, null, error);
            }
        }

        internal static bool ObservationFailure(Exception error)
        {
            return error is Win32Exception || error is InvalidOperationException || error is IOException ||
                error is UnauthorizedAccessException || error is SecurityException || error is NotSupportedException || error is ArgumentException;
        }

        private static string NormalizeObservedPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            try {
                if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path.Substring(4);
                if (!Path.IsPathRooted(path) || path.StartsWith("\\", StringComparison.Ordinal)) return null;
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            } catch (Exception error) { if (!ObservationFailure(error)) throw; return null; }
        }
    }

    internal sealed class WindowsManagedFileAccess : IManagedFileAccess
    {
        public IDisposable OpenExclusive(string path)
        {
            // Open existing only: this does not create, truncate, write or delete a file.
            // Read-only opens alone cannot establish that an executable image is replaceable.
            try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }

    internal sealed class WindowsManagedProcessSource : IManagedProcessSource
    {
        private const int MaximumModules = 4096;
        private const int MaximumSnapshotAttempts = 8;
        private const int ErrorBadLength = 24, ErrorNoMoreFiles = 18;
        private const int ModulePathCapacity = 260;
        private const uint SnapshotModules = 0x8, SnapshotModules32 = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ModuleEntry
        {
            internal uint Size, ModuleId, ProcessId, GlobalCount, ProcessCount;
            internal IntPtr BaseAddress;
            internal uint BaseSize;
            internal IntPtr Module;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ModulePathCapacity)] internal string Path;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Module32First(IntPtr snapshot, ref ModuleEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Module32NextW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Module32Next(IntPtr snapshot, ref ModuleEntry entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public IEnumerable<ManagedProcessObservation> Snapshot()
        {
            Process[] processes = Process.GetProcesses();
            var result = new List<ManagedProcessObservation>();
            try {
                if (processes.Length > ManagedProcesses.MaximumProcesses) throw new InvalidOperationException("Process inspection limit exceeded.");
                foreach (Process process in processes) result.Add(Observe(process));
                return result;
            } finally { foreach (Process process in processes) process.Dispose(); }
        }

        private static ManagedProcessObservation Observe(Process process)
        {
            string name = null, image = null, detail = null;
            var modules = new List<string>();
            bool complete = false, exited = false;
            try { name = process.ProcessName; }
            catch (Exception error) { if (!ManagedProcesses.ObservationFailure(error)) throw; detail = error.Message; }
            try { image = QueryImage(process.Id); }
            catch (Exception error) { if (!ManagedProcesses.ObservationFailure(error)) throw; detail = error.Message; }
            try {
                ReadModules(process, modules);
                complete = true;
            } catch (Exception error) { if (!ManagedProcesses.ObservationFailure(error)) throw; detail = error.Message; }
            // A process may exit between name, executable and module queries. A confirmed
            // exit is harmless; inability to confirm is retained as incomplete evidence.
            try { exited = process.HasExited; }
            catch (Exception error) { if (!ManagedProcesses.ObservationFailure(error)) throw; detail = error.Message; }
            return new ManagedProcessObservation(process.Id, name, image, modules, complete, exited, detail);
        }

        private static void ReadModules(Process process, List<string> modules)
        {
            // Toolhelp uses PID zero for the caller, not for the idle process.
            if (process.Id <= 0) throw new InvalidOperationException("Cannot enumerate idle process modules.");
            IntPtr snapshot = new IntPtr(-1);
            for (int attempt = 0; attempt < MaximumSnapshotAttempts; attempt++) {
                snapshot = CreateToolhelp32Snapshot(SnapshotModules | SnapshotModules32, (uint)process.Id);
                if (snapshot != new IntPtr(-1)) break;
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorBadLength || attempt == MaximumSnapshotAttempts - 1) throw new Win32Exception(error);
            }
            try {
                var entry = new ModuleEntry { Size = (uint)Marshal.SizeOf(typeof(ModuleEntry)) };
                bool more = Module32First(snapshot, ref entry);
                if (!more) {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreFiles) throw new Win32Exception(error);
                    throw new InvalidOperationException("No module evidence was returned for this process.");
                }
                do {
                    if (modules.Count >= MaximumModules) throw new InvalidOperationException("Module inspection limit exceeded.");
                    if (String.IsNullOrEmpty(entry.Path)) throw new InvalidOperationException("Module path is unavailable.");
                    if (entry.Path.Length >= ModulePathCapacity - 1) {
                        // Keep all known paths, but the fallback cannot prove completeness:
                        // an x64 .NET observer can omit the actual WOW64 client modules.
                        var observed = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
                        foreach (ProcessModule module in process.Modules) {
                            string path = module.FileName;
                            if (!observed.Add(path)) continue;
                            if (modules.Count >= MaximumModules) throw new InvalidOperationException("Module inspection limit exceeded.");
                            modules.Add(path);
                        }
                        throw new InvalidOperationException("A module path may be truncated; loaded files cannot be fully verified.");
                    }
                    modules.Add(entry.Path);
                    more = Module32Next(snapshot, ref entry);
                } while (more);
                int lastError = Marshal.GetLastWin32Error();
                if (lastError != ErrorNoMoreFiles) throw new Win32Exception(lastError);
            } finally { CloseHandle(snapshot); }
        }

        private static string QueryImage(int processId)
        {
            const uint queryLimitedInformation = 0x1000;
            IntPtr handle = OpenProcess(queryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var path = new StringBuilder(32768); int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return path.ToString();
            } finally { CloseHandle(handle); }
        }
    }
}
