// Install layout shared by the bootstrap (launcher\bootstrap) and the launcher (launcher 1.4).
//
//   <install>\WolfTeamDirilis.exe                       the bootstrap the shortcut starts. Replaced only by the setup
//                                                        (and once by a 1.2 launcher's own self-update: the migration).
//   <install>\launcher\WolfTeamLauncher.exe             the launcher as the release ships it (manifest file); the
//                                                        updater replaces it like any other file, it never runs from here
//   <install>\launcher\endpoints.ini                    the HTTPS addresses (manifest file, see LauncherConfig)
//   <install>\launcher\versions\<sha16>\WolfTeamLauncher.exe   immutable copies the bootstrap runs
//   <install>\launcher\state.txt, state.bak             install-wide transaction state and its copy (Bootstrapper)
//   <install>\launcher\install.lock                     opened exclusively by the bootstrap and the setup
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WolfTeamDirilis
{
    internal static class BootLayout
    {
        public const string BootstrapName = "WolfTeamDirilis.exe";
        public const string LauncherDirName = "launcher";
        public const string VersionsDirName = "versions";
        public const string LauncherName = "WolfTeamLauncher.exe";
        public const string EndpointsName = "endpoints.ini";
        public const string StateName = "state.txt";
        public const string StateBackupName = "state.bak";
        public const string LockName = "install.lock";

        /// <summary>The setup's AssemblyTitle (launcher\setup\SetupAssemblyInfo.cs) and the oldest setup the bootstrap's
        /// repair offer may run: older setups install a 1.2-style launcher as WolfTeamDirilis.exe (gate 1.4b #1).</summary>
        public const string SetupTitle = "WolfTeam Diriliş kurulumu";
        public static readonly Version MinRepairSetupVersion = new Version(1, 4);

        /// <summary>A setup that installs the bootstrap: our setup title and at least MinRepairSetupVersion.</summary>
        public static bool IsRepairSetup(string fileDescription, string fileVersion)
        {
            Version v;
            return fileDescription == SetupTitle && fileVersion != null && Version.TryParse(fileVersion, out v) && v >= MinRepairSetupVersion;
        }

        public static bool IsRepairSetupFile(string path)
        {
            if (!File.Exists(path)) return false;
            System.Diagnostics.FileVersionInfo fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return IsRepairSetup(fv.FileDescription, fv.FileVersion);
        }

        /// <summary>The setup's marker file in the install folder (launcher\setup SetupCore InstallPaths.MarkerName); its
        /// "setup=" line is the SHA-256 of the setup that installed (WolfTeamDirilis-Kaldir.exe is that setup's copy).</summary>
        public const string MarkerName = "WolfTeamDirilis.kurulum";
        public const string SetupHashKey = "setup=";

        /// <summary>The recorded setup SHA-256 (64 lower-case hex) from the marker, or null.</summary>
        public static string RecordedSetupHash(string install)
        {
            try
            {
                string path = Path.Combine(install, MarkerName);
                if (!File.Exists(path)) return null;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string l = line.Trim();
                    if (!l.StartsWith(SetupHashKey, StringComparison.Ordinal)) continue;
                    string h = l.Substring(SetupHashKey.Length).Trim();
                    if (h.Length != 64) return null;
                    foreach (char c in h) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return null;
                    return h;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>Only a sharing or lock violation means someone else holds a lock file; any other I/O error (disk,
        /// drive gone) is reported, never waited for.</summary>
        public static bool IsSharingViolation(IOException e)
        {
            int code = System.Runtime.InteropServices.Marshal.GetHRForException(e) & 0xFFFF;
            return code == 32 || code == 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        }

        public const string NetworkText = "WolfTeam Diriliş ağ sürücüsünde ya da ağ klasöründe çalışmaz (başka bir bilgisayar aynı "
            + "dosyaları kullanırken güncelleme ve kurulum güvenli değil). Oyunu bu bilgisayarın kendi diskine kur, örneğin "
            + "C:\\Oyunlar\\WolfTeam Dirilis.";

        /// <summary>The default install folder below a drive (launcher\setup InstallPaths.DefaultDir). ASCII only: the game
        /// opens its protection module through ANSI file APIs (see AnsiPathText).</summary>
        public const string DefaultFolder = @"Oyunlar\WolfTeam Dirilis";

        /// <summary>Wolfteam.bin and avital\packmodboot.xoe build the protection module path with the ANSI "%s\%s\%s": a game
        /// folder with a letter outside the system ANSI code page (for example "ş" on an English Windows, cp1252) cannot be
        /// opened and the game stops with "axProtect initialization Failed. file not found 0". On a Turkish Windows (cp1254)
        /// the same folder works.</summary>
        public static string AnsiPathText(string path)
        {
            return "Oyun bu klasörden açılamaz: klasörün yolunda bu bilgisayarın Windows dil ayarının desteklemediği bir harf var "
                + "(örneğin ş, ğ, ı, İ). Oyun böyle bir klasörde \"axProtect initialization Failed\" hatası verir.\r\n\r\n"
                + (string.IsNullOrEmpty(path) ? "" : "Klasör: " + path + "\r\n\r\n")
                + "Kurulum programını (WolfTeamDirilis-Setup.exe) wolf2team.com'dan indir ve oyunu yalnız İngilizce harfler "
                + "içeren bir klasöre yeniden kur, örneğin C:\\Oyunlar\\WolfTeam Dirilis.";
        }

        /// <summary>Tests only (real processes): this code page instead of the system ANSI code page.</summary>
        public const string AnsiCodePageTestVariable = "WOLFTEAM_TEST_ANSI_CODEPAGE";

        /// <summary>The system ANSI code page (CP_ACP), which the game's ANSI file APIs use.</summary>
        public static Encoding SystemAnsi()
        {
            string v = Environment.GetEnvironmentVariable(AnsiCodePageTestVariable);
            int cp;
            if (!string.IsNullOrEmpty(v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out cp))
            {
                try { return Encoding.GetEncoding(cp); }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return Encoding.Default;
        }

        /// <summary>True when the path survives the trip to the ANSI code page and back unchanged: no letter is dropped,
        /// replaced by '?' or "best fit" mapped (ş -> s). Only such a path can be opened through the game's ANSI APIs.</summary>
        public static bool RoundTripsAnsi(string path, Encoding ansi)
        {
            if (path == null || ansi == null) return false;
            try
            {
                // Exception fallbacks: no silent '?' or best-fit replacement can make a lost letter look like a match.
                Encoding strict = Encoding.GetEncoding(ansi.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return string.Equals(strict.GetString(strict.GetBytes(path)), path, StringComparison.Ordinal);
            }
            catch (ArgumentException) { return false; } // includes Encoder/DecoderFallbackException
            catch (NotSupportedException) { return false; }
        }

        /// <summary>null when the game can run from the folder under this code page, else the player's message.</summary>
        public static string AnsiPathProblem(string install, Encoding ansi)
        {
            return RoundTripsAnsi(install, ansi) ? null : AnsiPathText(install);
        }

        /// <summary>A UNC path or a drive letter mapped to a network share (gate 1.4d #1: not supported).</summary>
        public static bool IsNetworkPath(string path)
        {
            return IsNetworkPath(path, delegate(string root) { return new DriveInfo(root).DriveType; });
        }

        public static bool IsNetworkPath(string path, Func<string, DriveType> driveType)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Trim();
            if (p.StartsWith(@"\\") || p.StartsWith("//")) return true;
            if (p.Length < 2 || !char.IsLetter(p[0]) || p[1] != ':') return false;
            try { return driveType(p.Substring(0, 2) + "\\") == DriveType.Network; }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
        }
        /// <summary>Held by a launcher for its whole update (check, download, replace, verify): one at a time per install.</summary>
        public const string UpdateLockName = "update.lock";

        /// <summary>"--confirm-pipe &lt;handle&gt;": the inherited write end of the bootstrap's anonymous pipe.</summary>
        public const string ConfirmPipeFlag = "--confirm-pipe";
        /// <summary>Given by the bootstrap after a new launcher version did not start (the launcher tells the player).</summary>
        public const string RolledBackFlag = "--rolled-back";
        public const string GameDirFlag = "--gamedir";
        /// <summary>Given to the bootstrap by a launcher that just downloaded a new launcher version (Handover): it waits for
        /// the install lock instead of stepping aside.</summary>
        public const string AfterUpdateFlag = "--after-update";
        public const string ConfigFlag = "--config";

        /// <summary>The two bytes a launcher writes to the confirm pipe: its start finished, and its UI still runs later.</summary>
        public const byte SignalStarted = 1;
        public const byte SignalStable = 2;

        public static string LauncherDir(string install) { return Path.Combine(install, LauncherDirName); }
        public static string BootstrapPath(string install) { return Path.Combine(install, BootstrapName); }
        public static string StagedLauncher(string install) { return Path.Combine(LauncherDir(install), LauncherName); }
        public static string EndpointsPath(string install) { return Path.Combine(LauncherDir(install), EndpointsName); }
        public static string VersionsDir(string install) { return Path.Combine(LauncherDir(install), VersionsDirName); }
        public static string StatePath(string install) { return Path.Combine(LauncherDir(install), StateName); }
        public static string StateBackupPath(string install) { return Path.Combine(LauncherDir(install), StateBackupName); }
        public static string LockPath(string install) { return Path.Combine(LauncherDir(install), LockName); }
        public static string UpdateLockPath(string install) { return Path.Combine(LauncherDir(install), UpdateLockName); }

        public static string VersionExe(string install, string sha256)
        {
            return Path.Combine(Path.Combine(VersionsDir(install), sha256.Substring(0, 16)), LauncherName);
        }

        /// <summary>The install folder of a launcher exe: &lt;install&gt;\launcher\versions\&lt;x&gt;\WolfTeamLauncher.exe or
        /// &lt;install&gt;\launcher\WolfTeamLauncher.exe; any other place is its own folder (a developer build).</summary>
        public static string InstallDirOf(string exe)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(exe));
            DirectoryInfo d = new DirectoryInfo(dir);
            if (d.Parent != null && d.Parent.Parent != null
                && string.Equals(d.Parent.Name, VersionsDirName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Parent.Parent.Name, LauncherDirName, StringComparison.OrdinalIgnoreCase)
                && d.Parent.Parent.Parent != null)
                return d.Parent.Parent.Parent.FullName;
            if (string.Equals(d.Name, LauncherDirName, StringComparison.OrdinalIgnoreCase) && d.Parent != null)
                return d.Parent.FullName;
            return dir;
        }
    }

    /// <summary>launcher\state.txt (and its copy launcher\state.bak): a header line, then "key=value" lines. Hashes are 64
    /// lowercase hex characters. A file that is not exactly that (an unknown key, a bad value, no header or no "attempts"
    /// line, a truncated write) is damaged, never read as an empty state.</summary>
    internal sealed class BootState
    {
        public const string Header = "# WolfTeam Diriliş launcher versions (written by WolfTeamDirilis.exe; do not edit)";

        public string Current, Previous, Trial;
        public int Attempts;
        public bool Legacy;
        public readonly List<string> Failed = new List<string>();

        /// <summary>Null when the file does not exist or is damaged (Load tells which).</summary>
        public static BootState Read(string path)
        {
            bool damaged;
            return Load(path, out damaged);
        }

        public static BootState Load(string path, out bool damaged)
        {
            damaged = false;
            if (!File.Exists(path)) return null;
            BootState s = Parse(File.ReadAllText(path, Encoding.UTF8));
            damaged = s == null;
            return s;
        }

        public static BootState Parse(string text)
        {
            // The last line is "check=<SHA-256 of everything before it>": a cut or edited file is damaged.
            int at = text.LastIndexOf("check=", StringComparison.Ordinal);
            if (at <= 0 || text[at - 1] != '\n') return null;
            string body = text.Substring(0, at), check = text.Substring(at + 6).TrimEnd('\n');
            if (check != Check(body)) return null;
            string[] lines = body.Split('\n');
            if (lines.Length < 2 || lines[0] != Header) return null;
            BootState s = new BootState();
            bool attempts = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw.Length == 0) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0) return null;
                string k = raw.Substring(0, eq), v = raw.Substring(eq + 1);
                if (k == "current") { if ((s.Current = Hash(v)) == null) return null; }
                else if (k == "previous") { if ((s.Previous = Hash(v)) == null) return null; }
                else if (k == "trial") { if ((s.Trial = Hash(v)) == null) return null; }
                else if (k == "attempts")
                {
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out s.Attempts) || s.Attempts > 99) return null;
                    attempts = true;
                }
                else if (k == "legacy") { if (v != "1") return null; s.Legacy = true; }
                else if (k == "failed")
                {
                    foreach (string f in v.Split(','))
                    {
                        string h = Hash(f);
                        if (h == null) return null;
                        if (!s.Failed.Contains(h)) s.Failed.Add(h);
                    }
                }
                else return null;
            }
            return attempts ? s : null;
        }

        private static string Hash(string v)
        {
            if (v.Length != 64) return null;
            foreach (char c in v)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return null;
            }
            return v;
        }

        public string Text()
        {
            StringBuilder b = new StringBuilder();
            b.Append(Header).Append('\n');
            if (Current != null) b.Append("current=").Append(Current).Append('\n');
            if (Previous != null) b.Append("previous=").Append(Previous).Append('\n');
            if (Trial != null) b.Append("trial=").Append(Trial).Append('\n');
            b.Append("attempts=").Append(Attempts.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (Legacy) b.Append("legacy=1\n");
            if (Failed.Count > 0) b.Append("failed=").Append(string.Join(",", Failed.ToArray())).Append('\n');
            string body = b.ToString();
            return body + "check=" + Check(body) + "\n";
        }

        private static string Check(string body)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(body));
                StringBuilder b = new StringBuilder(64);
                foreach (byte x in h) b.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                return b.ToString();
            }
        }

        /// <summary>True when the install has launcher state the bootstrap can use (state.txt or state.bak whole).</summary>
        public static bool UsableIn(string install)
        {
            foreach (string p in new string[] { BootLayout.StatePath(install), BootLayout.StateBackupPath(install) })
            {
                try { if (Read(p) != null) return true; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }

        public void MarkFailed(string h)
        {
            if (h == null || Failed.Contains(h)) return;
            Failed.Add(h);
        }
    }
}
