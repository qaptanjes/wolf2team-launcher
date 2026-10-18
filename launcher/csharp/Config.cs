// WolfTeamDirilis.ini in the install folder, launcher\endpoints.ini (the HTTPS addresses, shipped with the release), and
// the per-user settings file under %APPDATA%\WolfTeamDirilis.
//
// HTTPS (security audit F2/F3): an https LoginUrl / UpdateBaseUrl / NewsUrl from endpoints.ini (only https values are
// taken from it) or from WolfTeamDirilis.ini wins over an http one. Without TLS 1.2 on the PC (Tls.Available false) the
// login and the news fall back to the http address of WolfTeamDirilis.ini, and the player is told (TlsFallbackUsed); the
// update never does: its list must come from an authentic source, so it fails instead (the offline rule still applies).
//
// ServerHost (the game server the status badge probes; launcher 1.6.1): endpoints.ini's ServerHost wins over the one in
// WolfTeamDirilis.ini, then DefaultServerHost. The retired address RetiredServerHost (the server moved on 2026-10-02;
// setups up to 1.6 wrote it into WolfTeamDirilis.ini) counts as not set, so those installs heal without a new file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WolfTeamDirilis
{
    internal sealed class LauncherConfig
    {
        public const string DefaultServerHost = "188.40.176.195";
        /// <summary>The game server's address until 2026-10-02 (dead since the move); a ServerHost with this value is ignored.</summary>
        public const string RetiredServerHost = "45.76.145.60";
        public const string FileName = "WolfTeamDirilis.ini"; // not "launcher.ini": the client already ships its own Launcher.ini
        public const string GameExeName = "Wolfteam.bin";

        public string ServerHost;
        public string LoginUrl;
        /// <summary>Directory URL (ends with '/') that manifest.json and the client files are served from. Contains the
        /// download secret: never show it in the UI, logs or console; use <see cref="Redact"/>.</summary>
        public string UpdateBaseUrl;
        /// <summary>news.json URL; may contain the download secret as well.</summary>
        public string NewsUrl;
        public string HomeUrl;
        public string SupportUrl;
        public int GamePort = 40706;
        /// <summary>The login or the news use their http address because this PC has no TLS 1.2 (MainForm tells the player).</summary>
        public bool TlsFallbackUsed;
        /// <summary>The login would go over plain HTTP - for any reason (no TLS 1.2 on this PC, or no https login address
        /// configured yet): MainForm asks the player before every session's first login and never sends the password
        /// without a "yes" (gate 1.4 #1: the decision follows the effective address, not why it was chosen).</summary>
        public bool InsecureLogin
        {
            get
            {
                Uri u;
                if (!Uri.TryCreate(LoginUrl, UriKind.Absolute, out u)) return true;
                return u.Scheme != Uri.UriSchemeHttps && !u.IsLoopback;
            }
        }

        public string GameDir;
        public string SourcePath;
        public bool FileFound;
        /// <summary>Human-readable notes about keys that were invalid and replaced by defaults (no URLs in them).</summary>
        public readonly List<string> Warnings = new List<string>();
        /// <summary>Notes for launcher\launcher.log and the selftest only (English, no URLs): settings that were healed
        /// and need nothing from the player.</summary>
        public readonly List<string> LogNotes = new List<string>();

        public string GameExePath
        {
            get { return Path.Combine(GameDir, GameExeName); }
        }

        public static LauncherConfig Load(string gameDir, string configPath)
        {
            LauncherConfig c = new LauncherConfig();
            c.GameDir = Path.GetFullPath(gameDir);
            c.SourcePath = configPath ?? Path.Combine(c.GameDir, FileName);
            Dictionary<string, string> kv;
            try
            {
                kv = ReadKv(c.SourcePath);
                c.FileFound = kv != null;
            }
            catch (IOException) { kv = null; c.Warnings.Add("WolfTeamDirilis.ini okunamadı; varsayılan ayarlar kullanılıyor."); }
            catch (UnauthorizedAccessException) { kv = null; c.Warnings.Add("WolfTeamDirilis.ini okunamadı; varsayılan ayarlar kullanılıyor."); }
            if (kv == null) kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> ep;
            try { ep = ReadKv(BootLayout.EndpointsPath(c.GameDir)); }
            catch (IOException) { ep = null; }
            catch (UnauthorizedAccessException) { ep = null; }
            if (ep == null) ep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            c.ServerHost = c.PickServerHost(Get(ep, "ServerHost"), Get(kv, "ServerHost"));

            string login = HttpUrlOrNull(Get(kv, "LoginUrl"));
            if (login == null)
            {
                if (Get(kv, "LoginUrl").Length > 0) c.Warnings.Add("LoginUrl geçersiz; varsayılan kullanılıyor.");
                login = "http://" + c.ServerHost + "/api/login.php";
            }
            c.LoginUrl = c.Pick(HttpsUrlOrNull(Get(ep, "LoginUrl")), login, true);

            string update = HttpUrlOrNull(Get(kv, "UpdateBaseUrl"));
            if (update == null && Get(kv, "UpdateBaseUrl").Length > 0) c.Warnings.Add("UpdateBaseUrl geçersiz; güncelleme kapalı.");
            c.UpdateBaseUrl = c.Pick(HttpsUrlOrNull(Get(ep, "UpdateBaseUrl")), update, false);
            if (c.UpdateBaseUrl != null && !c.UpdateBaseUrl.EndsWith("/")) c.UpdateBaseUrl += "/";

            string news = HttpUrlOrNull(Get(kv, "NewsUrl"));
            if (news == null && Get(kv, "NewsUrl").Length > 0) c.Warnings.Add("NewsUrl geçersiz; duyurular kapalı.");
            c.NewsUrl = c.Pick(HttpsUrlOrNull(Get(ep, "NewsUrl")), news, true);

            c.HomeUrl = HttpUrlOrNull(Get(kv, "HomeUrl"));
            c.SupportUrl = HttpUrlOrNull(Get(kv, "SupportUrl"));

            int port;
            if (int.TryParse(Get(kv, "GamePort"), out port) && port > 0 && port < 65536) c.GamePort = port;
            return c;
        }

        /// <summary>endpoints.ini's ServerHost, else WolfTeamDirilis.ini's, else DefaultServerHost. An invalid value is
        /// skipped with a warning, the retired address with a log note (setups up to 1.6 wrote it).</summary>
        private string PickServerHost(string fromEndpoints, string fromIni)
        {
            string ep = UsableHost(fromEndpoints, "endpoints.ini");
            if (ep != null) return ep;
            string ini = UsableHost(fromIni, FileName);
            return ini ?? DefaultServerHost;
        }

        private string UsableHost(string host, string file)
        {
            if (host.Length == 0) return null;
            if (!IsValidHost(host))
            {
                Warnings.Add(file + " içindeki ServerHost geçersiz; yok sayıldı.");
                return null;
            }
            if (string.Equals(host, RetiredServerHost, StringComparison.OrdinalIgnoreCase))
            {
                LogNotes.Add(file + ": ServerHost " + RetiredServerHost + " is retired (the server moved on 2026-10-02); ignored");
                return null;
            }
            return host;
        }

        /// <summary>The https address when there is one; without TLS 1.2 the http address of WolfTeamDirilis.ini if
        /// {fallback} allows it (never for the update list).</summary>
        private string Pick(string https, string ini, bool fallback)
        {
            if (https == null) return ini;
            if (Tls.Available) return https;
            if (fallback && ini != null && ini.StartsWith("http:", StringComparison.OrdinalIgnoreCase))
            {
                TlsFallbackUsed = true;
                return ini;
            }
            return https;
        }

        /// <summary>"key=value" lines (';', '#' and '[' lines skipped); null when the file does not exist.</summary>
        private static Dictionary<string, string> ReadKv(string path)
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return kv;
        }

        private static string HttpsUrlOrNull(string s)
        {
            string u = HttpUrlOrNull(s);
            return u != null && u.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? u : null;
        }

        private static string Get(Dictionary<string, string> kv, string key)
        {
            string v;
            return kv.TryGetValue(key, out v) && v != null ? v : "";
        }

        private static bool IsValidHost(string host)
        {
            return host.Length > 0 && Uri.CheckHostName(host) != UriHostNameType.Unknown;
        }

        private static string HttpUrlOrNull(string s)
        {
            Uri u;
            if (s.Length == 0 || !Uri.TryCreate(s, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            return u.AbsoluteUri;
        }

        /// <summary>scheme://host:port/… - the path (which may hold the download secret) is never shown.</summary>
        public static string Redact(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return "(yok)";
            return u.Scheme + "://" + u.Host + ":" + u.Port + "/…";
        }

        public static string HostPort(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return "?";
            return u.Host + ":" + u.Port;
        }

        public int LoginPort
        {
            get
            {
                Uri u;
                return Uri.TryCreate(LoginUrl, UriKind.Absolute, out u) ? u.Port : 80;
            }
        }

        public string LoginHost
        {
            get
            {
                Uri u;
                return Uri.TryCreate(LoginUrl, UriKind.Absolute, out u) ? u.Host : ServerHost;
            }
        }
    }

    /// <summary>Remembered user name only. The password is never written anywhere.</summary>
    internal static class UserSettings
    {
        /// <summary>Tests only: a scratch folder instead of %APPDATA%\WolfTeamDirilis.</summary>
        internal static string DirOverride = null;

        public static string Dir
        {
            get
            {
                if (DirOverride != null) return DirOverride;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WolfTeamDirilis");
            }
        }

        private static string UserFile
        {
            get { return Path.Combine(Dir, "user.txt"); }
        }

        public static string LoadUser()
        {
            try
            {
                if (!File.Exists(UserFile)) return null;
                string u = File.ReadAllText(UserFile, Encoding.UTF8).Trim();
                return u.Length > 0 && u.Length <= 32 ? u : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static void SaveUser(string user)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(UserFile, user, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static void ForgetUser()
        {
            try
            {
                if (File.Exists(UserFile)) File.Delete(UserFile);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
