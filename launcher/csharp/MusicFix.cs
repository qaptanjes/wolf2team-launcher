// One-time repair for installs made from the early package: it shipped Profiles\Player.txt with the owner's
// [Sound] musicvolume = -2500, the slider's minimum (stream gain (v + 2500) / 7500 = 0), so the lobby music was silent.
// Exactly -2500 becomes the factory default 1250 (Profiles\Default.prf); any other value is the player's choice and
// stays. Runs once per game folder (a marker in %APPDATA%\WolfTeamDirilis), never while the game runs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WolfTeamDirilis
{
    internal enum MusicFixResult { AlreadyDone, GameRunning, NothingToChange, Changed, Failed }

    internal static class MusicFix
    {
        public const int Muted = -2500;
        public const int FactoryDefault = 1250;
        private static readonly Regex Section = new Regex(@"^\s*\[([^\]]*)\]\s*$");
        private static readonly Regex Key = new Regex(@"^(\s*musicvolume\s*=\s*)(-?\d+)(\s*)$", RegexOptions.IgnoreCase);
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591); // byte-exact round trip

        /// <summary>Profile text with [Sound] musicvolume = -2500 set to 1250; everything else byte-identical.</summary>
        public static string FixProfile(string text, out bool changed)
        {
            changed = false;
            StringBuilder sb = new StringBuilder(text.Length + 8);
            string section = null;
            int pos = 0;
            while (pos < text.Length)
            {
                int nl = text.IndexOf('\n', pos);
                int end = nl < 0 ? text.Length : nl + 1;
                string line = text.Substring(pos, end - pos);
                string body = line.TrimEnd('\r', '\n');
                string eol = line.Substring(body.Length);
                // A UTF-8 byte order mark read as Latin-1 ("ï»¿") must not hide the first section header.
                string probe = pos == 0 && body.StartsWith("ï»¿") ? body.Substring(3) : body;
                Match s = Section.Match(probe);
                if (s.Success) section = s.Groups[1].Value.Trim().ToLowerInvariant();
                Match k = Key.Match(body);
                if (k.Success && section == "sound" && k.Groups[2].Value == Muted.ToString())
                {
                    body = k.Groups[1].Value + FactoryDefault + k.Groups[3].Value;
                    changed = true;
                }
                sb.Append(body).Append(eol);
                pos = end;
            }
            return sb.ToString();
        }

        private static string MarkerPath(string gameDir)
        {
            return Path.Combine(UserSettings.Dir, "music-fix-" + Updater.GameDirKey(gameDir) + ".txt");
        }

        public static MusicFixResult Run(string gameDir, bool gameRunning)
        {
            try
            {
                if (File.Exists(MarkerPath(gameDir))) return MusicFixResult.AlreadyDone;
                if (gameRunning) return MusicFixResult.GameRunning; // the game rewrites the profile on exit
                bool any = false;
                string profiles = Path.Combine(gameDir, "Profiles");
                if (Directory.Exists(profiles))
                {
                    foreach (string f in Directory.GetFiles(profiles, "*.txt"))
                    {
                        if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue;
                        bool changed;
                        // Bytes, not ReadAllText: that would honour a BOM and re-encode the file (Codex review #4).
                        string text = FixProfile(Latin1.GetString(File.ReadAllBytes(f)), out changed);
                        if (!changed) continue;
                        string tmp = f + ".tmp";
                        File.WriteAllBytes(tmp, Latin1.GetBytes(text));
                        File.Replace(tmp, f, null, true);
                        any = true;
                    }
                }
                Directory.CreateDirectory(UserSettings.Dir);
                File.WriteAllText(MarkerPath(gameDir), "music volume checked " + DateTime.UtcNow.ToString("o") + "\n", new UTF8Encoding(false));
                return any ? MusicFixResult.Changed : MusicFixResult.NothingToChange;
            }
            catch (IOException) { return MusicFixResult.Failed; }
            catch (UnauthorizedAccessException) { return MusicFixResult.Failed; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        /// <summary>True when a process runs from exactly this exe (the game); works across 32/64-bit.</summary>
        public static bool IsRunning(string exePath)
        {
            string want = Path.GetFullPath(exePath);
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(h, 0, sb, ref size)
                            && string.Equals(sb.ToString(0, size), want, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    finally { CloseHandle(h); }
                }
            }
            return false;
        }
    }
}
