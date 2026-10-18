// Pure rules of the installer: install-folder checks, command line, shortcut and uninstall-entry plans.
// Tested in launcher\tests\SetupTests.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WolfTeamDirilis.Setup
{
    internal static class SetupInfo
    {
        public const string Version = "1.6.1"; // same as the launcher it carries (Program.Version)
        public const string FileVersion = "1.6.1.0"; // Version padded to four parts (SetupAssemblyInfo.cs)
        public const string ProductName = "WolfTeam Diriliş";
        public const string Publisher = "WolfTeam Diriliş";
        public const string Website = "https://wolf2team.com";
        public const string LauncherExe = "WolfTeamDirilis.exe";
        /// <summary>Game files the launcher downloads later (about 1.9 GB) plus the launcher, for Programs and Features.</summary>
        public const int EstimatedSizeKb = 2000000;
    }

    /// <summary>A failure with a Turkish, one-paragraph message for the user.</summary>
    internal sealed class InstallException : Exception
    {
        /// <summary>True when the game or launcher from this folder is running: the UI offers "Tekrar dene".</summary>
        public bool Running;

        public InstallException(string message) : base(message) { }
    }

    /// <summary>Folders the installer never writes into (or never takes as the install folder itself).</summary>
    internal sealed class SystemFolders
    {
        public string Windows;
        /// <summary>Program Files folders: installing below them needs admin rights for every launcher update.</summary>
        public string[] ProgramFiles;
        /// <summary>Folders that may hold a subfolder install but must never BE the install folder (the uninstaller
        /// removes the install folder).</summary>
        public string[] Protected;

        public static SystemFolders Current()
        {
            SystemFolders f = new SystemFolders();
            f.Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            List<string> pf = new List<string>();
            foreach (string v in new string[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
            {
                string p = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(p)) pf.Add(p);
            }
            f.ProgramFiles = pf.ToArray();
            List<string> prot = new List<string>();
            foreach (Environment.SpecialFolder sf in new Environment.SpecialFolder[]
            {
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.CommonApplicationData
            })
            {
                string p = Environment.GetFolderPath(sf);
                if (!string.IsNullOrEmpty(p)) prot.Add(p);
            }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                prot.Add(Path.Combine(profile, "Downloads"));
                prot.Add(Path.Combine(profile, "OneDrive"));
                string users = Path.GetDirectoryName(profile);
                if (!string.IsNullOrEmpty(users)) prot.Add(users);
            }
            prot.Add(Path.GetTempPath());
            f.Protected = prot.ToArray();
            return f;
        }
    }

    internal enum FolderKind { Missing, Empty, OurInstall, OldPackage, Foreign }

    internal static class InstallPaths
    {
        /// <summary>Written into every install folder; the uninstaller removes only folders that have it.</summary>
        public const string MarkerName = "WolfTeamDirilis.kurulum";
        public const long RequiredBytes = 4L << 30;
        /// <summary>Game files add up to ~100 characters below the install folder; MAX_PATH is 260.</summary>
        public const int MaxLength = 120;
        /// <summary>The install folder the wizard suggests and creates below a picked parent. ASCII only since 1.5: the game
        /// cannot start from a folder with letters outside the system ANSI code page (BootLayout.AnsiPathText).</summary>
        public const string FolderName = "WolfTeam Dirilis";
        private const string Example = @"C:\Oyunlar\" + FolderName;

        public static string DefaultDir(string drive)
        {
            string d = string.IsNullOrEmpty(drive) ? "C:" : drive.TrimEnd('\\');
            return d + "\\" + BootLayout.DefaultFolder;
        }

        /// <summary>null when the game can start from the folder under this ANSI code page (the system one in the setup),
        /// else a Turkish reason. Not part of Check: an existing install in such a folder must stay removable.</summary>
        public static string AnsiProblem(string dir, Encoding ansi)
        {
            string full;
            try { full = Path.GetFullPath(dir.Trim()).TrimEnd('\\'); }
            catch (ArgumentException) { return "Klasör yolu geçersiz."; }
            catch (NotSupportedException) { return "Klasör yolu geçersiz."; }
            catch (PathTooLongException) { return "Klasör yolu çok uzun (en fazla " + MaxLength + " karakter)."; }
            if (BootLayout.RoundTripsAnsi(full, ansi)) return null;
            return "Bu klasör yolunda bu bilgisayarın Windows dil ayarının desteklemediği bir harf var (örneğin ş, ğ, ı, İ). "
                   + "Oyun böyle bir klasörden açılamaz (\"axProtect initialization Failed\" hatası verir). Yalnız İngilizce "
                   + "harfler kullan, örneğin " + DefaultDir(Path.GetPathRoot(full)) + ".";
        }

        /// <summary>null when the folder may be used, else a Turkish reason.</summary>
        public static string Check(string dir, SystemFolders sys)
        {
            if (string.IsNullOrWhiteSpace(dir)) return "Kurulum klasörünü seç.";
            dir = dir.Trim();
            if (dir.StartsWith(@"\\") || dir.StartsWith("//")) return "Ağ klasörüne kurulamaz; bilgisayarındaki bir sürücüyü seç.";
            if (dir.Length < 3 || !char.IsLetter(dir[0]) || dir[1] != ':' || (dir[2] != '\\' && dir[2] != '/'))
                return "Tam bir klasör yolu yaz, örneğin " + Example + ".";
            if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || dir.IndexOfAny(new char[] { '*', '?', '"', '<', '>', '|' }) >= 0
                || dir.IndexOf(':', 2) >= 0)
                return "Klasör adında şu karakterler olamaz: < > : \" | ? *";
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
            catch (ArgumentException) { return "Klasör yolu geçersiz."; }
            catch (NotSupportedException) { return "Klasör yolu geçersiz."; }
            catch (PathTooLongException) { return "Klasör yolu çok uzun (en fazla " + MaxLength + " karakter)."; }
            if (full.Length <= 2) return "Sürücünün kendisine kurulamaz; içinde bir klasör seç, örneğin " + Example + ".";
            if (full.Length > MaxLength) return "Klasör yolu çok uzun (en fazla " + MaxLength + " karakter).";
            foreach (string seg in full.Substring(3).Split('\\'))
            {
                if (seg.Length == 0 || seg.Trim() != seg || seg.EndsWith(".")) return "Klasör adı boşlukla ya da noktayla bitemez.";
            }
            if (Under(full, sys.Windows)) return "Windows klasörüne kurulamaz.";
            foreach (string pf in sys.ProgramFiles)
            {
                if (Under(full, pf))
                    return "Program Files altına kurulamaz: orada başlatıcı güncelleme indirirken her seferinde yönetici izni "
                           + "gerekir ve güncelleme yarım kalır. " + Example + " gibi bir klasör seç.";
            }
            foreach (string p in sys.Protected)
            {
                if (Same(full, p))
                    return "Bu klasörün kendisine kurulamaz; içinde yeni bir klasör seç (örneğin " + Path.Combine(full, FolderName) + ").";
            }
            return null;
        }

        public static FolderKind Classify(string dir)
        {
            if (!Directory.Exists(dir)) return FolderKind.Missing;
            if (File.Exists(Path.Combine(dir, MarkerName))) return FolderKind.OurInstall;
            if (File.Exists(Path.Combine(dir, "Wolfteam.bin")) || File.Exists(Path.Combine(dir, SetupInfo.LauncherExe))) return FolderKind.OldPackage;
            string[] entries = Directory.GetFileSystemEntries(dir);
            if (entries.Length == 0) return FolderKind.Empty;
            // What an uninstall leaves behind: Profiles ("keep my settings") and/or the launcher folder emptied a moment
            // before its last step (only empty folders go after the uninstall lets its lock go). Installing there is fine.
            foreach (string e in entries)
            {
                string n = Path.GetFileName(e);
                if (!Directory.Exists(e)) return FolderKind.Foreign;
                if (string.Equals(n, "Profiles", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n, "launcher", StringComparison.OrdinalIgnoreCase) && Directory.GetFileSystemEntries(e).Length == 0) continue;
                return FolderKind.Foreign;
            }
            return FolderKind.OldPackage;
        }

        public static string SpaceProblem(long freeBytes)
        {
            if (freeBytes >= RequiredBytes) return null;
            return "Diskte yeterli boş alan yok: en az 4 GB gerekiyor (oyun dosyaları yaklaşık 2 GB, güncellemeler için ek yer). "
                   + "Başka bir sürücü seç ya da yer aç.";
        }

        public static string FormatBytes(long bytes)
        {
            CultureInfo tr = CultureInfo.GetCultureInfo("tr-TR");
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", tr) + " GB";
            return (bytes >> 20).ToString(tr) + " MB";
        }

        /// <summary>True when the folder itself or any existing folder above it is a junction/symbolic link (the real
        /// place would differ from the checked path), or when that cannot be checked.</summary>
        public static bool CrossesReparsePoint(string dir)
        {
            try
            {
                string cur = Path.GetFullPath(dir).TrimEnd('\\');
                while (!string.IsNullOrEmpty(cur) && cur.Length > 3)
                {
                    if (Directory.Exists(cur) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0) return true;
                    cur = Path.GetDirectoryName(cur);
                }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (ArgumentException) { return true; }
        }

        public static bool Under(string path, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string p = path.TrimEnd('\\'), r = root.TrimEnd('\\');
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool Same(string a, string b)
        {
            return !string.IsNullOrEmpty(b) && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Command line, NSIS style: /S silent, /D=&lt;folder&gt; last (the rest of the line, spaces allowed, no
    /// quotes needed). Test-only overrides keep the smoke test away from the real desktop, Start menu, HKCU uninstall
    /// list and %APPDATA%.</summary>
    internal sealed class SetupOptions
    {
        public bool Silent, Uninstall, KeepProfiles, Launch, Inner;
        public bool Desktop = true, StartMenu = true;
        public int ParentPid;
        public string Dir;
        public string DesktopDir, StartMenuDir, RegistryKey, AppDataDir;

        public static SetupOptions Parse(string tail)
        {
            SetupOptions o = new SetupOptions();
            string s = tail ?? "";
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                if (string.Compare(s, i, "/D=", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    o.Dir = s.Substring(i + 3).Trim().Trim('"');
                    break;
                }
                string tok;
                if (s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) end = s.Length;
                    tok = s.Substring(i + 1, end - i - 1);
                    i = end + 1;
                }
                else
                {
                    int end = i;
                    while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
                    tok = s.Substring(i, end - i);
                    i = end;
                }
                o.Apply(tok);
            }
            return o;
        }

        private void Apply(string tok)
        {
            string low = tok.ToLowerInvariant();
            if (low == "/s") Silent = true;
            else if (low == "/uninstall") Uninstall = true;
            else if (low == "/nodesktop") Desktop = false;
            else if (low == "/nostartmenu") StartMenu = false;
            else if (low == "/keepprofiles") KeepProfiles = true;
            else if (low == "/launch") Launch = true;
            else if (low == "/inner") Inner = true;
            else if (low.StartsWith("/parent=")) int.TryParse(tok.Substring(8), NumberStyles.None, CultureInfo.InvariantCulture, out ParentPid);
            else if (low.StartsWith("/desktopdir=")) DesktopDir = tok.Substring(12);
            else if (low.StartsWith("/startmenudir=")) StartMenuDir = tok.Substring(14);
            else if (low.StartsWith("/regkey=")) RegistryKey = tok.Substring(8);
            else if (low.StartsWith("/appdata=")) AppDataDir = tok.Substring(9);
        }

        /// <summary>The same options as a command line for the uninstaller's second stage (folder last).</summary>
        public string ToInnerArgs(int parentPid, string dir)
        {
            StringBuilder b = new StringBuilder("/uninstall /inner /parent=" + parentPid.ToString(CultureInfo.InvariantCulture));
            if (Silent) b.Append(" /S");
            if (KeepProfiles) b.Append(" /keepprofiles");
            if (DesktopDir != null) b.Append(" \"/desktopdir=" + DesktopDir + "\"");
            if (StartMenuDir != null) b.Append(" \"/startmenudir=" + StartMenuDir + "\"");
            if (RegistryKey != null) b.Append(" \"/regkey=" + RegistryKey + "\"");
            if (AppDataDir != null) b.Append(" \"/appdata=" + AppDataDir + "\"");
            b.Append(" /D=").Append(dir);
            return b.ToString();
        }
    }

    internal sealed class ShortcutSpec
    {
        public string LinkPath, Target, WorkingDir, IconPath, Description;
    }

    internal static class ShortcutPlan
    {
        public const string LinkName = "WolfTeam Diriliş.lnk";
        public const string StartMenuFolder = "WolfTeam Diriliş";

        public static List<ShortcutSpec> Build(string installDir, string desktopDir, string programsDir, bool desktop, bool startMenu)
        {
            List<ShortcutSpec> list = new List<ShortcutSpec>();
            if (desktop) list.Add(Spec(installDir, Path.Combine(desktopDir, LinkName)));
            if (startMenu) list.Add(Spec(installDir, Path.Combine(Path.Combine(programsDir, StartMenuFolder), LinkName)));
            return list;
        }

        private static ShortcutSpec Spec(string dir, string link)
        {
            ShortcutSpec s = new ShortcutSpec();
            s.LinkPath = link;
            s.Target = Path.Combine(dir, SetupInfo.LauncherExe);
            s.WorkingDir = dir;
            s.IconPath = s.Target;
            s.Description = "WolfTeam Diriliş başlatıcısı: güncelle, giriş yap, oyna";
            return s;
        }
    }

    internal static class UninstallEntry
    {
        public const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WolfTeamDirilis";

        public static Dictionary<string, object> Values(string dir, string version, int sizeKb, DateTime date)
        {
            string un = "\"" + Path.Combine(dir, Installer.UninstallerName) + "\" /uninstall";
            Dictionary<string, object> v = new Dictionary<string, object>();
            v["DisplayName"] = SetupInfo.ProductName;
            v["DisplayVersion"] = version;
            v["Publisher"] = SetupInfo.Publisher;
            v["URLInfoAbout"] = SetupInfo.Website;
            v["InstallLocation"] = dir;
            v["DisplayIcon"] = Path.Combine(dir, SetupInfo.LauncherExe) + ",0";
            v["UninstallString"] = un;
            v["QuietUninstallString"] = un + " /S";
            v["InstallDate"] = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            v["NoModify"] = 1;
            v["NoRepair"] = 1;
            v["EstimatedSize"] = sizeKb;
            return v;
        }
    }
}
