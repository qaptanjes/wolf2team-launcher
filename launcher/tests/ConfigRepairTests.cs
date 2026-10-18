// Tests of the config.xfs repair (launcher\csharp\ConfigRepair.cs, Updater.Plan/ReplaceSeed). Every config.xfs here is
// synthetic, written by XfsWriter below in the archive format of tools/re/xfs.py (no client data).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WolfTeamDirilis.Tests
{
    /// <summary>Writes an XFS archive like tools/re/xfs.py replace_many: u48 directory offset, chunked zlib file data
    /// (64 KB full chunks, then one short chunk), then [u8 n][zlib header][u24 tree][u24 stored][zlib tree].</summary>
    internal static class XfsWriter
    {
        public static byte[] Zlib(byte[] plain)
        {
            MemoryStream ms = new MemoryStream();
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (DeflateStream z = new DeflateStream(ms, CompressionMode.Compress, true)) z.Write(plain, 0, plain.Length);
            uint a = 1, b = 0;
            foreach (byte x in plain) { a = (a + x) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        private static void U24(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); }
        private static void U16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
        private static void U32(Stream s, long v) { U16(s, (int)(v & 0xFFFF)); U16(s, (int)((v >> 16) & 0xFFFF)); }

        public static byte[] Chunks(byte[] plain)
        {
            MemoryStream o = new MemoryStream();
            for (int start = 0; start < plain.Length; start += 0x10000)
            {
                byte[] piece = new byte[Math.Min(0x10000, plain.Length - start)];
                Buffer.BlockCopy(plain, start, piece, 0, piece.Length);
                byte[] packed = Zlib(piece);
                if (piece.Length == 0x10000) U24(o, packed.Length);
                else { U24(o, 0x800000 | piece.Length); U24(o, packed.Length); }
                U16(o, XfsReader.WordSum(piece));
                o.Write(packed, 0, packed.Length);
            }
            return o.ToArray();
        }

        public static byte[] Archive(params KeyValuePair<string, byte[]>[] files)
        {
            return Archive(files.Length, files.Length, files);
        }

        /// <summary>rootChildren / entries other than files.Length make a damaged archive (tests).</summary>
        public static byte[] Archive(int rootChildren, int entries, params KeyValuePair<string, byte[]>[] files)
        {
            MemoryStream data = new MemoryStream();
            data.Write(new byte[6], 0, 6);
            MemoryStream tree = new MemoryStream();
            U32(tree, rootChildren); tree.WriteByte(4); tree.Write(Encoding.ASCII.GetBytes("Root"), 0, 4); tree.Write(new byte[15], 0, 15);
            foreach (KeyValuePair<string, byte[]> f in files)
            {
                long off = data.Length;
                byte[] blob = Chunks(f.Value);
                data.Write(blob, 0, blob.Length);
                byte[] name = Encoding.ASCII.GetBytes(f.Key);
                U32(tree, 0); tree.WriteByte((byte)name.Length); tree.Write(name, 0, name.Length);
                U32(tree, off); U16(tree, 0); U32(tree, f.Value.Length); U32(tree, blob.Length); tree.WriteByte(1);
            }
            long dir = data.Length;
            MemoryStream header = new MemoryStream();
            header.Write(Encoding.ASCII.GetBytes("XFS "), 0, 4);
            U32(header, 0); U32(header, entries); U32(header, 0); U32(header, dir); U32(header, 0); U32(header, 0x10000);
            header.Write(Encoding.ASCII.GetBytes("Xenesis File System version 1.0.0.0"), 0, 35);
            header.WriteByte(0);
            byte[] zh = Zlib(header.ToArray()), t = tree.ToArray(), zt = Zlib(t);
            data.WriteByte((byte)zh.Length);
            data.Write(zh, 0, zh.Length);
            U24(data, t.Length); U24(data, zt.Length);
            data.Write(zt, 0, zt.Length);
            byte[] all = data.ToArray();
            for (int i = 0; i < 6; i++) all[i] = (byte)(dir >> (8 * i));
            return all;
        }

        public static KeyValuePair<string, byte[]> F(string name, string text)
        {
            return new KeyValuePair<string, byte[]>(name, Encoding.GetEncoding(28591).GetBytes(text));
        }

        private const string Display = "\"HardwareCursor\" \"1\"\r\n\"ScreenWidth\" \"1024\"\r\n";

        /// <summary>A config the game would write: the AddAction block, keyboard and mouse binds.</summary>
        public static byte[] Good(string extra)
        {
            return Archive(F("display.cfg", Display), F("autoexec.cfg",
                "AddAction Forward 0\r\nADDACTION Backward 1\r\naddaction Fire 17\r\nenabledevice \"##keyboard\"\r\n"
                + "rangebind \"##keyboard\" \"##w\" 0 0 \"Forward\"\r\n\"HardwareCursor\" \"1\"\r\n" + extra));
        }

        /// <summary>The 2026-09-30 seed's signature: mouse binds on "Fare" and no AddAction line.</summary>
        public static byte[] Bad(string extra)
        {
            return Archive(F("display.cfg", Display.Replace("\"1\"", "\"0\"")), F("autoexec.cfg",
                "enabledevice \"Fare\"\r\nrangebind \"Fare\" \"##x-axis\" 0 0 \"Axis1\"\r\n\"HardwareCursor\" \"0\"\r\n" + extra));
        }
    }

    public static class ConfigRepairTests
    {
        private static string Sha(byte[] b)
        {
            using (SHA256 s = SHA256.Create()) return Updater.ToHex(s.ComputeHash(b));
        }

        private static string Seed(byte[] b, string once)
        {
            return "{\"path\":\"config.xfs\",\"size\":" + b.Length + ",\"sha256\":\"" + Sha(b) + "\""
                + (once == null ? "" : ",\"replaceOnce\":\"" + once + "\"") + "}";
        }

        private static LauncherConfig Config(string gameDir, string baseUrl)
        {
            File.WriteAllText(Path.Combine(gameDir, LauncherConfig.FileName), "UpdateBaseUrl=" + baseUrl + "\r\n", new UTF8Encoding(false));
            return LauncherConfig.Load(gameDir, null);
        }

        /// <summary>A game folder with config.xfs = local (null = none), a server whose seed is {seed} (with
        /// "replaceOnce": once when not null), and an updater that sees the game as closed.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly string Dir = Runner.TempDir("cfg");
            public readonly TestHttpServer Srv = new TestHttpServer();
            public Updater Up;
            public Manifest M;
            public bool Running;

            public Rig(byte[] local, byte[] seed, string once)
            {
                if (local != null) File.WriteAllBytes(CfgPath, local);
                Serve(seed, once);
            }

            public void Serve(byte[] seed, string once)
            {
                Serve(seed, once, null);
            }

            /// <summary>bin != null also lists a required file Wolfteam.bin.</summary>
            public void Serve(byte[] seed, string once, byte[] bin)
            {
                Srv.Files["/pub/config.xfs"] = seed;
                string files = "";
                if (bin != null)
                {
                    Srv.Files["/pub/Wolfteam.bin"] = bin;
                    files = "{\"path\":\"Wolfteam.bin\",\"size\":" + bin.Length + ",\"sha256\":\"" + Sha(bin) + "\"}";
                }
                Up = new Updater(Config(Dir, Srv.BaseUrl));
                Up.RetryDelayMs = 10;
                Up.GameRunning = delegate { return Running; };
                M = Updater.ParseManifest("{\"version\":\"1\",\"files\":[" + files + "],\"seeds\":[" + Seed(seed, once) + "]}", Dir, null);
            }

            public string CfgPath { get { return Path.Combine(Dir, "config.xfs"); } }

            public byte[] Cfg { get { return File.ReadAllBytes(CfgPath); } }

            public string[] Backups { get { return Directory.GetFiles(Dir, "config.xfs.bak-*"); } }

            public int Update()
            {
                List<ManifestEntry> todo = Up.Plan(M);
                Up.Apply(todo);
                return todo.Count;
            }

            public string Log
            {
                get
                {
                    string p = Path.Combine(BootLayout.LauncherDir(Dir), "launcher.log");
                    return File.Exists(p) ? File.ReadAllText(p) : "";
                }
            }

            public void Dispose()
            {
                Srv.Dispose();
                Runner.DeleteTree(Dir);
            }
        }

        private static string Dump(Dictionary<string, byte[]> files)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, byte[]> f in files) parts.Add(f.Key + "=" + Sha(f.Value));
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts.ToArray());
        }

        // ------------------------------------------------------------------ reader

        public static void TestReaderReadsTheArchiveFormat()
        {
            string big = new string('x', 70000) + "\r\nAddAction Jump 5\r\n"; // two chunks: one full, one short
            byte[] a = XfsWriter.Archive(XfsWriter.F("display.cfg", "\"A\" \"1\""), XfsWriter.F("autoexec.cfg", big));
            Dictionary<string, byte[]> files = XfsReader.ReadAll(a);
            Assert.True(files != null, "parses");
            Assert.Equal(2, files.Count, "two files");
            Assert.Equal(big, Encoding.ASCII.GetString(files["AUTOEXEC.CFG"]), "multi-chunk file, any case");
            Assert.Equal(ConfigState.Good, ConfigRepair.InspectBytes(a), "AddAction at the end of the second chunk");
            Assert.Equal(ConfigState.Good, ConfigRepair.InspectBytes(XfsWriter.Good("")), "game-written config");
            Assert.Equal(ConfigState.BrokenInput, ConfigRepair.InspectBytes(XfsWriter.Bad("")), "the bad seed");
        }

        public static void TestReaderRefusesWhatDoesNotParse()
        {
            byte[] good = XfsWriter.Good("");
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(new byte[0]), "empty");
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(Encoding.ASCII.GetBytes("the player's own settings")), "text");
            byte[] cut = new byte[good.Length - 10];
            Buffer.BlockCopy(good, 0, cut, 0, cut.Length);
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(cut), "truncated");
            for (int i = 6; i < good.Length; i += 7)
            {
                byte[] flip = (byte[])good.Clone();
                flip[i] ^= 0x55;
                // A damaged byte must never turn into BrokenInput (that would replace the file).
                Assert.True(ConfigRepair.InspectBytes(flip) != ConfigState.BrokenInput, "flipped byte " + i);
            }
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(XfsWriter.Archive(XfsWriter.F("display.cfg", "x"))),
                "no autoexec.cfg");
        }

        public static void TestDamagedActionlessArchivesAreNeverBrokenInput()
        {
            // Codex gate 1.6 #1: only a whole, consistent archive may authorize a replace.
            byte[] bad = XfsWriter.Bad("");
            Assert.Equal(ConfigState.BrokenInput, ConfigRepair.InspectBytes(bad), "the intact bad seed");
            string original = Dump(XfsReader.ReadAll(bad));
            int accepted = 0;
            string where = "";
            for (int i = 0; i < bad.Length; i++)
            {
                foreach (byte mask in new byte[] { 0x01, 0x55, 0x80 })
                {
                    byte[] flip = (byte[])bad.Clone();
                    flip[i] ^= mask;
                    if (ConfigRepair.InspectBytes(flip) == ConfigState.Unreadable) continue;
                    // Only a change the format ignores (e.g. unused padding bits of a deflate stream) may pass, and
                    // then every file must decode to exactly the original bytes (Adler-32 and word sums held).
                    Assert.Equal(original, Dump(XfsReader.ReadAll(flip)), "byte " + i + " ^ " + mask + " changed the content");
                    accepted++;
                    where += " " + i + "^" + mask;
                }
            }
            // What may pass is the deflate stream's own slack (.NET reads a stream whose BFINAL bit was cleared up to the
            // end of its record, unused padding bits): at most two bits per zlib record (4 records here).
            Assert.True(accepted <= 8, "almost every damaged byte is refused (" + accepted + " ignored:" + where + " of " + bad.Length + ")");
            for (int cut = 1; cut < bad.Length; cut++)
            {
                byte[] shorter = new byte[bad.Length - cut];
                Buffer.BlockCopy(bad, 0, shorter, 0, shorter.Length);
                Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(shorter), "cut " + cut);
            }
            byte[] longer = new byte[bad.Length + 1];
            Buffer.BlockCopy(bad, 0, longer, 0, bad.Length);
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(longer), "trailing byte");
            KeyValuePair<string, byte[]> auto = XfsWriter.F("autoexec.cfg", "enabledevice \"Fare\"\r\n");
            KeyValuePair<string, byte[]> disp = XfsWriter.F("display.cfg", "\"A\" \"1\"\r\n");
            Assert.Equal(ConfigState.BrokenInput, ConfigRepair.InspectBytes(XfsWriter.Archive(2, 2, disp, auto)), "consistent");
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(XfsWriter.Archive(3, 2, disp, auto)), "a declared child missing");
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(XfsWriter.Archive(1, 2, disp, auto)), "an entry past the tree");
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(XfsWriter.Archive(2, 3, disp, auto)), "entry count");
            Assert.Equal(ConfigState.Unreadable, ConfigRepair.InspectBytes(XfsWriter.Archive(2, 2, auto, auto)), "duplicate path");
        }

        // ------------------------------------------------------------------ rule 1: broken input config

        public static void TestBrokenConfigIsBackedUpAndReplaced()
        {
            byte[] bad = XfsWriter.Bad(""), seed = XfsWriter.Good("");
            using (Rig r = new Rig(bad, seed, null))
            {
                Assert.Equal(1, r.Update(), "planned");
                Assert.Equal(Sha(seed), Sha(r.Cfg), "replaced by the seed");
                Assert.Equal(1, r.Backups.Length, "one backup");
                Assert.Equal(Sha(bad), Sha(File.ReadAllBytes(r.Backups[0])), "backup holds the old file");
                Assert.True(r.Up.ConfigReplaced, "ConfigReplaced");
                Assert.True(r.Log.Contains("config.xfs replaced") && r.Log.Contains("no AddAction"), "logged");
                Assert.False(File.Exists(r.CfgPath + ".part"), "no staging file left");
                Assert.False(Updater.IsIncomplete(r.Dir), "update complete");
                File.WriteAllBytes(r.CfgPath, XfsWriter.Good("\"MouseSensitivity\" \"3\"\r\n")); // the game writes it back
                Assert.Equal(0, r.Up.Plan(r.M).Count, "a working config afterwards is left alone");
            }
        }

        public static void TestWorkingConfigIsKept()
        {
            byte[] mine = XfsWriter.Good("rangebind \"##keyboard\" \"##q\" 0 0 \"Fire\"\r\n");
            using (Rig r = new Rig(mine, XfsWriter.Good(""), null))
            {
                Assert.Equal(0, r.Update(), "nothing planned");
                Assert.Equal(Sha(mine), Sha(r.Cfg), "player's settings kept");
                Assert.Equal(0, r.Backups.Length, "no backup");
            }
        }

        public static void TestUnparsableConfigIsKept()
        {
            byte[] junk = Encoding.ASCII.GetBytes("not an archive at all");
            byte[] cut = XfsWriter.Bad("");
            Array.Resize(ref cut, cut.Length - 3);
            foreach (byte[] local in new byte[][] { junk, cut })
            {
                using (Rig r = new Rig(local, XfsWriter.Good(""), null))
                {
                    Assert.Equal(0, r.Update(), "nothing planned");
                    Assert.Equal(Sha(local), Sha(r.Cfg), "unparsable file untouched");
                    Assert.Equal(0, r.Backups.Length, "no backup");
                }
            }
        }

        public static void TestGameRunningSkipsTheRepair()
        {
            byte[] bad = XfsWriter.Bad("");
            using (Rig r = new Rig(bad, XfsWriter.Good(""), null))
            {
                r.Running = true;
                Assert.Equal(0, r.Update(), "not planned while the game runs");
                Assert.Equal(Sha(bad), Sha(r.Cfg), "untouched");
                Assert.True(r.Log.Contains("the game is running"), "logged");
                // The game starts between the check and the download: the file still stays.
                r.Running = false;
                List<ManifestEntry> todo = r.Up.Plan(r.M);
                Assert.Equal(1, todo.Count, "planned once the game is closed");
                r.Running = true;
                r.Up.Apply(todo);
                Assert.Equal(Sha(bad), Sha(r.Cfg), "not replaced: the game started meanwhile");
                Assert.Equal(0, r.Backups.Length, "no backup");
                Assert.False(r.Up.ConfigReplaced, "ConfigReplaced stays false");
                Assert.False(File.Exists(r.CfgPath + ".part"), "staged copy dropped");
                r.Running = false;
                Assert.Equal(1, r.Update(), "next start");
                Assert.True(r.Up.ConfigReplaced, "replaced on the next start");
            }
        }

        public static void TestAConfigTheGameFixedAfterTheCheckIsKept()
        {
            // Codex gate 1.6 #2: the game ran and wrote a working config between Plan and the replace.
            using (Rig r = new Rig(XfsWriter.Bad(""), XfsWriter.Good(""), null))
            {
                List<ManifestEntry> todo = r.Up.Plan(r.M);
                Assert.Equal(1, todo.Count, "planned");
                byte[] fixedByGame = XfsWriter.Good("\"GammaR\" \"2\"\r\n");
                File.WriteAllBytes(r.CfgPath, fixedByGame);
                r.Up.Apply(todo);
                Assert.Equal(Sha(fixedByGame), Sha(r.Cfg), "the working config stays");
                Assert.Equal(0, r.Backups.Length, "no backup");
                Assert.True(r.Log.Contains("changed since the check"), "logged");
            }
        }

        public static void TestTheGameStartWaitsForAnUpdateThatHoldsTheLock()
        {
            string dir = Runner.TempDir("lock");
            try
            {
                FileStream held;
                Directory.CreateDirectory(BootLayout.LauncherDir(dir));
                using (new FileStream(BootLayout.UpdateLockPath(dir), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.False(Updater.TryHoldUpdateLock(dir, out held), "another launcher is updating");
                    Assert.True(held == null, "nothing held");
                }
                Assert.True(Updater.TryHoldUpdateLock(dir, out held), "free again");
                Assert.True(held != null, "held while the game starts");
                // While the start holds it, an update waits (Updater.WithUpdateLock is the same file, FileShare.None).
                FileStream second;
                Assert.False(Updater.TryHoldUpdateLock(dir, out second), "exclusive");
                held.Dispose();
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestTheGameStartUsesAReadOnlyLockFileLikeTheUpdater()
        {
            // Codex gate 1.6 round 2 #1: a read-only update.lock still excludes the game start and the update.
            string dir = Runner.TempDir("rolock");
            int wait = Updater.LockWaitMs, retry = Updater.LockRetryMs;
            try
            {
                string path = BootLayout.UpdateLockPath(dir);
                Directory.CreateDirectory(BootLayout.LauncherDir(dir));
                File.WriteAllText(path, "");
                File.SetAttributes(path, FileAttributes.ReadOnly);
                FileStream held;
                Assert.True(Updater.TryHoldUpdateLock(dir, out held), "free read-only lock");
                Assert.True(held != null, "the start holds a real handle");
                Updater.LockWaitMs = 300;
                Updater.LockRetryMs = 50;
                bool ran = false;
                // Launch first: an update must wait (and here time out) instead of replacing anything.
                Assert.Throws<UpdateException>(delegate { Updater.WithUpdateLock(dir, null, delegate { ran = true; }); }, "update excluded");
                Assert.False(ran, "update body did not run");
                held.Dispose();
                // Update first: the start is refused while the update holds it.
                Updater.WithUpdateLock(dir, null, delegate
                {
                    FileStream h2;
                    Assert.False(Updater.TryHoldUpdateLock(dir, out h2), "start excluded");
                    ran = true;
                });
                Assert.True(ran, "update ran");
            }
            finally
            {
                Updater.LockWaitMs = wait;
                Updater.LockRetryMs = retry;
                try { File.SetAttributes(BootLayout.UpdateLockPath(dir), FileAttributes.Normal); } catch (IOException) { }
                Runner.DeleteTree(dir);
            }
        }

        public static void TestAReplaceThatLeftTheOldFileUnderTheBackupNameIsUndone()
        {
            // ReplaceFile's ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the old file was renamed to the backup name and the new
            // one was not moved in. The old file comes back, the backup stays, the id is put back.
            byte[] mine = XfsWriter.Good("z\r\n");
            using (Rig r = new Rig(mine, XfsWriter.Good(""), "id1"))
            {
                r.Up.ReplaceFile = delegate(string part, string target, string backup)
                {
                    File.Move(target, backup);
                    throw new IOException("unable to move replacement");
                };
                r.Update();
                Assert.Equal(Sha(mine), Sha(r.Cfg), "old file restored");
                Assert.Equal(1, r.Backups.Length, "the backup stays");
                Assert.Equal(Sha(mine), Sha(File.ReadAllBytes(r.Backups[0])), "backup content");
                Assert.True(SeedOnceState.Get(r.Dir, "config.xfs") == null, "id put back");
                Assert.False(r.Up.ConfigReplaced, "not replaced");
                Assert.False(File.Exists(r.CfgPath + ".part"), "staged copy dropped");
            }
        }

        public static void TestReplaceOnceNeedsAStoredIdFirst()
        {
            // Codex gate 1.6 #3: without a stored id every start would reset the player's settings again.
            byte[] mine = XfsWriter.Good("z\r\n");
            using (Rig r = new Rig(mine, XfsWriter.Good(""), "id1"))
            {
                Directory.CreateDirectory(SeedOnceState.FilePath(r.Dir)); // the state file cannot be written
                try
                {
                    r.Update();
                    Assert.Equal(Sha(mine), Sha(r.Cfg), "not replaced");
                    Assert.Equal(0, r.Backups.Length, "no backup");
                    Assert.True(r.Log.Contains("cannot be stored"), "logged");
                    Assert.False(Updater.IsIncomplete(r.Dir), "the update itself finished");
                }
                finally { Directory.Delete(SeedOnceState.FilePath(r.Dir)); }
            }
        }

        public static void TestAFailedReplaceKeepsTheFileAndTheOldId()
        {
            byte[] mine = XfsWriter.Good("z\r\n");
            using (Rig r = new Rig(mine, XfsWriter.Good(""), "id2"))
            {
                Assert.True(SeedOnceState.Set(r.Dir, "config.xfs", "id1"), "an earlier id");
                List<ManifestEntry> todo = r.Up.Plan(r.M);
                Assert.Equal(1, todo.Count, "planned");
                using (new FileStream(r.CfgPath, FileMode.Open, FileAccess.Read, FileShare.Read)) // no delete/rename
                    r.Up.Apply(todo);
                Assert.Equal(Sha(mine), Sha(r.Cfg), "file intact");
                Assert.Equal("id1", SeedOnceState.Get(r.Dir, "config.xfs"), "the old id is back");
                Assert.False(r.Up.ConfigReplaced, "not replaced");
                Assert.False(File.Exists(r.CfgPath + ".part"), "staged copy dropped");
                Assert.Equal(1, r.Update(), "tried again on the next start");
                Assert.Equal("id2", SeedOnceState.Get(r.Dir, "config.xfs"), "then recorded");
            }
        }

        public static void TestAFailedOptionalDownloadDoesNotBlockTheUpdate()
        {
            // Codex gate 1.6 #4: HTTP error and corrupt download of the replacement; the required file still updates.
            byte[] bin = Encoding.ASCII.GetBytes("game exe v2");
            foreach (string mode in new string[] { "404", "corrupt" })
            {
                byte[] mine = XfsWriter.Good("z\r\n");
                using (Rig r = new Rig(mine, XfsWriter.Good(""), "id1"))
                {
                    r.Serve(XfsWriter.Good(""), "id1", bin);
                    if (mode == "404") r.Srv.Status["/pub/config.xfs"] = 404;
                    else r.Srv.Corrupt["/pub/config.xfs"] = XfsWriter.Good("tampered\r\n");
                    Assert.Equal(2, r.Update(), mode + ": planned both");
                    Assert.Equal(Sha(bin), Sha(File.ReadAllBytes(Path.Combine(r.Dir, "Wolfteam.bin"))), mode + ": required file updated");
                    Assert.Equal(Sha(mine), Sha(r.Cfg), mode + ": config kept");
                    Assert.True(SeedOnceState.Get(r.Dir, "config.xfs") == null, mode + ": id not recorded");
                    Assert.False(Updater.IsIncomplete(r.Dir), mode + ": update complete");
                    Assert.True(r.Log.Contains("download failed"), mode + ": logged");
                    Assert.False(File.Exists(r.CfgPath + ".part"), mode + ": no staging file");
                }
            }
        }

        public static void TestABrokenReleaseSeedNeverReplacesAPlayersFile()
        {
            byte[] mine = XfsWriter.Bad("\"GammaR\" \"1\"\r\n");
            using (Rig r = new Rig(mine, XfsWriter.Bad(""), "id1"))
            {
                r.Update();
                Assert.Equal(Sha(mine), Sha(r.Cfg), "kept: the release copy would not help");
                Assert.Equal(0, r.Backups.Length, "no backup");
                Assert.True(r.Log.Contains("not a usable config"), "logged");
            }
        }

        public static void TestBackupsAreCappedAtTwo()
        {
            using (Rig r = new Rig(XfsWriter.Bad(""), XfsWriter.Good(""), null))
            {
                foreach (string old in new string[] { "20260101-000000", "20260102-000000", "20260103-000000" })
                    File.WriteAllText(r.CfgPath + ".bak-" + old, old);
                r.Update();
                string[] left = r.Backups;
                Array.Sort(left, StringComparer.Ordinal);
                Assert.Equal(2, left.Length, "two backups kept");
                Assert.True(left[0].EndsWith(".bak-20260103-000000"), "the newest older one stays");
                Assert.True(left[1].CompareTo(r.CfgPath + ".bak-2026") > 0 && !left[1].EndsWith("20260103-000000"), "the new backup stays");
            }
        }

        // ------------------------------------------------------------------ rule 2: replace once per id

        public static void TestReplaceOnceReplacesAWorkingConfigOnceThenNeverUntilTheIdChanges()
        {
            byte[] mine = XfsWriter.Good("\"MouseSensitivity\" \"9\"\r\n"), seed = XfsWriter.Good("");
            using (Rig r = new Rig(mine, seed, "id1"))
            {
                Assert.Equal(1, r.Update(), "planned once");
                Assert.Equal(Sha(seed), Sha(r.Cfg), "replaced");
                Assert.Equal(Sha(mine), Sha(File.ReadAllBytes(r.Backups[0])), "backup");
                Assert.True(r.Log.Contains("replace once id1"), "logged with the id");
                byte[] later = XfsWriter.Good("\"MouseSensitivity\" \"4\"\r\n");
                File.WriteAllBytes(r.CfgPath, later); // the player changes settings
                Assert.Equal(0, r.Update(), "same id: never again");
                Assert.Equal(Sha(later), Sha(r.Cfg), "player's new settings kept");
                r.Serve(seed, "id2");
                Assert.Equal(1, r.Update(), "a new id replaces once more");
                Assert.Equal(Sha(seed), Sha(r.Cfg), "replaced for id2");
                Assert.Equal(2, r.Backups.Length, "two backups");
            }
        }

        public static void TestReplaceOnceWaitsForTheGameToClose()
        {
            byte[] mine = XfsWriter.Good("x\r\n");
            using (Rig r = new Rig(mine, XfsWriter.Good(""), "id1"))
            {
                r.Running = true;
                Assert.Equal(0, r.Update(), "skipped while running");
                Assert.Equal(Sha(mine), Sha(r.Cfg), "untouched");
                r.Running = false;
                Assert.Equal(1, r.Update(), "id not recorded while skipped: done on the next start");
            }
        }

        public static void TestReplaceOnceIsRecordedForAFreshDownloadAndAnIdenticalCopy()
        {
            byte[] seed = XfsWriter.Good("");
            using (Rig r = new Rig(null, seed, "id1"))
            {
                Assert.Equal(1, r.Update(), "missing seed downloaded");
                Assert.Equal(Sha(seed), Sha(r.Cfg), "fresh copy");
                File.WriteAllBytes(r.CfgPath, XfsWriter.Good("y\r\n"));
                Assert.Equal(0, r.Update(), "a fresh install already has this id's defaults");
            }
            using (Rig r = new Rig(seed, seed, "id1"))
            {
                Assert.Equal(0, r.Update(), "identical copy: nothing to download");
                File.WriteAllBytes(r.CfgPath, XfsWriter.Good("y\r\n"));
                Assert.Equal(0, r.Update(), "id recorded for the identical copy");
                Assert.Equal(0, r.Backups.Length, "no backup");
            }
        }

        public static void TestReplaceOnceInTheManifest()
        {
            string dir = Runner.TempDir("once");
            try
            {
                byte[] b = Encoding.ASCII.GetBytes("abc");
                string sha = Sha(b);
                Func<string, string> seedWith = delegate(string extra)
                {
                    return "{\"files\":[],\"seeds\":[{\"path\":\"config.xfs\",\"size\":3,\"sha256\":\"" + sha + "\"" + extra + "}]}";
                };
                Assert.Equal("2026-10-01-input", Updater.ParseManifest(seedWith(",\"replaceOnce\":\"2026-10-01-input\""), dir, null).Seeds[0].ReplaceOnce, "id");
                Assert.True(Updater.ParseManifest(seedWith(",\"replaceOnce\":null"), dir, null).Seeds[0].ReplaceOnce == null, "null = none");
                Assert.True(Updater.ParseManifest(seedWith(",\"unknownKey\":{\"a\":1}"), dir, null).Seeds.Count == 1, "unknown keys ignored");
                foreach (string bad in new string[] { ",\"replaceOnce\":\"\"", ",\"replaceOnce\":\"a b\"", ",\"replaceOnce\":\"..\\\\x/y\"",
                                                      ",\"replaceOnce\":1", ",\"replaceOnce\":\"" + new string('a', 65) + "\"" })
                    Assert.Throws<UpdateException>(delegate { Updater.ParseManifest(seedWith(bad), dir, null); }, "bad id " + bad);
                string onFile = "{\"files\":[{\"path\":\"a.dat\",\"size\":3,\"sha256\":\"" + sha + "\",\"replaceOnce\":\"x\"}]}";
                Assert.Throws<UpdateException>(delegate { Updater.ParseManifest(onFile, dir, null); }, "only seeds take it");
            }
            finally { Runner.DeleteTree(dir); }
        }
    }
}
