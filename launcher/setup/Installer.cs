// The install / uninstall engine, shared by the wizard and the silent mode (/S). Per-user: writes only the install
// folder, the user's desktop and Start menu, HKCU and %APPDATA%\WolfTeamDirilis. No admin rights needed.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WolfTeamDirilis.Setup
{
    /// <summary>The files the setup carries (embedded resources in the real exe, a dictionary in tests).</summary>
    internal sealed class Payload
    {
        /// <summary>The bootstrap the shortcut starts, the launcher it runs (launcher\csharp\BootLayout.cs), and the rest.</summary>
        /// <summary>In install order: the launcher and its addresses first, the bootstrap (the shortcut's exe) last, so a
        /// setup stopped halfway leaves the old exe working (Codex 1.4 round 4).</summary>
#if SIGNPATH_PREPARATION
        // Source-only CI qualification: do not embed or require artwork extracted from the game.
        public static readonly string[] Names = { "launcher\\WolfTeamLauncher.exe", "WolfTeamDirilis.ini", "KURULUM.txt",
                                                  "WolfTeamDirilis.exe" };
#else
        public static readonly string[] Names = { "launcher\\WolfTeamLauncher.exe", "bg.png", "WolfTeamDirilis.ini", "KURULUM.txt",
                                                  "WolfTeamDirilis.exe" };
#endif
        /// <summary>The HTTPS addresses (security audit R5): the launcher updates only over HTTPS.</summary>
        public const string Endpoints = "launcher\\endpoints.ini";
        private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, byte[] data)
        {
            files[name] = data;
        }

        public byte[] Get(string name)
        {
            byte[] b;
            if (!files.TryGetValue(name, out b)) throw new InstallException("Kurulum dosyası bozuk (" + name + " yok). Kurulumu siteden yeniden indir.");
            return b;
        }

        public bool Has(string name)
        {
            return files.ContainsKey(name);
        }

        public static Payload FromResources()
        {
            Payload p = new Payload();
            Assembly a = Assembly.GetExecutingAssembly();
            foreach (string n in new List<string>(Names) { Endpoints })
            {
                using (Stream s = a.GetManifestResourceStream(n))
                {
                    if (s == null) continue;
                    MemoryStream ms = new MemoryStream();
                    s.CopyTo(ms);
                    p.Add(n, ms.ToArray());
                }
            }
            return p;
        }
    }

    internal sealed class Installer
    {
        public const string UninstallerName = "WolfTeamDirilis-Kaldir.exe";
        /// <summary>Files of the old zip package that started the game before the installer existed.</summary>
        private static readonly string[] Legacy = { "OYNA.bat", "WolfTeamLauncher.ps1", "WolfTeamLauncher.bat" };

        private readonly SetupOptions o;
        private readonly Payload payload;
        private readonly string selfExe;

        /// <summary>The ANSI code page the game will use on this PC (tests: another one).</summary>
        public Encoding Ansi = BootLayout.SystemAnsi();

        public Installer(SetupOptions options, Payload payload, string selfExe)
        {
            o = options;
            this.payload = payload;
            this.selfExe = selfExe;
        }

        public string RegKey
        {
            get { return o.RegistryKey ?? UninstallEntry.DefaultKey; }
        }

        public string DesktopDir
        {
            get { return o.DesktopDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }

        public string ProgramsDir
        {
            get { return o.StartMenuDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs); }
        }

        public string AppDataDir
        {
            get { return o.AppDataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WolfTeamDirilis"); }
        }

        /// <summary>The folder of an earlier install (registry entry + marker file), or null.</summary>
        public string ExistingInstall()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    string dir = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, InstallPaths.MarkerName))) return null;
                    return dir;
                }
            }
            catch (System.Security.SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }

        public static long FreeBytes(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; }
            catch (ArgumentException) { return -1; }
            catch (IOException) { return -1; }
            catch (UnauthorizedAccessException) { return -1; }
        }

        /// <summary>Checks everything that can be checked before a file is written. Throws InstallException.</summary>
        public string Validate(string requested)
        {
            string err = InstallPaths.Check(requested, SystemFolders.Current());
            if (err != null) throw new InstallException(err);
            // The game opens its protection module through ANSI APIs ("axProtect initialization Failed" otherwise).
            string ansi = InstallPaths.AnsiProblem(requested, Ansi);
            if (ansi != null) throw new InstallException(ansi);
            string dir = Path.GetFullPath(requested.Trim()).TrimEnd('\\');
            if (BootLayout.IsNetworkPath(dir)) throw new InstallException(BootLayout.NetworkText); // gate 1.4d #1
            if (InstallPaths.CrossesReparsePoint(dir))
                throw new InstallException("Klasör yolu bir bağlantı (junction ya da sembolik bağ) içeriyor. "
                                           + InstallPaths.DefaultDir(Path.GetPathRoot(dir)) + " gibi bir klasör seç.");
            if (InstallPaths.Classify(dir) == FolderKind.Foreign)
                throw new InstallException("Seçtiğin klasör boş değil ve içinde başka dosyalar var. Boş bir klasör seç ya da yeni bir klasör adı yaz, "
                                           + "örneğin " + Path.Combine(dir, InstallPaths.FolderName) + ".");
            long free = FreeBytes(dir);
            if (free >= 0)
            {
                string space = InstallPaths.SpaceProblem(free + ExistingGameBytes(dir));
                if (space != null) throw new InstallException(space);
            }
            List<string> running = ProcessCheck.RunningUnder(dir, Process.GetCurrentProcess().Id);
            if (running.Count > 0)
            {
                InstallException e = new InstallException("Bu klasördeki oyun ya da başlatıcı açık (" + string.Join(", ", running.ToArray())
                                                          + "). Kapatıp Tekrar dene'ye bas.");
                e.Running = true;
                throw e;
            }
            return dir;
        }

        private static long ExistingGameBytes(string dir)
        {
            long n = 0;
            foreach (string f in new string[] { "wolf.xfs", "sound.xfs" })
            {
                FileInfo fi = new FileInfo(Path.Combine(dir, f));
                if (fi.Exists) n += fi.Length;
            }
            return n;
        }

        /// <summary>Installs or repairs. progress(percent, Turkish step text) may be null. Holds the maintenance lock
        /// (install.lock, then update.lock, then a fresh process check; MaintenanceLock) through every write, so it never
        /// runs while a bootstrap starts or checks a launcher version, a launcher updates, or a launcher runs; the
        /// bootstrap adopts the launcher written here on its next start.</summary>
        public void Install(Action<int, string> progress)
        {
            if (progress == null) progress = delegate { };
            string dir = Validate(o.Dir);
            bool created = !Directory.Exists(dir);
            MaintenanceLock lck = null;
            try
            {
                progress(5, "Klasör hazırlanıyor");
                string noWrite = "Bu klasöre yazma iznin yok. " + InstallPaths.DefaultDir(Path.GetPathRoot(dir)) + " gibi bir klasör seç.";
                try
                {
                    // Nothing but the (empty) folder and the lock files is written before the maintenance lock is held.
                    Directory.CreateDirectory(dir);
                    Action<string> before = BeforeLock;
                    if (before != null) before(dir);
                    // A fresh folder's lock files are opened with FileShare.Delete, so a failed install can retire them
                    // while it still holds them (the cleanup below).
                    lck = MaintenanceLock.Take(dir, created);
                }
                catch (UnauthorizedAccessException) { throw new InstallException(noWrite); }
                catch (IOException e) { throw new InstallException("Klasör oluşturulamadı: " + e.Message); }
                // Under the lock: the folder counts as ours to remove on failure only if it still holds nothing but our
                // lock files (another setup may have installed into it between its creation and our lock; gate 1.4b #2).
                // Ownership is granted only by a successful check: an error while checking means "not ours".
                bool fresh = created;
                created = false;
                if (fresh)
                {
                    try { created = Remover.OnlyLockFiles(dir, new string[] { InstallLock.LockPath(dir), BootLayout.UpdateLockPath(dir) }); }
                    catch (IOException) { created = false; }
                    catch (UnauthorizedAccessException) { created = false; }
                }
                try
                {
                    // A unique probe created exclusively: an existing file is never overwritten or deleted.
                    string probe = Path.Combine(dir, "yazma-denemesi-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                    using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    File.Delete(probe);
                }
                catch (UnauthorizedAccessException) { throw new InstallException(noWrite); }
                catch (IOException e) { throw new InstallException("Klasöre yazılamadı: " + e.Message); }

                progress(20, "Başlatıcı kopyalanıyor");
                // Gate 1.4e: the shortcut's exe may still be a 1.2 launcher, which honours no lock. The maintenance lock
                // pinned it (FileShare.None: Windows cannot start it) before its final process check. Every file is
                // staged first (a temp name beside its target); then the pin is let go and the bootstrap replaces the exe
                // FIRST, with one atomic MoveFileEx. Windows refuses to replace an exe that is running, so if a 1.2 start
                // slipped in between, that replace fails and nothing else has changed (the temps are removed). Once the
                // lock-honouring bootstrap is in place the other files are moved in, the launcher file first. The exe is a
                // whole 1.2 launcher or the bootstrap at every moment; a setup killed right after the bootstrap move
                // leaves a bootstrap that asks for the setup again (running the setup again completes it).
                string uninst = Path.Combine(dir, UninstallerName);
                bool copySelf = !InstallPaths.Same(Path.GetFullPath(selfExe), uninst);
                byte[] marker = Encoding.UTF8.GetBytes(
                    "WolfTeam Diriliş kurulum klasörü. Bu dosyayı silme: kaldırma programı yalnız bu dosyanın olduğu klasörü siler.\r\n"
                    + "version=" + SetupInfo.Version + "\r\n" + BootLayout.SetupHashKey + Sha256Hex(selfExe) + "\r\n");
                List<KeyValuePair<string, byte[]>> files = new List<KeyValuePair<string, byte[]>>();
                files.Add(new KeyValuePair<string, byte[]>(Path.Combine(dir, Payload.Names[0]), payload.Get(Payload.Names[0])));
                if (copySelf) files.Add(new KeyValuePair<string, byte[]>(uninst, File.ReadAllBytes(selfExe)));
                files.Add(new KeyValuePair<string, byte[]>(Path.Combine(dir, InstallPaths.MarkerName), marker));
                files.Add(new KeyValuePair<string, byte[]>(Path.Combine(dir, Payload.Endpoints), payload.Get(Payload.Endpoints)));
                for (int i = 1; i < Payload.Names.Length; i++)
                {
                    if (Payload.Names[i] == SetupInfo.LauncherExe) continue;
                    files.Add(new KeyValuePair<string, byte[]>(Path.Combine(dir, Payload.Names[i]), payload.Get(Payload.Names[i])));
                }
                string exe = Path.Combine(dir, SetupInfo.LauncherExe);
                List<string> temps = new List<string>();
                try
                {
                    string bootTmp = Stage(exe, payload.Get(SetupInfo.LauncherExe));
                    temps.Add(bootTmp);
                    foreach (KeyValuePair<string, byte[]> f in files) temps.Add(Stage(f.Key, f.Value));
                    Step("staged"); // paused here: the exe is still pinned
                    lck.ReleasePin();
                    Step("unpinned");
                    try { Replace(bootTmp, exe); }
                    catch (IOException)
                    {
                        InstallException e = new InstallException("Başlatıcı (" + SetupInfo.LauncherExe + ") şu an açılıyor ya da açık; "
                                                                  + "hiçbir dosya değiştirilmedi. Kapatıp Tekrar dene'ye bas.");
                        e.Running = true;
                        throw e;
                    }
                    temps.Remove(bootTmp);
                    Step(SetupInfo.LauncherExe);
                    for (int i = 0; i < files.Count; i++)
                    {
                        Replace(temps[0], files[i].Key);
                        temps.RemoveAt(0);
                        Step(Path.GetFileName(files[i].Key) == Path.GetFileName(uninst) ? UninstallerName
                            : files[i].Key.Substring(dir.Length + 1));
                    }
                }
                finally
                {
                    foreach (string t in temps) DeleteFile(t);
                }
                DeleteFile(Path.Combine(dir, SetupInfo.LauncherExe) + ".old"); // a pre-release 1.4 setup's 1.2 copy
                // A repair keeps the bootstrap's launcher versions and their state: the launcher written here is tried by
                // the next start, with the confirmed version as the fallback. Only a lost state (neither state.txt nor
                // state.bak whole) is started over, after the new files are in place (no launcher runs now: Validate
                // refused a folder with a running one), so an interrupted repair never loses a working fallback.
                ResetLostLauncherState(dir);
                ForgiveBundledLauncher(dir);
                foreach (string l in Legacy) DeleteFile(Path.Combine(dir, l));

                progress(60, "Kısayollar oluşturuluyor");
                List<ShortcutSpec> all = ShortcutPlan.Build(dir, DesktopDir, ProgramsDir, true, true);
                List<ShortcutSpec> wanted = ShortcutPlan.Build(dir, DesktopDir, ProgramsDir, o.Desktop, o.StartMenu);
                foreach (ShortcutSpec s in all)
                {
                    bool want = wanted.Exists(delegate(ShortcutSpec w) { return w.LinkPath == s.LinkPath; });
                    if (want) Shortcuts.Create(s);
                    else DeleteFile(s.LinkPath); // a repair without that option removes our earlier shortcut
                }
                DeleteEmptyDir(Path.Combine(ProgramsDir, ShortcutPlan.StartMenuFolder));

                progress(80, "Programlar ve Özellikler'e ekleniyor");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    foreach (KeyValuePair<string, object> kv in UninstallEntry.Values(dir, SetupInfo.Version, SetupInfo.EstimatedSizeKb, DateTime.Now))
                    {
                        if (kv.Value is int) k.SetValue(kv.Key, kv.Value, RegistryValueKind.DWord);
                        else k.SetValue(kv.Key, kv.Value, RegistryValueKind.String);
                    }
                }
                progress(100, "Kurulum tamamlandı");
            }
            catch (Exception e)
            {
                // A failed fresh install leaves no half folder behind (gate 1.4b #2): what it wrote is removed while it
                // still holds the maintenance lock; after the lock only empty folders go. Without the lock (another setup
                // or a bootstrap had it) nothing but empty folders is touched: the folder may hold someone else's work.
                bool cleanup = created && (e is InstallException || e is IOException || e is UnauthorizedAccessException || e is COMException);
                string[] locks = { InstallLock.LockPath(dir), BootLayout.UpdateLockPath(dir) };
                List<string> ignored = new List<string>();
                if (cleanup && lck != null) Remover.RemoveCreatedInstallHeld(dir, locks);
                if (lck != null) { lck.Dispose(); lck = null; }
                Action<string> after = AfterFailedInstallUnlocked;
                if (cleanup && after != null) after(dir);
                if (cleanup) Remover.RemoveEmptyFolders(dir, locks, ignored);
                if (e is IOException || e is UnauthorizedAccessException || e is COMException)
                    throw new InstallException("Kurulum tamamlanamadı: " + e.Message);
                throw;
            }
            finally
            {
                if (lck != null) lck.Dispose();
            }
        }

        internal static string Sha256Hex(string path)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                StringBuilder b = new StringBuilder(64);
                foreach (byte x in sha.ComputeHash(f)) b.Append(x.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return b.ToString();
            }
        }

        /// <summary>Test hook: called after each step of the payload (the endpoints file, then every file name); a throwing hook
        /// plays a setup killed there.</summary>
        internal static Action<string> Fault = null;

        private static void Step(string name)
        {
            Action<string> f = Fault;
            if (f != null) f(name);
        }

        /// <summary>A repair gives the launcher it just installed a new, bounded trial even if the bootstrap rejected that
        /// same file before (a first start killed before its confirmation): its hash leaves the rejected list. Confirmed
        /// versions and other rejections stay (Codex 1.4 round 5 #2).</summary>
        internal static void ForgiveBundledLauncher(string dir)
        {
            BootState s = BootState.Read(BootLayout.StatePath(dir)) ?? BootState.Read(BootLayout.StateBackupPath(dir));
            if (s == null) return;
            string h;
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream f = new FileStream(BootLayout.StagedLauncher(dir), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StringBuilder b = new StringBuilder(64);
                foreach (byte x in sha.ComputeHash(f)) b.Append(x.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                h = b.ToString();
            }
            if (!s.Failed.Remove(h) && s.Trial != h) return;
            if (s.Trial == h) s.Attempts = 0;
            byte[] text = new UTF8Encoding(false).GetBytes(s.Text());
            WriteAtomic(BootLayout.StatePath(dir), text);
            WriteAtomic(BootLayout.StateBackupPath(dir), text);
        }

        internal static void ResetLostLauncherState(string dir)
        {
            string launcherDir = Path.Combine(dir, "launcher");
            if (BootState.UsableIn(dir)) return;
            foreach (string f in new string[] { "state.txt", "state.txt.part", "state.bak", "state.bak.part" })
                DeleteFile(Path.Combine(launcherDir, f));
            Remover.DeleteCreatedTree(Path.Combine(launcherDir, "versions"));
        }

        /// <summary>Removes the install folder (keeping Profiles when asked), our shortcuts, the Programs and Features
        /// entry and the launcher's settings in %APPDATA%. Returns what could not be removed (empty = all gone).</summary>
        public List<string> RemoveEverything(string dir)
        {
            string err = InstallPaths.Check(dir, SystemFolders.Current());
            if (err != null) throw new InstallException("Kaldırılamadı: " + err);
            if (BootLayout.IsNetworkPath(dir)) throw new InstallException("Kaldırılamadı: " + BootLayout.NetworkText);
            List<string> failed;
            bool filesGone;
            string[] locks = { InstallLock.LockPath(dir), BootLayout.UpdateLockPath(dir) };
            // The maintenance lock (both lock files, then a fresh process check) is held through every removal: the
            // folder, the shortcuts, the uninstall entry, the launcher's settings, then the lock files (deleted while held:
            // they vanish when the handles close) and the marker last. Afterwards only folders that are empty by then are
            // removed, so a setup that installs into the same folder right after keeps everything (gate 1.4b #2).
            Remover.CheckOurs(dir); // before a lock file is created: never write into a folder that is not ours
            using (MaintenanceLock m = MaintenanceLock.Take(dir, true))
            {
                // The pinned exe goes first: if a start slipped in after the pin was let go, it cannot be deleted
                // (Windows keeps a running exe) and nothing is removed (gate 1.4e).
                m.ReleasePin();
                string exe = Path.Combine(dir, SetupInfo.LauncherExe);
                if (!DeleteFile(exe))
                {
                    InstallException e = new InstallException("Başlatıcı (" + SetupInfo.LauncherExe + ") şu an açılıyor ya da açık; hiçbir şey silinmedi. "
                                                              + "Kapatıp Tekrar dene'ye bas.");
                    e.Running = true;
                    throw e;
                }
                failed = Remover.RemoveInstall(dir, o.KeepProfiles, locks);
                filesGone = failed.Count == 0;
                foreach (ShortcutSpec s in ShortcutPlan.Build(dir, DesktopDir, ProgramsDir, true, true))
                {
                    if (File.Exists(s.LinkPath) && !Shortcuts.PointsInto(s.LinkPath, dir)) continue; // someone else's link
                    if (!DeleteFile(s.LinkPath)) failed.Add(s.LinkPath);
                }
                DeleteEmptyDir(Path.Combine(ProgramsDir, ShortcutPlan.StartMenuFolder));
                try
                {
                    bool ours;
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                    {
                        string loc = k == null ? null : k.GetValue("InstallLocation") as string;
                        // The entry is removed only when it describes this folder (a newer install elsewhere keeps its own).
                        ours = k != null && !string.IsNullOrEmpty(loc) && InstallPaths.Same(loc, dir);
                    }
                    if (ours) Registry.CurrentUser.DeleteSubKeyTree(RegKey, false);
                }
                catch (UnauthorizedAccessException) { failed.Add("HKCU\\" + RegKey); }
                catch (System.Security.SecurityException) { failed.Add("HKCU\\" + RegKey); }
                RemoveLauncherSettings(dir, failed);
                if (filesGone) Remover.RetireLocksAndMarker(dir, locks, failed);
            }
            Action<string> unlocked = AfterUninstallUnlocked;
            if (unlocked != null) unlocked(dir);
            if (filesGone) Remover.RemoveEmptyFolders(dir, locks, failed);
            return failed;
        }

        /// <summary>Test hooks: before a setup takes its maintenance lock; right after a failed fresh install let it go.</summary>
        internal static Action<string> BeforeLock = null, AfterFailedInstallUnlocked = null;

        /// <summary>Test hook: runs right after the uninstall let its maintenance lock go.</summary>
        internal static Action<string> AfterUninstallUnlocked = null;

        /// <summary>The launcher's own files of THIS install in %APPDATA%\WolfTeamDirilis (names from launcher\csharp:
        /// HashCache, the verified list and the update/music markers keyed by the game folder, the 1.2/1.3 self-update
        /// files keyed by the exe path). Another install's files stay (approval gate 1.3 #4); the shared user.txt goes only
        /// when no other install's file is left; anything else there is left alone, then the folder goes if it is empty.</summary>
        private void RemoveLauncherSettings(string dir, List<string> failed)
        {
            if (!Directory.Exists(AppDataDir)) return;
            string g = InstallLock.KeyOf(dir), x = InstallLock.KeyOf(Path.Combine(dir, SetupInfo.LauncherExe));
            foreach (string name in new string[] { "hashcache-" + g + ".tsv", "hashcache-" + g + ".tsv.tmp", "update-incomplete-" + g + ".txt",
                                                   "music-fix-" + g + ".txt", "verified-" + g + ".json", "verified-" + g + ".json.tmp",
                                                   "seed-once-" + g + ".txt", "seed-once-" + g + ".txt.tmp",
                                                   "selfupdate-" + x + ".txt", "selfupdate-failed-" + x + ".txt", "selfupdate-trial-" + x + ".txt",
                                                   "selfupdate-trial-" + x + ".txt.part", "selfupdate-helper-" + x + ".exe" })
            {
                string f = Path.Combine(AppDataDir, name);
                if (File.Exists(f) && !DeleteFile(f)) failed.Add(f);
            }
            bool others = false;
            foreach (string pattern in new string[] { "hashcache-*", "verified-*", "update-incomplete-*", "music-fix-*", "seed-once-*", "selfupdate-*" })
            {
                others |= Directory.GetFiles(AppDataDir, pattern).Length > 0;
            }
            string user = Path.Combine(AppDataDir, "user.txt");
            if (!others && File.Exists(user) && !DeleteFile(user)) failed.Add(user);
            DeleteEmptyDir(AppDataDir);
        }

        private static void WriteAtomic(string path, byte[] data)
        {
            Replace(Stage(path, data), path);
        }

        /// <summary>{data} written and flushed to a temp file beside {path}; returns its name (Replace moves it in).</summary>
        private static string Stage(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".kurulum-tmp";
            try
            {
                using (FileStream f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    f.Write(data, 0, data.Length);
                    f.Flush(true);
                }
            }
            catch
            {
                DeleteFile(tmp); // a failed write leaves no temp file behind
                throw;
            }
            return tmp;
        }

        private static void CopyAtomic(string src, string path)
        {
            WriteAtomic(path, File.ReadAllBytes(src));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string target, int flags);

        /// <summary>One write-through MoveFileEx(REPLACE_EXISTING) of the flushed temp file: the target is the old or the
        /// new file at every moment (a setup stopped halfway never leaves the shortcut's exe missing).</summary>
        private static void Replace(string tmp, string path)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            if (!MoveFileEx(tmp, path, 0x1 | 0x8))
            {
                int err = Marshal.GetLastWin32Error();
                DeleteFile(tmp);
                throw new IOException(Path.GetFileName(path) + " yazılamadı (Windows hata kodu " + err + ").");
            }
        }

        internal static bool DeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void DeleteEmptyDir(string dir)
        {
            try { if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class Remover
    {
        /// <summary>Throws unless the folder is a WolfTeam Diriliş install reached without a junction.</summary>
        public static void CheckOurs(string dir)
        {
            if (InstallPaths.CrossesReparsePoint(dir))
                throw new InstallException("Klasör yolu bir bağlantı (junction) içeriyor; hiçbir şey silinmedi.");
            if (!File.Exists(Path.Combine(dir, InstallPaths.MarkerName)))
                throw new InstallException("Bu klasör WolfTeam Diriliş kurulumuyla oluşturulmamış; hiçbir şey silinmedi.");
        }

        /// <summary>Deletes an install folder that has our marker file. Junctions and symbolic links inside are removed
        /// as links; what they point to is never touched. With keepProfiles the Profiles folder stays (and so does the
        /// install folder that holds it). The marker goes last, so an interrupted uninstall can run again.</summary>
        public static List<string> RemoveInstall(string dir, bool keepProfiles)
        {
            List<string> failed = RemoveInstall(dir, keepProfiles, new string[0]);
            return failed.Count == 0 ? FinishRemove(dir, keepProfiles, new string[0]) : failed;
        }

        /// <summary>Empties the folder except {keep} (the held lock files), the marker and the Profiles folder when
        /// kept; FinishRemove takes the rest.</summary>
        public static List<string> RemoveInstall(string dir, bool keepProfiles, string[] keep)
        {
            CheckOurs(dir);
            List<string> failed = new List<string>();
            foreach (string e in Directory.GetFileSystemEntries(dir))
            {
                string name = Path.GetFileName(e);
                if (string.Equals(name, InstallPaths.MarkerName, StringComparison.OrdinalIgnoreCase)) continue;
                if (keepProfiles && string.Equals(name, "Profiles", StringComparison.OrdinalIgnoreCase)) continue;
                DeleteEntry(e, failed, keep);
            }
            return failed;
        }

        /// <summary>After RemoveInstall: the kept lock files and their folder, then the marker (last, so an interrupted
        /// uninstall can run again) and the install folder when it is empty.</summary>
        public static List<string> FinishRemove(string dir, bool keepProfiles, string[] keep)
        {
            List<string> failed = new List<string>();
            foreach (string keepFile in keep ?? new string[0])
            {
                if (File.Exists(keepFile)) DeleteEntry(keepFile, failed, null);
            }
            foreach (string keepFile in keep ?? new string[0])
            {
                string parent = Path.GetDirectoryName(keepFile);
                try { if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent); }
                catch (IOException) { failed.Add(parent); }
                catch (UnauthorizedAccessException) { failed.Add(parent); }
            }
            if (failed.Count > 0) return failed;
            DeleteEntry(Path.Combine(dir, InstallPaths.MarkerName), failed, null);
            try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }
            catch (IOException) { failed.Add(dir); }
            catch (UnauthorizedAccessException) { failed.Add(dir); }
            return failed;
        }

        /// <summary>Inside the uninstall's maintenance lock, once everything else is gone: the held lock files are
        /// deleted (FileShare.Delete handles: they vanish when the handles close), then the marker, last.</summary>
        public static void RetireLocksAndMarker(string dir, string[] locks, List<string> failed)
        {
            bool ok = true;
            foreach (string l in locks)
            {
                try { if (File.Exists(l)) File.Delete(l); }
                catch (IOException) { failed.Add(l); ok = false; }
                catch (UnauthorizedAccessException) { failed.Add(l); ok = false; }
            }
            if (ok) DeleteEntry(Path.Combine(dir, InstallPaths.MarkerName), failed, null);
        }

        /// <summary>After the lock is released: the lock files' folder and the install folder, only when they are empty
        /// (a setup that installed into the folder meanwhile keeps all of its files).</summary>
        public static void RemoveEmptyFolders(string dir, string[] locks, List<string> failed)
        {
            List<string> folders = new List<string>();
            foreach (string l in locks)
            {
                string parent = Path.GetDirectoryName(l);
                if (!folders.Contains(parent)) folders.Add(parent);
            }
            folders.Add(dir);
            foreach (string f in folders)
            {
                try { if (Directory.Exists(f) && Directory.GetFileSystemEntries(f).Length == 0) Directory.Delete(f); }
                catch (IOException) { failed.Add(f); }
                catch (UnauthorizedAccessException) { failed.Add(f); }
            }
        }

        /// <summary>True when {dir} holds nothing but {locks} (and their folder): a folder this setup just created and
        /// that nobody wrote into before it took the lock.</summary>
        public static bool OnlyLockFiles(string dir, string[] locks)
        {
            foreach (string e in Directory.GetFileSystemEntries(dir))
            {
                if (Kept(e, locks)) continue;
                if (Directory.Exists(e) && HoldsKept(e, locks))
                {
                    if (!OnlyLockFiles(e, locks)) return false;
                    continue;
                }
                return false;
            }
            return true;
        }

        /// <summary>A fresh install that failed, inside its maintenance lock (lock files held with FileShare.Delete):
        /// everything in the folder it created goes, the held lock files are deleted last (gone when the handles close).</summary>
        public static void RemoveCreatedInstallHeld(string dir, string[] locks)
        {
            List<string> ignored = new List<string>();
            if (!Directory.Exists(dir)) return;
            foreach (string e in Directory.GetFileSystemEntries(dir)) DeleteEntry(e, ignored, locks);
            foreach (string l in locks)
            {
                try { if (File.Exists(l)) File.Delete(l); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Only for a folder this installer created a moment ago and failed to fill.</summary>
        internal static void DeleteCreatedTree(string dir)
        {
            List<string> ignored = new List<string>();
            if (Directory.Exists(dir)) DeleteEntry(dir, ignored, null);
        }

        private static bool Kept(string path, string[] keep)
        {
            if (keep == null) return false;
            foreach (string k in keep)
            {
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(k), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool HoldsKept(string dirPath, string[] keep)
        {
            if (keep == null) return false;
            foreach (string k in keep)
            {
                if (InstallPaths.Under(k, dirPath)) return true;
            }
            return false;
        }

        private static void DeleteEntry(string path, List<string> failed, string[] keep)
        {
            if (Kept(path, keep)) return;
            try
            {
                FileAttributes a = File.GetAttributes(path);
                bool isDir = (a & FileAttributes.Directory) != 0;
                if ((a & FileAttributes.ReparsePoint) != 0)
                {
                    // Removes the link itself (a junction to C:\ must not take C:\ with it).
                    if (isDir) Directory.Delete(path, false); else File.Delete(path);
                    return;
                }
                if (isDir)
                {
                    foreach (string e in Directory.GetFileSystemEntries(path)) DeleteEntry(e, failed, keep);
                    if (HoldsKept(path, keep)) return; // a kept file's folder stays for now
                    Directory.Delete(path, false);
                    return;
                }
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
            catch (IOException) { failed.Add(path); }
            catch (UnauthorizedAccessException) { failed.Add(path); }
        }
    }

    internal static class ProcessCheck
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private const int QueryLimitedInformation = 0x1000;

        public static List<string> RunningUnder(string dir)
        {
            return RunningUnder(dir, -1);
        }

        /// <summary>File names of running processes whose exe lies in <paramref name="dir"/> (the game, the launcher, the
        /// anti-cheat helpers). Works across 32/64-bit, without admin rights for the user's own processes.</summary>
        /// <summary>Test hook in place of the native query (QueryImage): the image path of a process id, null = cannot
        /// be inspected.</summary>
        internal static Func<int, string> ImagePathOverride = null;

        /// <summary>Process names of the bootstrap, the launcher, the uninstaller and the game.</summary>
        private static readonly string[] OurNames = { "WolfTeamDirilis", "WolfTeamLauncher", "WolfTeamDirilis-Kaldir", "Wolfteam", "Wolfteam.bin" };

        private static bool Ours(Process p)
        {
            string n = ProcessNameOf(p);
            foreach (string o in OurNames) if (string.Equals(n, o, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ProcessNameOf(Process p)
        {
            try { return p.ProcessName; }
            catch (InvalidOperationException) { return ""; }
        }

        private static void Add(List<string> names, string n)
        {
            if (!names.Contains(n)) names.Add(n);
        }

        public static List<string> RunningUnder(string dir, int excludePid)
        {
            List<string> names = new List<string>();
            Func<int, string> query = ImagePathOverride ?? new Func<int, string>(QueryImage);
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == excludePid || p.Id == 0 || p.Id == 4) continue;
                    string path = query(p.Id);
                    if (path == null)
                    {
                        // Not inspectable (open or query failed, e.g. another user's process): one with our name counts
                        // as running (fail closed, gate 1.4d #1); anything else is not ours.
                        if (Ours(p)) Add(names, ProcessNameOf(p) + " (denetlenemedi)");
                        continue;
                    }
                    if (InstallPaths.Under(path, dir) && !InstallPaths.Same(path, dir)) Add(names, Path.GetFileName(path));
                }
            }
            return names;
        }

        /// <summary>The full image path of a process; null when it cannot be opened or queried.</summary>
        internal static string QueryImage(int pid)
        {
            IntPtr h = OpenProcess(QueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                return sb.ToString(0, size);
            }
            finally { CloseHandle(h); }
        }
    }

    /// <summary>.lnk files through the shell's own ShellLink COM object (no WSH dependency).</summary>
    internal static class Shortcuts
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public static void Create(ShortcutSpec s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s.LinkPath));
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(s.Target);
                link.SetWorkingDirectory(s.WorkingDir);
                link.SetIconLocation(s.IconPath, 0);
                link.SetDescription(s.Description);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(s.LinkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        public static string ReadTarget(string lnk)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
                StringBuilder sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                return sb.ToString();
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        /// <summary>True when the shortcut at <paramref name="lnk"/> points into <paramref name="dir"/> (so it is ours
        /// to delete); false for a link that cannot be read or points elsewhere.</summary>
        public static bool PointsInto(string lnk, string dir)
        {
            try { return InstallPaths.Under(ReadTarget(lnk), dir); }
            catch (COMException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidCastException) { return false; }
        }
    }

    /// <summary>What a setup or uninstall holds through all its writes (gate 1.4b #2). Order, always the same:
    ///   1. launcher\install.lock (the bootstrap's): no bootstrap starts, adopts or checks a launcher version;
    ///   2. launcher\update.lock (the launcher's update transaction): an update in progress finishes first;
    ///   3. then the processes running from the folder are checked again (a launcher that started after the first
    ///      check and still runs, its update finished or not): any one = refused, both locks released.
    /// No cycle: the bootstrap holds only install.lock, a launcher holds only update.lock and never waits for install.lock
    /// while holding it (it releases update.lock before it restarts through the bootstrap).</summary>
    internal sealed class MaintenanceLock : IDisposable
    {
        private FileStream install, update;

        /// <summary>How long the second process check waits for a process that is just ending (a start that steps aside).</summary>
        internal static int SettleMs = 3000;

        /// <summary>Test hook: runs after both locks are held, before the second process check.</summary>
        internal static Action<string> AfterLocks = null;

        public static MaintenanceLock Take(string dir, bool removal)
        {
            MaintenanceLock m = new MaintenanceLock();
            try
            {
                m.install = removal ? InstallLock.TakeForRemoval(dir) : InstallLock.Take(dir);
                m.update = InstallLock.TakeFile(BootLayout.UpdateLockPath(dir), removal ? FileShare.Delete : FileShare.None);
                Action<string> hook = AfterLocks;
                if (hook != null) hook(dir);
                // The shortcut's exe is pinned before the final process check (gate 1.4e): with FileShare.None Windows
                // cannot start it (a 1.2 launcher there honours no lock), until ReleasePin. A sharing violation here means
                // it is running.
                string exe = Path.Combine(dir, SetupInfo.LauncherExe);
                if (File.Exists(exe))
                {
                    try { m.pin = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None); }
                    catch (FileNotFoundException) { }
                    catch (IOException e)
                    {
                        if (!BootLayout.IsSharingViolation(e)) throw new InstallException(SetupInfo.LauncherExe + " açılamadı: " + e.Message);
                        InstallException busy = new InstallException(InstallLock.BusyText);
                        busy.Running = true;
                        throw busy;
                    }
                }
                int self = Process.GetCurrentProcess().Id;
                DateTime end = DateTime.UtcNow.AddMilliseconds(SettleMs);
                List<string> running;
                while ((running = ProcessCheck.RunningUnder(dir, self)).Count > 0 && DateTime.UtcNow < end) System.Threading.Thread.Sleep(200);
                if (running.Count > 0)
                {
                    // Only the bootstrap (a start that waits for the install lock, e.g. the restart after a launcher
                    // update): it does nothing while we hold the lock, but its exe cannot be replaced or removed now.
                    bool onlyBootstrap = running.TrueForAll(delegate(string n) { return string.Equals(n, SetupInfo.LauncherExe, StringComparison.OrdinalIgnoreCase); });
                    InstallException e = new InstallException(onlyBootstrap ? InstallLock.BusyText
                        : "Bu klasördeki oyun ya da başlatıcı açık (" + string.Join(", ", running.ToArray()) + "). Kapatıp Tekrar dene'ye bas.");
                    e.Running = true;
                    throw e;
                }
                return m;
            }
            catch
            {
                m.Dispose();
                throw;
            }
        }

        private FileStream pin;

        /// <summary>Lets the shortcut's exe go, right before it is replaced or deleted.</summary>
        public void ReleasePin()
        {
            if (pin != null) { pin.Dispose(); pin = null; }
        }

        public void Dispose()
        {
            ReleasePin();
            if (update != null) { update.Dispose(); update = null; }
            if (install != null) { install.Dispose(); install = null; }
        }
    }

    /// <summary>The install lock: launcher\install.lock opened exclusively, the same file the bootstrap holds while it
    /// starts and checks a launcher version (launcher\bootstrap\Bootstrap.cs). A file in the install folder works for
    /// every Windows user and session of that folder, and Windows releases it when its holder dies.</summary>
    internal static class InstallLock
    {
        /// <summary>A bootstrap holds it at most about 2.5 minutes (a new launcher that never confirms its start).</summary>
        internal static int TimeoutMs = 180000;

        public const string BusyText = "Başlatıcı şu an açılıyor ya da yeni sürümünü deniyor. Birkaç dakika sonra tekrar dene.";

        /// <summary>Updater.GameDirKey of a path: the first 12 hex of SHA-256 of its lower-case full path.</summary>
        public static string KeyOf(string path)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path.Trim()).TrimEnd('\\').ToLowerInvariant()));
                StringBuilder b = new StringBuilder();
                foreach (byte x in h) b.Append(x.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return b.ToString().Substring(0, 12);
            }
        }

        public static string LockPath(string dir)
        {
            return Path.Combine(Path.Combine(Path.GetFullPath(dir.Trim()).TrimEnd('\\'), "launcher"), "install.lock");
        }

        /// <summary>For an install: the lock, waiting while a bootstrap holds it.</summary>
        public static FileStream Take(string dir)
        {
            return Take(dir, FileShare.None);
        }

        /// <summary>For an uninstall: also lets the lock file itself be deleted at the end.</summary>
        public static FileStream TakeForRemoval(string dir)
        {
            return Take(dir, FileShare.Delete);
        }

        private static FileStream Take(string dir, FileShare share)
        {
            return TakeFile(LockPath(dir), share);
        }

        /// <summary>A lock file opened with {share}, waiting up to TimeoutMs while someone else holds it.</summary>
        public static FileStream TakeFile(string path, FileShare share)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DateTime end = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share); }
                catch (UnauthorizedAccessException) { throw; }
                catch (IOException e)
                {
                    // Only a sharing violation means someone holds it; any other error is reported at once (gate 1.4d #3).
                    if (!BootLayout.IsSharingViolation(e)) throw new InstallException("Kilit dosyası açılamadı (" + Path.GetFileName(path) + "): " + e.Message);
                    if (DateTime.UtcNow >= end) throw new InstallException(BusyText);
                    System.Threading.Thread.Sleep(250);
                }
            }
        }
    }
}
