// WolfTeamDirilis.exe from launcher 1.4 on: the stable bootstrap the desktop shortcut starts. It is never replaced by a
// self-update, only by the setup (also the 1.2 -> 1.4 migration), so a broken launcher version can always be recovered
// on the next shortcut start. Layout: launcher\csharp\BootLayout.cs.
//
// Every start, holding launcher\install.lock (a file in the install folder, opened exclusively: one bootstrap or setup
// at a time for every Windows user of that folder; released by Windows when the process dies):
//   1. read launcher\state.txt (install-wide: current / previous / trial version, trial attempts, failed versions);
//      no state yet = first start after the setup (also after the 1.2 migration)
//   2. adopt launcher\WolfTeamLauncher.exe (what the updater or the setup put there) when its SHA-256 is new: copied to
//      launcher\versions\<sha16>\ (flushed, hash checked, one directory rename) and recorded as the trial
//   3. a trial is started under supervision (at most MaxTrialAttempts starts, counted before each start, so a
//      bootstrap killed or a PC restarted during a trial ends in a rollback after one more try); confirmed = it becomes
//      current, the old current becomes previous; otherwise it is marked failed and never tried again
//   4. otherwise current, then previous, is started under supervision; nothing else ever runs (a 1.2 launcher is never
//      started inside a 1.4 install: it would update the bootstrap away and has no consent or update lock - gate
//      1.4b #1); with nothing left the player is offered the setup's repair (the setup copy WolfTeamDirilis-Kaldir.exe)
// Supervision: the launcher gets the write end of an anonymous pipe (--confirm-pipe, inherited handle: only that child
// and what it starts can write it) and writes byte 1 once its start finished and byte 2 from a later UI tick. Confirmed
// = both bytes, and no crash exit within ExitCheckMs; or byte 1 and a clean exit (the player closed the window). No
// byte 1 within StartTimeoutMs, no byte 2 within StableTimeoutMs, or a failing exit = not confirmed (the child is
// stopped). State changes are written to state.txt.part, flushed, and moved over state.txt with one write-through
// MoveFileEx: the file always holds the old or the new state.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WolfTeamDirilis
{
    internal static class BootProgram
    {
        /// <summary>The bootstrap's own version (changes only with the setup).</summary>
        public const string Version = "1.5";

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        public const string NothingText = "WolfTeam Diriliş başlatıcısı açılamadı. Kurulum programını (WolfTeamDirilis-Setup.exe) "
            + "wolf2team.com'dan indirip aynı klasöre yeniden kur; oyun dosyaların silinmez.";

        [STAThread]
        private static int Main(string[] args)
        {
            string self = System.Reflection.Assembly.GetEntryAssembly().Location;
            string install = Path.GetDirectoryName(Path.GetFullPath(self));
            bool dialogs = Environment.GetEnvironmentVariable("WOLFTEAM_BOOT_TEST_NODIALOG") != "1"; // tests: no dialog
            if (BootLayout.IsNetworkPath(install))
            {
                // Not supported (gate 1.4d #1): another computer may be playing from the same files.
                if (dialogs) MessageBox.Show(BootLayout.NetworkText, "WolfTeam Diriliş", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            // The selftest (no version transaction) still runs: the launcher reports the same problem as a FAIL line.
            if (Array.IndexOf(args, "--selftest") >= 0) return Diagnose(install, args);
            string ansi = BootLayout.AnsiPathProblem(install, BootLayout.SystemAnsi());
            if (ansi != null)
            {
                // The game could only fail with "axProtect initialization Failed" from this folder (letters outside the
                // system ANSI code page): no launcher start, no update, no trial is counted.
                if (dialogs) MessageBox.Show(ansi, "WolfTeam Diriliş", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            ProcessLauncherRunner runner = new ProcessLauncherRunner(install, BootTimeouts.FromEnvironment());
            BootOutcome outcome;
            string error = null;
            try
            {
                outcome = Run(install, self, args, runner);
            }
            catch (Exception e)
            {
                // Never a start without the install lock (gate 1.4d #2): an unexpected error is reported instead.
                outcome = BootOutcome.LockUnavailable;
                error = e.GetType().Name + ": " + e.Message;
            }
            if (outcome == BootOutcome.NothingToStart)
            {
                OfferRepair(install);
                return 1;
            }
            if (outcome == BootOutcome.LockUnavailable)
            {
                if (dialogs) MessageBox.Show(LockText + (error == null ? "" : "\r\n\r\n(" + error + ")"), "WolfTeam Diriliş",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }

        public const string LockText = "WolfTeam Diriliş başlatılamadı: oyun klasöründeki kilit dosyası (launcher\\install.lock) "
            + "açılamadı. Oyun klasörüne yazma iznin olmayabilir ya da başka bir kurulum işlemi sürüyor olabilir. Kurulum ya da "
            + "kaldırma açıksa bitmesini bekle; değilse oyunu kendi kullanıcınla yazabildiğin bir klasöre yeniden kur.";

        public const string RepairQuestion = "WolfTeam Diriliş başlatıcısı açılamadı.\r\n\r\nKurulumu onarmak ister misin? Kurulum programı "
            + "açılır ve başlatıcıyı yeniden kurar; oyun dosyaların ve ayarların silinmez.";

        /// <summary>"Kurulumu onar": the setup copy in the install folder (WolfTeamDirilis-Kaldir.exe is the setup itself),
        /// run from a %TEMP% copy so it can replace its own file, with the install folder preset (the wizard's repair).
        /// Only a setup that installs the bootstrap (BootLayout.IsRepairSetupFile, checked on the temp copy that runs) is
        /// offered; an older or foreign file, or none: where to download the setup (gate 1.4b #1).</summary>
        private static void OfferRepair(string install)
        {
            bool dialogs = Environment.GetEnvironmentVariable("WOLFTEAM_BOOT_TEST_NODIALOG") != "1"; // tests: no dialog
            string temp = null;
            FileStream pin = null;
            try
            {
                // The setup copy must be byte for byte the setup that installed (its SHA-256 is in the marker, written by
                // that setup) and a setup that installs the bootstrap (>= 1.4). Checked on the %TEMP% copy through a handle
                // that denies writing and deleting and stays open until the process exists (gate 1.4d #4).
                string setup = Path.Combine(install, RepairSetupName), recorded = BootLayout.RecordedSetupHash(install);
                if (recorded != null && BootFiles.LooksLikePe(setup))
                {
                    temp = Path.Combine(Path.GetTempPath(), "WolfTeamDirilis-Onar-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    File.Copy(setup, temp, true);
                    pin = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (BootFiles.Sha256(pin) != recorded || !BootLayout.IsRepairSetupFile(temp))
                    {
                        pin.Dispose(); pin = null;
                        BootFiles.TryDelete(temp); temp = null;
                    }
                }
            }
            catch (IOException) { if (pin != null) { pin.Dispose(); pin = null; } }            // the temp copy is removed below
            catch (UnauthorizedAccessException) { if (pin != null) { pin.Dispose(); pin = null; } }
            if (temp == null || pin == null)
            {
                if (pin != null) pin.Dispose();
                if (temp != null) BootFiles.TryDelete(temp);
                BootLog(install, "repair: no verified setup copy, download message shown");
                if (dialogs) MessageBox.Show(NothingText, "WolfTeam Diriliş", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (pin)
            {
                BootLog(install, "repair offered");
                if (!dialogs || MessageBox.Show(RepairQuestion, "WolfTeam Diriliş", MessageBoxButtons.YesNo, MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button1) != DialogResult.Yes)
                {
                    pin.Dispose();
                    BootFiles.TryDelete(temp);
                    return;
                }
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(temp, "/D=" + install);
                    psi.UseShellExecute = false;
                    psi.WorkingDirectory = Path.GetTempPath();
                    using (Process.Start(psi)) { }
                    BootLog(install, "repair setup started");
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception
                          || e is InvalidOperationException)) throw;
                    pin.Dispose();
                    BootFiles.TryDelete(temp);
                    MessageBox.Show(NothingText, "WolfTeam Diriliş", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>One line in launcher\bootstrap.log, written under install.lock like the start's own log (no lost
        /// lines between bootstraps); skipped if the lock is not free within a second.</summary>
        private static void BootLog(string install, string line)
        {
            try
            {
                FileStream lck = null;
                for (int i = 0; i < 10 && lck == null; i++)
                {
                    lck = InstallLockFile.TryTake(BootLayout.LockPath(install));
                    if (lck == null) Thread.Sleep(100);
                }
                if (lck == null) return;
                using (lck)
                {
                    File.AppendAllText(Path.Combine(BootLayout.LauncherDir(install), "bootstrap.log"),
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z " + line + "\r\n", new UTF8Encoding(false));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The setup's copy in the install folder (launcher\setup\Installer.cs UninstallerName).</summary>
        public const string RepairSetupName = "WolfTeamDirilis-Kaldir.exe";

        /// <summary>"WolfTeamDirilis.exe --selftest": the launcher's diagnostics, run outside the version transaction (no
        /// adoption, no trial, no state change; a diagnostic run never confirms a start). Runs the current confirmed
        /// version, else the release file, with the same output handles; returns its exit code.</summary>
        public static int Diagnose(string install, string[] args)
        {
            string sha;
            string exe = new Bootstrapper(install, System.Reflection.Assembly.GetEntryAssembly().Location, null).DiagnosticExe(out sha);
            if (exe == null)
            {
                Console.Error.WriteLine("No confirmed launcher version to run the selftest with (repair the install).");
                return 2;
            }
            List<string> list = new List<string> { BootLayout.GameDirFlag, install };
            foreach (string a in args)
            {
                if (a != "--no-swap") list.Add(a);
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, Bootstrapper.Join(list));
                psi.UseShellExecute = false;
                psi.WorkingDirectory = install;
                psi.RedirectStandardOutput = true; // passed on to whoever started the bootstrap (a pipe, a file, a console)
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                Process started;
                // The same pinned start as a supervised one (gate 1.4b #4): checked bytes = started bytes.
                using (FileStream pin = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (sha != null && BootFiles.Sha256(pin) != sha)
                    {
                        Console.Error.WriteLine("The launcher version changed on disk (repair the install).");
                        return 2;
                    }
                    started = Process.Start(psi);
                }
                using (Process p = started)
                {
                    Stream o = Console.OpenStandardOutput();
                    if (o == Stream.Null && AttachConsole(-1)) o = Console.OpenStandardOutput();
                    using (StreamWriter w = new StreamWriter(o, new UTF8Encoding(false)))
                    {
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null) w.WriteLine(line);
                    }
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return 2; }
            catch (InvalidOperationException) { return 2; }
            catch (IOException) { return 2; }
            catch (UnauthorizedAccessException) { return 2; }
        }

        /// <summary>How long a start after an update waits for the bootstrap that supervised the old launcher.</summary>
        public static int AfterUpdateWaitMs = 60000;

        /// <summary>The whole start under the install lock. A start while another holds it steps aside (a double click:
        /// that one opens a launcher); a start with --after-update (the launcher that just downloaded a new version) waits
        /// for it instead, since the bootstrap that holds it only finishes the old launcher's confirmation.</summary>
        public static BootOutcome Run(string install, string self, string[] args, ILauncherRunner runner)
        {
            FileStream lck;
            bool afterUpdate = Array.IndexOf(args ?? new string[0], BootLayout.AfterUpdateFlag) >= 0;
            try
            {
                Directory.CreateDirectory(BootLayout.LauncherDir(install));
                lck = InstallLockFile.TryTake(BootLayout.LockPath(install));
                DateTime end = DateTime.UtcNow.AddMilliseconds(AfterUpdateWaitMs);
                while (lck == null && afterUpdate && DateTime.UtcNow < end)
                {
                    Thread.Sleep(200);
                    lck = InstallLockFile.TryTake(BootLayout.LockPath(install));
                }
            }
            catch (UnauthorizedAccessException) { return BootOutcome.LockUnavailable; } // never a start without the lock
            catch (IOException) { return BootOutcome.LockUnavailable; }                // not a sharing violation: reported
            if (lck == null) return BootOutcome.AnotherStartRuns;
            using (lck) return new Bootstrapper(install, self, runner).Run(args);
        }
    }

    internal enum BootOutcome { Started, StartedPrevious, NothingToStart, AnotherStartRuns, LockUnavailable }

    internal enum ChildResult { Confirmed, Failed }

    internal interface ILauncherRunner
    {
        /// <summary>Starts the launcher and waits until it confirmed its start or failed (then it is stopped).</summary>
        /// <summary>{expectedSha}: the version's SHA-256; the runner checks it on the handle it keeps open (deny write and
        /// delete) until the process was created, so the bytes it checked are the bytes that start (gate 1.4 #5).</summary>
        ChildResult Supervise(string exe, string args, string expectedSha);
    }

    /// <summary>The install lock: launcher\install.lock opened with FileShare.None. Works across Windows users and
    /// sessions (it is a file in the shared folder) and is released by Windows when the holder dies.</summary>
    internal static class InstallLockFile
    {
        /// <summary>Null when another bootstrap or the setup holds it (a sharing violation). A user who may not write
        /// the lock file still locks it with an exclusive read handle (the share mode is what locks, as in the updater).
        /// Throws UnauthorizedAccessException when it cannot be opened at all, IOException for any other I/O error.</summary>
        public static FileStream TryTake(string path)
        {
            try
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (UnauthorizedAccessException)
                {
                    try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
                    catch (FileNotFoundException) { throw new UnauthorizedAccessException("install.lock cannot be created"); }
                }
            }
            catch (IOException e)
            {
                if (e is FileNotFoundException || !BootLayout.IsSharingViolation(e)) throw;
                return null;
            }
        }
    }

    internal sealed class BootTimeouts
    {
        public int StartMs = 120000;
        public int StableMs = 15000;
        public int ExitCheckMs = 1000;

        /// <summary>Tests only: WOLFTEAM_BOOT_TEST_TIMEOUTS=start,stable,exitcheck (ms). It only shortens the waits of
        /// the player who sets it.</summary>
        public static BootTimeouts FromEnvironment()
        {
            BootTimeouts t = new BootTimeouts();
            string v = Environment.GetEnvironmentVariable("WOLFTEAM_BOOT_TEST_TIMEOUTS");
            if (string.IsNullOrEmpty(v)) return t;
            string[] p = v.Split(',');
            int a, b, c;
            if (p.Length == 3 && int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out a)
                && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out b)
                && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out c) && a > 0 && b > 0 && c >= 0)
            {
                t.StartMs = a; t.StableMs = b; t.ExitCheckMs = c;
            }
            return t;
        }
    }

    internal static class BootFiles
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string target, int flags);

        private const int MOVEFILE_REPLACE_EXISTING = 0x1, MOVEFILE_WRITE_THROUGH = 0x8;

        /// <summary>Writes through "&lt;path&gt;.part" (flushed to the disk) and one write-through replace.</summary>
        public static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".part";
            using (FileStream f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(data, 0, data.Length);
                f.Flush(true);
            }
            if (!MoveFileEx(tmp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
                throw new IOException("MoveFileEx failed: " + Marshal.GetLastWin32Error());
        }

        public static string Sha256(string path)
        {
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                return Sha256(f);
        }

        public static string Sha256(Stream f)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(f);
                StringBuilder b = new StringBuilder(64);
                foreach (byte x in h) b.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                return b.ToString();
            }
        }

        /// <summary>SHA-256 or null when the file cannot be read.</summary>
        public static string TrySha256(string path)
        {
            try { return File.Exists(path) ? Sha256(path) : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static void CopyFlushed(string source, string target)
        {
            using (FileStream src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (FileStream dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
                dst.Flush(true);
            }
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>A Windows program file ("MZ" and a "PE\0\0" header).</summary>
        public static bool LooksLikePe(string path)
        {
            try
            {
                using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    byte[] b = new byte[64];
                    if (f.Read(b, 0, 64) < 64 || b[0] != 'M' || b[1] != 'Z') return false;
                    int pe = BitConverter.ToInt32(b, 60);
                    if (pe < 64 || pe > f.Length - 4) return false;
                    f.Position = pe;
                    byte[] sig = new byte[4];
                    return f.Read(sig, 0, 4) == 4 && sig[0] == 'P' && sig[1] == 'E' && sig[2] == 0 && sig[3] == 0;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal sealed class Bootstrapper
    {
        public const int MaxTrialAttempts = 2;

        /// <summary>Test hook: called with a step name ("copied"); a throwing hook plays a bootstrap killed there.</summary>
        internal static Action<string> Fault = null;

        private static void Step(string name)
        {
            Action<string> f = Fault;
            if (f != null) f(name);
        }

        private readonly string install, self;
        private readonly ILauncherRunner runner;
        /// <summary>What this start did, for launcher\bootstrap.log and the tests.</summary>
        public readonly List<string> Log = new List<string>();

        public Bootstrapper(string install, string self, ILauncherRunner runner)
        {
            this.install = Path.GetFullPath(install);
            this.self = self;
            this.runner = runner;
        }

        /// <summary>A 1.2 launcher copy from a pre-release 1.4 setup; never started, removed when seen.</summary>
        private string LegacyExe
        {
            get { return BootLayout.BootstrapPath(install) + ".old"; }
        }

        public BootOutcome Run(string[] userArgs)
        {
            try { return RunLocked(userArgs); }
            finally { WriteLog(); }
        }

        private BootOutcome RunLocked(string[] userArgs)
        {
            BootState s = ReadState();
            if (s == null) return BootOutcome.NothingToStart;
            if (File.Exists(LegacyExe))
            {
                BootFiles.TryDelete(LegacyExe);
                Note("old 1.2 launcher copy removed (never started)");
            }
            Adopt(s);
            bool rolledBack = false;
            if (s.Trial != null)
            {
                string trial = s.Trial;
                if (s.Failed.Contains(trial) || s.Attempts >= MaxTrialAttempts || !VersionOk(trial))
                {
                    Note("trial " + Short(trial) + " given up (attempts " + s.Attempts + ")");
                    Fail(s);
                    rolledBack = true;
                }
                else
                {
                    s.Attempts++;
                    // Counted before the start: a start that is killed still counts. Without the record no trial runs.
                    if (!Save(s))
                    {
                        s.Attempts--;
                        Note("trial " + Short(trial) + " not started: state not writable");
                    }
                    else
                    {
                        Note("trial " + Short(trial) + " start " + s.Attempts);
                        if (runner.Supervise(BootLayout.VersionExe(install, trial), LauncherArgs(userArgs, false), trial) == ChildResult.Confirmed)
                        {
                            if (s.Current != null && s.Current != trial) s.Previous = s.Current;
                            s.Current = trial;
                            s.Trial = null;
                            s.Attempts = 0;
                            s.Legacy = false;
                            Save(s);
                            Note("trial " + Short(trial) + " confirmed");
                            Collect(s);
                            return BootOutcome.Started;
                        }
                        Note("trial " + Short(trial) + " failed");
                        Fail(s);
                        rolledBack = true;
                    }
                }
            }
            // A rejected version never runs again, also not as current or previous (Codex 1.4 round 6 #1).
            if (s.Current != null && !s.Failed.Contains(s.Current) && VersionOk(s.Current))
            {
                if (runner.Supervise(BootLayout.VersionExe(install, s.Current), LauncherArgs(userArgs, rolledBack), s.Current) == ChildResult.Confirmed)
                {
                    Collect(s);
                    return BootOutcome.Started;
                }
                Note("current " + Short(s.Current) + " failed");
            }
            // A failing current version is not marked failed: the player may have stopped it (Task Manager). The previous
            // version runs for this start only; the next start tries the current one again.
            if (s.Previous != null && !s.Failed.Contains(s.Previous) && VersionOk(s.Previous))
            {
                if (runner.Supervise(BootLayout.VersionExe(install, s.Previous), LauncherArgs(userArgs, true), s.Previous) == ChildResult.Confirmed)
                    return BootOutcome.StartedPrevious;
                Note("previous " + Short(s.Previous) + " failed");
            }
            Note("no launcher version left to start: the setup's repair is offered");
            return BootOutcome.NothingToStart;
        }

        /// <summary>launcher\state.txt, or its copy state.bak when it is missing or damaged; null when neither is usable.</summary>
        private BootState ReadRecorded()
        {
            bool damaged, backupDamaged;
            BootState s = BootState.Load(BootLayout.StatePath(install), out damaged);
            if (s != null) return s;
            BootState b = BootState.Load(BootLayout.StateBackupPath(install), out backupDamaged);
            if (b != null) Note("state " + (damaged ? "damaged" : "missing") + ": using its copy");
            return b;
        }

        /// <summary>The launcher a selftest may run: a confirmed, hash-checked, not rejected version from the recorded state
        /// (state.txt or its copy); on a genuine first start the release file unless it was rejected; else null.</summary>
        public string DiagnosticExe()
        {
            string sha;
            return DiagnosticExe(out sha);
        }

        /// <summary>{sha}: the expected SHA-256 of a recorded version; null for the release file on a first start.</summary>
        public string DiagnosticExe(out string sha)
        {
            sha = null;
            BootState s;
            try { s = ReadRecorded(); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            if (s != null)
            {
                foreach (string h in new string[] { s.Current, s.Previous })
                {
                    if (h != null && !s.Failed.Contains(h) && VersionOk(h)) { sha = h; return BootLayout.VersionExe(install, h); }
                }
                return null;
            }
            if (!FirstStart()) return null;
            string staged = BootLayout.StagedLauncher(install);
            return BootFiles.LooksLikePe(staged) ? staged : null;
        }

        /// <summary>A first start: no state, no copy and no launcher version made yet (a new or repaired install).</summary>
        private bool FirstStart()
        {
            if (File.Exists(BootLayout.StatePath(install)) || File.Exists(BootLayout.StateBackupPath(install))) return false;
            string versions = BootLayout.VersionsDir(install);
            try
            {
                foreach (string d in Directory.GetDirectories(versions))
                {
                    if (!d.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            catch (DirectoryNotFoundException) { }
            return true;
        }

        /// <summary>The state; null when it is lost (damaged or missing while launcher versions exist, and no usable copy):
        /// then no version is trusted or adopted (a rejected one could run again); the setup's repair starts over.</summary>
        private BootState ReadState()
        {
            BootState s = null;
            bool primaryOk;
            try
            {
                primaryOk = BootState.Read(BootLayout.StatePath(install)) != null;
                s = ReadRecorded();
            }
            catch (IOException) { return null; }
            if (s != null)
            {
                if (!primaryOk) Save(s); // the file again from its copy
                return s;
            }
            if (!FirstStart())
            {
                Note("state lost: nothing is adopted until the setup repairs the install");
                return null;
            }
            // First start after the setup (also after the 1.2 migration; the 1.2 launcher is never a fallback).
            s = new BootState();
            Note("no state: first start");
            // Recorded before any version folder is made (Codex 1.4 round 5 #1): a start killed after the copy is then
            // never mistaken for a lost state.
            if (!Save(s))
            {
                Note("state not writable: nothing adopted");
                return null;
            }
            return s;
        }

        /// <summary>launcher\WolfTeamLauncher.exe with a new SHA-256 becomes the trial.</summary>
        private void Adopt(BootState s)
        {
            string staged = BootLayout.StagedLauncher(install);
            if (!File.Exists(staged)) return;
            string h = BootFiles.TrySha256(staged);
            if (h == null || s.Failed.Contains(h)) return;
            if (h == s.Current || h == s.Trial)
            {
                // Known version: only a damaged or deleted copy is made again from the release file.
                if (!VersionOk(h) && BootFiles.LooksLikePe(staged) && CopyToVersions(staged, h)) Note("repaired copy of " + Short(h));
                return;
            }
            if (!BootFiles.LooksLikePe(staged)) { Note("staged launcher is not a program"); return; }
            if (!CopyToVersions(staged, h)) { Note("staged launcher could not be copied"); return; }
            string before = s.Trial;
            int attempts = s.Attempts;
            s.Trial = h;
            s.Attempts = 0;
            if (!Save(s))
            {
                s.Trial = before;
                s.Attempts = attempts;
                Note("staged launcher not adopted: state not writable");
                return;
            }
            Note("adopted " + Short(h) + " as trial");
        }

        private bool CopyToVersions(string staged, string h)
        {
            string dir = Path.GetDirectoryName(BootLayout.VersionExe(install, h));
            if (VersionOk(h)) return true;
            string tmp = dir + ".part";
            try
            {
                BootFiles.TryDelete(tmp);
                if (Directory.Exists(dir)) Directory.Delete(dir, true); // a damaged copy
                Directory.CreateDirectory(tmp);
                string exe = Path.Combine(tmp, BootLayout.LauncherName);
                BootFiles.CopyFlushed(staged, exe);
                if (BootFiles.Sha256(exe) != h) { BootFiles.TryDelete(tmp); return false; } // changed while copying
                Directory.Move(tmp, dir);
                Step("copied");
                return true;
            }
            catch (IOException) { BootFiles.TryDelete(tmp); return false; }
            catch (UnauthorizedAccessException) { BootFiles.TryDelete(tmp); return false; }
        }

        private bool VersionOk(string h)
        {
            return BootFiles.TrySha256(BootLayout.VersionExe(install, h)) == h;
        }

        private void Fail(BootState s)
        {
            s.MarkFailed(s.Trial);
            s.Trial = null;
            s.Attempts = 0;
            Save(s);
        }

        /// <summary>False when the state could not be written (disk full, no rights).</summary>
        private bool Save(BootState s)
        {
            try
            {
                // The file, then its copy: one of them always holds a whole, current or previous, state.
                byte[] text = new UTF8Encoding(false).GetBytes(s.Text());
                BootFiles.WriteAtomic(BootLayout.StatePath(install), text);
                BootFiles.WriteAtomic(BootLayout.StateBackupPath(install), text);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Removes version folders that are neither current, previous nor trial.</summary>
        private void Collect(BootState s)
        {
            string versions = BootLayout.VersionsDir(install);
            if (!Directory.Exists(versions)) return;
            HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string h in new string[] { s.Current, s.Previous, s.Trial })
            {
                if (h != null) keep.Add(h.Substring(0, 16));
            }
            try
            {
                foreach (string d in Directory.GetDirectories(versions))
                {
                    if (!keep.Contains(Path.GetFileName(d))) BootFiles.TryDelete(d);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>--gamedir &lt;install&gt; [--rolled-back] plus the player's own arguments (--config made absolute),
        /// without the internal flags of older launchers.</summary>
        public string LauncherArgs(string[] userArgs, bool rolledBack)
        {
            List<string> list = new List<string> { BootLayout.GameDirFlag, install };
            if (rolledBack) list.Add(BootLayout.RolledBackFlag);
            for (int i = 0; userArgs != null && i < userArgs.Length; i++)
            {
                string a = userArgs[i];
                if (a == "--no-swap" || a == BootLayout.RolledBackFlag || a == BootLayout.AfterUpdateFlag) continue;
                if (a == BootLayout.GameDirFlag || a == BootLayout.ConfirmPipeFlag || a == "--handover" || a == "--parent"
                    || a == "--apply-self-update") { i++; continue; }
                list.Add(a);
                if (a == BootLayout.ConfigFlag && i + 1 < userArgs.Length)
                {
                    string v = userArgs[++i];
                    try { v = Path.GetFullPath(v); }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                    catch (PathTooLongException) { }
                    list.Add(v);
                }
            }
            return Join(list);
        }

        /// <summary>Windows (CommandLineToArgvW) quoting.</summary>
        public static string Join(List<string> args)
        {
            StringBuilder b = new StringBuilder();
            foreach (string a in args)
            {
                if (b.Length > 0) b.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) { b.Append(a); continue; }
                b.Append('"');
                int slashes = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') b.Append('\\', slashes * 2 + 1); else b.Append('\\', slashes);
                    slashes = 0;
                    b.Append(c);
                }
                b.Append('\\', slashes * 2);
                b.Append('"');
            }
            return b.ToString();
        }

        private static string Short(string h)
        {
            return h == null ? "-" : h.Substring(0, 16);
        }

        private void Note(string line)
        {
            Log.Add(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z " + line);
        }

        /// <summary>launcher\bootstrap.log, the newest 200 lines (for support; never shipped).</summary>
        private void WriteLog()
        {
            if (Log.Count == 0) return;
            try
            {
                string path = Path.Combine(BootLayout.LauncherDir(install), "bootstrap.log");
                List<string> lines = new List<string>();
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path, Encoding.UTF8));
                lines.AddRange(Log);
                if (lines.Count > 200) lines.RemoveRange(0, lines.Count - 200);
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Real processes: the launcher gets the write end of an anonymous pipe (an inherited handle).</summary>
    internal sealed class ProcessLauncherRunner : ILauncherRunner
    {
        private readonly string install;
        private readonly BootTimeouts t;

        public ProcessLauncherRunner(string install, BootTimeouts timeouts)
        {
            this.install = install;
            t = timeouts;
        }

        /// <summary>Test hook: runs after the hash check, before Process.Start, while the checked handle is open.</summary>
        internal static Action<string> BeforeStart = null;

        public ChildResult Supervise(string exe, string args, string expectedSha)
        {
            using (AnonymousPipeServerStream server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable))
            {
                Process p;
                try
                {
                    // Held (read only, others may only read) from the hash check until the process exists: nobody can
                    // change or swap the checked file in between.
                    using (FileStream pin = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (expectedSha != null && BootFiles.Sha256(pin) != expectedSha) return ChildResult.Failed;
                        ProcessStartInfo psi = new ProcessStartInfo(exe, args + " " + BootLayout.ConfirmPipeFlag + " " + server.GetClientHandleAsString());
                        psi.WorkingDirectory = install;
                        psi.UseShellExecute = false; // CreateProcess with inherited handles: the child gets the pipe
                        if (BeforeStart != null) BeforeStart(exe);
                        p = Process.Start(psi);
                    }
                }
                catch (System.ComponentModel.Win32Exception) { return ChildResult.Failed; }   // not a program, missing
                catch (InvalidOperationException) { return ChildResult.Failed; }
                catch (IOException) { return ChildResult.Failed; }
                catch (UnauthorizedAccessException) { return ChildResult.Failed; }
                server.DisposeLocalCopyOfClientHandle(); // the child's copy is the only writer left: its exit ends the pipe
                if (p == null) return ChildResult.Failed;
                using (p)
                using (ManualResetEvent started = new ManualResetEvent(false))
                using (ManualResetEvent stable = new ManualResetEvent(false))
                {
                    Thread reader = new Thread(delegate()
                    {
                        try
                        {
                            int b;
                            while ((b = server.ReadByte()) >= 0)
                            {
                                if (b == BootLayout.SignalStarted) started.Set();
                                else if (b == BootLayout.SignalStable && started.WaitOne(0)) stable.Set();
                            }
                        }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                    });
                    reader.IsBackground = true;
                    reader.Start();
                    ChildResult r = Watch(p, started, stable);
                    if (r == ChildResult.Failed) Stop(p);
                    return r;
                }
            }
        }

        private ChildResult Watch(Process p, WaitHandle started, WaitHandle stable)
        {
            if (!WaitFor(started, p, t.StartMs)) return ChildResult.Failed;
            if (!WaitFor(stable, p, t.StableMs))
            {
                // The window came up and the player closed it before the second tick: a working start.
                return p.HasExited && p.ExitCode == 0 ? ChildResult.Confirmed : ChildResult.Failed;
            }
            if (p.WaitForExit(t.ExitCheckMs)) return p.ExitCode == 0 ? ChildResult.Confirmed : ChildResult.Failed;
            return ChildResult.Confirmed;
        }

        /// <summary>True when the signal came before the timeout; false on timeout or when the process ended without it.</summary>
        private static bool WaitFor(WaitHandle signal, Process p, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (signal.WaitOne(100)) return true;
                if (p.HasExited) return signal.WaitOne(500); // bytes written just before the exit may still be in flight
            }
            return signal.WaitOne(0);
        }

        private static void Stop(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill();
                p.WaitForExit(10000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
