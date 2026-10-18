// Launcher tests: manifest seeds, fresh-install download against a local HTTP server, resume, corrupt downloads, the
// play-button gate and the progress texts. No network beyond 127.0.0.1; %APPDATA% is redirected to a temp folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WolfTeamDirilis.Tests
{
    internal static class LauncherTestMain
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--screenshots") return LauncherScreens.Render(args[1]);
            string appData = Runner.TempDir("appdata");
            UserSettings.DirOverride = appData;
            Tls.Enable();
            try
            {
                int a = Runner.Run(typeof(LauncherTests));
                int b = Runner.Run(typeof(BootstrapTests));
                int c = Runner.Run(typeof(ConfigRepairTests));
                return a != 0 || b != 0 || c != 0 ? 1 : 0;
            }
            finally { Runner.DeleteTree(appData); }
        }
    }

    public static class LauncherTests
    {
        private static string Sha(byte[] b)
        {
            using (SHA256 s = SHA256.Create()) return Updater.ToHex(s.ComputeHash(b));
        }

        private static string Entry(string path, byte[] b)
        {
            return "{\"path\":\"" + path.Replace("\\", "\\\\") + "\",\"size\":" + b.Length + ",\"sha256\":\"" + Sha(b) + "\"}";
        }

        private static byte[] Bytes(string s, int repeat)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < repeat; i++) sb.Append(s).Append(i);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static LauncherConfig Config(string gameDir, string baseUrl)
        {
            string ini = Path.Combine(gameDir, LauncherConfig.FileName);
            File.WriteAllText(ini, "UpdateBaseUrl=" + baseUrl + "\r\n", new UTF8Encoding(false));
            return LauncherConfig.Load(gameDir, null);
        }

        // ------------------------------------------------------------------ manifest

        public static void TestManifestReadsSeedsSeparately()
        {
            string dir = Runner.TempDir("m");
            try
            {
                string json = "{\"version\":\"1\",\"files\":[" + Entry("Wolfteam.bin", Bytes("a", 3)) + "],\"seeds\":["
                              + Entry("config.xfs", Bytes("c", 3)) + "," + Entry("Profiles\\Default.prf", Bytes("p", 2)) + "]}";
                Manifest m = Updater.ParseManifest(json, dir, Path.Combine(dir, "WolfTeamDirilis.exe"));
                Assert.Equal(1, m.Files.Count, "files");
                Assert.Equal(2, m.Seeds.Count, "seeds");
                Assert.True(m.Seeds[0].IsSeed && !m.Files[0].IsSeed, "IsSeed flags");
                Assert.Equal(Path.Combine(dir, "Profiles\\Default.prf"), m.Seeds[1].FullPath, "seed path");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestManifestWithoutSeedsStillParses()
        {
            string dir = Runner.TempDir("m");
            try
            {
                Manifest m = Updater.ParseManifest("{\"version\":\"1\",\"files\":[]}", dir, null);
                Assert.Equal(0, m.Seeds.Count, "no seeds");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestSeedThatIsAlsoAFileRejectsTheManifest()
        {
            string dir = Runner.TempDir("m");
            try
            {
                byte[] b = Bytes("x", 2);
                string json = "{\"files\":[" + Entry("config.xfs", b) + "],\"seeds\":[" + Entry("CONFIG.xfs", b) + "]}";
                Assert.Throws<UpdateException>(delegate { Updater.ParseManifest(json, dir, null); }, "duplicate");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestBadSeedPathRejectsTheManifest()
        {
            string dir = Runner.TempDir("m");
            try
            {
                string json = "{\"files\":[],\"seeds\":[" + Entry("..\\evil.dll", Bytes("x", 1)) + "]}";
                Assert.Throws<UpdateException>(delegate { Updater.ParseManifest(json, dir, null); }, "traversal");
            }
            finally { Runner.DeleteTree(dir); }
        }

        // ------------------------------------------------------------------ plan + apply (local server)

        public static void TestFreshInstallDownloadsEveryFileAndMissingSeedsThenIsUpToDate()
        {
            string dir = Runner.TempDir("fresh");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] bin = Bytes("game", 5000), xfs = Bytes("data", 20000), cfg = Bytes("cfg", 10), prf = Bytes("prf", 10);
                    srv.Files["/pub/Wolfteam.bin"] = bin;
                    srv.Files["/pub/rez/wolf.xfs"] = xfs;
                    srv.Files["/pub/config.xfs"] = cfg;
                    srv.Files["/pub/Profiles/Default.prf"] = prf;
                    string json = "{\"version\":\"7\",\"files\":[" + Entry("Wolfteam.bin", bin) + "," + Entry("rez\\wolf.xfs", xfs)
                                  + "],\"seeds\":[" + Entry("config.xfs", cfg) + "," + Entry("Profiles\\Default.prf", prf) + "]}";
                    srv.Files["/pub/manifest.json"] = Encoding.UTF8.GetBytes(json);
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    Manifest m = up.FetchManifest();
                    List<ManifestEntry> todo = up.Plan(m);
                    Assert.Equal(4, todo.Count, "everything is missing on a fresh install");
                    up.Apply(todo);
                    Assert.Equal(Sha(xfs), Sha(File.ReadAllBytes(Path.Combine(dir, "rez\\wolf.xfs"))), "wolf.xfs content");
                    Assert.Equal(Sha(prf), Sha(File.ReadAllBytes(Path.Combine(dir, "Profiles\\Default.prf"))), "seed content");
                    Assert.Equal(0, up.Plan(m).Count, "up to date afterwards");
                    Assert.False(Updater.IsIncomplete(dir), "incomplete marker cleared");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestSeedIsNeverOverwrittenOnceThePlayerHasIt()
        {
            string dir = Runner.TempDir("seed");
            try
            {
                File.WriteAllText(Path.Combine(dir, "config.xfs"), "the player's own settings");
                string json = "{\"files\":[],\"seeds\":[" + Entry("config.xfs", Bytes("default", 3)) + "]}";
                Updater up = new Updater(Config(dir, "http://127.0.0.1:9/"));
                Manifest m = Updater.ParseManifest(json, dir, null);
                Assert.Equal(0, up.Plan(m).Count, "an existing seed is left alone");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestDroppedConnectionResumesWithARangeRequest()
        {
            string dir = Runner.TempDir("resume");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] xfs = Bytes("resume", 30000);
                    srv.Files["/pub/wolf.xfs"] = xfs;
                    srv.DropAfter = 50000;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    Manifest m = Updater.ParseManifest("{\"files\":[" + Entry("wolf.xfs", xfs) + "]}", dir, null);
                    up.Apply(up.Plan(m));
                    Assert.Equal(Sha(xfs), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "content after resume");
                    bool ranged = false;
                    lock (srv.Log) foreach (string l in srv.Log) ranged |= l == "/pub/wolf.xfs range=50000";
                    Assert.True(ranged, "second request continued at byte 50000: " + string.Join(" | ", srv.Log));
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestCorruptDownloadFailsWithATurkishMessageAndKeepsTheMarker()
        {
            string dir = Runner.TempDir("corrupt");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] good = Bytes("good", 100);
                    byte[] bad = (byte[])good.Clone();
                    bad[5] ^= 1;
                    srv.Corrupt["/pub/Wolfteam.bin"] = bad;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    Manifest m = Updater.ParseManifest("{\"files\":[" + Entry("Wolfteam.bin", good) + "]}", dir, null);
                    UpdateException e = Assert.Throws<UpdateException>(delegate { up.Apply(up.Plan(m)); }, "corrupt");
                    Assert.True(e.Message.Contains("bozuk"), "message: " + e.Message);
                    Assert.False(e.Message.Contains("OYUNU BAŞLAT"), "retry is the Tekrar dene button now: " + e.Message);
                    Assert.False(File.Exists(Path.Combine(dir, "Wolfteam.bin")), "nothing unverified in place");
                    Assert.True(Updater.IsIncomplete(dir), "incomplete marker stays");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestProgressCarriesPerFileAndTotalBytes()
        {
            string dir = Runner.TempDir("progress");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] a = Bytes("aaaa", 40000), b = Bytes("bbbb", 40000);
                    srv.Files["/pub/a.dat"] = a;
                    srv.Files["/pub/b.dat"] = b;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    List<string> seen = new List<string>();
                    long lastTotal = -1;
                    bool monotonic = true, fileOk = true;
                    up.Progress = delegate(UpdateProgress p)
                    {
                        if (p.Checking) return;
                        monotonic &= p.Done >= lastTotal;
                        lastTotal = p.Done;
                        fileOk &= p.FileDone <= p.FileSize && p.FileSize > 0 && p.Total == a.Length + b.Length;
                        seen.Add(p.File);
                    };
                    Manifest m = Updater.ParseManifest("{\"files\":[" + Entry("a.dat", a) + "," + Entry("b.dat", b) + "]}", dir, null);
                    up.Apply(up.Plan(m));
                    Assert.True(monotonic, "total never goes back");
                    Assert.True(fileOk, "per-file bytes within the file size");
                    Assert.True(seen.Contains("b.dat"), "second file reported");
                    Assert.Equal((long)(a.Length + b.Length), lastTotal, "ends at the total");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestNoWriteAccessGivesAnInstallFolderHint()
        {
            string msg = Updater.WriteProblem("wolf.xfs", new UnauthorizedAccessException("denied"));
            Assert.True(msg.Contains("wolf.xfs") && msg.Contains("yazma izni"), msg);
            string other = Updater.WriteProblem("wolf.xfs", new IOException("in use"));
            Assert.True(other.Contains("Oyun açıksa"), other);
        }

        // ------------------------------------------------------------------ play-button gate

        public static void TestPlayIsEnabledOnlyAfterASuccessfulCheck()
        {
            Assert.True(LaunchGate.CanPlay(UpdateState.Done, true), "done");
            Assert.False(LaunchGate.CanPlay(UpdateState.Done, false), "done but no Wolfteam.bin");
            Assert.False(LaunchGate.CanPlay(UpdateState.Running, true), "while checking/downloading");
            Assert.False(LaunchGate.CanPlay(UpdateState.Failed, true), "after a failed download");
            Assert.False(LaunchGate.CanPlay(UpdateState.ManifestFailed, true), "after the list could not be fetched");
            Assert.True(LaunchGate.CanPlay(UpdateState.NotConfigured, true), "no update address (developer setup)");
        }

        public static void TestOfflinePlayWithTheLastVerifiedVersion()
        {
            Assert.True(LaunchGate.CanPlay(UpdateState.Offline, true), "offline, files match the last verified list");
            Assert.False(LaunchGate.CanPlay(UpdateState.Offline, false), "offline without Wolfteam.bin");
            Assert.True(LaunchGate.CanRetry(UpdateState.Offline), "the server can be tried again");
        }

        private static Manifest InstallFromServer(TestHttpServer srv, string dir, out Updater up, out byte[] bin)
        {
            bin = Bytes("game", 3000);
            byte[] cfg = Bytes("cfg", 10);
            srv.Files["/pub/Wolfteam.bin"] = bin;
            srv.Files["/pub/config.xfs"] = cfg;
            srv.Files["/pub/manifest.json"] = Encoding.UTF8.GetBytes("{\"version\":\"9\",\"files\":[" + Entry("Wolfteam.bin", bin)
                                                                     + "],\"seeds\":[" + Entry("config.xfs", cfg) + "]}");
            up = new Updater(Config(dir, srv.BaseUrl));
            up.RetryDelayMs = 10;
            Manifest m = up.FetchManifest();
            up.Apply(up.Plan(m));
            up.SaveVerified(m);
            return m;
        }

        public static void TestUnreachableServerFallsBackToTheLastVerifiedManifest()
        {
            string dir = Runner.TempDir("offline");
            try
            {
                Updater up;
                byte[] bin;
                using (TestHttpServer srv = new TestHttpServer()) InstallFromServer(srv, dir, out up, out bin);
                // the server is gone now (closed port)
                UpdateException e = Assert.Throws<UpdateException>(delegate { up.FetchManifest(); }, "no server");
                Assert.True(e.Unreachable && e.ManifestUnavailable, "unreachable");
                Assert.True(up.VerifyLastVerified(), "local files match the stored list");
                File.WriteAllText(Path.Combine(dir, "config.xfs"), "the player's settings"); // a seed may differ
                Assert.True(up.VerifyLastVerified(), "a changed seed does not matter");
                File.WriteAllText(Path.Combine(dir, "Wolfteam.bin"), "patched");
                Assert.False(up.VerifyLastVerified(), "a changed game file blocks offline play");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestNoStoredManifestMeansNoOfflinePlay()
        {
            string dir = Runner.TempDir("offline-none");
            try
            {
                File.WriteAllText(Path.Combine(dir, "Wolfteam.bin"), "x");
                Updater up = new Updater(Config(dir, "http://127.0.0.1:9/pub/"));
                Assert.False(up.VerifyLastVerified(), "nothing verified yet");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestOnlyNetworkErrorsAnd5xxCountAsUnreachable()
        {
            string dir = Runner.TempDir("status");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    srv.Status["/pub/manifest.json"] = 503;
                    Assert.True(Assert.Throws<UpdateException>(delegate { up.FetchManifest(); }, "503").Unreachable, "503");
                    srv.Status["/pub/manifest.json"] = 404;
                    Assert.False(Assert.Throws<UpdateException>(delegate { up.FetchManifest(); }, "404").Unreachable, "404 is a wrong address");
                    srv.Status.Clear();
                    srv.Files["/pub/manifest.json"] = Encoding.UTF8.GetBytes("{\"files\":[{\"path\":\"..\\\\x\"}]}");
                    Assert.False(Assert.Throws<UpdateException>(delegate { up.FetchManifest(); }, "bad").Unreachable, "invalid list");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        // ------------------------------------------------------------------ block updates (launcher 1.2)

        private const int BS = Updater.MinBlockSize;

        private static string BlockEntry(string path, byte[] b, int bs)
        {
            List<string> hashes = new List<string>();
            using (SHA256 s = SHA256.Create())
            {
                for (int off = 0; off < b.Length; off += bs)
                    hashes.Add("\"" + Updater.ToHex(s.ComputeHash(b, off, Math.Min(bs, b.Length - off))) + "\"");
            }
            string e = Entry(path, b);
            return e.Substring(0, e.Length - 1) + ",\"blocks\":{\"size\":" + bs + ",\"sha256\":[" + string.Join(",", hashes.ToArray()) + "]}}";
        }

        /// <summary>Deterministic bytes that differ in every block (so no two blocks hash alike).</summary>
        private static byte[] Data(int length, int seed)
        {
            byte[] b = new byte[length];
            new Random(seed).NextBytes(b);
            return b;
        }

        private static byte[] Changed(byte[] old, int newLength, params int[] blocks)
        {
            byte[] b = new byte[newLength];
            Array.Copy(old, b, Math.Min(old.Length, newLength));
            if (newLength > old.Length)
            {
                byte[] tail = Data(newLength - old.Length, 99);
                Array.Copy(tail, 0, b, old.Length, tail.Length);
            }
            foreach (int k in blocks) b[k * BS + 17] ^= 0x5A;
            return b;
        }

        private static List<string> RangeLog(TestHttpServer srv)
        {
            lock (srv.Log) return new List<string>(srv.Log);
        }

        public static void TestManifestBlocksAreParsedAndValidated()
        {
            string dir = Runner.TempDir("blocks");
            try
            {
                byte[] big = Data(3 * BS + 5, 1);
                Manifest m = Updater.ParseManifest("{\"files\":[" + BlockEntry("wolf.xfs", big, BS) + "]}", dir, null);
                Assert.Equal(BS, m.Files[0].BlockSize, "block size");
                Assert.Equal(4, m.Files[0].BlockHashes.Length, "one hash per block, the short last one included");
                string good = BlockEntry("wolf.xfs", big, BS);
                string[] bad =
                {
                    good.Replace("\"size\":" + BS, "\"size\":" + (BS / 2)),                  // block too small
                    good.Replace(",\"sha256\":[\"", ",\"sha256\":[\"g"),                    // not a hex hash
                    good.Replace("]}}", ",\"" + new string('a', 64) + "\"]}}"),             // one hash too many
                    good.Replace("\"blocks\":{", "\"blocks\":{\"x\":1,").Replace("\"sha256\":[", "\"sha\":["),
                    good.Replace("\"blocks\":{\"size\"", "\"blocks\":[{\"size\"").Replace("]}}", "]}]}"),
                };
                foreach (string b in bad)
                    Assert.Throws<UpdateException>(delegate { Updater.ParseManifest("{\"files\":[" + b + "]}", dir, null); }, b);
                // a seed and the launcher exe are never patched in place
                Manifest s = Updater.ParseManifest("{\"files\":[" + BlockEntry("WolfTeamDirilis.exe", big, BS) + "],\"seeds\":["
                                                   + BlockEntry("config.xfs", big, BS) + "]}", dir, Path.Combine(dir, "WolfTeamDirilis.exe"));
                Assert.True(s.Files[0].BlockHashes == null && s.Seeds[0].BlockHashes == null, "no blocks for exe/seed");
                Manifest l = Updater.ParseManifest("{\"files\":[" + BlockEntry("launcher/WolfTeamLauncher.exe", big, BS) + "]}", dir, null);
                Assert.True(l.Files[0].IsLauncher && l.Files[0].BlockHashes == null, "the launcher file is always replaced whole");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestOnlyChangedBlocksAreDownloadedOverOneConnection()
        {
            string dir = Runner.TempDir("delta");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(10 * BS + 300, 2);
                    byte[] v2 = Changed(v1, 11 * BS + 700, 2, 3, 7);   // blocks 2, 3, 7 edited, grown by 1.x blocks
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs"), v1);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    long total = -1, last = -1;
                    bool monotonic = true;
                    up.Progress = delegate(UpdateProgress p)
                    {
                        if (p.Checking) return;
                        total = p.Total;
                        monotonic &= p.Done >= last;
                        last = p.Done;
                    };
                    Manifest m = Updater.ParseManifest("{\"files\":[" + BlockEntry("wolf.xfs", v2, BS) + "]}", dir, null);
                    List<ManifestEntry> todo = up.Plan(m);
                    Assert.Equal(1, todo.Count, "wolf.xfs differs");
                    up.Apply(todo);
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "patched content");
                    // blocks 2, 3, 7, the old short block 10 and the new short block 11
                    long want = 2 * BS + BS + BS + (v2.Length - 11 * BS);
                    Assert.Equal(want, srv.BytesSent, "only the changed blocks were sent");
                    Assert.Equal(want, total, "the progress total is the real download size");
                    Assert.Equal(want, last, "progress ends at the total");
                    Assert.True(monotonic, "progress never goes back");
                    List<string> log = RangeLog(srv);
                    Assert.Equal("/pub/wolf.xfs range=" + (2 * BS) + "-" + (4 * BS - 1), log[0], "one request for the run 2..3");
                    Assert.Equal(3, log.Count, "three runs: 2-3, 7, 10-11: " + string.Join(" | ", log.ToArray()));
                    Assert.Equal(1, srv.Connections, "one keep-alive connection");
                    Assert.Equal(0, up.Plan(m).Count, "up to date afterwards (the hash cache knows the new file)");
                    Assert.False(Updater.IsIncomplete(dir), "marker cleared");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestAShorterFileIsCutToTheNewSize()
        {
            string dir = Runner.TempDir("shrink");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(8 * BS + 10, 3);
                    byte[] v2 = Changed(v1, 5 * BS + 40, 1);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs"), v1);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    Manifest m = Updater.ParseManifest("{\"files\":[" + BlockEntry("wolf.xfs", v2, BS) + "]}", dir, null);
                    up.Apply(up.Plan(m));
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "content");
                    Assert.Equal((long)(BS + 40), srv.BytesSent, "block 1 and the new short last block");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestABadBlockIsNeverWrittenAndTheNextStartFetchesOnlyWhatIsMissing()
        {
            string dir = Runner.TempDir("delta-resume");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(10 * BS, 4);
                    byte[] v2 = Changed(v1, v1.Length, 2, 7);
                    byte[] wrong = (byte[])v2.Clone();
                    wrong[7 * BS + 3] ^= 1;   // the server's block 7 does not match the list
                    string path = Path.Combine(dir, "wolf.xfs");
                    File.WriteAllBytes(path, v1);
                    srv.Corrupt["/pub/wolf.xfs"] = wrong;
                    string json = "{\"files\":[" + BlockEntry("wolf.xfs", v2, BS) + "]}";
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    Manifest m = Updater.ParseManifest(json, dir, null);
                    UpdateException e = Assert.Throws<UpdateException>(delegate { up.Apply(up.Plan(m)); }, "bad block");
                    Assert.True(e.Message.Contains("bozuk"), e.Message);
                    Assert.True(Updater.IsIncomplete(dir), "marker keeps the game closed");
                    byte[] now = File.ReadAllBytes(path);
                    Assert.Equal(v2[2 * BS + 17], now[2 * BS + 17], "block 2 was written");
                    Assert.Equal(v1[7 * BS + 3], now[7 * BS + 3], "the bad block 7 was not written");

                    // next start: the server is fixed; only block 7 is fetched
                    lock (srv.Files) srv.Corrupt.Clear();
                    srv.Files["/pub/wolf.xfs"] = v2;
                    lock (srv.Log) srv.Log.Clear();
                    Updater up2 = new Updater(Config(dir, srv.BaseUrl));
                    Manifest m2 = Updater.ParseManifest(json, dir, null);
                    up2.Apply(up2.Plan(m2));
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(path)), "content after the second start");
                    List<string> log = RangeLog(srv);
                    Assert.Equal(1, log.Count, "one request: " + string.Join(" | ", log.ToArray()));
                    Assert.Equal("/pub/wolf.xfs range=" + (7 * BS) + "-" + (8 * BS - 1), log[0], "only block 7");
                    Assert.False(Updater.IsIncomplete(dir), "marker cleared");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestADroppedBlockDownloadContinuesWithTheMissingBlocks()
        {
            string dir = Runner.TempDir("delta-drop");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(6 * BS, 5);
                    byte[] v2 = Changed(v1, v1.Length, 1, 2, 3);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs"), v1);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    srv.DropAfter = BS + 100;   // the run 1..3 breaks inside block 2
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    up.Apply(up.Plan(Updater.ParseManifest("{\"files\":[" + BlockEntry("wolf.xfs", v2, BS) + "]}", dir, null)));
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "content");
                    List<string> log = RangeLog(srv);
                    Assert.Equal("/pub/wolf.xfs range=" + (2 * BS) + "-" + (4 * BS - 1), log[1], "retry starts at block 2");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestServerWithoutRangeSupportFallsBackToAWholeDownload()
        {
            string dir = Runner.TempDir("norange");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(6 * BS, 6);
                    byte[] v2 = Changed(v1, v1.Length, 4);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs"), v1);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    srv.IgnoreRange = true;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    up.Apply(up.Plan(Updater.ParseManifest("{\"files\":[" + BlockEntry("wolf.xfs", v2, BS) + "]}", dir, null)));
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "content");
                    Assert.False(File.Exists(Path.Combine(dir, "wolf.xfs.part")), "no .part left");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestABlockListThatDoesNotFitTheFileFallsBackToAWholeDownload()
        {
            string dir = Runner.TempDir("badlist");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(6 * BS, 7);
                    byte[] v2 = Changed(v1, v1.Length, 1, 4);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs"), v1);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    // the list claims block 4 is unchanged (the old hash): the patched file cannot match the SHA-256
                    string listed = BlockEntry("wolf.xfs", v2, BS);
                    string oldBlock4, newBlock4;
                    using (SHA256 s = SHA256.Create())
                    {
                        oldBlock4 = Updater.ToHex(s.ComputeHash(v1, 4 * BS, BS));
                        newBlock4 = Updater.ToHex(s.ComputeHash(v2, 4 * BS, BS));
                    }
                    listed = listed.Replace(newBlock4, oldBlock4);
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    up.Apply(up.Plan(Updater.ParseManifest("{\"files\":[" + listed + "]}", dir, null)));
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "content after the fallback");
                    List<string> log = RangeLog(srv);
                    Assert.Equal("/pub/wolf.xfs", log[log.Count - 1], "the last request is the whole file");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestAnInterruptedFallbackDownloadResumesOnTheNextStart()
        {
            string dir = Runner.TempDir("fallback-resume");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(6 * BS, 9);
                    byte[] v2 = Changed(v1, v1.Length, 1, 4);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs"), v1);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    // an earlier start fell back to the whole download (the list does not fit the file) and was
                    // interrupted after 3 blocks: its .part must be resumed, not thrown away
                    byte[] part = new byte[3 * BS];
                    Array.Copy(v2, part, part.Length);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs.part"), part);
                    string listed = BlockEntry("wolf.xfs", v2, BS);
                    using (SHA256 s = SHA256.Create())
                        listed = listed.Replace(Updater.ToHex(s.ComputeHash(v2, 4 * BS, BS)), Updater.ToHex(s.ComputeHash(v1, 4 * BS, BS)));
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.RetryDelayMs = 10;
                    up.Apply(up.Plan(Updater.ParseManifest("{\"files\":[" + listed + "]}", dir, null)));
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "content");
                    List<string> log = RangeLog(srv);
                    Assert.Equal("/pub/wolf.xfs range=" + (3 * BS), log[log.Count - 1], "the whole download resumed at the .part: "
                                 + string.Join(" | ", log.ToArray()));
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestASuccessfulBlockUpdateRemovesAnOldPart()
        {
            string dir = Runner.TempDir("old-part");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(4 * BS, 10);
                    byte[] v2 = Changed(v1, v1.Length, 2);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs"), v1);
                    File.WriteAllBytes(Path.Combine(dir, "wolf.xfs.part"), new byte[BS]);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.Apply(up.Plan(Updater.ParseManifest("{\"files\":[" + BlockEntry("wolf.xfs", v2, BS) + "]}", dir, null)));
                    Assert.Equal(Sha(v2), Sha(File.ReadAllBytes(Path.Combine(dir, "wolf.xfs"))), "content");
                    Assert.Equal((long)BS, srv.BytesSent, "only block 2");
                    Assert.False(File.Exists(Path.Combine(dir, "wolf.xfs.part")), "old .part removed");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestAFileHeldOpenByTheGameIsNotPatched()
        {
            string dir = Runner.TempDir("locked");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] v1 = Data(4 * BS, 8);
                    byte[] v2 = Changed(v1, v1.Length, 0);
                    string path = Path.Combine(dir, "wolf.xfs");
                    File.WriteAllBytes(path, v1);
                    srv.Files["/pub/wolf.xfs"] = v2;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    Manifest m = Updater.ParseManifest("{\"files\":[" + BlockEntry("wolf.xfs", v2, BS) + "]}", dir, null);
                    List<ManifestEntry> todo = up.Plan(m);
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        UpdateException e = Assert.Throws<UpdateException>(delegate { up.Apply(todo); }, "locked");
                        Assert.True(e.Message.Contains("Oyun açıksa"), e.Message);
                    }
                    Assert.Equal(Sha(v1), Sha(File.ReadAllBytes(path)), "untouched");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestContentRangeHeaders()
        {
            long a, b, t;
            Assert.True(Updater.ParseContentRange("bytes 0-15/1780880805", out a, out b, out t) && a == 0 && b == 15 && t == 1780880805, "nginx");
            Assert.True(Updater.ParseContentRange("bytes 5-9/*", out a, out b, out t) && t == -1, "unknown total");
            Assert.False(Updater.ParseContentRange("bytes */100", out a, out b, out t), "416 form");
            Assert.False(Updater.ParseContentRange(null, out a, out b, out t), "missing");
            Assert.False(Updater.ParseContentRange("bytes 9-5/100", out a, out b, out t), "reversed");
        }

        public static void TestRetryIsOfferedAfterEveryFailure()
        {
            Assert.True(LaunchGate.CanRetry(UpdateState.Failed), "failed");
            Assert.True(LaunchGate.CanRetry(UpdateState.ManifestFailed), "manifest failed");
            Assert.False(LaunchGate.CanRetry(UpdateState.Running), "running");
            Assert.False(LaunchGate.CanRetry(UpdateState.Done), "done");
        }

        // ------------------------------------------------------------------ one-time music volume fix

        private const string Profile = "[Controls]\r\nmusicvolume = -2500\r\n\r\n[Sound]\r\nsound16bit = 1\r\nmusicvolume = -2500\r\npremusicvolume = 1700\r\n";

        public static void TestMutedMusicInTheSoundSectionIsRaisedToTheFactoryDefault()
        {
            bool changed;
            string fixedText = MusicFix.FixProfile(Profile, out changed);
            Assert.True(changed, "changed");
            Assert.Equal(Profile.Replace("sound16bit = 1\r\nmusicvolume = -2500", "sound16bit = 1\r\nmusicvolume = 1250"), fixedText,
                "only [Sound] musicvolume, line endings kept");
        }

        public static void TestAVolumeThePlayerChoseIsKept()
        {
            foreach (string v in new string[] { "-2499", "0", "1250", "5000", "-3000" })
            {
                bool changed;
                string t = "[Sound]\nmusicvolume = " + v + "\n";
                Assert.Equal(t, MusicFix.FixProfile(t, out changed), "value " + v);
                Assert.False(changed, "not changed " + v);
            }
        }

        public static void TestMigrationRunsOnceAndNotWhileTheGameRuns()
        {
            string dir = Runner.TempDir("music");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "Profiles"));
                string player = Path.Combine(dir, "Profiles\\Player.txt");
                File.WriteAllText(player, Profile, Encoding.GetEncoding(28591));
                Assert.Equal(MusicFixResult.GameRunning, MusicFix.Run(dir, true), "game running: later");
                Assert.True(File.ReadAllText(player).Contains("musicvolume = -2500\r\npremusic"), "untouched while running");
                Assert.Equal(MusicFixResult.Changed, MusicFix.Run(dir, false), "fixed");
                Assert.True(File.ReadAllText(player).Contains("[Sound]\r\nsound16bit = 1\r\nmusicvolume = 1250\r\n"), "file fixed");
                File.WriteAllText(player, Profile, Encoding.GetEncoding(28591)); // the player mutes the music again
                Assert.Equal(MusicFixResult.AlreadyDone, MusicFix.Run(dir, false), "only once");
                Assert.True(File.ReadAllText(player).Contains("musicvolume = -2500\r\npremusic"), "the player's later choice stays");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestMusicFixKeepsEveryOtherByteOfABomProfile()
        {
            // Codex review #4: File.ReadAllText honours a BOM even with an explicit encoding.
            string dir = Runner.TempDir("music-bom");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "Profiles"));
                byte[] head = { 0xEF, 0xBB, 0xBF };
                byte[] rest = Encoding.UTF8.GetBytes("[Sound]\r\nmusicvolume = -2500\r\nname = Ş\r\n");
                byte[] orig = new byte[head.Length + rest.Length];
                head.CopyTo(orig, 0);
                rest.CopyTo(orig, head.Length);
                string player = Path.Combine(dir, "Profiles\\Player.txt");
                File.WriteAllBytes(player, orig);
                Assert.Equal(MusicFixResult.Changed, MusicFix.Run(dir, false), "fixed");
                byte[] want = Encoding.UTF8.GetBytes("[Sound]\r\nmusicvolume = 1250\r\nname = Ş\r\n");
                byte[] got = File.ReadAllBytes(player);
                Assert.Equal(Convert.ToBase64String(head) + Convert.ToBase64String(want),
                    Convert.ToBase64String(new byte[] { got[0], got[1], got[2] }) + Convert.ToBase64String(got, 3, got.Length - 3), "bytes");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestSeedCreatedWhileDownloadingIsKept()
        {
            // Codex review #3: the game may write its own file between Plan and the end of the download.
            string dir = Runner.TempDir("seed-race");
            using (TestHttpServer srv = new TestHttpServer())
            {
                try
                {
                    byte[] def = Bytes("default", 50);
                    srv.Files["/pub/config.xfs"] = def;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    Manifest m = Updater.ParseManifest("{\"files\":[],\"seeds\":[" + Entry("config.xfs", def) + "]}", dir, null);
                    List<ManifestEntry> todo = up.Plan(m);
                    Assert.Equal(1, todo.Count, "missing seed planned");
                    File.WriteAllText(Path.Combine(dir, "config.xfs"), "written by the game meanwhile");
                    up.Apply(todo);
                    Assert.Equal("written by the game meanwhile", File.ReadAllText(Path.Combine(dir, "config.xfs")), "player's file kept");
                    Assert.False(File.Exists(Path.Combine(dir, "config.xfs.part")), "staged copy dropped");
                }
                finally { Runner.DeleteTree(dir); }
            }
        }

        public static void TestMigrationWithoutProfilesIsMarkedDone()
        {
            string dir = Runner.TempDir("music-empty");
            try
            {
                Assert.Equal(MusicFixResult.NothingToChange, MusicFix.Run(dir, false), "fresh install");
                Assert.Equal(MusicFixResult.AlreadyDone, MusicFix.Run(dir, false), "remembered");
            }
            finally { Runner.DeleteTree(dir); }
        }

        // ------------------------------------------------------------------ login answers (LauncherWebServer / AccountService)

        public static void TestServerAnswersAreShownInTurkish()
        {
            Assert.Equal("Kullanıcı adı veya şifre yanlış.", Net.TranslateServerError("Invalid login."), "invalid");
            Assert.Equal("Çok fazla giriş denemesi. Birkaç dakika bekleyip tekrar dene.",
                Net.TranslateServerError("Too many login attempts. Try again later."), "throttled");
            Assert.Equal("Sunucu şu an yoğun. Biraz sonra tekrar dene.", Net.TranslateServerError("Server busy. Try again."), "busy");
            Assert.Equal("Hesabın yasaklı (bitiş: 2026-10-01T10:00 UTC: hile).",
                Net.TranslateServerError("Account banned until 2026-10-01T10:00 UTC: hile"), "banned");
        }

        // ------------------------------------------------------------------ texts

        public static void TestSizesAreTurkish()
        {
            Assert.Equal("0 MB", ProgressText.Size(0), "zero");
            Assert.Equal("540 MB", ProgressText.Size(540L << 20), "MB");
            Assert.Equal("3,2 MB", ProgressText.Size((long)(3.2 * (1 << 20))), "small MB with a decimal");
            Assert.Equal("0,5 MB", ProgressText.Size(1L << 19), "half a MB");
            Assert.Equal("1,9 GB", ProgressText.Size((long)(1.9 * (1L << 30))), "GB");
        }

        public static void TestRemainingTime()
        {
            Assert.Equal("", ProgressText.Eta(100, 0), "unknown speed");
            Assert.Equal("1 dk'dan az kaldı", ProgressText.Eta(10L << 20, 1 << 20), "10 s");
            Assert.Equal("yaklaşık 4 dk kaldı", ProgressText.Eta(240L << 20, 1 << 20), "240 s");
            Assert.Equal("yaklaşık 2 sa 5 dk kaldı", ProgressText.Eta(7500L << 20, 1 << 20), "7500 s");
        }

        // ------------------------------------------------------------------ launcher 1.4: bootstrap layout, HTTPS

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        public static void TestTheBootstrapIsNeverUpdatedByTheLauncher()
        {
            // The bootstrap runs while the launcher runs and is the recovery path for a broken launcher: only the setup
            // replaces it. The launcher itself (launcher\WolfTeamLauncher.exe) is an ordinary file.
            string dir = Runner.TempDir("boot-skip");
            try
            {
                byte[] boot = Bytes("bootstrap", 50), launcher = Bytes("launcher 1.4", 50);
                File.WriteAllText(Path.Combine(dir, "WolfTeamDirilis.exe"), "an older bootstrap");
                using (TestHttpServer srv = new TestHttpServer())
                {
                    srv.Files["/pub/WolfTeamDirilis.exe"] = boot;
                    srv.Files["/pub/launcher/WolfTeamLauncher.exe"] = launcher;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    Manifest m = Updater.ParseManifest("{\"files\":[" + Entry("WolfTeamDirilis.exe", boot) + ","
                        + Entry("launcher/WolfTeamLauncher.exe", launcher) + "]}", dir, BootLayout.BootstrapPath(dir));
                    Assert.True(m.Files[0].IsBootstrap && !m.Files[0].IsLauncher, "bootstrap entry");
                    Assert.True(m.Files[1].IsLauncher && !m.Files[1].IsBootstrap, "launcher entry");
                    List<ManifestEntry> todo = up.Plan(m);
                    Assert.Equal(1, todo.Count, "only the launcher");
                    Assert.True(todo[0].IsLauncher, "the launcher is planned");
                    up.Apply(todo);
                    Assert.Equal("an older bootstrap", File.ReadAllText(Path.Combine(dir, "WolfTeamDirilis.exe")), "bootstrap untouched");
                    Assert.Equal(Sha(launcher), Updater.HashFile(BootLayout.StagedLauncher(dir), null, null), "launcher file in place");
                    up.SaveVerified(m);
                    Assert.True(up.VerifyLastVerified(), "a different bootstrap does not block offline play");
                }
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestFilesAreFetchedWithTheirHashAndListsWithATimeInTheUrl()
        {
            // Security audit R5 goes through Cloudflare: a changed file must never come from a cache as the old copy.
            string dir = Runner.TempDir("cachebust");
            try
            {
                byte[] a = Bytes("new game file", 100);
                using (TestHttpServer srv = new TestHttpServer())
                {
                    string json = "{\"files\":[" + Entry("Wolfteam.bin", a) + "]}";
                    srv.Files["/pub/manifest.json"] = Encoding.UTF8.GetBytes(json);
                    srv.Files["/pub/Wolfteam.bin"] = a;
                    Updater up = new Updater(Config(dir, srv.BaseUrl));
                    up.Apply(up.Plan(up.FetchManifest()));
                    Assert.True(srv.Queries.Exists(delegate(string q) { return q.StartsWith("/pub/manifest.json?t="); }), "list with ?t=");
                    Assert.True(srv.Queries.Contains("/pub/Wolfteam.bin?h=" + Sha(a).Substring(0, 16)), "file with ?h=");
                }
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestTheInstallFolderOfALauncherVersion()
        {
            string i = "C:\\Oyunlar\\WolfTeam Diriliş";
            Assert.Equal(i, BootLayout.InstallDirOf(i + "\\launcher\\versions\\0123456789abcdef\\WolfTeamLauncher.exe"), "a version the bootstrap runs");
            Assert.Equal(i, BootLayout.InstallDirOf(i + "\\launcher\\WolfTeamLauncher.exe"), "the release file");
            Assert.Equal("C:\\dev\\build", BootLayout.InstallDirOf("C:\\dev\\build\\WolfTeamLauncher.exe"), "a developer build");
        }

        public static void TestHttpsEndpointsWinAndOnlyHttpsIsTakenFromTheEndpointsFile()
        {
            string dir = Runner.TempDir("endpoints");
            try
            {
                Tls.SetForTests(true);
                WriteText(Path.Combine(dir, LauncherConfig.FileName), "LoginUrl=http://1.2.3.4/api/login.php\r\n"
                    + "UpdateBaseUrl=http://1.2.3.4:8080/s/client/\r\nNewsUrl=http://1.2.3.4:8080/s/news.json\r\n");
                WriteText(BootLayout.EndpointsPath(dir), "LoginUrl=https://example.org/a/launcher/login\r\n"
                    + "UpdateBaseUrl=https://example.org/d/client/\r\nNewsUrl=http://evil.example/news.json\r\n");
                LauncherConfig c = LauncherConfig.Load(dir, null);
                Assert.Equal("https://example.org/a/launcher/login", c.LoginUrl, "https login");
                Assert.Equal("https://example.org/d/client/", c.UpdateBaseUrl, "https update");
                Assert.Equal("http://1.2.3.4:8080/s/news.json", c.NewsUrl, "an http value in endpoints.ini is ignored (no downgrade)");
                Assert.False(c.TlsFallbackUsed || c.InsecureLogin, "no fallback, https login");
            }
            finally { Tls.SetForTests(true); Runner.DeleteTree(dir); }
        }

        public static void TestServerHostFromEndpointsWinsOverTheIni()
        {
            string dir = Runner.TempDir("serverhost");
            try
            {
                WriteText(Path.Combine(dir, LauncherConfig.FileName), "ServerHost=10.1.2.3\r\n");
                Assert.Equal("10.1.2.3", LauncherConfig.Load(dir, null).ServerHost, "the ini's host without endpoints.ini");
                WriteText(BootLayout.EndpointsPath(dir), "﻿; addresses\r\nUpdateBaseUrl=https://example.org/d/client/\r\n");
                Assert.Equal("10.1.2.3", LauncherConfig.Load(dir, null).ServerHost, "an endpoints.ini without ServerHost keeps the ini's");
                WriteText(BootLayout.EndpointsPath(dir), "﻿ServerHost=game.example.org\r\nUpdateBaseUrl=https://example.org/d/client/\r\n");
                LauncherConfig c = LauncherConfig.Load(dir, null);
                Assert.Equal("game.example.org", c.ServerHost, "endpoints.ini wins (also as the first line after a BOM)");
                Assert.Equal(0, c.Warnings.Count + c.LogNotes.Count, "nothing to report");

                WriteText(BootLayout.EndpointsPath(dir), "ServerHost=not a host\r\n");
                c = LauncherConfig.Load(dir, null);
                Assert.Equal("10.1.2.3", c.ServerHost, "an invalid endpoints host is skipped");
                Assert.Equal(1, c.Warnings.Count, "and reported");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestTheRetiredServerAddressCountsAsNotSet()
        {
            // The server moved on 2026-10-02; setups up to 1.6 wrote ServerHost=45.76.145.60 into WolfTeamDirilis.ini and
            // the status badge showed "Çevrimdışı" while the game worked. Those installs heal without a new file.
            string dir = Runner.TempDir("retired");
            try
            {
                Assert.Equal("45.76.145.60", LauncherConfig.RetiredServerHost, "the retired address");
                Assert.Equal("188.40.176.195", LauncherConfig.DefaultServerHost, "the current address");
                WriteText(Path.Combine(dir, LauncherConfig.FileName), "ServerHost=45.76.145.60\r\n");
                LauncherConfig c = LauncherConfig.Load(dir, null);
                Assert.Equal(LauncherConfig.DefaultServerHost, c.ServerHost, "retired ini host -> the default");
                Assert.Equal(0, c.Warnings.Count, "the player is not bothered");
                Assert.Equal(1, c.LogNotes.Count, "a log note instead");
                Assert.True(c.LogNotes[0].Contains("retired") && c.LogNotes[0].Contains(LauncherConfig.FileName), "the note names the file");
                Assert.Equal("http://188.40.176.195/api/login.php", c.LoginUrl, "the derived http login follows the healed host");

                WriteText(BootLayout.EndpointsPath(dir), "ServerHost=game.example.org\r\n");
                Assert.Equal("game.example.org", LauncherConfig.Load(dir, null).ServerHost, "retired ini host + endpoints host");

                WriteText(Path.Combine(dir, LauncherConfig.FileName), "ServerHost=10.1.2.3\r\n");
                WriteText(BootLayout.EndpointsPath(dir), "ServerHost=45.76.145.60\r\n");
                c = LauncherConfig.Load(dir, null);
                Assert.Equal("10.1.2.3", c.ServerHost, "a retired endpoints host is skipped too");
                Assert.Equal(1, c.LogNotes.Count, "and noted");

                File.Delete(BootLayout.EndpointsPath(dir));
                File.Delete(Path.Combine(dir, LauncherConfig.FileName));
                c = LauncherConfig.Load(dir, null);
                Assert.Equal("188.40.176.195", c.ServerHost, "no settings at all -> the current address");
                Assert.Equal(0, c.Warnings.Count + c.LogNotes.Count, "nothing to report");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestTheVersionResourceMatchesTheVersion()
        {
            string[] p = Program.Version.Split('.');
            string four = Program.Version;
            for (int i = p.Length; i < 4; i++) four += ".0";
            Assert.Equal(four, Program.FileVersion, "Program.FileVersion is Program.Version in four parts");
        }

        public static void TestTwoUpdatesOfOneInstallNeverOverlap()
        {
            // Gate 1.4 #3: a second launcher (a second window, another Windows user, a launcher left over from a killed
            // bootstrap) waits for the first update's whole transaction before it plans its own.
            string dir = Runner.TempDir("updlock");
            try
            {
                byte[] a = Bytes("game file", 20000);
                using (TestHttpServer srv = new TestHttpServer())
                {
                    srv.Files["/pub/manifest.json"] = Encoding.UTF8.GetBytes("{\"files\":[" + Entry("Wolfteam.bin", a) + "]}");
                    srv.Files["/pub/Wolfteam.bin"] = a;
                    srv.DelayMs = 400;
                    DateTime aEnd = DateTime.MinValue, bStart = DateTime.MinValue;
                    bool bWaited = false, aDone = false;
                    System.Threading.ManualResetEvent aIn = new System.Threading.ManualResetEvent(false);
                    System.Threading.Thread first = new System.Threading.Thread(delegate()
                    {
                        Updater.WithUpdateLock(dir, null, delegate
                        {
                            aIn.Set();
                            Updater up = new Updater(Config(dir, srv.BaseUrl));
                            Manifest m = up.FetchManifest();
                            up.Apply(up.Plan(m));
                            up.SaveVerified(m);
                            aDone = true;
                            aEnd = DateTime.UtcNow;
                        });
                    });
                    first.Start();
                    Assert.True(aIn.WaitOne(10000), "first update holds the lock");
                    int planned = -1;
                    Updater.WithUpdateLock(dir, delegate { bWaited = true; }, delegate
                    {
                        bStart = DateTime.UtcNow;
                        Updater up = new Updater(Config(dir, srv.BaseUrl));
                        planned = up.Plan(up.FetchManifest()).Count;
                    });
                    first.Join(30000);
                    Assert.True(aDone && bWaited, "the second waited");
                    Assert.True(bStart >= aEnd, "it began after the first ended");
                    Assert.Equal(0, planned, "and found nothing left to do");
                }
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestTheUpdateLockFailsClosed()
        {
            // Gate 1.4 re-review #1/#4: a read-only lock file still locks; an unreadable one or a non-contention error
            // stops the update with a message (never an unlocked update, never an endless wait).
            string dir = Runner.TempDir("updlock-closed");
            try
            {
                string lockPath = BootLayout.UpdateLockPath(dir);
                Directory.CreateDirectory(BootLayout.LauncherDir(dir));
                File.WriteAllText(lockPath, "");
                File.SetAttributes(lockPath, FileAttributes.ReadOnly);
                bool ran = false, otherBlocked = false;
                Updater.WithUpdateLock(dir, null, delegate
                {
                    ran = true;
                    try { using (new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } }
                    catch (IOException) { otherBlocked = true; }
                });
                Assert.True(ran && otherBlocked, "read-only lock file: held exclusively");
                File.SetAttributes(lockPath, FileAttributes.Normal);

                System.Security.AccessControl.FileSecurity sec = File.GetAccessControl(lockPath);
                System.Security.AccessControl.FileSystemAccessRule deny = new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User,
                    System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.WriteData,
                    System.Security.AccessControl.AccessControlType.Deny);
                sec.AddAccessRule(deny);
                File.SetAccessControl(lockPath, sec);
                ran = false;
                try
                {
                    Assert.Throws<UpdateException>(delegate { Updater.WithUpdateLock(dir, null, delegate { ran = true; }); }, "denied lock: error");
                    Assert.False(ran, "no update without the lock");
                }
                finally
                {
                    sec = File.GetAccessControl(lockPath);
                    sec.RemoveAccessRule(deny);
                    File.SetAccessControl(lockPath, sec);
                }

                string other = Runner.TempDir("updlock-file");
                try
                {
                    File.WriteAllText(Path.Combine(other, "launcher"), "a file where the folder should be");
                    bool waited = false;
                    Assert.Throws<UpdateException>(delegate { Updater.WithUpdateLock(other, delegate { waited = true; }, delegate { ran = true; }); }, "disk error: error");
                    Assert.False(ran || waited, "no wait, no update");
                }
                finally { Runner.DeleteTree(other); }
                Assert.True(Updater.IsContention(new IOException("x", unchecked((int)0x80070020))), "sharing violation waits");
                Assert.True(Updater.IsContention(new IOException("x", unchecked((int)0x80070021))), "lock violation waits");
                Assert.False(Updater.IsContention(new IOException("x", unchecked((int)0x80070015))), "device not ready does not");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestAnUpdateWaitsForAnotherProcessAndGoesOnWhenItDies()
        {
            // Gate 1.4 re-review: the lock owner is another real process; it dies mid-transaction, Windows releases the
            // lock, the waiting update proceeds.
            string dir = Runner.TempDir("updlock-proc");
            System.Diagnostics.Process holder = null;
            try
            {
                string lockPath = BootLayout.UpdateLockPath(dir);
                Directory.CreateDirectory(BootLayout.LauncherDir(dir));
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"$f = [IO.File]::Open('" + lockPath.Replace("'", "''") + "', 'OpenOrCreate', 'ReadWrite', 'None'); [Console]::Out.WriteLine('held'); Start-Sleep 60\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                holder = System.Diagnostics.Process.Start(psi);
                Assert.Equal("held", holder.StandardOutput.ReadLine(), "the other process holds the lock");
                bool waited = false, ran = false;
                System.Threading.ManualResetEvent waiting = new System.Threading.ManualResetEvent(false);
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    Updater.WithUpdateLock(dir, delegate { waited = true; waiting.Set(); }, delegate { ran = true; });
                });
                t.Start();
                Assert.True(waiting.WaitOne(10000), "the update waits");
                System.Threading.Thread.Sleep(1200);
                Assert.False(ran, "and does not run while the other holds the lock");
                holder.Kill();
                holder.WaitForExit(10000);
                Assert.True(t.Join(10000), "the lock comes free when the owner dies");
                Assert.True(waited && ran, "then the update runs");
            }
            finally
            {
                if (holder != null && !holder.HasExited) holder.Kill();
                Runner.DeleteTree(dir);
            }
        }

        public static void TestAHeldUpdateLockIsWaitedForOnlySoLong()
        {
            // Gate 1.4b #3: a hung or suspended holder ends the wait with Tekrar dene; the lock is never bypassed.
            string dir = Runner.TempDir("updlock-bounded");
            int before = Updater.LockWaitMs;
            Updater.LockWaitMs = 1200;
            try
            {
                Directory.CreateDirectory(BootLayout.LauncherDir(dir));
                using (new FileStream(BootLayout.UpdateLockPath(dir), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    bool ran = false, waited = false;
                    UpdateException e = Assert.Throws<UpdateException>(delegate
                    {
                        Updater.WithUpdateLock(dir, delegate { waited = true; }, delegate { ran = true; });
                    }, "bounded");
                    Assert.True(waited && !ran, "waited, never ran without the lock");
                    Assert.Equal(Updater.LockTimeoutText, e.Message, "Tekrar dene message");
                }
            }
            finally
            {
                Updater.LockWaitMs = before;
                Runner.DeleteTree(dir);
            }
        }

        public static void TestAnHttpLoginAddressAlwaysNeedsConsent()
        {
            // Gate 1.4 #1: an endpoints file with only an https update address leaves the login on http even with TLS 1.2.
            string dir = Runner.TempDir("httplogin");
            try
            {
                Tls.SetForTests(true);
                WriteText(Path.Combine(dir, LauncherConfig.FileName), "LoginUrl=http://1.2.3.4/api/login.php\r\n");
                WriteText(BootLayout.EndpointsPath(dir), "UpdateBaseUrl=https://example.org/d/client/\r\n");
                LauncherConfig c = LauncherConfig.Load(dir, null);
                Assert.Equal("http://1.2.3.4/api/login.php", c.LoginUrl, "login stays http");
                Assert.False(c.TlsFallbackUsed, "not a TLS fallback");
                Assert.True(c.InsecureLogin, "but the player is asked before the password is sent");
                WriteText(Path.Combine(dir, LauncherConfig.FileName), "LoginUrl=http://127.0.0.1:9/api/login.php\r\n");
                Assert.False(LauncherConfig.Load(dir, null).InsecureLogin, "this PC (tests) needs no consent");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestWithoutTlsLoginAndNewsFallBackButTheUpdateNever()
        {
            string dir = Runner.TempDir("notls");
            try
            {
                WriteText(Path.Combine(dir, LauncherConfig.FileName), "LoginUrl=http://1.2.3.4/api/login.php\r\n"
                    + "UpdateBaseUrl=http://1.2.3.4:8080/s/client/\r\nNewsUrl=http://1.2.3.4:8080/s/news.json\r\n");
                WriteText(BootLayout.EndpointsPath(dir), "LoginUrl=https://example.org/a/launcher/login\r\n"
                    + "UpdateBaseUrl=https://example.org/d/client/\r\nNewsUrl=https://example.org/d/news.json\r\n");
                Tls.SetForTests(false);
                LauncherConfig c = LauncherConfig.Load(dir, null);
                Assert.Equal("http://1.2.3.4/api/login.php", c.LoginUrl, "login falls back (the player is told)");
                Assert.Equal("http://1.2.3.4:8080/s/news.json", c.NewsUrl, "news falls back");
                Assert.True(c.TlsFallbackUsed, "told");
                Assert.True(c.InsecureLogin, "the password is not sent without the player's yes (MainForm.OnStart)");
                LauncherLog.TlsFallback(c);
                string log = File.ReadAllText(Path.Combine(BootLayout.LauncherDir(dir), "launcher.log"));
                Assert.True(log.Contains("TLS 1.2 unavailable") && log.Contains("1.2.3.4:80"), "the fallback is logged: " + log);
                Assert.False(log.Contains("/api/") || log.Contains("/s/"), "no URL path in the log");
                Assert.Equal("https://example.org/d/client/", c.UpdateBaseUrl, "the update keeps https");
                UpdateException e = Assert.Throws<UpdateException>(delegate { new Updater(c).FetchManifest(); }, "no update without TLS");
                Assert.Equal(Tls.MissingText, e.Message, "the reason");
                Assert.True(e.Unreachable && e.ManifestUnavailable, "the last verified version may still be played");
            }
            finally { Tls.SetForTests(true); Runner.DeleteTree(dir); }
        }

        public static void TestAnHttpUpdateAddressIsRefusedWithoutARequest()
        {
            // Approval gate 1.4 #1: no update list from plain HTTP (only HTTPS, or this PC for tests).
            string dir = Runner.TempDir("insecure");
            try
            {
                WriteText(Path.Combine(dir, LauncherConfig.FileName), "UpdateBaseUrl=http://192.0.2.1:9/s/client/\r\n");
                LauncherConfig c = LauncherConfig.Load(dir, null);
                Updater up = new Updater(c);
                DateTime t0 = DateTime.UtcNow;
                UpdateException e = Assert.Throws<UpdateException>(delegate { up.FetchManifest(); }, "refused");
                Assert.Equal(Updater.InsecureText, e.Message, "the reason");
                Assert.True((DateTime.UtcNow - t0).TotalSeconds < 2, "before any connection");
                Assert.True(e.Unreachable && e.ManifestUnavailable, "the last verified version may still be played");
                Assert.True(Updater.SecureSource("https://example.org/x/") && Updater.SecureSource("http://127.0.0.1:9/x/"), "https and loopback");
                Assert.False(Updater.SecureSource("http://example.org/x/") || Updater.SecureSource("ftp://example.org/x/"), "others");
            }
            finally { Runner.DeleteTree(dir); }
        }

        public static void TestTheLauncherConfirmsThroughTheInheritedPipe()
        {
            using (System.IO.Pipes.AnonymousPipeServerStream server = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable))
            {
                Assert.False(Handover.Open("12x"), "not a handle");
                // In one process the client stream wraps the server's own copy of the handle: closing it ends the pipe.
                Assert.True(Handover.Open(server.GetClientHandleAsString()), "opened");
                Handover.Signal(BootLayout.SignalStarted);
                Handover.Signal(BootLayout.SignalStable);
                Assert.Equal(BootLayout.SignalStarted, server.ReadByte(), "first");
                Assert.Equal(BootLayout.SignalStable, server.ReadByte(), "second");
                Assert.Equal(-1, server.ReadByte(), "closed after the second signal");
                Assert.False(Handover.Connected, "closed");
                Handover.Signal(BootLayout.SignalStable); // nobody waits: no error
            }
        }

        public static void TestANewLauncherFileIsHandedToTheBootstrap()
        {
            string dir = Runner.TempDir("restart");
            try
            {
                string running = Path.Combine(dir, "launcher\\versions\\aaaaaaaaaaaaaaaa\\WolfTeamLauncher.exe");
                WriteText(running, "launcher 1.4");
                WriteText(BootLayout.StagedLauncher(dir), "launcher 1.4");
                Assert.False(Handover.StagedDiffersFromRunning(dir, running), "same version");
                WriteText(BootLayout.StagedLauncher(dir), "launcher 1.5");
                Assert.True(Handover.StagedDiffersFromRunning(dir, running), "a new version");
                Assert.False(Handover.RestartViaBootstrap(dir, new string[0]), "no bootstrap: no restart");
                File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), BootLayout.BootstrapPath(dir));
                Assert.True(Handover.RestartViaBootstrap(dir, new string[] { "--confirm-pipe", "123", "--gamedir", dir, "--rolled-back" }), "started");
                Assert.Equal("--config \"C:\\a b\\x.ini\"", Handover.Quote(new List<string> { "--config", "C:\\a b\\x.ini" }), "quoting");
            }
            finally { Runner.DeleteTree(dir); }
        }

        // ------------------------------------------------------------------ texts that must never be cut

        public static void TestLongNewsTitlesWrapInsteadOfBeingCut()
        {
            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(10, 10))
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            {
                const string longTitle = "Eski sunucular geri döndü: Av Bölgesi, Gizli Karargah, Zafer Tepesi, Yıldırım Ordusu";
                int one = NewsList.TitleHeight(g, "Kısa başlık", 376);
                int two = NewsList.TitleHeight(g, longTitle, 376);
                Assert.True(two >= one + 15, "long title takes two lines: " + one + " / " + two);
                Assert.True(NewsList.Fits(g, longTitle, Theme.NewsTitle, 376, two), "whole title fits its wrapped height");
            }
        }

        public static void TestLongMessagesFitTheMessageStrip()
        {
            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(10, 10))
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            {
                string[] texts =
                {
                    Updater.WriteProblem("EventNoticeVersion.txt", new UnauthorizedAccessException()),
                    "Önceki güncelleme yarım kaldı ve güncelleme listesi alınamadı. Oyun bu hâliyle açılamaz; bağlantını kontrol edip Tekrar dene'ye bas.",
                    "Hesabın yasaklı (bitiş: 2026-10-01T10:00 UTC: hile ve küfür nedeniyle, itiraz için yöneticiye yaz).",
                    "Kullanıcı adını yaz.",
                };
                foreach (string t in texts)
                {
                    System.Drawing.Font f = MessageStrip.FontFor(g, t, MessageStrip.TextWidth);
                    Assert.True(NewsList.Fits(g, t, f, MessageStrip.TextWidth, MessageStrip.TextHeight), "fits: " + t);
                }
                Assert.True(MessageStrip.FontFor(g, "Kullanıcı adını yaz.", MessageStrip.TextWidth) == Theme.Body, "short text keeps the body font");
            }
        }

        public static void TestATextJustWiderThanOneLineIsNotTakenAsFitting()
        {
            // Codex review 1.3 #5: a clipped second line must not count as fitting.
            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(10, 10))
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            {
                string t = "Kısa";
                while (g.MeasureString(t, Theme.Body).Width < MessageStrip.TextWidth + 30) t += " kelime";
                int oneLine = (int)Math.Ceiling(Theme.Body.GetHeight(g)) + 1;
                Assert.False(NewsList.Fits(g, t, Theme.Body, MessageStrip.TextWidth, oneLine * 3 / 2), "a half-visible second line does not count");
                Assert.True(MessageStrip.FontFor(g, t, MessageStrip.TextWidth) == Theme.Small, "falls back to two small lines");
            }
        }

        public static void TestLaunchErrorsAreTurkish()
        {
            Assert.Equal("Oyun başlatılamadı. Wolfteam.bin açılamıyor; başlatıcıyı kapatıp tekrar aç.",
                MainForm.LaunchProblem(new InvalidOperationException("No process is associated with this object.")), "no English text");
        }

        public static void TestDownloadLineShowsFileAndTotals()
        {
            UpdateProgress p = new UpdateProgress();
            p.File = "wolf.xfs";
            p.Index = 3;
            p.Count = 120;
            p.FileDone = 35;
            p.FileSize = 100;
            p.Done = 540L << 20;
            p.Total = (long)(1.9 * (1L << 30));
            Assert.Equal("İndiriliyor 3/120: wolf.xfs %35, 540 MB / 1,9 GB", ProgressText.Line(p), "line");
        }
    }
}
