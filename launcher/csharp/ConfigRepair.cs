// config.xfs repair (launcher 1.6). From 2026-09-30 to 2026-10-01 the release shipped a default config.xfs whose
// autoexec.cfg had no "AddAction" lines (no movement/fire actions, so no key bindings in matches) and HardwareCursor 0
// (magenta box around the cursor on Parallels). config.xfs is a seed (fetched only when missing) and the game writes it
// back on exit, so such an install never healed. Two rules, both applied by Updater.Plan/DownloadOne under the update
// lock and only while the game is not running:
//   1. broken input config: config.xfs parses as an XFS archive and its autoexec.cfg has zero AddAction lines (a
//      config the game wrote itself always has the AddAction block). It is backed up and replaced by the manifest seed.
//      A file that cannot be parsed is never touched.
//   2. replace once: a seed entry with "replaceOnce": "<id>" replaces the player's copy once per new id (owner decision
//      2026-10-01: everyone gets the fixed defaults once); the applied id is remembered per game folder in
//      %APPDATA%\WolfTeamDirilis\seed-once-<key>.txt and the file is left alone until the id changes.
// The XFS reader is read-only and minimal (tools/re/xfs.py has the format): u48 directory offset; directory = [u8 n]
// [zlib header n bytes: "XFS " ... "Xenesis File System"][u24 tree length][u24 stored length][zlib tree]; tree =
// pre-order [u32 children][u8 name length][name][15 bytes: u32 offset, u16 offset high, u32 size, u32 stored, u8 flag];
// file data (flag 1) = chunks [u24 h (0x800000 short: a second u24 with the stored length follows unless 0x400000;
// 0x400000 raw)][u16 sum of the plain data as u16 words][zlib or raw data].
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace WolfTeamDirilis
{
    internal enum ConfigState { Unreadable, Good, BrokenInput }

    internal static class XfsReader
    {
        public const int MaxArchiveBytes = 4 << 20;   // config.xfs is a few KB
        private const int MaxPlainBytes = 16 << 20;
        private const int Chunk = 0x10000;
        private const int Short = 0x800000;
        private const int Raw = 0x400000;

        /// <summary>Files of the archive (path with "/", case kept) -> plain bytes; null when anything does not parse
        /// (bad offsets, zlib errors, a chunk sum that does not match, sizes that disagree).</summary>
        public static Dictionary<string, byte[]> ReadAll(byte[] a)
        {
            try { return Parse(a); }
            catch (InvalidDataException) { return null; }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
            catch (OverflowException) { return null; }
        }

        private static Dictionary<string, byte[]> Parse(byte[] a)
        {
            if (a == null || a.Length < 6) return null;
            long dirOffset = (long)U24(a, 0) + ((long)U24(a, 3) << 24);
            if (dirOffset < 6 || dirOffset >= a.Length) return null;
            int d = (int)dirOffset;
            int n = a[d];
            if (d + 1 + n + 6 > a.Length) return null;
            byte[] header = Inflate(a, d + 1, n, 4096);
            if (header == null || header.Length < 24 || header[0] != 'X' || header[1] != 'F' || header[2] != 'S' || header[3] != ' ')
                return null;
            if (Encoding.ASCII.GetString(header).IndexOf("Xenesis File System", StringComparison.Ordinal) < 0) return null;
            // The header repeats the directory offset (u64 at 16) and counts the file entries (u32 at 8).
            if (BitConverter.ToInt64(header, 16) != dirOffset) return null;
            uint declaredFiles = BitConverter.ToUInt32(header, 8);
            int p = d + 1 + n;
            int treeLen = U24(a, p), treeStored = U24(a, p + 3);
            // The tree record ends the archive: nothing may follow it.
            if ((long)p + 6 + treeStored != a.Length) return null;
            byte[] tree = Inflate(a, p + 6, treeStored, treeLen);
            if (tree == null || tree.Length != treeLen) return null;

            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;
            List<KeyValuePair<string, uint>> stack = new List<KeyValuePair<string, uint>>();
            string rootName;
            int rootAt;
            uint rootChildren = Entry(tree, ref pos, out rootName, out rootAt);
            if (rootChildren == 0 || !Zero(tree, rootAt)) return null;
            stack.Add(new KeyValuePair<string, uint>(rootName, rootChildren));
            long plainTotal = 0;
            while (true)
            {
                while (stack.Count > 0 && stack[stack.Count - 1].Value == 0) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) break;
                // Every declared child must be there (a short tree is damaged, not "a config without AddAction").
                if (pos >= tree.Length) return null;
                KeyValuePair<string, uint> top = stack[stack.Count - 1];
                stack[stack.Count - 1] = new KeyValuePair<string, uint>(top.Key, top.Value - 1);
                string name;
                int at;
                uint children = Entry(tree, ref pos, out name, out at);
                if (name.Length == 0) return null;
                if (children == 0 && !Zero(tree, at))
                {
                    long off = BitConverter.ToUInt32(tree, at) | ((long)BitConverter.ToUInt16(tree, at + 4) << 32);
                    long size = BitConverter.ToUInt32(tree, at + 6);
                    long stored = BitConverter.ToUInt32(tree, at + 10);
                    int flag = tree[at + 14];
                    plainTotal += size;
                    if (plainTotal > MaxPlainBytes) return null;
                    // File data lies between the 6-byte offset field and the directory.
                    if (off < 6 || off + stored > dirOffset) return null;
                    StringBuilder path = new StringBuilder();
                    for (int i = 1; i < stack.Count; i++) path.Append(stack[i].Key).Append('/');
                    path.Append(name);
                    byte[] data = ReadFile(a, off, (int)size, stored, flag);
                    if (data == null || files.ContainsKey(path.ToString())) return null;
                    files[path.ToString()] = data;
                }
                else
                {
                    // A folder: its payload is all zero (an empty folder has no children).
                    if (!Zero(tree, at) || stack.Count > 64) return null;
                    stack.Add(new KeyValuePair<string, uint>(name, children));
                }
            }
            // The whole tree is used, and the header's entry count matches.
            if (pos != tree.Length || files.Count != declaredFiles) return null;
            return files;
        }

        private static bool Zero(byte[] tree, int at)
        {
            for (int i = 0; i < 15; i++)
            {
                if (tree[at + i] != 0) return false;
            }
            return true;
        }

        /// <summary>One tree entry at pos; at = start of its 15-byte payload.</summary>
        private static uint Entry(byte[] tree, ref int pos, out string name, out int at)
        {
            if (pos + 5 > tree.Length) throw new InvalidDataException();
            uint children = BitConverter.ToUInt32(tree, pos);
            int n = tree[pos + 4];
            if (pos + 20 + n > tree.Length) throw new InvalidDataException();
            name = Encoding.GetEncoding(28591).GetString(tree, pos + 5, n);
            at = pos + 5 + n;
            pos += 20 + n;
            return children;
        }

        /// <summary>A file's plain bytes; null unless its chunks take exactly {stored} bytes and decode to exactly
        /// {size} bytes, every zlib record ends with its Adler-32 and every chunk matches its word sum.</summary>
        private static byte[] ReadFile(byte[] a, long offset, int size, long stored, int flag)
        {
            if (offset < 6 || size < 0 || offset + stored > a.Length) return null;
            if (flag == 0)
            {
                if (stored != size) return null;
                byte[] raw = new byte[size];
                Buffer.BlockCopy(a, (int)offset, raw, 0, size);
                return raw;
            }
            if (flag != 1) return null;
            MemoryStream outp = new MemoryStream(size);
            long pos = offset, end = offset + stored;
            while (outp.Length < size)
            {
                if (pos + 5 > end) return null;
                int h0 = U24(a, (int)pos), h = h0, header = 5;
                if ((h & Short) != 0 && (h & Raw) == 0)
                {
                    if (pos + 8 > end) return null;
                    h = U24(a, (int)pos + 3);
                    if ((h & ~0xFFFFF) != 0) return null; // a plain stored length, no flag bits
                    header = 8;
                }
                int chunkStored = h & 0xFFFFF;
                int check = BitConverter.ToUInt16(a, (int)pos + header - 2);
                if (pos + header + chunkStored > end) return null;
                byte[] plain;
                if ((h0 & Raw) != 0)
                {
                    plain = new byte[chunkStored];
                    Buffer.BlockCopy(a, (int)pos + header, plain, 0, chunkStored);
                }
                else
                {
                    plain = Inflate(a, (int)pos + header, chunkStored, Chunk);
                    if (plain == null) return null;
                }
                // A short chunk states its plain length; a full one is exactly 64 KB.
                int want = (h0 & Short) != 0 ? h0 & 0xFFFFF : Chunk;
                if (plain.Length == 0 || plain.Length != want || WordSum(plain) != check) return null;
                outp.Write(plain, 0, plain.Length);
                pos += header + chunkStored;
            }
            if (outp.Length != size || pos != end) return null;
            return outp.ToArray();
        }

        /// <summary>16-bit sum of the data read as little-endian u16 words (odd tail byte ignored).</summary>
        internal static int WordSum(byte[] b)
        {
            int sum = 0;
            for (int i = 0; i + 1 < b.Length; i += 2) sum = (sum + (b[i] | b[i + 1] << 8)) & 0xFFFF;
            return sum;
        }

        /// <summary>A zlib stream (2-byte header, deflate data) at a[off..off+len), at most max bytes out; null when
        /// it is not zlib, does not inflate or is larger than max.</summary>
        private static byte[] Inflate(byte[] a, int off, int len, int max)
        {
            if (len < 6 || off < 0 || off + len > a.Length) return null;
            int cmf = a[off], flg = a[off + 1];
            if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0) return null;
            byte[] plain;
            using (DeflateStream z = new DeflateStream(new MemoryStream(a, off + 2, len - 6, false), CompressionMode.Decompress))
            {
                MemoryStream outp = new MemoryStream();
                byte[] buf = new byte[8192];
                int r;
                while ((r = z.Read(buf, 0, buf.Length)) > 0)
                {
                    outp.Write(buf, 0, r);
                    if (outp.Length > max) return null;
                }
                plain = outp.ToArray();
            }
            // The record ends with the big-endian Adler-32 of the plain data.
            int e = off + len - 4;
            uint want = (uint)(a[e] << 24 | a[e + 1] << 16 | a[e + 2] << 8 | a[e + 3]);
            return Adler32(plain) == want ? plain : null;
        }

        internal static uint Adler32(byte[] data)
        {
            uint s1 = 1, s2 = 0;
            foreach (byte b in data)
            {
                s1 = (s1 + b) % 65521;
                s2 = (s2 + s1) % 65521;
            }
            return (s2 << 16) | s1;
        }

        private static int U24(byte[] a, int p)
        {
            return a[p] | a[p + 1] << 8 | a[p + 2] << 16;
        }
    }

    internal static class ConfigRepair
    {
        public const string ConfigPath = "config.xfs";

        /// <summary>True for the root config.xfs seed (the only file rule 1 looks at).</summary>
        public static bool IsConfig(ManifestEntry e)
        {
            return string.Equals(e.Path, ConfigPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Good / BrokenInput for a config.xfs that parses and has autoexec.cfg; Unreadable otherwise (then the
        /// file is never touched).</summary>
        public static ConfigState Inspect(string path)
        {
            byte[] data;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > XfsReader.MaxArchiveBytes) return ConfigState.Unreadable;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > XfsReader.MaxArchiveBytes) return ConfigState.Unreadable;
                    data = new byte[fs.Length];
                    if (Updater.ReadFull(fs, data, data.Length) != data.Length) return ConfigState.Unreadable;
                }
            }
            catch (IOException) { return ConfigState.Unreadable; }
            catch (UnauthorizedAccessException) { return ConfigState.Unreadable; }
            return InspectBytes(data);
        }

        public static ConfigState InspectBytes(byte[] data)
        {
            Dictionary<string, byte[]> files = XfsReader.ReadAll(data);
            byte[] autoexec;
            if (files == null || !files.TryGetValue("autoexec.cfg", out autoexec)) return ConfigState.Unreadable;
            return CountAddActions(Encoding.GetEncoding(28591).GetString(autoexec)) > 0 ? ConfigState.Good : ConfigState.BrokenInput;
        }

        /// <summary>Lines that contain "AddAction" (any case).</summary>
        public static int CountAddActions(string text)
        {
            int n = 0;
            foreach (string line in text.Split('\n'))
            {
                if (line.IndexOf("addaction", StringComparison.OrdinalIgnoreCase) >= 0) n++;
            }
            return n;
        }

        public const int KeepBackups = 2;

        /// <summary>"config.xfs.bak-20261001-103000-1234567" (UTC, 100 ns): names sort in time order, so pruning by
        /// name keeps the newest. When an existing backup name sorts at or after it (same instant, clock set back), the
        /// new name is the greatest existing one plus "-001", "-002", ..., so the new backup is always the "newest".</summary>
        public static string BackupPath(string target, DateTime utc)
        {
            string p = target + ".bak-" + utc.ToString("yyyyMMdd-HHmmss-fffffff", System.Globalization.CultureInfo.InvariantCulture);
            string max = null;
            try
            {
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(target), Path.GetFileName(target) + ".bak-*"))
                {
                    if (max == null || string.Compare(f, max, StringComparison.OrdinalIgnoreCase) > 0) max = f;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (max == null || string.Compare(p, max, StringComparison.OrdinalIgnoreCase) > 0) return p;
            string q = max;
            for (int i = 1; File.Exists(q) || Directory.Exists(q); i++) q = max + "-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            return q;
        }

        /// <summary>Deletes all but the newest KeepBackups "&lt;target&gt;.bak-*" files. Newest by name: the name holds the
        /// UTC time of the replace (a backup keeps the old file's own write time).</summary>
        public static void PruneBackups(string target)
        {
            try
            {
                string dir = Path.GetDirectoryName(target);
                List<string> list = new List<string>();
                foreach (string f in Directory.GetFiles(dir, Path.GetFileName(target) + ".bak-*"))
                {
                    if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0) list.Add(f);
                }
                list.Sort(delegate(string x, string y) { return string.Compare(y, x, StringComparison.OrdinalIgnoreCase); });
                for (int i = KeepBackups; i < list.Count; i++) Updater.SafeDelete(list[i]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The "replaceOnce" ids already applied in this game folder: %APPDATA%\WolfTeamDirilis\seed-once-&lt;key&gt;.txt,
    /// lines "&lt;seed path&gt;\t&lt;id&gt;".</summary>
    internal static class SeedOnceState
    {
        public static string FilePath(string gameDir)
        {
            return Path.Combine(UserSettings.Dir, "seed-once-" + Updater.GameDirKey(gameDir) + ".txt");
        }

        public static string Get(string gameDir, string seedPath)
        {
            try
            {
                string f = FilePath(gameDir);
                if (!File.Exists(f)) return null;
                foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && string.Equals(line.Substring(0, tab), seedPath, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(tab + 1).Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>Records id for seedPath (null removes the record); false when it could not be written. Updater.ReplaceSeed
        /// stores the id before it replaces the file and replaces nothing when this fails.</summary>
        public static bool Set(string gameDir, string seedPath, string id)
        {
            try
            {
                string f = FilePath(gameDir), tmp = f + ".tmp";
                Directory.CreateDirectory(UserSettings.Dir);
                List<string> lines = new List<string>();
                if (File.Exists(f))
                {
                    foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0 && !string.Equals(line.Substring(0, tab), seedPath, StringComparison.OrdinalIgnoreCase)) lines.Add(line);
                    }
                }
                if (id != null) lines.Add(seedPath + "\t" + id);
                File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(f)) File.Replace(tmp, f, null, true); else File.Move(tmp, f);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
