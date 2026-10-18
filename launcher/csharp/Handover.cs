// The launcher's side of the bootstrap (launcher\bootstrap\Bootstrap.cs, launcher 1.4):
// - confirms its start through the inherited pipe handle it got with --confirm-pipe (MainForm: byte 1 on the first UI
//   timer tick after the start work was started, byte 2 from a tick 3 s later);
// - after the updater replaced launcher\WolfTeamLauncher.exe with a new version, starts the bootstrap again and closes:
//   the bootstrap adopts the new version as a trial and rolls it back itself when it does not confirm its start.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace WolfTeamDirilis
{
    /// <summary>launcher\launcher.log in the install folder: security-relevant events of normal starts (the newest 200
    /// lines; never a password, token or URL path). Not shipped (release set: **/*.log).</summary>
    internal static class LauncherLog
    {
        public static void Write(string gameDir, string line)
        {
            try
            {
                string path = Path.Combine(BootLayout.LauncherDir(gameDir), "launcher.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path, System.Text.Encoding.UTF8));
                lines.Add(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z " + line);
                if (lines.Count > 200) lines.RemoveRange(0, lines.Count - 200);
                File.WriteAllLines(path, lines.ToArray(), new System.Text.UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The TLS fallbacks of this start, if any (MainForm.OnShown).</summary>
        public static void TlsFallback(LauncherConfig cfg)
        {
            if (!cfg.TlsFallbackUsed) return;
            Write(cfg.GameDir, "TLS 1.2 unavailable: " + (cfg.InsecureLogin ? "login (only after the player's yes) and " : "")
                + "news use their http address (" + LauncherConfig.HostPort(cfg.LoginUrl) + "); updates stay https-only");
        }
    }

    internal static class Handover
    {
        private static AnonymousPipeClientStream pipe;

        /// <summary>Set by Program when the bootstrap rolled a new launcher version back (MainForm tells the player).</summary>
        public static bool RolledBack;

        /// <summary>Opens the write end the bootstrap handed over; false when there is none or it is not valid.</summary>
        public static bool Open(string handle)
        {
            if (string.IsNullOrEmpty(handle) || handle.Length > 24) return false;
            foreach (char c in handle)
            {
                if (c < '0' || c > '9') return false;
            }
            try
            {
                pipe = new AnonymousPipeClientStream(PipeDirection.Out, handle);
                return true;
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static bool Connected
        {
            get { return pipe != null; }
        }

        /// <summary>Writes one signal byte; after SignalStable the pipe is closed. A bootstrap that stopped waiting
        /// (broken pipe) is not an error.</summary>
        public static void Signal(byte b)
        {
            AnonymousPipeClientStream p = pipe;
            if (p == null) return;
            try
            {
                p.WriteByte(b);
                p.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            if (b == BootLayout.SignalStable) Close();
        }

        /// <summary>Before a restart for an update: this launcher's window came up, so its start is confirmed now (the
        /// bootstrap that supervises it must finish before the restarted one can take the install lock).</summary>
        public static void ConfirmNow()
        {
            Signal(BootLayout.SignalStarted);
            Signal(BootLayout.SignalStable);
        }

        public static void Close()
        {
            AnonymousPipeClientStream p = pipe;
            pipe = null;
            if (p != null) p.Dispose();
        }

        /// <summary>True when launcher\WolfTeamLauncher.exe is not the exe running now (a new version to hand over to).</summary>
        public static bool StagedDiffersFromRunning(string gameDir, string runningExe)
        {
            string staged = BootLayout.StagedLauncher(gameDir);
            try
            {
                if (!File.Exists(staged)) return false;
                if (string.Equals(Path.GetFullPath(staged), Path.GetFullPath(runningExe), StringComparison.OrdinalIgnoreCase)) return false;
                return Updater.HashFile(staged, null, null) != Updater.HashFile(runningExe, null, null);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Starts the bootstrap with the player's arguments (without the internal ones); false when it did not start.</summary>
        public static bool RestartViaBootstrap(string gameDir, string[] args)
        {
            string boot = BootLayout.BootstrapPath(gameDir);
            if (!File.Exists(boot)) return false;
            List<string> keep = new List<string> { BootLayout.AfterUpdateFlag };
            for (int i = 0; args != null && i < args.Length; i++)
            {
                string a = args[i];
                if (a == BootLayout.RolledBackFlag || a == BootLayout.AfterUpdateFlag) continue;
                if (a == BootLayout.ConfirmPipeFlag || a == BootLayout.GameDirFlag) { i++; continue; }
                keep.Add(a);
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(boot, Quote(keep));
                psi.WorkingDirectory = gameDir;
                psi.UseShellExecute = false;
                using (Process.Start(psi)) { }
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (IOException) { return false; }
        }

        /// <summary>Windows (CommandLineToArgvW) quoting.</summary>
        internal static string Quote(List<string> args)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
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
    }
}
