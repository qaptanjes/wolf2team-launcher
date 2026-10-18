// Client updater: manifest.json {version, files:[{path,size,sha256[,blocks]}]} under UpdateBaseUrl.
// Only files inside the game folder are ever written. A download goes to "<file>.part" (resumed with an HTTP Range
// request when a partial file exists), is checked against the manifest SHA-256 and then replaces the original.
// A changed file with a block list ("blocks": {size, sha256[]}, e.g. wolf.xfs) is patched in place instead: only the
// blocks whose SHA-256 differs are fetched with Range requests (UpdaterDelta.cs), then the whole file is checked.
// The bootstrap WolfTeamDirilis.exe is never written (only the setup replaces it; BootLayout.cs). The launcher itself
// (launcher\WolfTeamLauncher.exe) is an ordinary file here: it never runs from that path; the bootstrap runs copies.
// Every file is fetched with "?h=<sha256 prefix>" (manifest and news with "?t=<time>"), so a cache between the server
// and the player (Cloudflare) never serves an older copy of a changed file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WolfTeamDirilis
{
    internal sealed class ManifestEntry
    {
        public string Path;      // relative, backslash separated, validated
        public long Size;
        public string Sha256;    // 64 lowercase hex
        public string FullPath;  // resolved inside the game folder
        public bool IsBootstrap; // WolfTeamDirilis.exe: never updated by the launcher (only by the setup)
        public bool IsLauncher;  // launcher\WolfTeamLauncher.exe: a new one is handed to the bootstrap after the update
        public bool IsSeed;      // from "seeds": downloaded only when missing (replaced only by ConfigRepair's rules)
        /// <summary>Seeds only, optional "replaceOnce": the player's copy is replaced once per new id (ConfigRepair.cs).</summary>
        public string ReplaceOnce;
        /// <summary>Set by Plan: why an existing seed is replaced (None = only downloaded when missing).</summary>
        internal SeedReplace Replace;
        /// <summary>Optional "blocks": block size in bytes and one SHA-256 per block (null when the entry has none).</summary>
        public int BlockSize;
        public string[] BlockHashes;
        /// <summary>Block hashes of the local copy computed by Plan (with the size and mtime they belong to), so
        /// Apply does not read the file again when it did not change in between.</summary>
        internal string[] LocalBlocks;
        internal long LocalStampLength;
        internal long LocalStampTicks;
    }

    internal enum SeedReplace { None, Once, BrokenConfig }

    internal sealed class Manifest
    {
        public string Version;
        public List<ManifestEntry> Files = new List<ManifestEntry>();
        /// <summary>The JSON text it was parsed from (stored as the "last verified" list after a full check).</summary>
        public string Raw;
        /// <summary>Defaults the game rewrites per player (config.xfs): fetched when missing; an existing copy is replaced
        /// only by ConfigRepair.cs's rules (broken config.xfs, a new "replaceOnce" id).</summary>
        public List<ManifestEntry> Seeds = new List<ManifestEntry>();
    }

    internal sealed class UpdateProgress
    {
        public bool Checking;
        public string File;
        public int Index;
        public int Count;
        public long Done;
        public long Total;
        /// <summary>Bytes of the current file so far, and its size.</summary>
        public long FileDone;
        public long FileSize;
        public double BytesPerSecond;

        public int Percent
        {
            get { return Total <= 0 ? 0 : (int)Math.Min(100, Done * 100 / Total); }
        }
    }

    /// <summary>A failure with a Turkish, one-line, URL-free message for the message strip.</summary>
    internal sealed class UpdateException : Exception
    {
        /// <summary>True only when the manifest could not be fetched or parsed AND no earlier update is unfinished:
        /// the files on disk are then a complete older version and the game may still be started.</summary>
        public bool ManifestUnavailable;

        /// <summary>The list could not be fetched at all: network error, timeout or HTTP 5xx. A 4xx (wrong address) or
        /// an invalid list is not "unreachable" and never falls back to the last verified list.</summary>
        public bool Unreachable;

        public UpdateException(string message) : base(message) { }
    }

    internal sealed partial class Updater
    {
        public const int MaxManifestBytes = 8 * 1024 * 1024;
        private static readonly Regex HexSha = new Regex(@"\A[0-9a-fA-F]{64}\z");
        private static readonly Regex OnceId = new Regex(@"\A[A-Za-z0-9._-]{1,64}\z");

        private readonly LauncherConfig cfg;
        private readonly string bootstrapExe;
        private readonly HashCache cache;

        /// <summary>Called from the worker thread; the UI marshals it.</summary>
        public Action<UpdateProgress> Progress;

        /// <summary>Tries per file on network errors (each try resumes the .part file); the wait grows per try.</summary>
        public const int MaxAttempts = 5;
        internal int RetryDelayMs = 1000;

        public Updater(LauncherConfig cfg)
        {
            this.cfg = cfg;
            bootstrapExe = BootLayout.BootstrapPath(cfg.GameDir);
            cache = new HashCache(cfg.GameDir);
            GameRunning = delegate { return MusicFix.IsRunning(cfg.GameExePath); };
        }

        /// <summary>True while the game runs from this folder: an existing seed is then never replaced (the game writes
        /// config.xfs back on exit). Tests replace it.</summary>
        internal Func<bool> GameRunning;

        /// <summary>File.Replace(replacement, target, backup) for ReplaceSeed; tests inject ReplaceFile's partial
        /// failures (the old file left only under the backup name).</summary>
        internal Action<string, string, string> ReplaceFile = delegate(string part, string target, string backup)
        {
            File.Replace(part, target, backup, true);
        };

        /// <summary>Set when Apply replaced the player's config.xfs (ConfigRepair.cs); MainForm shows one line.</summary>
        public bool ConfigReplaced;

        public const string WaitingText = "Başka bir başlatıcı penceresi oyunu güncelliyor; bitmesi bekleniyor…";

        /// <summary>Runs {body} holding launcher\update.lock (FileShare.None: every launcher process and Windows user of the
        /// install, released when the holder dies), so no two updates of one install plan, download, replace or verify at
        /// the same time (gate 1.4 #3). Waits while another launcher holds it ({waiting} is called once). The bootstrap's
        /// start confirmation never waits for it (MainForm's timer ticks run on the UI thread).</summary>
        public static void WithUpdateLock(string gameDir, Action waiting, Action body)
        {
            FileStream held = null;
            bool told = false;
            string path = BootLayout.UpdateLockPath(gameDir);
            DateTime end = DateTime.UtcNow.AddMilliseconds(LockWaitMs);
            while (held == null)
            {
                try
                {
                    held = OpenLock(gameDir, path);
                }
                catch (UnauthorizedAccessException)
                {
                    // Fail closed: never update without the lock (gate 1.4 re-review #1).
                    throw new UpdateException("Güncelleme kilidi açılamadı (" + BootLayout.UpdateLockName + "). Oyun klasörüne yazma iznini kontrol et.");
                }
                catch (IOException e)
                {
                    if (!IsContention(e))
                        throw new UpdateException("Güncelleme kilidi açılamadı (" + BootLayout.UpdateLockName + "): " + e.Message);
                    if (!told && waiting != null) { waiting(); told = true; }
                    if (DateTime.UtcNow >= end) throw new UpdateException(LockTimeoutText); // never past the held lock
                    System.Threading.Thread.Sleep(LockRetryMs);
                }
            }
            try { body(); }
            finally { held.Dispose(); }
        }

        /// <summary>The exclusive handle on launcher\update.lock, exactly as every holder opens it (updates and the game
        /// start): read/write, or read-only when the file is read-only (FileShare.None is what locks).</summary>
        private static FileStream OpenLock(string gameDir, string path)
        {
            Directory.CreateDirectory(BootLayout.LauncherDir(gameDir));
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (UnauthorizedAccessException)
            {
                // e.g. a read-only lock file: the share mode (None) is what locks, read access is enough.
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        }

        /// <summary>Takes launcher\update.lock without waiting, for the game start (MainForm.LaunchGame), opened the same
        /// way WithUpdateLock opens it: false when another launcher holds it (it is updating, maybe replacing config.xfs).
        /// Only when the lock cannot be opened at all (no access even read-only, or another non-sharing error) does it
        /// return true with held = null: WithUpdateLock fails on exactly those errors, so no update can run then either
        /// (Codex gate 1.6 round 2 #1).</summary>
        public static bool TryHoldUpdateLock(string gameDir, out FileStream held)
        {
            held = null;
            try
            {
                held = OpenLock(gameDir, BootLayout.UpdateLockPath(gameDir));
                return true;
            }
            catch (IOException e) { return !IsContention(e); }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal static int LockRetryMs = 500;

        /// <summary>The longest wait for another launcher's update (gate 1.4b #3); then Tekrar dene. A whole first
        /// download fits in it on a slow line; a hung or suspended holder does not keep this launcher waiting forever.</summary>
        internal static int LockWaitMs = 20 * 60 * 1000;

        public const string LockTimeoutText = "Başka bir başlatıcı penceresi oyunu hâlâ güncelliyor. O pencereyi kapatıp Tekrar dene'ye bas "
            + "(açık bir pencere görmüyorsan bilgisayarı yeniden başlat).";

        /// <summary>Only a sharing or lock violation means another launcher holds the lock; any other error (disk, drive
        /// gone) is reported instead of waiting forever.</summary>
        public static bool IsContention(IOException e)
        {
            return BootLayout.IsSharingViolation(e);
        }

        public Manifest FetchManifest()
        {
            if (cfg.UpdateBaseUrl == null) throw ManifestProblem("Güncelleme adresi ayarlı değil.");
            if (!SecureSource(cfg.UpdateBaseUrl))
            {
                // Security audit F2: a list from a plain HTTP address could come from anyone on the way and name any
                // file hashes. Updates come only over HTTPS (or from this PC, for tests).
                UpdateException h = ManifestProblem(InsecureText);
                h.Unreachable = true; // the files on disk are still the last verified version
                throw h;
            }
            if (!Tls.Available)
            {
                // Never a silent downgrade: the list's source must be authentic, so without TLS 1.2 there is no update.
                UpdateException t = ManifestProblem(Tls.MissingText);
                t.Unreachable = true;
                throw t;
            }
            byte[] data;
            try
            {
                data = Net.DownloadSmall(Net.Fresh(cfg.UpdateBaseUrl + "manifest.json"), MaxManifestBytes, 15000);
            }
            catch (Exception e)
            {
                if (!(e is WebException || e is IOException || e is InvalidDataException)) throw;
                UpdateException u = ManifestProblem(Tls.IsTlsFailure(e) ? TlsFailureText
                    : "Güncelleme listesi alınamadı. İnternet bağlantını kontrol edip Tekrar dene'ye bas.");
                u.Unreachable = IsUnreachable(e);
                throw u;
            }
            try
            {
                return ParseManifest(Encoding.UTF8.GetString(data), cfg.GameDir, bootstrapExe);
            }
            catch (UpdateException e)
            {
                throw ManifestProblem(e.Message);
            }
        }

        public const string InsecureText = "Güncelleme adresi güvenli değil (HTTPS değil), güncelleme yapılmadı. Kurulum "
            + "programını wolf2team.com'dan indirip aynı klasöre yeniden kur.";

        /// <summary>HTTPS, or plain HTTP to this PC (loopback: the tests' local server).</summary>
        internal static bool SecureSource(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            return u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;
        }

        public const string TlsFailureText = "Güncelleme sunucusuyla güvenli bağlantı kurulamadı. Bilgisayarın tarih ve saatini "
            + "kontrol et, Windows Update'i çalıştır ve Tekrar dene'ye bas.";

        internal static bool IsUnreachable(Exception e)
        {
            WebException we = e as WebException;
            if (we != null)
            {
                HttpWebResponse r = we.Response as HttpWebResponse;
                if (we.Status != WebExceptionStatus.ProtocolError || r == null) return true; // no answer at all
                int code = (int)r.StatusCode;
                r.Close();
                return code >= 500;
            }
            return e is IOException && !(e is InvalidDataException); // connection dropped while reading
        }

        // ------------------------------------------------------------ last verified list (offline play)

        private static string VerifiedPath(string gameDir)
        {
            return System.IO.Path.Combine(UserSettings.Dir, "verified-" + GameDirKey(gameDir) + ".json");
        }

        /// <summary>Stores the list after every file matched it (call only after a successful Apply).</summary>
        public void SaveVerified(Manifest m)
        {
            if (m == null || m.Raw == null) return;
            string path = VerifiedPath(cfg.GameDir), tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(UserSettings.Dir);
                File.WriteAllText(tmp, m.Raw, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null, true); else File.Move(tmp, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>True when a last verified list exists, no update is unfinished and every game file (seeds aside:
        /// they are the player's) still has the size and SHA-256 of that list.</summary>
        public bool VerifyLastVerified()
        {
            if (IsIncomplete(cfg.GameDir)) return false;
            string text;
            try
            {
                string path = VerifiedPath(cfg.GameDir);
                if (!File.Exists(path)) return false;
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            Manifest m;
            try { m = ParseManifest(text, cfg.GameDir, bootstrapExe); }
            catch (UpdateException) { return false; }
            if (m.Files.Count == 0) return false;
            foreach (ManifestEntry e in Plan(m))
            {
                if (!e.IsSeed) return false;
            }
            return true;
        }

        /// <summary>A manifest failure is only harmless when no earlier update was interrupted; otherwise the folder
        /// may hold a mix of old and new files and the game must not start.</summary>
        private UpdateException ManifestProblem(string message)
        {
            if (IsIncomplete(cfg.GameDir))
                return new UpdateException("Önceki güncelleme yarım kaldı ve güncelleme listesi alınamadı. "
                    + "Oyun bu hâliyle açılamaz; bağlantını kontrol edip Tekrar dene'ye bas.");
            UpdateException u = new UpdateException(message);
            u.ManifestUnavailable = true;
            return u;
        }

        // ------------------------------------------------------------ "update incomplete" marker
        // Written before the first file of an update is replaced and removed only after every planned file was
        // downloaded, hash-checked and put in place (or a later Plan finds nothing left to do). It lives in %APPDATA%
        // (one per game folder) so it never ends up in a client package or in the manifest.

        private static string MarkerPath(string gameDir)
        {
            return System.IO.Path.Combine(UserSettings.Dir, "update-incomplete-" + GameDirKey(gameDir) + ".txt");
        }

        public static string GameDirKey(string gameDir)
        {
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(gameDir).ToLowerInvariant()))).Substring(0, 12);
        }

        /// <summary>True when an earlier update was interrupted (or the marker cannot be checked: fail closed).</summary>
        public static bool IsIncomplete(string gameDir)
        {
            try { return File.Exists(MarkerPath(gameDir)); }
            catch (Exception e)
            {
                if (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException) return true;
                throw;
            }
        }

        private void MarkIncomplete()
        {
            try
            {
                Directory.CreateDirectory(UserSettings.Dir);
                File.WriteAllText(MarkerPath(cfg.GameDir), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n",
                    new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                // Without the marker an interruption could not be detected later, so nothing is replaced.
                if (e is IOException || e is UnauthorizedAccessException)
                    throw new UpdateException("Güncelleme başlatılamadı: ayar klasörüne yazılamıyor.");
                throw;
            }
        }

        private void ClearIncomplete()
        {
            string marker = MarkerPath(cfg.GameDir);
            try { if (File.Exists(marker)) File.Delete(marker); }
            catch (Exception e)
            {
                if (e is IOException || e is UnauthorizedAccessException)
                    throw new UpdateException("Güncelleme bitti ama kaydı silinemedi. Başlatıcıyı kapatıp tekrar aç.");
                throw;
            }
        }

        /// <summary>Strict parse: a single bad entry (absolute path, "..", bad hash) rejects the whole manifest.</summary>
        public static Manifest ParseManifest(string json, string gameDir, string bootstrapExe)
        {
            const string bad = "Güncelleme listesi geçersiz. Yöneticiye haber ver.";
            Dictionary<string, object> root;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = MaxManifestBytes * 2;
                root = js.DeserializeObject(json.TrimStart('﻿')) as Dictionary<string, object>;
            }
            catch (ArgumentException) { throw new UpdateException(bad); }
            catch (InvalidOperationException) { throw new UpdateException(bad); }
            if (root == null) throw new UpdateException(bad);
            Manifest m = new Manifest();
            m.Raw = json;
            object v;
            m.Version = root.TryGetValue("version", out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
            object[] files = root.TryGetValue("files", out v) ? v as object[] : null;
            if (files == null) throw new UpdateException(bad);
            object[] seeds = new object[0];
            if (root.TryGetValue("seeds", out v) && v != null)
            {
                seeds = v as object[];
                if (seeds == null) throw new UpdateException(bad);
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string self = bootstrapExe == null ? null : System.IO.Path.GetFullPath(bootstrapExe);
            ParseEntries(files, false, m.Files, gameDir, self, seen);
            ParseEntries(seeds, true, m.Seeds, gameDir, self, seen);
            return m;
        }

        private static void ParseEntries(object[] list, bool seed, List<ManifestEntry> into, string gameDir, string self,
            HashSet<string> seen)
        {
            const string bad = "Güncelleme listesi geçersiz. Yöneticiye haber ver.";
            foreach (object o in list)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) throw new UpdateException(bad);
                object p, s, h;
                if (!d.TryGetValue("path", out p) || !d.TryGetValue("size", out s) || !d.TryGetValue("sha256", out h))
                    throw new UpdateException(bad);
                ManifestEntry e = new ManifestEntry();
                e.Path = p as string;
                string full = ResolveInside(gameDir, e.Path);
                if (full == null) throw new UpdateException(bad);
                e.Path = e.Path.Replace('/', '\\');
                e.FullPath = full;
                try { e.Size = Convert.ToInt64(s, CultureInfo.InvariantCulture); }
                catch (FormatException) { throw new UpdateException(bad); }
                catch (InvalidCastException) { throw new UpdateException(bad); }
                catch (OverflowException) { throw new UpdateException(bad); }
                e.Sha256 = h as string;
                if (e.Size < 0 || e.Sha256 == null || !HexSha.IsMatch(e.Sha256)) throw new UpdateException(bad);
                e.Sha256 = e.Sha256.ToLowerInvariant();
                // Names ending in the updater's own staging suffixes would collide with .part/.new/.old files.
                string lower = full.ToLowerInvariant();
                if (lower.EndsWith(".part") || lower.EndsWith(".new") || lower.EndsWith(".old")) throw new UpdateException(bad);
                if (!seen.Add(full)) throw new UpdateException(bad);
                e.IsBootstrap = self != null && string.Equals(full, self, StringComparison.OrdinalIgnoreCase);
                e.IsLauncher = string.Equals(full, BootLayout.StagedLauncher(gameDir), StringComparison.OrdinalIgnoreCase);
                e.IsSeed = seed;
                if (seed && (e.IsBootstrap || e.IsLauncher)) throw new UpdateException(bad);
                object once;
                if (d.TryGetValue("replaceOnce", out once) && once != null)
                {
                    // Only on seeds, and a plain id (it is written to the state file and the log).
                    e.ReplaceOnce = once as string;
                    if (!seed || e.ReplaceOnce == null || !OnceId.IsMatch(e.ReplaceOnce)) throw new UpdateException(bad);
                }
                object b;
                if (d.TryGetValue("blocks", out b) && b != null) ParseBlocks(b, e);
                into.Add(e);
            }
        }

        public const int MinBlockSize = 64 << 10;
        public const int MaxBlockSize = 64 << 20;

        /// <summary>"blocks": {"size": N, "sha256": [...]}: N in MinBlockSize..MaxBlockSize and exactly one hash per
        /// block of the file (the last block may be shorter). Anything else rejects the manifest.</summary>
        private static void ParseBlocks(object o, ManifestEntry e)
        {
            const string bad = "Güncelleme listesi geçersiz. Yöneticiye haber ver.";
            Dictionary<string, object> d = o as Dictionary<string, object>;
            object s, h;
            if (d == null || !d.TryGetValue("size", out s) || !d.TryGetValue("sha256", out h)) throw new UpdateException(bad);
            long size;
            try { size = Convert.ToInt64(s, CultureInfo.InvariantCulture); }
            catch (FormatException) { throw new UpdateException(bad); }
            catch (InvalidCastException) { throw new UpdateException(bad); }
            catch (OverflowException) { throw new UpdateException(bad); }
            object[] list = h as object[];
            if (size < MinBlockSize || size > MaxBlockSize || list == null) throw new UpdateException(bad);
            long count = (e.Size + size - 1) / size;
            if (list.Length != count) throw new UpdateException(bad);
            string[] hashes = new string[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                string x = list[i] as string;
                if (x == null || !HexSha.IsMatch(x)) throw new UpdateException(bad);
                hashes[i] = x.ToLowerInvariant();
            }
            // Never patched in place: the bootstrap is the setup's, seeds are the player's, and the launcher file is always
            // replaced whole (an interrupted patch would leave mixed bytes the bootstrap could adopt; Codex 1.4 round 6 #2).
            if (e.IsBootstrap || e.IsLauncher || e.IsSeed) return;
            e.BlockSize = (int)size;
            e.BlockHashes = hashes;
        }

        /// <summary>Full path of a manifest path inside gameDir, or null if the path is absolute, has a drive, a
        /// ".." or "." segment, an empty segment, invalid characters, or would resolve outside gameDir.</summary>
        public static string ResolveInside(string gameDir, string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Length > 240) return null;
            string p = rel.Replace('/', '\\');
            if (p.StartsWith("\\") || p.Contains(":") || System.IO.Path.IsPathRooted(p)) return null;
            if (p.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || p.IndexOfAny(new char[] { '*', '?' }) >= 0) return null;
            foreach (string seg in p.Split('\\'))
            {
                if (seg.Length == 0 || seg == "." || seg == ".." || seg.Trim() != seg || seg.EndsWith(".")) return null;
            }
            string root = System.IO.Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";
            string full;
            try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, p)); }
            catch (Exception) { return null; }
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length) return null;
            return full;
        }

        /// <summary>Entries whose local copy is missing or differs. Hashes only files whose size matches and whose
        /// size+mtime is not in the cache, so a normal start costs a few stat calls.</summary>
        public List<ManifestEntry> Plan(Manifest m)
        {
            List<ManifestEntry> todo = new List<ManifestEntry>();
            List<ManifestEntry> toHash = new List<ManifestEntry>();
            long hashTotal = 0;
            foreach (ManifestEntry e in m.Files)
            {
                // The bootstrap is replaced only by the setup (it runs while the launcher runs; a broken one could not be
                // recovered). It is neither downloaded nor part of the offline check.
                if (e.IsBootstrap) continue;
                string local = e.FullPath;
                // A file reached through a junction is not trusted even when its bytes match; Apply then refuses the
                // whole update, so the game does not start from such a folder.
                if (CrossesReparsePoint(local)) { todo.Add(e); continue; }
                FileInfo fi = new FileInfo(local);
                if (!fi.Exists || fi.Length != e.Size) { todo.Add(e); continue; }
                string cached = cache.Get(local, fi);
                if (cached != null)
                {
                    if (cached != e.Sha256) todo.Add(e);
                    continue;
                }
                toHash.Add(e);
                hashTotal += e.Size;
            }
            UpdateProgress pr = new UpdateProgress();
            pr.Checking = true;
            pr.Count = toHash.Count;
            pr.Total = hashTotal;
            for (int i = 0; i < toHash.Count; i++)
            {
                ManifestEntry e = toHash[i];
                pr.Index = i + 1;
                pr.File = e.Path;
                string local = e.FullPath;
                string hash;
                try
                {
                    FileInfo before = new FileInfo(local);
                    if (e.BlockHashes != null)
                    {
                        string[] blocks;
                        hash = HashFileAndBlocks(local, e.BlockSize, pr, Progress, out blocks);
                        RememberLocalBlocks(e, before, blocks);
                    }
                    else
                    {
                        hash = HashFile(local, pr);
                    }
                    cache.Put(local, before, hash);
                }
                catch (IOException) { hash = null; }
                catch (UnauthorizedAccessException) { hash = null; }
                if (hash != e.Sha256) todo.Add(e);
            }
            cache.Save();
            bool? running = null;
            foreach (ManifestEntry e in m.Seeds)
            {
                // A seed is the player's file once it exists (settings they changed): it is replaced only by the two
                // rules of ConfigRepair.cs (a broken config.xfs, a new "replaceOnce" id), never while the game runs.
                e.Replace = SeedReplace.None;
                if (CrossesReparsePoint(e.FullPath) || !File.Exists(e.FullPath)) { todo.Add(e); continue; }
                SeedReplace why = ReplaceReason(e);
                if (why == SeedReplace.None) continue;
                string have;
                try { have = HashFile(e.FullPath, null); }
                catch (IOException) { have = null; }
                catch (UnauthorizedAccessException) { have = null; }
                if (have == e.Sha256)
                {
                    // Already the release's copy (a broken one would be the release's own fault: never fetched again).
                    if (why == SeedReplace.Once) RecordOnce(e);
                    continue;
                }
                if (running == null) running = GameRunning();
                if (running.Value)
                {
                    LauncherLog.Write(cfg.GameDir, e.Path + " not replaced (" + Describe(e, why) + "): the game is running; next start");
                    continue;
                }
                e.Replace = why;
                todo.Add(e);
            }
            return todo;
        }

        /// <summary>Why an existing seed is to be replaced: a "replaceOnce" id not applied in this folder yet, else a
        /// config.xfs with the broken-input signature (an unparsable file is never touched by that rule).</summary>
        private SeedReplace ReplaceReason(ManifestEntry e)
        {
            if (e.ReplaceOnce != null && !string.Equals(SeedOnceState.Get(cfg.GameDir, e.Path), e.ReplaceOnce, StringComparison.Ordinal))
                return SeedReplace.Once;
            if (ConfigRepair.IsConfig(e) && ConfigRepair.Inspect(e.FullPath) == ConfigState.BrokenInput) return SeedReplace.BrokenConfig;
            return SeedReplace.None;
        }

        private static string Describe(ManifestEntry e, SeedReplace why)
        {
            return why == SeedReplace.Once ? "replace once " + e.ReplaceOnce : "no AddAction lines in autoexec.cfg";
        }

        private void RecordOnce(ManifestEntry e)
        {
            if (e.ReplaceOnce != null && !SeedOnceState.Set(cfg.GameDir, e.Path, e.ReplaceOnce))
                LauncherLog.Write(cfg.GameDir, e.Path + ": replace-once id could not be recorded");
        }

        /// <summary>Replaces an existing seed with its verified download (Plan chose it: ConfigRepair.cs). The old file
        /// becomes "&lt;file&gt;.bak-&lt;UTC time&gt;" (the newest ConfigRepair.KeepBackups are kept). Never fatal: when
        /// anything is wrong the player's file stays and the update goes on. The launcher starts the game only while it
        /// holds the update lock (MainForm.LaunchGame), so it cannot start the game between the check and the replace.</summary>
        private void ReplaceSeed(ManifestEntry e, string part, string target)
        {
            string why = Describe(e, e.Replace);
            if (ConfigRepair.IsConfig(e) && ConfigRepair.Inspect(part) != ConfigState.Good)
            {
                // The release's own copy would not fix anything: never put it over a player's file.
                SafeDelete(part);
                LauncherLog.Write(cfg.GameDir, e.Path + " not replaced (" + why + "): the release copy is not a usable config");
                return;
            }
            // Decided again on the file as it is now: since Plan the game may have run and written a working config.
            if (ReplaceReason(e) != e.Replace)
            {
                SafeDelete(part);
                LauncherLog.Write(cfg.GameDir, e.Path + " not replaced (" + why + "): the file changed since the check");
                return;
            }
            if (GameRunning())
            {
                SafeDelete(part);
                LauncherLog.Write(cfg.GameDir, e.Path + " not replaced (" + why + "): the game is running; next start");
                return;
            }
            // The id is stored BEFORE the file is replaced: if it cannot be stored, nothing is replaced (otherwise every
            // start would reset the player's settings again; Codex gate 1.6 #3). A failed replace puts the old id back.
            string previous = SeedOnceState.Get(cfg.GameDir, e.Path);
            if (e.ReplaceOnce != null && !SeedOnceState.Set(cfg.GameDir, e.Path, e.ReplaceOnce))
            {
                SafeDelete(part);
                LauncherLog.Write(cfg.GameDir, e.Path + " not replaced (" + why + "): the replace-once id cannot be stored");
                return;
            }
            string backup = ConfigRepair.BackupPath(target, DateTime.UtcNow);
            try
            {
                File.SetAttributes(target, FileAttributes.Normal);
                ReplaceFile(part, target, backup);
            }
            catch (Exception x)
            {
                if (!(x is IOException || x is UnauthorizedAccessException)) throw;
                // ReplaceFile can leave the old file under the backup name only: copy it back (the backup stays).
                bool restored = true;
                try { if (!File.Exists(target) && File.Exists(backup)) File.Copy(backup, target); }
                catch (IOException) { restored = false; }
                catch (UnauthorizedAccessException) { restored = false; }
                if (e.ReplaceOnce != null) SeedOnceState.Set(cfg.GameDir, e.Path, previous);
                SafeDelete(part);
                LauncherLog.Write(cfg.GameDir, e.Path + " not replaced (" + why + "): " + x.GetType().Name
                    + (restored ? "" : "; the old file is " + System.IO.Path.GetFileName(backup)));
                return;
            }
            cache.Put(target, new FileInfo(target), e.Sha256);
            ConfigRepair.PruneBackups(target);
            ConfigReplaced = true;
            LauncherLog.Write(cfg.GameDir, e.Path + " replaced with the release default (" + why + "); old file kept as "
                + System.IO.Path.GetFileName(backup));
        }

        private string HashFile(string path, UpdateProgress pr)
        {
            return HashFile(path, pr, Progress);
        }

        internal static string HashFile(string path, UpdateProgress pr, Action<UpdateProgress> report)
        {
            string[] none;
            return HashFileAndBlocks(path, 0, pr, report, out none);
        }

        /// <summary>Whole-file SHA-256 and, with blockSize > 0, the SHA-256 of every blockSize block (the last one may
        /// be shorter), in one read of the file.</summary>
        internal static string HashFileAndBlocks(string path, int blockSize, UpdateProgress pr, Action<UpdateProgress> report,
            out string[] blocks)
        {
            List<string> list = blockSize > 0 ? new List<string>() : null;
            using (SHA256 sha = SHA256.Create())
            using (SHA256 blockSha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                byte[] buf = new byte[blockSize > 0 ? blockSize : 1 << 20];
                int n;
                long lastReport = 0;
                while ((n = ReadFull(fs, buf, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    if (list != null) list.Add(ToHex(blockSha.ComputeHash(buf, 0, n)));
                    if (pr != null)
                    {
                        pr.Done += n;
                        if (pr.Done - lastReport >= (8 << 20)) { lastReport = pr.Done; if (report != null) report(pr); }
                    }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                if (pr != null && report != null) report(pr);
                blocks = list == null ? null : list.ToArray();
                return ToHex(sha.Hash);
            }
        }

        /// <summary>Reads until buf holds count bytes or the stream ends; returns the number of bytes read.</summary>
        internal static int ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0, n;
            while (got < count && (n = s.Read(buf, got, count - got)) > 0) got += n;
            return got;
        }

        public static string ToHex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private void Report(UpdateProgress p)
        {
            Action<UpdateProgress> cb = Progress;
            if (cb != null) cb(p);
        }

        /// <summary>Downloads and replaces the planned files. Call it after every Plan, also with an empty list: an
        /// empty list means every file matches the manifest, which is what clears an earlier "incomplete" marker.</summary>
        public void Apply(List<ManifestEntry> todo)
        {
            if (todo.Count == 0) { ClearIncomplete(); return; }
            if (!SecureSource(cfg.UpdateBaseUrl)) throw new UpdateException(InsecureText);
            // Every target and staging path is checked before anything is changed, so a junction found halfway
            // cannot leave a half-applied update behind.
            foreach (ManifestEntry e in todo)
            {
                string target = e.FullPath;
                if (CrossesReparsePoint(target) || CrossesReparsePoint(target + ".part"))
                    throw new UpdateException(Short(e.Path) + " yazılamadı: klasörde bağlantı (junction) var.");
            }
            // Changed files with a block list are patched in place: find the blocks that differ first, so the
            // progress total and the free-space check use the bytes that are really downloaded.
            Dictionary<ManifestEntry, DeltaPlan> deltas = PlanDeltas(todo);
            long total = 0;
            foreach (ManifestEntry e in todo)
            {
                DeltaPlan dp;
                total += deltas.TryGetValue(e, out dp) ? dp.Bytes : e.Size;
            }
            CheckFreeSpace(SpaceNeeded(todo, 0, deltas));
            MarkIncomplete();

            UpdateProgress pr = new UpdateProgress();
            pr.Count = todo.Count;
            pr.Total = total;
            SpeedMeter speed = new SpeedMeter();
            for (int i = 0; i < todo.Count; i++)
            {
                ManifestEntry e = todo[i];
                pr.Index = i + 1;
                pr.File = e.Path;
                long doneBefore = pr.Done;
                DeltaPlan delta;
                deltas.TryGetValue(e, out delta);
                pr.FileDone = 0;
                pr.FileSize = delta != null ? delta.Bytes : e.Size;
                bool hashRetried = false;
                // Replacing an existing seed (ConfigRepair.cs) is optional: when its download or staging fails, the
                // player's file stays, the failure is logged and the required files still update (Codex gate 1.6 #4).
                bool optional = e.IsSeed && e.Replace != SeedReplace.None;
                try
                {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (delta != null) ApplyDelta(e, delta, pr, doneBefore, speed);
                        else DownloadOne(e, pr, doneBefore, speed);
                        break;
                    }
                    catch (DeltaFallback)
                    {
                        // The server ignored the Range request, or the patched file does not match the whole-file
                        // SHA-256: download the whole file after all. The total grows by what is still to come.
                        pr.Total += e.Size - (delta.Bytes - delta.Fetched);
                        doneBefore = pr.Done;
                        delta = null;
                        pr.FileDone = 0;
                        pr.FileSize = e.Size;
                        attempt = 0;
                        deltas.Remove(e);
                        CheckFreeSpace(SpaceNeeded(todo, i, deltas)); // this whole file and everything still to come
                        continue;
                    }
                    catch (HashMismatch)
                    {
                        // A block that does not match its hash is never written; the .part of a whole download is.
                        if (delta == null) SafeDelete(PartPath(e));
                        if (hashRetried) throw new UpdateException(Short(e.Path) + " bozuk indi. Tekrar dene'ye bas; düzelmezse yöneticiye haber ver.");
                        hashRetried = true;
                    }
                    catch (WebException)
                    {
                        if (attempt >= MaxAttempts) throw new UpdateException(Short(e.Path) + " indirilemedi. İnternet bağlantını kontrol edip Tekrar dene'ye bas.");
                        System.Threading.Thread.Sleep(RetryDelayMs * attempt);
                    }
                    catch (NetworkIOException)
                    {
                        if (attempt >= MaxAttempts) throw new UpdateException(Short(e.Path) + " indirilemedi. İnternet bağlantını kontrol edip Tekrar dene'ye bas.");
                        System.Threading.Thread.Sleep(RetryDelayMs * attempt);
                    }
                    pr.Done = doneBefore + (delta != null ? delta.Fetched : 0);
                }
                }
                catch (UpdateException x)
                {
                    if (!optional) throw;
                    SafeDelete(PartPath(e));
                    LauncherLog.Write(cfg.GameDir, e.Path + " not replaced (" + Describe(e, e.Replace) + "): download failed: " + x.Message);
                }
                pr.Done = doneBefore + pr.FileSize;
                pr.FileDone = pr.FileSize;
                Report(pr);
            }
            cache.Save();
            ClearIncomplete();
        }

        private static string Short(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            return name.Length > 48 ? name.Substring(0, 45) + "…" : name;
        }

        private static string PartPath(ManifestEntry e)
        {
            return e.FullPath + ".part";
        }

        /// <summary>Disk space for todo[from..]: a block update needs only the growth of its file, a whole download
        /// its size minus what an earlier .part already holds.</summary>
        private static long SpaceNeeded(List<ManifestEntry> todo, int from, Dictionary<ManifestEntry, DeltaPlan> deltas)
        {
            long need = 0;
            for (int i = from; i < todo.Count; i++)
            {
                ManifestEntry e = todo[i];
                DeltaPlan dp;
                if (deltas.TryGetValue(e, out dp))
                {
                    need += Math.Max(0, e.Size - dp.LocalLength);
                    continue;
                }
                FileInfo part = new FileInfo(PartPath(e));
                need += e.Size - (part.Exists && part.Length <= e.Size ? part.Length : 0);
            }
            return need;
        }

        private void CheckFreeSpace(long need)
        {
            try
            {
                DriveInfo di = new DriveInfo(System.IO.Path.GetPathRoot(cfg.GameDir));
                long margin = 16L << 20;
                if (di.AvailableFreeSpace < need + margin)
                    throw new UpdateException("Diskte yer kalmadı. " + ((need + margin) >> 20) + " MB boş alan gerekiyor.");
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class HashMismatch : Exception { }

        /// <summary>A read failure on the response stream (as opposed to a local disk write failure).</summary>
        private sealed class NetworkIOException : Exception
        {
            public NetworkIOException(Exception inner) : base("network", inner) { }
        }

        private void DownloadOne(ManifestEntry e, UpdateProgress pr, long doneBefore, SpeedMeter speed)
        {
            string target = e.FullPath;
            string part = target + ".part";
            if (CrossesReparsePoint(target) || CrossesReparsePoint(part))
                throw new UpdateException(Short(e.Path) + " yazılamadı: klasörde bağlantı (junction) var.");
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            }
            catch (IOException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
            catch (UnauthorizedAccessException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }

            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > e.Size) { SafeDelete(part); have = 0; }

            if (have < e.Size || e.Size == 0)
            {
                HttpWebRequest req = Net.StaticRequest(FileUrl(e), 20000);
                req.ReadWriteTimeout = 30000;
                if (have > 0) req.AddRange(have);
                HttpWebResponse resp;
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException we)
                {
                    HttpWebResponse r = we.Response as HttpWebResponse;
                    if (r != null && (int)r.StatusCode == 416)
                    {
                        // Our partial file does not fit the server copy any more: start over.
                        r.Close();
                        SafeDelete(part);
                    }
                    throw;
                }
                using (resp)
                {
                    FileMode mode = FileMode.Append;
                    if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                    {
                        mode = FileMode.Create; // server ignored the Range header
                        have = 0;
                    }
                    else if (have == 0)
                    {
                        mode = FileMode.Create;
                    }
                    pr.Done = doneBefore + have;
                    pr.FileDone = have;
                    Stream src = resp.GetResponseStream();
                    FileStream dst;
                    try
                    {
                        dst = new FileStream(part, mode, FileAccess.Write, FileShare.None, 1 << 16);
                    }
                    catch (IOException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
                    catch (UnauthorizedAccessException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
                    using (dst)
                    {
                        byte[] buf = new byte[1 << 16];
                        long written = have;
                        DateTime lastReport = DateTime.MinValue;
                        while (true)
                        {
                            int n;
                            try { n = src.Read(buf, 0, buf.Length); }
                            catch (IOException io) { throw new NetworkIOException(io); }
                            if (n <= 0) break;
                            if (written + n > e.Size) throw new HashMismatch(); // longer than the manifest says
                            try { dst.Write(buf, 0, n); }
                            catch (IOException io) { throw WriteFailure(e, io); }
                            written += n;
                            pr.Done = doneBefore + written;
                            pr.FileDone = written;
                            speed.Add(n);
                            DateTime now = DateTime.UtcNow;
                            if ((now - lastReport).TotalMilliseconds >= 200)
                            {
                                lastReport = now;
                                pr.BytesPerSecond = speed.BytesPerSecond;
                                Report(pr);
                            }
                        }
                        try { dst.Flush(true); }
                        catch (IOException io) { throw WriteFailure(e, io); }
                        if (written < e.Size) throw new NetworkIOException(new EndOfStreamException());
                    }
                }
            }

            string got;
            try { got = HashFile(part, null); }
            catch (IOException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
            if (got != e.Sha256)
            {
                SafeDelete(part);
                throw new HashMismatch();
            }

            if (e.IsSeed && e.Replace != SeedReplace.None && File.Exists(target))
            {
                ReplaceSeed(e, part, target);
                return;
            }
            if (e.IsSeed)
            {
                // A missing seed never replaces a file: File.Move fails when the game created its own copy meanwhile
                // (Codex review #3), and then the player's file wins.
                try { File.Move(part, target); }
                catch (IOException)
                {
                    if (!File.Exists(target)) throw new UpdateException(WriteProblem(Short(e.Path), new IOException()));
                    SafeDelete(part);
                    return;
                }
                catch (UnauthorizedAccessException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
                cache.Put(target, new FileInfo(target), e.Sha256);
                RecordOnce(e); // a fresh copy is the release's: the current "replaceOnce" id is done
                return;
            }
            try
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Replace(part, target, null, true);
                }
                else
                {
                    File.Move(part, target);
                }
            }
            catch (IOException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
            catch (UnauthorizedAccessException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
            cache.Put(target, new FileInfo(target), e.Sha256);
        }

        /// <summary>The file's URL with "?h=&lt;first 16 hex of its SHA-256&gt;": a new version has a new URL, so no cache
        /// between the server and the player can answer with the old bytes (the server ignores the query).</summary>
        internal string FileUrl(ManifestEntry e)
        {
            return cfg.UpdateBaseUrl + EscapePath(e.Path) + "?h=" + e.Sha256.Substring(0, 16);
        }

        /// <summary>True when the path itself or any existing folder between it and the game folder is a junction or
        /// symbolic link, which could redirect the write outside the game folder.</summary>
        private bool CrossesReparsePoint(string path)
        {
            return CrossesReparsePoint(path, cfg.GameDir);
        }

        internal static bool CrossesReparsePoint(string path, string gameDir)
        {
            string root = System.IO.Path.GetFullPath(gameDir).TrimEnd('\\');
            string cur = path;
            try
            {
                while (cur != null && cur.Length > root.Length)
                {
                    if (File.Exists(cur) || Directory.Exists(cur))
                    {
                        if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0) return true;
                    }
                    cur = System.IO.Path.GetDirectoryName(cur);
                }
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            return false;
        }

        private static Exception WriteFailure(ManifestEntry e, IOException io)
        {
            int code = Marshal.GetHRForException(io) & 0xFFFF;
            if (code == 112 || code == 39) // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
                return new UpdateException("Diskte yer kalmadı. " + ((e.Size >> 20) + 16) + " MB boş alan gerekiyor.");
            return new UpdateException(WriteProblem(Short(e.Path), io));
        }

        /// <summary>Turkish text for a local write failure. No write permission (e.g. a folder under Program Files
        /// without admin rights) gets its own hint; anything else is most likely a file the running game holds.</summary>
        internal static string WriteProblem(string shortName, Exception e)
        {
            if (e is UnauthorizedAccessException)
                return shortName + " yazılamadı: oyun klasörüne yazma izni yok. Oyunu kurulum programıyla C:\\Oyunlar gibi bir klasöre kur.";
            return shortName + " yazılamadı. Oyun açıksa kapatıp Tekrar dene'ye bas.";
        }

        internal static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static string EscapePath(string rel)
        {
            string[] segs = rel.Split('\\');
            for (int i = 0; i < segs.Length; i++) segs[i] = Uri.EscapeDataString(segs[i]);
            return string.Join("/", segs);
        }
    }

    /// <summary>Bytes per second over the last 5 seconds.</summary>
    internal sealed class SpeedMeter
    {
        private readonly Queue<KeyValuePair<DateTime, long>> samples = new Queue<KeyValuePair<DateTime, long>>();
        private long sum;

        public void Add(long bytes)
        {
            DateTime now = DateTime.UtcNow;
            samples.Enqueue(new KeyValuePair<DateTime, long>(now, bytes));
            sum += bytes;
            while (samples.Count > 1 && (now - samples.Peek().Key).TotalSeconds > 5)
                sum -= samples.Dequeue().Value;
        }

        public double BytesPerSecond
        {
            get
            {
                if (samples.Count < 2) return 0;
                double secs = Math.Max(0.5, (DateTime.UtcNow - samples.Peek().Key).TotalSeconds);
                return sum / secs;
            }
        }
    }

    /// <summary>size + mtime -> sha256 per file, under %APPDATA%\WolfTeamDirilis, one file per game folder.</summary>
    internal sealed class HashCache
    {
        private readonly string file;
        private readonly Dictionary<string, string[]> map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();
        private bool dirty;

        public HashCache(string gameDir)
        {
            string key = Updater.GameDirKey(gameDir);
            file = System.IO.Path.Combine(UserSettings.Dir, "hashcache-" + key + ".tsv");
            try
            {
                if (File.Exists(file))
                {
                    foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length == 4) map[f[0]] = f;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public string Get(string path, FileInfo fi)
        {
            lock (sync)
            {
                string[] f;
                if (!map.TryGetValue(path, out f)) return null;
                if (f[1] != fi.Length.ToString(CultureInfo.InvariantCulture)) return null;
                if (f[2] != fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)) return null;
                return f[3];
            }
        }

        public void Put(string path, FileInfo fi, string sha)
        {
            lock (sync)
            {
                fi.Refresh();
                map[path] = new string[]
                {
                    path, fi.Length.ToString(CultureInfo.InvariantCulture),
                    fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), sha
                };
                dirty = true;
            }
        }

        public void Save()
        {
            lock (sync)
            {
                if (!dirty) return;
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                    StringBuilder sb = new StringBuilder();
                    foreach (string[] f in map.Values) sb.Append(string.Join("\t", f)).Append('\n');
                    string tmp = file + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(file)) File.Replace(tmp, file, null, true);
                    else File.Move(tmp, file);
                    dirty = false;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
