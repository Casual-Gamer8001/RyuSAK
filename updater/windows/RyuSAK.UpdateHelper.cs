// RyuSAK Windows update helper (C# 5, .NET Framework 4). Built by scripts/build-update-helper.js.
//
// RyuSAK hands updates over the way MDPro4's updater does: the app downloads and verifies the installer,
// copies this helper into the update's staging folder, writes a job file, starts the helper and quits.
// From then on the helper owns the install:
//   1. one helper per install (a named mutex, plus a lock file RyuSAK checks before it opens),
//   2. wait a bounded time for every RyuSAK process from this install to exit, or stop with no changes,
//   3. hash the installer bytes it is about to run and compare them with the job,
//   4. back up the install, run Inno Setup silently with its own log, then check the exit code and the
//      version that got installed,
//   5. put the backup back if anything fails, and keep it as recovery files if even that fails,
//   6. record the outcome for RyuSAK to show and reopen RyuSAK.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("RyuSAK Update Helper")]
[assembly: AssemblyProduct("RyuSAK Update Helper")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace RyuSAK.UpdateHelper
{
    internal static class ExitCodes
    {
        public const int Success = 0;
        public const int InvalidJob = 2;
        public const int AlreadyRunning = 3;
        public const int Aborted = 10;
        public const int RolledBack = 11;
        public const int RecoveryNeeded = 12;
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool headless = Array.IndexOf(args, "--headless") >= 0;
            string jobPath = ParseJobPath(args);
            if (jobPath == null)
            {
                Fail(headless, null, "The helper requires a --job <path> argument.");
                return ExitCodes.InvalidJob;
            }

            UpdateJob job;
            try
            {
                job = UpdateJob.Load(jobPath);
            }
            catch (Exception error)
            {
                Fail(headless, jobPath, error.Message);
                return ExitCodes.InvalidJob;
            }

            using (Mutex mutex = new Mutex(false, GetInstallationMutexName(job.InstallDir)))
            {
                bool owned = false;
                try
                {
                    try
                    {
                        owned = mutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }

                    if (!owned)
                    {
                        UpdateLog.Write(job.LogPath, "Another update is already running for " + job.InstallDir + ". Nothing was changed.");
                        return ExitCodes.AlreadyRunning;
                    }

                    UpdateRunner runner = new UpdateRunner(job);
                    if (headless)
                    {
                        runner.Run();
                    }
                    else
                    {
                        try
                        {
                            NativeMethods.SetProcessDPIAware();
                        }
                        catch (Exception)
                        {
                        }

                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new UpdateWindow(runner, job));
                    }

                    return runner.ExitCode;
                }
                finally
                {
                    if (owned)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        private static string ParseJobPath(string[] args)
        {
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], "--job", StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        // Same idea as MDPro4: the mutex name is derived from the install path, so two installs never block each other.
        private static string GetInstallationMutexName(string installDir)
        {
            string normalized = Path.GetFullPath(installDir).TrimEnd('\\').ToUpperInvariant();
            return "Local\\RyuSAK.Update." + Hashing.Sha256Hex(Encoding.UTF8.GetBytes(normalized));
        }

        private static void Fail(bool headless, string jobPath, string message)
        {
            Console.Error.WriteLine(message);
            if (jobPath != null)
            {
                try
                {
                    UpdateLog.Write(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jobPath)), "helper-error.log"), message);
                }
                catch (Exception)
                {
                }
            }

            if (!headless)
            {
                MessageBox.Show(message, "RyuSAK Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    [DataContract]
    internal sealed class UpdateJob
    {
        public const string SchemaName = "ryusak-update-job-v1";

        [DataMember(Name = "schema")] public string Schema { get; set; }
        [DataMember(Name = "installerPath")] public string InstallerPath { get; set; }
        [DataMember(Name = "installerSha256")] public string InstallerSha256 { get; set; }
        [DataMember(Name = "installerSize")] public long InstallerSize { get; set; }
        [DataMember(Name = "installDir")] public string InstallDir { get; set; }
        [DataMember(Name = "launchExe")] public string LaunchExe { get; set; }
        [DataMember(Name = "waitPid")] public int WaitPid { get; set; }
        [DataMember(Name = "currentVersion")] public string CurrentVersion { get; set; }
        [DataMember(Name = "targetVersion")] public string TargetVersion { get; set; }
        [DataMember(Name = "portable")] public bool Portable { get; set; }
        [DataMember(Name = "logPath")] public string LogPath { get; set; }
        [DataMember(Name = "installerLogPath")] public string InstallerLogPath { get; set; }
        [DataMember(Name = "statusPath")] public string StatusPath { get; set; }
        [DataMember(Name = "lockPath")] public string LockPath { get; set; }
        [DataMember(Name = "backupDir")] public string BackupDir { get; set; }
        [DataMember(Name = "preserve")] public string[] Preserve { get; set; }
        [DataMember(Name = "handoffTimeoutSeconds")] public int HandoffTimeoutSeconds { get; set; }
        [DataMember(Name = "installTimeoutSeconds")] public int InstallTimeoutSeconds { get; set; }

        public static UpdateJob Load(string jobPath)
        {
            if (!File.Exists(jobPath))
            {
                throw new InvalidDataException("The update job file was not found: " + jobPath);
            }

            UpdateJob job;
            try
            {
                // Tolerate a UTF-8 byte order mark (e.g. a job edited by hand); the JSON serializer does not.
                byte[] bytes = File.ReadAllBytes(jobPath);
                int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                using (MemoryStream stream = new MemoryStream(bytes, offset, bytes.Length - offset))
                {
                    job = (UpdateJob)new DataContractJsonSerializer(typeof(UpdateJob)).ReadObject(stream);
                }
            }
            catch (Exception error)
            {
                throw new InvalidDataException("The update job file could not be parsed: " + error.Message);
            }

            if (job == null)
            {
                throw new InvalidDataException("The update job file is empty.");
            }

            job.Validate();
            return job;
        }

        private void Validate()
        {
            if (Schema != SchemaName)
            {
                throw new InvalidDataException("Unsupported update job schema '" + Schema + "'.");
            }

            InstallDir = Path.GetFullPath(Require("installDir", InstallDir)).TrimEnd('\\');
            if (!Directory.Exists(InstallDir))
            {
                throw new InvalidDataException("The update job field 'installDir' points to a missing directory: " + InstallDir);
            }

            InstallerPath = RequireFile("installerPath", InstallerPath);
            LaunchExe = RequireFile("launchExe", LaunchExe);
            if (!LaunchExe.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update job field 'launchExe' is outside the install directory.");
            }

            InstallerSha256 = Require("installerSha256", InstallerSha256).Trim().ToLowerInvariant();
            if (InstallerSha256.Length != 64)
            {
                throw new InvalidDataException("The update job field 'installerSha256' is not a SHA-256 hash.");
            }

            Require("targetVersion", TargetVersion);
            LogPath = Path.GetFullPath(Require("logPath", LogPath));
            InstallerLogPath = Path.GetFullPath(Require("installerLogPath", InstallerLogPath));
            StatusPath = Path.GetFullPath(Require("statusPath", StatusPath));
            LockPath = Path.GetFullPath(Require("lockPath", LockPath));
            BackupDir = Path.GetFullPath(Require("backupDir", BackupDir)).TrimEnd('\\');
            string stagingRoot = Path.GetDirectoryName(BackupDir).TrimEnd('\\') + "\\";
            if ((InstallDir + "\\").StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update job's staging folder must not contain the install directory.");
            }

            if (Preserve == null)
            {
                Preserve = new string[0];
            }

            if (HandoffTimeoutSeconds <= 0)
            {
                HandoffTimeoutSeconds = 120;
            }

            if (InstallTimeoutSeconds <= 0)
            {
                InstallTimeoutSeconds = 900;
            }
        }

        private static string Require(string fieldName, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("The update job field '" + fieldName + "' is missing.");
            }

            return value;
        }

        private static string RequireFile(string fieldName, string value)
        {
            string fullPath = Path.GetFullPath(Require(fieldName, value));
            if (!File.Exists(fullPath))
            {
                throw new InvalidDataException("The update job field '" + fieldName + "' points to a missing file: " + fullPath);
            }

            return fullPath;
        }
    }

    [DataContract]
    internal sealed class UpdateStatus
    {
        [DataMember(Name = "schema", Order = 0)] public string Schema { get; set; }
        [DataMember(Name = "result", Order = 1)] public string Result { get; set; }
        [DataMember(Name = "fromVersion", Order = 2)] public string FromVersion { get; set; }
        [DataMember(Name = "targetVersion", Order = 3)] public string TargetVersion { get; set; }
        [DataMember(Name = "message", Order = 4)] public string Message { get; set; }
        [DataMember(Name = "logPath", Order = 5)] public string LogPath { get; set; }
        [DataMember(Name = "recoveryPath", Order = 6, EmitDefaultValue = false)] public string RecoveryPath { get; set; }
        [DataMember(Name = "finishedAt", Order = 7)] public string FinishedAt { get; set; }
        [DataMember(Name = "acknowledged", Order = 8)] public bool Acknowledged { get; set; }
    }

    // Thrown before any install file was touched.
    internal sealed class NoChangesException : Exception
    {
        public NoChangesException(string message, bool appStillRunning)
            : base(message)
        {
            AppStillRunning = appStillRunning;
        }

        public bool AppStillRunning { get; private set; }
    }

    // Thrown when setup ran past its time limit and couldn't be stopped, so a rollback would race it.
    internal sealed class SetupStillRunningException : Exception
    {
        public SetupStillRunningException(string message)
            : base(message)
        {
        }
    }

    internal static class UpdateLog
    {
        private static readonly object Sync = new object();

        public static void Write(string logPath, string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message + Environment.NewLine;
            lock (Sync)
            {
                try
                {
                    string directory = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.AppendAllText(logPath, line, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // Logging must never be the reason an update fails.
                }
            }
        }
    }

    internal static class Hashing
    {
        public static string Sha256Hex(byte[] data)
        {
            using (SHA256CryptoServiceProvider sha = new SHA256CryptoServiceProvider())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        public static string Sha256Hex(Stream stream)
        {
            using (SHA256CryptoServiceProvider sha = new SHA256CryptoServiceProvider())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    internal static class NativeMethods
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint SnapshotProcesses = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        // Works for elevated processes too, unlike Process.MainModule.
        public static string GetProcessImagePath(int processId)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                StringBuilder buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // Maps every running process id to its parent's id.
        public static Dictionary<int, int> GetParentProcessIds()
        {
            Dictionary<int, int> parents = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == InvalidHandleValue)
            {
                return parents;
            }

            try
            {
                ProcessEntry32 entry = new ProcessEntry32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return parents;
        }
    }

    internal sealed class UpdateRunner
    {
        private const int RetryAttempts = 20;
        private const int RetryDelayMilliseconds = 500;
        private const int MaxBackupFiles = 5000;
        private const long MaxBackupBytes = 3L * 1024 * 1024 * 1024;
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        private readonly UpdateJob _job;
        private readonly string _installRoot;
        private readonly string _stagingRoot;
        private readonly List<string> _backedUpFiles = new List<string>();
        private readonly HashSet<string> _backedUpSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directoriesBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public UpdateRunner(UpdateJob job)
        {
            _job = job;
            _installRoot = job.InstallDir + "\\";
            _stagingRoot = Path.GetDirectoryName(job.BackupDir).TrimEnd('\\') + "\\";
            ExitCode = ExitCodes.Aborted;
            Outcome = "aborted";
            OutcomeMessage = "The update did not run.";
        }

        public event Action<int, string> ProgressChanged;

        public int ExitCode { get; private set; }

        public string Outcome { get; private set; }

        public string OutcomeMessage { get; private set; }

        // True when RyuSAK is not reopened, so the window has to explain what happened.
        public bool KeepWindowOpen { get; private set; }

        public string RecoveryPath { get; private set; }

        public void Run()
        {
            WriteLock();
            Log("Starting update from " + _job.CurrentVersion + " to " + _job.TargetVersion + " using " + _job.InstallerPath);
            bool changesStarted = false;
            bool relaunch = false;

            try
            {
                Report(-1, "Waiting for RyuSAK to close...");
                WaitForRyuSakToExit();

                Report(-1, "Checking the installer...");
                using (OpenVerifiedInstaller())
                {
                    BackUpInstall();
                    changesStarted = true;
                    Report(-1, "Installing RyuSAK " + _job.TargetVersion + "...");
                    RunInstaller();
                }

                VerifyInstalledVersion();
                Log("Update applied successfully.");
                Finish("success", "RyuSAK " + _job.TargetVersion + " was installed.", ExitCodes.Success);
                relaunch = true;
                Report(100, "RyuSAK " + _job.TargetVersion + " is installed. Reopening RyuSAK...");
                CleanUpAfterSuccess();
            }
            catch (NoChangesException error)
            {
                Log(error.Message);
                DeleteDirectoryQuietly(_job.BackupDir);
                relaunch = !error.AppStillRunning;
                Finish("aborted", error.Message, ExitCodes.Aborted);
            }
            catch (Exception error)
            {
                Log("Update failed: " + error.Message);
                if (!changesStarted)
                {
                    DeleteDirectoryQuietly(_job.BackupDir);
                    relaunch = true;
                    Finish("aborted", error.Message + " No files were changed.", ExitCodes.Aborted);
                }
                else if (!(error is SetupStillRunningException) && RollBack())
                {
                    relaunch = true;
                    Finish("rolled-back", error.Message, ExitCodes.RolledBack);
                }
                else
                {
                    RecoveryPath = _job.BackupDir;
                    Log("Recovery files retained at " + RecoveryPath);
                    Finish("failed", error.Message, ExitCodes.RecoveryNeeded);
                }
            }
            finally
            {
                KeepWindowOpen = !relaunch;
                WriteStatus();
                DeleteLock();
                if (relaunch)
                {
                    Relaunch();
                }
            }
        }

        private void Finish(string outcome, string message, int exitCode)
        {
            Outcome = outcome;
            OutcomeMessage = message;
            ExitCode = exitCode;
        }

        private void WaitForRyuSakToExit()
        {
            int timeoutSeconds = _job.HandoffTimeoutSeconds;
            DateTime started = DateTime.UtcNow;
            DateTime deadline = started.AddSeconds(timeoutSeconds);

            if (_job.WaitPid > 0)
            {
                Process parent = null;
                try
                {
                    parent = Process.GetProcessById(_job.WaitPid);
                }
                catch (ArgumentException)
                {
                    Log("Process " + _job.WaitPid + " already exited.");
                }

                if (parent != null)
                {
                    using (parent)
                    {
                        if (!IsFromThisInstall(parent.Id))
                        {
                            Log("Process " + _job.WaitPid + " is no longer RyuSAK.");
                        }
                        else if (!parent.WaitForExit(MillisecondsUntil(deadline)))
                        {
                            throw new NoChangesException("RyuSAK did not close within " + timeoutSeconds + " seconds, so the update was cancelled. No files were changed.", true);
                        }
                    }
                }
            }

            // Chromium's helper processes (GPU, renderer, crashpad) also run from RyuSAK.exe and can outlive the
            // main process for a moment. Setup can't replace RyuSAK.exe until they are gone too.
            bool reported = false;
            while (true)
            {
                List<int> running = FindRyuSakProcesses();
                if (running.Count == 0)
                {
                    Log("RyuSAK closed after " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " seconds.");
                    return;
                }

                if (!reported)
                {
                    Log("Waiting for RyuSAK processes to close: " + string.Join(", ", running) + ".");
                    reported = true;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new NoChangesException("RyuSAK is still running (process " + string.Join(", ", running) + "), so the update was cancelled. No files were changed.", true);
                }

                Thread.Sleep(250);
            }
        }

        private List<int> FindRyuSakProcesses()
        {
            List<int> running = new List<int>();
            int self = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_job.LaunchExe)))
            {
                using (process)
                {
                    if (process.Id != self && IsFromThisInstall(process.Id))
                    {
                        running.Add(process.Id);
                    }
                }
            }

            return running;
        }

        private bool IsFromThisInstall(int processId)
        {
            string imagePath = NativeMethods.GetProcessImagePath(processId);
            // If Windows won't say where the process runs from, assume it is this install and keep waiting.
            return imagePath == null || string.Equals(Path.GetFullPath(imagePath), _job.LaunchExe, StringComparison.OrdinalIgnoreCase);
        }

        // Hashes the exact bytes setup will run from: the file stays open with read-only sharing until setup exits.
        private FileStream OpenVerifiedInstaller()
        {
            FileStream stream;
            try
            {
                stream = new FileStream(_job.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception error)
            {
                throw new NoChangesException("The downloaded installer could not be opened (" + error.Message + "). No files were changed.", false);
            }

            try
            {
                if (_job.InstallerSize > 0 && stream.Length != _job.InstallerSize)
                {
                    throw new NoChangesException("The downloaded installer is " + stream.Length + " bytes instead of " + _job.InstallerSize + ". No files were changed.", false);
                }

                string actual = Hashing.Sha256Hex(stream);
                if (!string.Equals(actual, _job.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new NoChangesException("The downloaded installer failed its SHA-256 check. No files were changed.", false);
                }

                Log("Verified installer SHA-256 " + actual + ".");
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private void BackUpInstall()
        {
            List<string> files = new List<string>();
            CollectInstallEntries(_installRoot, files, _directoriesBefore);

            long totalBytes = 0;
            foreach (string file in files)
            {
                totalBytes += new FileInfo(file).Length;
            }

            if (files.Count > MaxBackupFiles || totalBytes > MaxBackupBytes)
            {
                throw new NoChangesException("The install folder holds " + files.Count + " files (" + (totalBytes / 1048576) + " MB), more than a RyuSAK install should, so it was not backed up. Please update RyuSAK from GitHub instead. No files were changed.", false);
            }

            if (Directory.Exists(_job.BackupDir))
            {
                // Keep the recovery files of an earlier attempt instead of overwriting them.
                string previous = _job.BackupDir + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                Directory.Move(_job.BackupDir, previous);
                Log("Kept the previous backup at " + previous);
            }

            Directory.CreateDirectory(_job.BackupDir);
            for (int index = 0; index < files.Count; index++)
            {
                string source = files[index];
                string relative = GetRelativePath(source);
                string target = Path.Combine(_job.BackupDir, relative);
                InvokeWithRetry(delegate
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target, true);
                }, "back up " + relative);
                _backedUpFiles.Add(relative);
                _backedUpSet.Add(relative);
                Report((index + 1) * 100 / files.Count, "Backing up RyuSAK (" + (index + 1) + " of " + files.Count + " files)...");
            }

            Log("Backed up " + files.Count + " files (" + (totalBytes / 1048576) + " MB) to " + _job.BackupDir);
        }

        private void RunInstaller()
        {
            StringBuilder arguments = new StringBuilder("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP- /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS");
            arguments.Append(" /DIR=").Append(Quote(_job.InstallDir));
            arguments.Append(" /LOG=").Append(Quote(_job.InstallerLogPath));
            if (_job.Portable)
            {
                arguments.Append(" /PORTABLE=1");
            }

            Log("Running setup: " + _job.InstallerPath + " " + arguments);
            ProcessStartInfo info = new ProcessStartInfo(_job.InstallerPath, arguments.ToString());
            info.UseShellExecute = false;
            info.WorkingDirectory = Path.GetDirectoryName(_job.InstallerPath);

            using (Process setup = Process.Start(info))
            {
                int timeoutMilliseconds = Math.Max(60, _job.InstallTimeoutSeconds) * 1000;
                if (!setup.WaitForExit(timeoutMilliseconds))
                {
                    string limit = "Setup did not finish within " + (timeoutMilliseconds / 60000) + " minutes";
                    if (!StopProcessTree(setup))
                    {
                        throw new SetupStillRunningException(limit + " and could not be stopped, so the previous version was not restored while it might still be writing files.");
                    }

                    throw new InvalidOperationException(limit + ", so it was stopped.");
                }

                Log("Setup exited with code " + setup.ExitCode + " (" + DescribeSetupExitCode(setup.ExitCode) + ").");
                if (setup.ExitCode != 0)
                {
                    throw new InvalidOperationException("Setup failed: " + DescribeSetupExitCode(setup.ExitCode) + ". Its log is " + _job.InstallerLogPath + ".");
                }
            }
        }

        // Inno Setup's Setup.exe is only a loader: the setup engine that writes the files runs as its child (a .tmp
        // copy in %TEMP%). Killing just the loader would leave the engine writing files during the rollback, so the
        // whole process tree is stopped. Returns false if any of it is still running.
        private bool StopProcessTree(Process root)
        {
            List<Process> tree = new List<Process>();
            tree.Add(root);
            try
            {
                Dictionary<int, int> parents = NativeMethods.GetParentProcessIds();
                for (int index = 0; index < tree.Count; index++)
                {
                    Process parent = tree[index];
                    DateTime parentStarted;
                    if (!TryGetStartTime(parent, out parentStarted))
                    {
                        continue;
                    }

                    foreach (KeyValuePair<int, int> entry in parents)
                    {
                        if (entry.Value != parent.Id || entry.Key == parent.Id)
                        {
                            continue;
                        }

                        Process child;
                        try
                        {
                            child = Process.GetProcessById(entry.Key);
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }

                        // A recorded parent id can belong to an older process that had the same id; a real child
                        // never started before its parent.
                        DateTime childStarted;
                        if (TryGetStartTime(child, out childStarted) && childStarted >= parentStarted)
                        {
                            tree.Add(child);
                        }
                        else
                        {
                            child.Dispose();
                        }
                    }
                }

                for (int index = tree.Count - 1; index >= 0; index--)
                {
                    Process process = tree[index];
                    try
                    {
                        if (!process.HasExited)
                        {
                            Log("Stopping setup process " + process.Id + " (" + process.ProcessName + ").");
                            process.Kill();
                        }
                    }
                    catch (Exception error)
                    {
                        Log("Could not stop setup process " + process.Id + ": " + error.Message);
                    }
                }

                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                bool stopped = true;
                foreach (Process process in tree)
                {
                    try
                    {
                        if (!process.WaitForExit(MillisecondsUntil(deadline)))
                        {
                            stopped = false;
                            Log("Setup process " + process.Id + " is still running.");
                        }
                    }
                    catch (Exception error)
                    {
                        stopped = false;
                        Log("Could not confirm that setup process " + process.Id + " exited: " + error.Message);
                    }
                }

                return stopped;
            }
            finally
            {
                // The root belongs to the caller.
                for (int index = 1; index < tree.Count; index++)
                {
                    tree[index].Dispose();
                }
            }
        }

        private static bool TryGetStartTime(Process process, out DateTime started)
        {
            try
            {
                started = process.StartTime;
                return true;
            }
            catch (Exception)
            {
                started = DateTime.MinValue;
                return false;
            }
        }

        // MDPro4 checks a version marker; for RyuSAK that is the product version of the installed RyuSAK.exe.
        private void VerifyInstalledVersion()
        {
            if (!File.Exists(_job.LaunchExe))
            {
                throw new InvalidOperationException("Setup finished but " + _job.LaunchExe + " is missing.");
            }

            string installed = FileVersionInfo.GetVersionInfo(_job.LaunchExe).ProductVersion;
            if (!string.Equals(NormalizeVersion(installed), NormalizeVersion(_job.TargetVersion), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Setup installed version " + (installed ?? "unknown") + " instead of " + _job.TargetVersion + ".");
            }

            Log("Installed version is " + installed + ".");
        }

        private bool RollBack()
        {
            Report(-1, "Restoring the previous version...");
            Log("Restoring the previous version...");
            bool restored = true;

            // Remove files the failed install added.
            List<string> current = new List<string>();
            HashSet<string> currentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                CollectInstallEntries(_installRoot, current, currentDirectories);
            }
            catch (Exception error)
            {
                restored = false;
                Log("Rollback could not list the install folder: " + error.Message);
            }

            foreach (string file in current)
            {
                string relative = GetRelativePath(file);
                if (_backedUpSet.Contains(relative))
                {
                    continue;
                }

                string path = file;
                try
                {
                    InvokeWithRetry(delegate { DeleteFile(path); }, "remove " + relative);
                }
                catch (Exception error)
                {
                    restored = false;
                    Log("Rollback could not remove " + path + ": " + error.Message);
                }
            }

            // Put every file back the way it was before setup ran.
            for (int index = 0; index < _backedUpFiles.Count; index++)
            {
                string relative = _backedUpFiles[index];
                string source = Path.Combine(_job.BackupDir, relative);
                string target = Path.Combine(_installRoot, relative);
                try
                {
                    InvokeWithRetry(delegate
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if (File.Exists(target))
                        {
                            File.SetAttributes(target, FileAttributes.Normal);
                        }

                        File.Copy(source, target, true);
                    }, "restore " + relative);
                }
                catch (Exception error)
                {
                    restored = false;
                    Log("Rollback failed for " + target + ": " + error.Message);
                }

                Report((index + 1) * 100 / _backedUpFiles.Count, "Restoring the previous version (" + (index + 1) + " of " + _backedUpFiles.Count + " files)...");
            }

            // Remove folders the failed install created, deepest first.
            List<string> newDirectories = new List<string>();
            foreach (string directory in currentDirectories)
            {
                if (!_directoriesBefore.Contains(directory))
                {
                    newDirectories.Add(directory);
                }
            }

            newDirectories.Sort(delegate(string left, string right) { return right.Length.CompareTo(left.Length); });
            foreach (string directory in newDirectories)
            {
                string fullPath = Path.Combine(_installRoot, directory);
                try
                {
                    if (Directory.Exists(fullPath) && Directory.GetFileSystemEntries(fullPath).Length == 0)
                    {
                        Directory.Delete(fullPath);
                    }
                }
                catch (Exception error)
                {
                    Log("Rollback could not remove the folder " + fullPath + ": " + error.Message);
                }
            }

            if (restored)
            {
                Log("Rolled back the unsuccessful update.");
                DeleteDirectoryQuietly(_job.BackupDir);
            }

            return restored;
        }

        private void CleanUpAfterSuccess()
        {
            DeleteDirectoryQuietly(_job.BackupDir);
            try
            {
                InvokeWithRetry(delegate { DeleteFile(_job.InstallerPath); }, "remove package " + _job.InstallerPath);
                Log("Removed downloaded package " + _job.InstallerPath);
            }
            catch (Exception error)
            {
                Log("Could not remove " + _job.InstallerPath + ": " + error.Message);
            }
        }

        private void Relaunch()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(_job.LaunchExe);
                info.WorkingDirectory = _job.InstallDir;
                info.UseShellExecute = false;
                using (Process.Start(info))
                {
                }

                Log("Reopened " + _job.LaunchExe);
            }
            catch (Exception error)
            {
                Log("Could not reopen RyuSAK: " + error.Message);
            }
        }

        private void CollectInstallEntries(string directory, List<string> files, HashSet<string> directories)
        {
            foreach (string file in Directory.GetFiles(directory))
            {
                string relative = GetRelativePath(file);
                if (IsExcluded(file, relative))
                {
                    continue;
                }

                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    Log("Skipping linked file " + relative);
                    continue;
                }

                files.Add(file);
            }

            foreach (string child in Directory.GetDirectories(directory))
            {
                string relative = GetRelativePath(child);
                if (IsExcluded(child, relative))
                {
                    continue;
                }

                // Never follow junctions or symbolic links out of the install folder.
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    Log("Skipping linked folder " + relative);
                    continue;
                }

                directories.Add(relative);
                CollectInstallEntries(child, files, directories);
            }
        }

        private bool IsExcluded(string fullPath, string relative)
        {
            if ((fullPath.TrimEnd('\\') + "\\").StartsWith(_stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string entry in _job.Preserve)
            {
                string preserved = (entry ?? string.Empty).Replace('/', '\\').Trim('\\');
                if (preserved.Length > 0 && (relative.Equals(preserved, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(preserved + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetRelativePath(string fullPath)
        {
            if (!fullPath.StartsWith(_installRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to touch a path outside the install directory: " + fullPath);
            }

            return fullPath.Substring(_installRoot.Length);
        }

        private void InvokeWithRetry(Action action, string description)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception error)
                {
                    bool transient = error is IOException || error is UnauthorizedAccessException;
                    if (!transient || attempt >= RetryAttempts)
                    {
                        throw;
                    }

                    Log("Retry " + attempt + " failed for " + description + ": " + error.Message);
                    Thread.Sleep(RetryDelayMilliseconds);
                }
            }
        }

        private void DeleteDirectoryQuietly(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                InvokeWithRetry(delegate { Directory.Delete(path, true); }, "remove " + path);
            }
            catch (Exception error)
            {
                Log("Could not remove " + path + ": " + error.Message);
            }
        }

        private static void DeleteFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        private void WriteLock()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_job.LockPath));
                string json = "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"startedAt\":\"" + DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "\"}";
                File.WriteAllText(_job.LockPath, json, new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                Log("Could not write " + _job.LockPath + ": " + error.Message);
            }
        }

        private void DeleteLock()
        {
            try
            {
                DeleteFile(_job.LockPath);
            }
            catch (Exception error)
            {
                Log("Could not remove " + _job.LockPath + ": " + error.Message);
            }
        }

        private void WriteStatus()
        {
            UpdateStatus status = new UpdateStatus();
            status.Schema = "ryusak-update-status-v1";
            status.Result = Outcome;
            status.FromVersion = _job.CurrentVersion;
            status.TargetVersion = _job.TargetVersion;
            status.Message = OutcomeMessage;
            status.LogPath = _job.LogPath;
            status.RecoveryPath = RecoveryPath;
            status.FinishedAt = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_job.StatusPath));
                using (MemoryStream buffer = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(UpdateStatus)).WriteObject(buffer, status);
                    File.WriteAllBytes(_job.StatusPath, buffer.ToArray());
                }
            }
            catch (Exception error)
            {
                Log("Could not record the update result: " + error.Message);
            }
        }

        private void Report(int percent, string text)
        {
            Action<int, string> handler = ProgressChanged;
            if (handler != null)
            {
                handler(percent, text);
            }
        }

        private void Log(string message)
        {
            UpdateLog.Write(_job.LogPath, message);
        }

        private static int MillisecondsUntil(DateTime deadline)
        {
            double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
            return remaining <= 0 ? 0 : (int)Math.Min(remaining, int.MaxValue);
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return string.Empty;
            }

            string normalized = version.Trim();
            int metadata = normalized.IndexOf('+');
            if (metadata >= 0)
            {
                normalized = normalized.Substring(0, metadata);
            }

            while (normalized.Split('.').Length > 3 && normalized.EndsWith(".0", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 2);
            }

            return normalized;
        }

        private static string DescribeSetupExitCode(int exitCode)
        {
            switch (exitCode)
            {
                case 0: return "success";
                case 1: return "setup failed to initialize";
                case 2: return "setup was cancelled before installing";
                case 3: return "a fatal error happened while preparing to install";
                case 4: return "a fatal error happened while installing";
                case 5: return "setup was cancelled while installing";
                case 6: return "setup was terminated";
                case 7: return "setup found a problem while preparing to install";
                case 8: return "setup found a problem and needs a restart";
                default: return "unknown exit code";
            }
        }
    }

    internal sealed class UpdateWindow : Form
    {
        private readonly UpdateRunner _runner;
        private readonly UpdateJob _job;
        private readonly Label _heading;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly LinkLabel _openLog;
        private readonly LinkLabel _openBackup;
        private readonly Button _close;
        private bool _running = true;

        public UpdateWindow(UpdateRunner runner, UpdateJob job)
        {
            _runner = runner;
            _job = job;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "RyuSAK Update";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 168);
            try
            {
                // Read before setup replaces RyuSAK.exe.
                Icon = Icon.ExtractAssociatedIcon(job.LaunchExe);
            }
            catch (Exception)
            {
            }

            _heading = new Label();
            _heading.AutoSize = true;
            _heading.UseMnemonic = false;
            _heading.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            _heading.Location = new Point(18, 16);
            _heading.Text = "Updating RyuSAK to v" + job.TargetVersion;

            // Fixed width so long messages wrap instead of running off the window.
            _status = new Label();
            _status.AutoSize = false;
            _status.AutoEllipsis = true;
            _status.UseMnemonic = false;
            _status.Location = new Point(18, 48);
            _status.Size = new Size(444, 36);
            _status.Text = "Starting the updater...";

            _progress = new ProgressBar();
            _progress.Location = new Point(18, 88);
            _progress.Size = new Size(444, 18);
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 30;

            _openLog = new LinkLabel();
            _openLog.AutoSize = true;
            _openLog.Location = new Point(18, 130);
            _openLog.Text = "Open log";
            _openLog.LinkClicked += delegate { OpenPath(_job.LogPath); };

            _openBackup = new LinkLabel();
            _openBackup.AutoSize = true;
            _openBackup.Location = new Point(90, 130);
            _openBackup.Text = "Open backup folder";
            _openBackup.Visible = false;
            _openBackup.LinkClicked += delegate { OpenPath(_runner.RecoveryPath); };

            _close = new Button();
            _close.Enabled = false;
            _close.Location = new Point(387, 124);
            _close.Size = new Size(75, 28);
            _close.Text = "Close";
            _close.Click += delegate { Close(); };

            Controls.AddRange(new Control[] { _heading, _status, _progress, _openLog, _openBackup, _close });
            ResumeLayout(false);
            PerformLayout();

            FormClosing += OnFormClosing;
            Shown += delegate
            {
                FitStatusToWindow();
                StartRunner();
            };
            _runner.ProgressChanged += OnProgress;
        }

        // DPI scaling can leave the fixed-size label wider than the window, which stops long messages from wrapping.
        private void FitStatusToWindow()
        {
            _status.Width = ClientSize.Width - 2 * _status.Left;
        }

        private void StartRunner()
        {
            Thread worker = new Thread(delegate()
            {
                try
                {
                    _runner.Run();
                }
                catch (Exception error)
                {
                    UpdateLog.Write(_job.LogPath, "Unexpected helper error: " + error);
                }

                try
                {
                    BeginInvoke((Action)OnFinished);
                }
                catch (InvalidOperationException)
                {
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void OnProgress(int percent, string text)
        {
            try
            {
                BeginInvoke((Action)delegate { ShowProgress(percent, text); });
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ShowProgress(int percent, string text)
        {
            _status.Text = text;
            if (percent < 0)
            {
                if (_progress.Style != ProgressBarStyle.Marquee)
                {
                    _progress.Style = ProgressBarStyle.Marquee;
                }
            }
            else
            {
                if (_progress.Style != ProgressBarStyle.Continuous)
                {
                    _progress.Style = ProgressBarStyle.Continuous;
                }

                _progress.Value = Math.Max(0, Math.Min(100, percent));
            }
        }

        private void OnFinished()
        {
            _running = false;
            if (!_runner.KeepWindowOpen)
            {
                Close();
                return;
            }

            // The message gets the progress bar's space so up to three lines fit.
            _progress.Visible = false;
            _status.Height = _progress.Bottom - _status.Top;
            FitStatusToWindow();
            _heading.Text = _runner.Outcome == "aborted" ? "Update cancelled" : "RyuSAK couldn't be updated";
            _status.Text = _runner.RecoveryPath != null
                ? "The update failed and some files couldn't be restored. The original files are in the backup folder."
                : _runner.OutcomeMessage;
            _openBackup.Visible = _runner.RecoveryPath != null;
            _close.Enabled = true;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_running && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
            }
        }

        private static void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            {
                return;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(path);
                info.UseShellExecute = true;
                using (Process.Start(info))
                {
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
