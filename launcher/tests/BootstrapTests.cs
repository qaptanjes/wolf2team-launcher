// Bootstrap tests (launcher 1.4, launcher\bootstrap\Bootstrap.cs):
// - Bootstrapper with a scripted runner: the transaction rules (adopt, trial, rollback, attempts, migration);
// - the real bootstrap exe with a stand-in launcher (FakeLauncher.cs) in real processes: confirmation over the inherited
//   pipe, crashes, hangs, an unstartable file, the bootstrap killed during a trial (a PC restart), two starts at once.
// ops\build-setup.ps1 builds both exes first and passes them in WT_TEST_BOOTSTRAP_EXE and WT_TEST_FAKE_LAUNCHER_EXE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WolfTeamDirilis.Tests
{
    public static class BootstrapTests
    {
        private static string FakeExe
        {
            get
            {
                string f = Environment.GetEnvironmentVariable("WT_TEST_FAKE_LAUNCHER_EXE");
                if (string.IsNullOrEmpty(f) || !File.Exists(f)) throw new TestFailure("WT_TEST_FAKE_LAUNCHER_EXE not set (run ops\\build-setup.ps1)");
                return f;
            }
        }

        private static string BootExe
        {
            get
            {
                string f = Environment.GetEnvironmentVariable("WT_TEST_BOOTSTRAP_EXE");
                if (string.IsNullOrEmpty(f) || !File.Exists(f)) throw new TestFailure("WT_TEST_BOOTSTRAP_EXE not set (run ops\\build-setup.ps1)");
                return f;
            }
        }

        /// <summary>A launcher version: the stand-in exe with its mode marker (each call a new SHA-256).</summary>
        private static byte[] Version(string mode)
        {
            List<byte> b = new List<byte>(File.ReadAllBytes(FakeExe));
            b.AddRange(Encoding.ASCII.GetBytes("WTFAKE:" + mode + ";" + Guid.NewGuid().ToString("N")));
            return b.ToArray();
        }

        /// <summary>A file with valid MZ/PE headers that Windows cannot start (cut short).</summary>
        private static byte[] Unstartable()
        {
            byte[] all = File.ReadAllBytes(FakeExe);
            byte[] cut = new byte[1024];
            Array.Copy(all, cut, cut.Length);
            return cut;
        }

        private static string Sha(byte[] b)
        {
            using (System.Security.Cryptography.SHA256 s = System.Security.Cryptography.SHA256.Create())
                return Updater.ToHex(s.ComputeHash(b));
        }

        private static void Stage(string install, byte[] launcher)
        {
            Directory.CreateDirectory(BootLayout.LauncherDir(install));
            File.WriteAllBytes(BootLayout.StagedLauncher(install), launcher);
        }

        private static BootState State(string install)
        {
            BootState s = BootState.Read(BootLayout.StatePath(install));
            if (s == null) throw new TestFailure("no state file");
            return s;
        }

        // ------------------------------------------------------------ scripted runner

        private sealed class Killed : Exception { }

        /// <summary>Confirms "ok"/"close" versions, fails the others; "kill" plays a bootstrap killed during the start.</summary>
        private sealed class ScriptRunner : ILauncherRunner
        {
            public readonly List<string> Calls = new List<string>();
            /// <summary>SHA-256 of versions that fail whatever their mode.</summary>
            public readonly HashSet<string> Fail = new HashSet<string>();

            private static string ModeOf(string exe)
            {
                if (!File.Exists(exe)) return "missing";
                byte[] all = File.ReadAllBytes(exe);
                string tail = Encoding.ASCII.GetString(all, Math.Max(0, all.Length - 64), Math.Min(64, all.Length));
                int i = tail.LastIndexOf("WTFAKE:", StringComparison.Ordinal);
                return i < 0 ? "unstartable" : tail.Substring(i + 7, tail.IndexOf(';', i) - i - 7);
            }

            public ChildResult Supervise(string exe, string args, string expectedSha)
            {
                string mode = ModeOf(exe);
                Calls.Add(mode + " " + args);
                if (mode == "kill") throw new Killed();
                if (File.Exists(exe) && Fail.Contains(Sha(File.ReadAllBytes(exe)))) return ChildResult.Failed;
                return mode == "ok" || mode == "close" ? ChildResult.Confirmed : ChildResult.Failed;
            }
        }

        private static BootOutcome Boot(string install, ScriptRunner r, params string[] args)
        {
            return BootProgram.Run(install, BootLayout.BootstrapPath(install), args, r);
        }

        private static string NewInstall(string label)
        {
            string d = Runner.TempDir(label);
            File.WriteAllText(BootLayout.BootstrapPath(d), "bootstrap");
            return d;
        }

        public static void TestAFirstStartAdoptsTheReleaseLauncherAndConfirmsIt()
        {
            string d = NewInstall("boot-first");
            try
            {
                byte[] v1 = Version("ok");
                Stage(d, v1);
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, r), "started");
                BootState s = State(d);
                Assert.Equal(Sha(v1), s.Current, "current");
                Assert.True(s.Trial == null && s.Previous == null, "no trial, no previous");
                Assert.True(File.Exists(BootLayout.VersionExe(d, Sha(v1))), "copy in versions");
                Assert.True(r.Calls[0].Contains("--gamedir \"" + d + "\""), "the install folder is passed: " + r.Calls[0]);
                Assert.Equal(1, r.Calls.Count, "one start");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestAFailingNewVersionIsRolledBackAndNeverTriedAgain()
        {
            string d = NewInstall("boot-rollback");
            try
            {
                byte[] v1 = Version("ok"), v2 = Version("crash");
                Stage(d, v1);
                Boot(d, new ScriptRunner());
                Stage(d, v2);
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, r), "a launcher runs");
                Assert.Equal(2, r.Calls.Count, "new, then current");
                Assert.True(r.Calls[0].StartsWith("crash "), "the new version was tried");
                Assert.True(r.Calls[1].StartsWith("ok ") && r.Calls[1].Contains(BootLayout.RolledBackFlag), "current with --rolled-back");
                BootState s = State(d);
                Assert.Equal(Sha(v1), s.Current, "current kept");
                Assert.True(s.Failed.Contains(Sha(v2)) && s.Trial == null, "marked failed");
                ScriptRunner again = new ScriptRunner();
                Boot(d, again);
                Assert.Equal(1, again.Calls.Count, "the failed version is not tried again");
                Assert.False(again.Calls[0].Contains(BootLayout.RolledBackFlag), "no message any more");
                Assert.False(Directory.Exists(Path.GetDirectoryName(BootLayout.VersionExe(d, Sha(v2)))), "its copy is removed");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestAConfirmedNewVersionBecomesCurrentAndTheOldOnePrevious()
        {
            string d = NewInstall("boot-upgrade");
            try
            {
                byte[] v1 = Version("ok"), v2 = Version("ok"), v3 = Version("ok");
                Stage(d, v1); Boot(d, new ScriptRunner());
                Stage(d, v2); Boot(d, new ScriptRunner());
                BootState s = State(d);
                Assert.True(s.Current == Sha(v2) && s.Previous == Sha(v1), "v2 current, v1 previous");
                Stage(d, v3); Boot(d, new ScriptRunner());
                s = State(d);
                Assert.True(s.Current == Sha(v3) && s.Previous == Sha(v2), "v3 current, v2 previous");
                Assert.False(Directory.Exists(Path.GetDirectoryName(BootLayout.VersionExe(d, Sha(v1)))), "v1 copy removed");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestABootstrapKilledDuringTrialsGivesUpAfterTwoStarts()
        {
            // Approval gate 1.3 #1: a replacement that never confirms, with the bootstrap killed (or the PC restarted)
            // during its start, is still rolled back by a later shortcut start.
            string d = NewInstall("boot-kill");
            try
            {
                byte[] v1 = Version("ok"), v2 = Version("kill");
                Stage(d, v1); Boot(d, new ScriptRunner());
                Stage(d, v2);
                for (int i = 1; i <= 2; i++)
                {
                    try { Boot(d, new ScriptRunner()); throw new TestFailure("not killed"); }
                    catch (Killed) { }
                    BootState k = State(d);
                    Assert.True(k.Trial == Sha(v2) && k.Attempts == i, "attempt " + i + " counted before the start");
                    Assert.Equal(Sha(v1), k.Current, "current kept");
                }
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, r), "a launcher runs");
                Assert.Equal(1, r.Calls.Count, "the trial is not started a third time");
                Assert.True(r.Calls[0].StartsWith("ok ") && r.Calls[0].Contains(BootLayout.RolledBackFlag), "current with --rolled-back");
                Assert.True(State(d).Failed.Contains(Sha(v2)), "given up");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestABrokenCurrentVersionFallsBackToPreviousForThisStartOnly()
        {
            string d = NewInstall("boot-previous");
            try
            {
                byte[] v1 = Version("ok"), v2 = Version("ok");
                Stage(d, v1); Boot(d, new ScriptRunner());
                Stage(d, v2); Boot(d, new ScriptRunner());
                // v2 is damaged after its confirmation (a virus scanner, a disk error): its copy is not the confirmed file
                File.WriteAllBytes(BootLayout.VersionExe(d, Sha(v2)), Version("crash"));
                File.Delete(BootLayout.StagedLauncher(d)); // and the release file is gone too: nothing to repair from
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.StartedPrevious, Boot(d, r), "previous runs");
                Assert.True(r.Calls[0].StartsWith("ok ") && r.Calls[0].Contains(BootLayout.RolledBackFlag), "v1 with --rolled-back");
                BootState s = State(d);
                Assert.True(s.Current == Sha(v2) && s.Previous == Sha(v1), "state unchanged: the next start tries current again");
                Stage(d, v2);
                ScriptRunner repaired = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, repaired), "the copy is made again from the release file");
                Assert.False(repaired.Calls[0].Contains(BootLayout.RolledBackFlag), "current runs again");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestABootstrapKilledRightAfterTheFirstCopyResumesOnTheNextStart()
        {
            // Codex 1.4 round 5 #1: the state exists before the first version folder, so this is no "lost state".
            string d = NewInstall("boot-firstcopy");
            try
            {
                byte[] v = Version("ok");
                Stage(d, v);
                Bootstrapper.Fault = delegate(string step) { if (step == "copied") throw new Killed(); };
                try { Boot(d, new ScriptRunner()); throw new TestFailure("not killed"); }
                catch (Killed) { }
                finally { Bootstrapper.Fault = null; }
                Assert.True(File.Exists(BootLayout.StatePath(d)) && File.Exists(BootLayout.VersionExe(d, Sha(v))), "state and copy");
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, r), "the next start adopts and confirms");
                Assert.Equal(Sha(v), State(d).Current, "confirmed");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestARejectedPreviousVersionIsNotAFallback()
        {
            // Codex 1.4 round 6 #1: A previous, B current; A staged again (a release rollback) and rejected; B then fails.
            string d = NewInstall("boot-prevfail");
            try
            {
                byte[] a = Version("ok"), b = Version("ok");
                Stage(d, a); Boot(d, new ScriptRunner());
                Stage(d, b); Boot(d, new ScriptRunner());
                Stage(d, a); // the release goes back to A, and A does not start this time
                ScriptRunner r = new ScriptRunner();
                r.Fail.Add(Sha(a));
                Boot(d, r);
                BootState s = State(d);
                Assert.True(s.Failed.Contains(Sha(a)) && s.Current == Sha(b) && s.Previous == Sha(a), "A rejected, still previous: " + s.Text());
                File.Delete(BootLayout.StagedLauncher(d));
                ScriptRunner r2 = new ScriptRunner();
                r2.Fail.Add(Sha(b)); // B fails now
                Assert.Equal(BootOutcome.NothingToStart, Boot(d, r2), "no fallback to a rejected version");
                Assert.Equal(1, r2.Calls.Count, "only B was tried, never A again");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestAFailedFirstTrialNeverStartsA12Launcher()
        {
            // Gate 1.4b #1: a 1.2 launcher inside a 1.4 install would update the bootstrap away (the served
            // WolfTeamDirilis.exe is 1.2) and has no consent or update lock. A failed first trial offers the repair instead;
            // a 1.2 copy (WolfTeamDirilis.exe.old, from a pre-release setup) is never started and is removed.
            string d = NewInstall("boot-migrate");
            try
            {
                File.WriteAllBytes(BootLayout.BootstrapPath(d) + ".old", Version("legacy"));
                Stage(d, Version("crash"));
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.NothingToStart, Boot(d, r), "nothing to start: the repair is offered");
                foreach (string c in r.Calls) Assert.False(c.StartsWith("legacy"), "the 1.2 launcher never runs: " + c);
                Assert.False(File.Exists(BootLayout.BootstrapPath(d) + ".old"), "the 1.2 copy is removed");
                Assert.False(State(d).Legacy, "no legacy record");
                byte[] v = Version("ok");
                Stage(d, v); // what the repair puts there
                ScriptRunner ok = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, ok, "--no-swap"), "1.4 runs");
                Assert.False(ok.Calls[0].Contains("--no-swap"), "1.2's flag is not passed on");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestNothingToStartIsReported()
        {
            string d = NewInstall("boot-nothing");
            try
            {
                Assert.Equal(BootOutcome.NothingToStart, Boot(d, new ScriptRunner()), "empty folder");
                Stage(d, Encoding.ASCII.GetBytes("not a program"));
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.NothingToStart, Boot(d, r), "a file that is no program is never adopted");
                Assert.Equal(0, r.Calls.Count, "nothing started");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestASecondStartStepsAsideWhileTheLockIsHeld()
        {
            string d = NewInstall("boot-lock");
            try
            {
                Stage(d, Version("ok"));
                Directory.CreateDirectory(BootLayout.LauncherDir(d));
                using (new FileStream(BootLayout.LockPath(d), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    ScriptRunner r = new ScriptRunner();
                    Assert.Equal(BootOutcome.AnotherStartRuns, Boot(d, r), "steps aside");
                    Assert.Equal(0, r.Calls.Count, "nothing started");
                }
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestAStartAfterAnUpdateWaitsForTheLock()
        {
            string d = NewInstall("boot-wait");
            try
            {
                Stage(d, Version("ok"));
                Directory.CreateDirectory(BootLayout.LauncherDir(d));
                FileStream held = new FileStream(BootLayout.LockPath(d), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Thread release = new Thread(delegate() { Thread.Sleep(700); held.Dispose(); });
                release.Start();
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, r, BootLayout.AfterUpdateFlag), "waited, then started");
                Assert.False(r.Calls[0].Contains(BootLayout.AfterUpdateFlag), "the flag is not passed on");
                release.Join();
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestTheStateFileRoundTripsAndADamagedOneIsNeverReadAsEmpty()
        {
            string d = Runner.TempDir("boot-state");
            try
            {
                string p = Path.Combine(d, "state.txt");
                string a = new string('a', 64), b = new string('b', 64);
                BootState s = new BootState();
                s.Current = a; s.Trial = b; s.Attempts = 1; s.Legacy = true; s.MarkFailed(b);
                BootFiles.WriteAtomic(p, Encoding.UTF8.GetBytes(s.Text()));
                BootState t = BootState.Read(p);
                Assert.Equal(s.Text(), t.Text(), "round trip");
                Assert.False(File.Exists(p + ".part"), "no temp file left");
                foreach (string bad in new string[] { "", "garbage", "current=" + a + "\nattempts=0\n", BootState.Header + "\ncurrent=" + a + "\n",
                                                      BootState.Header + "\ncurrent=XYZ\nattempts=0\n", BootState.Header + "\nattempts=0\nfoo=1\n",
                                                      BootState.Header + "\nattempts=0\nfailed=" + a + ",junk\n", s.Text().Substring(0, 80) })
                {
                    File.WriteAllText(p, bad);
                    bool damaged;
                    Assert.True(BootState.Load(p, out damaged) == null && damaged, "damaged: " + bad.Replace("\n", "|"));
                }
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestALostStateNeverRunsARejectedVersion()
        {
            // Approval gate 1.4 #4: after a rollback (A confirmed, B rejected) a damaged state file must not let B run.
            string d = NewInstall("boot-lost");
            try
            {
                byte[] a = Version("ok"), b = Version("crash");
                Stage(d, a); Boot(d, new ScriptRunner());
                Stage(d, b); Boot(d, new ScriptRunner());
                Assert.True(State(d).Failed.Contains(Sha(b)), "B rejected");
                // Codex 1.4 round 2 #1: a file cut right after "attempts=" (the "failed=" line lost) is damaged too.
                string whole = File.ReadAllText(BootLayout.StatePath(d));
                File.WriteAllText(BootLayout.StatePath(d), whole.Substring(0, whole.IndexOf("attempts=0\n") + "attempts=0\n".Length));
                ScriptRunner cut = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, cut), "from the copy after a cut");
                Assert.True(cut.Calls.Count == 1 && cut.Calls[0].StartsWith("ok "), "B is not run after a cut file");
                Assert.True(State(d).Failed.Contains(Sha(b)), "the rejection survives");
                // Codex 1.4 round 3 #2: a selftest after the same damage runs A, never B.
                File.WriteAllText(BootLayout.StatePath(d), whole.Substring(0, whole.IndexOf("attempts=0\n") + "attempts=0\n".Length));
                Assert.Equal(BootLayout.VersionExe(d, Sha(a)), new Bootstrapper(d, BootLayout.BootstrapPath(d), null).DiagnosticExe(), "selftest uses A");
                File.WriteAllText(BootLayout.StatePath(d), "garbage");
                ScriptRunner r = new ScriptRunner();
                Assert.Equal(BootOutcome.Started, Boot(d, r), "from the copy");
                Assert.Equal(1, r.Calls.Count, "B is not run");
                Assert.True(r.Calls[0].StartsWith("ok "), "A runs");
                Assert.Equal(Sha(a), State(d).Current, "the file is made again from its copy");
                File.WriteAllText(BootLayout.StatePath(d), "garbage");
                File.WriteAllText(BootLayout.StateBackupPath(d), "");
                ScriptRunner lost = new ScriptRunner();
                Assert.Equal(BootOutcome.NothingToStart, Boot(d, lost), "both lost: nothing trusted");
                Assert.Equal(0, lost.Calls.Count, "B is not run");
                Assert.True(new Bootstrapper(d, BootLayout.BootstrapPath(d), null).DiagnosticExe() == null, "no selftest with a lost state");
                File.Delete(BootLayout.StatePath(d));
                File.Delete(BootLayout.StateBackupPath(d));
                Assert.Equal(BootOutcome.NothingToStart, Boot(d, new ScriptRunner()), "no state but versions exist: lost, not a first start");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestAStartAlwaysHoldsTheInstallLock()
        {
            // Gate 1.4d #2: no start without install.lock. A user who may only read the lock file still takes it
            // (exclusive read handle); while maintenance holds it the start steps aside; without any access it stops
            // with an error, nothing started (the old unlocked "read-only" start is gone).
            string d = NewInstall("boot-lockacl");
            string lp = BootLayout.LockPath(d);
            System.Security.Principal.SecurityIdentifier me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            System.Security.AccessControl.FileSystemAccessRule noWrite = new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData,
                System.Security.AccessControl.AccessControlType.Deny);
            System.Security.AccessControl.FileSystemAccessRule noRead = new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
            try
            {
                Stage(d, Version("ok"));
                Assert.Equal(BootOutcome.Started, Boot(d, new ScriptRunner()), "first start");
                System.Security.AccessControl.FileSecurity sec = File.GetAccessControl(lp);
                sec.AddAccessRule(noWrite);
                File.SetAccessControl(lp, sec);
                try
                {
                    ScriptRunner r = new ScriptRunner();
                    Assert.Equal(BootOutcome.Started, Boot(d, r), "read-only lock file: still locked and started");
                    Assert.Equal(1, r.Calls.Count, "one launcher");
                    using (new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.None)) // maintenance holds it
                    {
                        ScriptRunner r2 = new ScriptRunner();
                        Assert.Equal(BootOutcome.AnotherStartRuns, Boot(d, r2), "steps aside during maintenance");
                        Assert.Equal(0, r2.Calls.Count, "nothing started without the lock");
                    }
                    sec = File.GetAccessControl(lp);
                    sec.AddAccessRule(noRead);
                    File.SetAccessControl(lp, sec);
                    ScriptRunner r3 = new ScriptRunner();
                    Assert.Equal(BootOutcome.LockUnavailable, Boot(d, r3), "no access at all: an error");
                    Assert.Equal(0, r3.Calls.Count, "nothing started");
                }
                finally
                {
                    sec = File.GetAccessControl(lp);
                    sec.RemoveAccessRule(noWrite);
                    sec.RemoveAccessRule(noRead);
                    File.SetAccessControl(lp, sec);
                }
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestNetworkFoldersAreRefused()
        {
            // Gate 1.4d #1: another computer could be playing from the same files.
            Assert.True(BootLayout.IsNetworkPath(@"\\server\share\WolfTeam"), "UNC");
            Assert.True(BootLayout.IsNetworkPath("//server/share/WolfTeam"), "UNC with slashes");
            string asked = null;
            Assert.True(BootLayout.IsNetworkPath(@"Z:\Oyunlar\WolfTeam", delegate(string root) { asked = root; return DriveType.Network; }), "mapped letter");
            Assert.Equal(@"Z:\", asked, "the drive root is asked");
            Assert.False(BootLayout.IsNetworkPath(@"C:\Oyunlar\WolfTeam", delegate(string root) { return DriveType.Fixed; }), "a local disk");
            Assert.False(BootLayout.IsNetworkPath(@"D:\Oyun", delegate(string root) { return DriveType.Removable; }), "a USB disk");
            Assert.False(BootLayout.IsNetworkPath(Path.GetTempPath()), "this PC's temp folder");
            Assert.True(BootLayout.IsSharingViolation(new IOException("x", unchecked((int)0x80070020))), "sharing violation");
            Assert.False(BootLayout.IsSharingViolation(new IOException("x", unchecked((int)0x80070015))), "device not ready");
        }

        public static void TestTheAnsiRoundTripOfTheGameFolder()
        {
            // Wolfteam.bin builds the protection module path with ANSI "%s\%s\%s": "ş" on an English Windows (cp1252) ->
            // "axProtect initialization Failed. file not found 0". Turkish Windows (cp1254) opens the same folder.
            Encoding cp1252 = Encoding.GetEncoding(1252), cp1254 = Encoding.GetEncoding(1254);
            Assert.False(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\WolfTeam Diriliş", cp1252), "cp1252 rejects Diriliş (best fit ş -> s is a loss)");
            Assert.True(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\WolfTeam Diriliş", cp1254), "cp1254 accepts Diriliş");
            Assert.True(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\WolfTeam Dirilis", cp1252), "ASCII under cp1252");
            Assert.True(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\WolfTeam Dirilis", cp1254), "ASCII under cp1254");
            Assert.True(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\WolfTeam Dirilis", Encoding.GetEncoding(932)), "ASCII under cp932");
            Assert.False(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\İ", cp1252), "İ under cp1252");
            Assert.False(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\Игры", cp1254), "Cyrillic under cp1254");
            Assert.False(BootLayout.RoundTripsAnsi(null, cp1252), "no path");
            Assert.True(BootLayout.RoundTripsAnsi(@"C:\x", Encoding.UTF8), "a UTF-8 system code page round-trips everything");
            Assert.True(BootLayout.RoundTripsAnsi(@"C:\Oyunlar\WolfTeam Diriliş", Encoding.UTF8), "UTF-8: Diriliş too");
            string text = BootLayout.AnsiPathProblem(@"C:\Oyunlar\WolfTeam Diriliş", cp1252);
            Assert.True(text != null && text.Contains(@"C:\Oyunlar\WolfTeam Dirilis") && text.Contains("axProtect"), "the message: " + text);
            Assert.Equal(null, BootLayout.AnsiPathProblem(@"C:\Oyunlar\WolfTeam Diriliş", cp1254), "no message under cp1254");
            Assert.Equal(@"Oyunlar\WolfTeam Dirilis", BootLayout.DefaultFolder, "ASCII default");
        }

        public static void TestTheRealBootstrapStartsNothingFromAFolderTheGameCannotOpen()
        {
            // RealInstall folders contain "ş": refused under cp1252 (no launcher, no state, no trial), started under cp1254.
            string d = RealInstall("boot-ansi");
            try
            {
                byte[] v = Version("ok");
                Stage(d, v);
                for (int i = 0; i < 2; i++) // twice: more than the trial attempts a start may use
                {
                    const string cp = "1252";
                    ProcessStartInfo psi = new ProcessStartInfo(BootLayout.BootstrapPath(d), "");
                    psi.UseShellExecute = false;
                    psi.WorkingDirectory = d;
                    psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_NODIALOG"] = "1";
                    psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_TIMEOUTS"] = Timeouts;
                    psi.EnvironmentVariables["WT_FAKE_STAY_MS"] = "300";
                    psi.EnvironmentVariables[BootLayout.AnsiCodePageTestVariable] = cp;
                    using (Process p = Process.Start(psi))
                    {
                        Assert.True(p.WaitForExit(60000), "ends");
                        Assert.Equal(1, p.ExitCode, "refused under cp" + cp);
                    }
                }
                Assert.Equal(0, LogLines(d).Length, "no launcher started");
                Assert.False(File.Exists(BootLayout.StatePath(d)), "no state written, no trial counted");
                Assert.False(Directory.Exists(BootLayout.VersionsDir(d)), "no version folder made");

                ProcessStartInfo tr = new ProcessStartInfo(BootLayout.BootstrapPath(d), "");
                tr.UseShellExecute = false;
                tr.WorkingDirectory = d;
                tr.EnvironmentVariables["WOLFTEAM_BOOT_TEST_TIMEOUTS"] = Timeouts;
                tr.EnvironmentVariables["WT_FAKE_STAY_MS"] = "1500";
                tr.EnvironmentVariables[BootLayout.AnsiCodePageTestVariable] = "1254";
                tr.EnvironmentVariables["WOLFTEAM_BOOT_TEST_NODIALOG"] = "1";
                using (Process p = Process.Start(tr))
                {
                    Assert.True(p.WaitForExit(60000), "ends");
                    Assert.Equal(0, p.ExitCode, "Turkish Windows starts");
                }
                Assert.Equal(1, LogLines(d).Length, "one launcher started under cp1254");
                Assert.Equal(Sha(v), State(d).Current, "confirmed");
            }
            finally { Cleanup(d); }
        }

        public static void TestOnlyASetupThatInstallsTheBootstrapIsOfferedForRepair()
        {
            // Gate 1.4b #1 (re-review): an older setup would put a 1.2-style launcher back as WolfTeamDirilis.exe.
            Assert.True(BootLayout.IsRepairSetup(BootLayout.SetupTitle, "1.4.0.0"), "setup 1.4");
            Assert.True(BootLayout.IsRepairSetup(BootLayout.SetupTitle, "1.10.0.0"), "a later setup");
            Assert.False(BootLayout.IsRepairSetup(BootLayout.SetupTitle, "1.3.0.0"), "setup 1.3");
            Assert.False(BootLayout.IsRepairSetup("", "0.0.0.0"), "1.1/1.2 setups have no version resource");
            Assert.False(BootLayout.IsRepairSetup(null, null), "nothing");
            Assert.False(BootLayout.IsRepairSetup("WolfTeam Diriliş", "1.4.0.0"), "the bootstrap");
            Assert.False(BootLayout.IsRepairSetup("WolfTeam Diriliş başlatıcısı", "1.4.0.0"), "the launcher");
            Assert.False(BootLayout.IsRepairSetupFile(FakeExe), "a foreign program");
        }

        public static void TestRejectionsAreNeverForgotten()
        {
            // Gate 1.4 #4: no eviction of old rejections.
            BootState s = new BootState();
            for (int i = 0; i < 40; i++) s.MarkFailed(i.ToString("x64"));
            BootState t = BootState.Parse(s.Text());
            Assert.Equal(40, t.Failed.Count, "all 40 kept");
            Assert.True(t.Failed.Contains(0.ToString("x64")), "the first one too");
        }

        public static void TestTheRunnerStartsOnlyTheCheckedBytes()
        {
            // Gate 1.4 #5: the hash is checked on the handle that stays open (no write, no delete) until the process exists.
            string d = Runner.TempDir("boot-pin");
            try
            {
                string exe = Path.Combine(d, "WolfTeamLauncher.exe");
                byte[] v = Version("ok");
                File.WriteAllBytes(exe, v);
                ProcessLauncherRunner r = new ProcessLauncherRunner(d, new BootTimeouts());
                Assert.Equal(ChildResult.Failed, r.Supervise(exe, BootLayout.GameDirFlag + " \"" + d + "\"", new string('0', 64)), "wrong hash: not started");
                Assert.Equal(0, LogLines(d).Length, "nothing ran");

                // the right hash: while the process is being created the checked file cannot be changed or deleted
                bool writeBlocked = false, deleteBlocked = false;
                ProcessLauncherRunner.BeforeStart = delegate(string path)
                {
                    try { using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } }
                    catch (IOException) { writeBlocked = true; }
                    catch (UnauthorizedAccessException) { writeBlocked = true; }
                    try { File.Delete(path); }
                    catch (IOException) { deleteBlocked = true; }
                    catch (UnauthorizedAccessException) { deleteBlocked = true; }
                };
                try
                {
                    Assert.Equal(ChildResult.Confirmed, r.Supervise(exe, BootLayout.GameDirFlag + " \"" + d + "\"", Sha(v)), "right hash: started and confirmed");
                }
                finally { ProcessLauncherRunner.BeforeStart = null; }
                Assert.True(writeBlocked && deleteBlocked, "checked file pinned until the process exists");
                Assert.Equal(Sha(v), Sha(File.ReadAllBytes(exe)), "unchanged");
            }
            finally { Cleanup(d); }
        }

        public static void TestTheLauncherArgumentsDropTheInternalFlagsOfOlderLaunchers()
        {
            string d = NewInstall("boot-args");
            try
            {
                Bootstrapper b = new Bootstrapper(d, BootLayout.BootstrapPath(d), new ScriptRunner());
                string args = b.LauncherArgs(new string[] { "--no-swap", "--handover", "x", "--parent", "1", "--gamedir", "C:\\x",
                    "--confirm-pipe", "5", "--config", "a.ini", "--selftest" }, true);
                Assert.Equal("--gamedir \"" + d + "\" --rolled-back --config " + Quote(Path.GetFullPath("a.ini")) + " --selftest", args, "arguments");
            }
            finally { Runner.DeleteTree(d); }
        }

        private static string Quote(string s)
        {
            return s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;
        }

        // ------------------------------------------------------------ real processes

        private const string Timeouts = "6000,3000,400";

        private static string RealInstall(string label)
        {
            string d = Runner.TempDir(label);
            File.Copy(BootExe, BootLayout.BootstrapPath(d));
            return d;
        }

        private static Process StartBoot(string install, int stayMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(BootLayout.BootstrapPath(install), "");
            psi.UseShellExecute = false;
            psi.WorkingDirectory = install;
            // These fixtures deliberately contain Turkish characters. Locale refusal is tested separately.
            psi.EnvironmentVariables[BootLayout.AnsiCodePageTestVariable] = "1254";
            psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_NODIALOG"] = "1";
            psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_TIMEOUTS"] = Timeouts;
            psi.EnvironmentVariables["WT_FAKE_STAY_MS"] = stayMs.ToString();
            return Process.Start(psi);
        }

        /// <summary>Runs the bootstrap and waits for it to end.</summary>
        private static int RunBoot(string install, int stayMs)
        {
            using (Process p = StartBoot(install, stayMs))
            {
                if (!p.WaitForExit(60000)) { p.Kill(); throw new TestFailure("bootstrap did not end"); }
                return p.ExitCode;
            }
        }

        private static string[] LogLines(string install)
        {
            string f = Path.Combine(install, "fake-log.txt");
            for (int i = 0; i < 20; i++)
            {
                try { return File.Exists(f) ? File.ReadAllLines(f) : new string[0]; }
                catch (IOException) { Thread.Sleep(100); }
            }
            return new string[0];
        }

        /// <summary>Stops every launcher (and bootstrap) still running from the folder, then deletes it.</summary>
        private static void Cleanup(string install)
        {
            foreach (string name in new string[] { "WolfTeamLauncher", "WolfTeamDirilis" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.MainModule.FileName.StartsWith(install, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    finally { p.Dispose(); }
                }
            }
            Runner.DeleteTree(install);
        }

        private static void WaitForLine(string install, string prefix, int count)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < end)
            {
                int n = 0;
                foreach (string l in LogLines(install)) if (l.StartsWith(prefix)) n++;
                if (n >= count) return;
                Thread.Sleep(100);
            }
            string log = Path.Combine(BootLayout.LauncherDir(install), "bootstrap.log");
            throw new TestFailure("no '" + prefix + "' start seen; bootstrap log: " +
                (File.Exists(log) ? File.ReadAllText(log) : "missing"));
        }

        public static void TestFakeLauncherDoesNotLoseItsLogWhenAReaderHoldsTheFile()
        {
            string d = Runner.TempDir("fake-log-lock");
            try
            {
                string log = Path.Combine(d, "fake-log.txt");
                File.WriteAllText(log, "");
                ProcessStartInfo psi = new ProcessStartInfo(FakeExe, BootLayout.GameDirFlag + " \"" + d + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables["WT_FAKE_STAY_MS"] = "300";
                using (FileStream reader = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (Process child = Process.Start(psi))
                {
                    Thread.Sleep(750);
                    reader.Dispose();
                    Assert.True(child.WaitForExit(10000), "fake launcher ends after reader releases the log");
                    Assert.Equal(0, child.ExitCode, "fake launcher succeeded");
                }
                Assert.Equal(1, LogLines(d).Length, "handshake log is not silently dropped");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestRealProcessesConfirmRollBackAndRecover()
        {
            string d = RealInstall("boot-real");
            try
            {
                byte[] v1 = Version("ok");
                Stage(d, v1);
                Assert.Equal(0, RunBoot(d, 1500), "first start");
                Assert.Equal(Sha(v1), State(d).Current, "confirmed over the inherited pipe");
                string[] log = LogLines(d);
                Assert.True(log.Length == 1 && log[0].StartsWith("ok --gamedir"), "the launcher got the install folder: " + string.Join("|", log));

                foreach (string mode in new string[] { "crash", "half", "hang", "signalcrash", "unstartable" })
                {
                    byte[] bad = mode == "unstartable" ? Unstartable() : Version(mode);
                    Stage(d, bad);
                    int before = LogLines(d).Length;
                    // stay long: a "half" launcher that exits cleanly after byte 1 would count as closed by the player
                    Assert.Equal(0, RunBoot(d, 20000), mode + ": a launcher runs");
                    BootState s = State(d);
                    Assert.Equal(Sha(v1), s.Current, mode + ": current kept");
                    Assert.True(s.Failed.Contains(Sha(bad)) && s.Trial == null, mode + ": rolled back");
                    string[] after = LogLines(d);
                    string last = after[after.Length - 1];
                    Assert.True(last.StartsWith("ok ") && last.Contains(BootLayout.RolledBackFlag), mode + ": current with --rolled-back: " + last);
                    Assert.Equal(before + (mode == "unstartable" ? 1 : 2), after.Length, mode + ": starts");
                }

                byte[] quick = Version("close");
                Stage(d, quick);
                Assert.Equal(0, RunBoot(d, 1500), "quick close");
                Assert.Equal(Sha(quick), State(d).Current, "a window the player closed at once is a working start");
            }
            finally { Cleanup(d); }
        }

        public static void TestRealBootstrapKilledDuringATrialRecoversOnTheNextStart()
        {
            // Approval gate 1.3 #1 with real processes: the bootstrap and the new launcher are killed while the start is
            // unconfirmed (as by a PC restart), twice; the next shortcut start rolls back to the confirmed version.
            string d = RealInstall("boot-realkill");
            try
            {
                byte[] v1 = Version("ok"), hang = Version("hang");
                Stage(d, v1);
                RunBoot(d, 1500);
                Stage(d, hang);
                for (int i = 1; i <= 2; i++)
                {
                    using (Process boot = StartBoot(d, 60000))
                    {
                        WaitForLine(d, "hang ", i);
                        boot.Kill();
                        boot.WaitForExit(5000);
                    }
                    Cleanup2(d); // the "restart" also ends the unconfirmed launcher
                    BootState k = State(d);
                    Assert.True(k.Trial == Sha(hang) && k.Attempts == i, "attempt " + i + " recorded");
                }
                int starts = LogLines(d).Length;
                Assert.Equal(0, RunBoot(d, 1500), "next start");
                string[] log = LogLines(d);
                Assert.Equal(starts + 1, log.Length, "the trial is not started again");
                Assert.True(log[log.Length - 1].StartsWith("ok ") && log[log.Length - 1].Contains(BootLayout.RolledBackFlag), "current runs");
                Assert.True(State(d).Failed.Contains(Sha(hang)), "given up");
            }
            finally { Cleanup(d); }
        }

        /// <summary>Kills the launchers of the folder, keeps the folder.</summary>
        private static void Cleanup2(string install)
        {
            foreach (Process p in Process.GetProcessesByName("WolfTeamLauncher"))
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(install, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }

        public static void TestARealLauncherUpdateGoesThroughTheBootstrap()
        {
            // The launcher downloads a new launcher file while the bootstrap still supervises it: it confirms at once and
            // starts the bootstrap with --after-update, which waits for the lock and then tries the new version.
            string d = RealInstall("boot-realupdate");
            try
            {
                byte[] v1 = Version("update"), v2 = Version("ok");
                File.WriteAllBytes(Path.Combine(d, "next.bin"), v2);
                Stage(d, v1);
                Assert.Equal(0, RunBoot(d, 20000), "first start");
                DateTime end = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < end && State(d).Current != Sha(v2)) Thread.Sleep(200);
                BootState s = State(d);
                Assert.True(s.Current == Sha(v2) && s.Previous == Sha(v1) && s.Failed.Count == 0, "v1 confirmed, then v2: " + s.Text());
                string[] log = LogLines(d);
                Assert.True(log.Length == 2 && log[0].StartsWith("update ") && log[1].StartsWith("ok "), "both ran: " + string.Join("|", log));
            }
            finally { Cleanup(d); }
        }

        public static void TestOnlyTheBootstrapKilledLeavesTheLockFree()
        {
            // Gate 1.4 #3: the bootstrap alone is killed while its launcher runs unconfirmed; the next start is not blocked,
            // and the attempt was recorded (the launcher's own updates are serialized by launcher\update.lock).
            string d = RealInstall("boot-bootkill");
            try
            {
                byte[] v1 = Version("ok"), hang = Version("hang");
                Stage(d, v1);
                Assert.Equal(0, RunBoot(d, 1500), "initial launcher confirmed before kill test");
                Stage(d, hang);
                using (Process boot = StartBoot(d, 60000))
                {
                    WaitForLine(d, "hang ", 1);
                    boot.Kill();
                    boot.WaitForExit(5000);
                }
                Assert.Equal(1, State(d).Attempts, "attempt recorded");
                Assert.Equal(0, RunBoot(d, 1500), "the next start runs (lock released by Windows) while the old child lives");
                BootState s = State(d);
                Assert.True(s.Failed.Contains(Sha(hang)) && s.Current == Sha(v1), "second attempt failed, current runs: " + s.Text());
            }
            finally { Cleanup(d); }
        }

        public static void TestTwoRealStartsAtOnceOpenOneLauncher()
        {
            string d = RealInstall("boot-twice");
            try
            {
                Stage(d, Version("ok"));
                RunBoot(d, 500);
                int before = LogLines(d).Length;
                using (Process a = StartBoot(d, 1500))
                using (Process b = StartBoot(d, 1500))
                {
                    Assert.True(a.WaitForExit(60000) && b.WaitForExit(60000), "both end");
                }
                Assert.Equal(before + 1, LogLines(d).Length, "one launcher");
            }
            finally { Cleanup(d); }
        }

        public static void TestASelftestThroughTheRealBootstrapLeavesTheVersionsAlone()
        {
            // Approval gate 1.4 #2: "WolfTeamDirilis.exe --selftest" must not run as a (failing) trial.
            string d = RealInstall("boot-selftest");
            try
            {
                byte[] v = Version("ok");
                Stage(d, v);
                ProcessStartInfo psi = new ProcessStartInfo(BootLayout.BootstrapPath(d), "--selftest");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.EnvironmentVariables[BootLayout.AnsiCodePageTestVariable] = "1254";
                psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_NODIALOG"] = "1";
                psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_TIMEOUTS"] = Timeouts;
                psi.EnvironmentVariables["WT_FAKE_STAY_MS"] = "300";
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    Assert.True(p.WaitForExit(60000) && p.ExitCode == 0, "the launcher's exit code is passed on");
                }
                string[] log = LogLines(d);
                Assert.True(log.Length == 1 && log[0].Contains("--selftest") && !log[0].Contains(BootLayout.ConfirmPipeFlag), "diagnostics only: " + string.Join("|", log));
                Assert.False(File.Exists(BootLayout.StatePath(d)), "no state written");
                Assert.Equal(0, RunBoot(d, 1500), "a normal start afterwards");
                Assert.Equal(Sha(v), State(d).Current, "the launcher is still eligible and confirmed");
            }
            finally { Cleanup(d); }
        }

        public static void TestARealFailedFirstTrialStartsNoOld12Copy()
        {
            // Gate 1.4b #1 with real processes: the first 1.4 trial crashes; a 1.2 copy next to the bootstrap never
            // starts; the bootstrap ends with "nothing to start" (the player gets the repair offer); after the repair (a
            // working launcher file) the next start confirms it.
            string d = RealInstall("boot-real12");
            try
            {
                File.WriteAllBytes(BootLayout.BootstrapPath(d) + ".old", Version("legacy"));
                Stage(d, Version("crash"));
                ProcessStartInfo psi = new ProcessStartInfo(BootLayout.BootstrapPath(d), "");
                psi.UseShellExecute = false;
                psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_TIMEOUTS"] = Timeouts;
                psi.EnvironmentVariables["WOLFTEAM_BOOT_TEST_NODIALOG"] = "1";
                psi.EnvironmentVariables["WT_FAKE_STAY_MS"] = "1500";
                psi.EnvironmentVariables[BootLayout.AnsiCodePageTestVariable] = "1254";
                using (Process p = Process.Start(psi)) Assert.True(p.WaitForExit(60000) && p.ExitCode == 1, "nothing to start");
                System.Threading.Thread.Sleep(1000);
                foreach (string l in LogLines(d)) Assert.False(l.StartsWith("legacy"), "the 1.2 copy never ran: " + l);
                Assert.False(File.Exists(BootLayout.BootstrapPath(d) + ".old"), "1.2 copy removed");
                string bootLog = File.ReadAllText(Path.Combine(BootLayout.LauncherDir(d), "bootstrap.log"));
                Assert.True(bootLog.Contains("repair: no verified setup copy"), "no setup copy: the download message, not a repair: " + bootLog);

                // a foreign program under the setup copy's name is never run as the repair
                File.Copy(FakeExe, Path.Combine(d, BootProgram.RepairSetupName), true);
                using (Process p = Process.Start(psi)) Assert.True(p.WaitForExit(60000) && p.ExitCode == 1, "nothing to start again");
                bootLog = File.ReadAllText(Path.Combine(BootLayout.LauncherDir(d), "bootstrap.log"));
                Assert.False(bootLog.Contains("repair offered"), "a foreign setup copy is not offered: " + bootLog);
                byte[] v = Version("ok");
                Stage(d, v);
                Assert.Equal(0, RunBoot(d, 1500), "after the repair");
                Assert.Equal(Sha(v), State(d).Current, "1.4 confirmed");
            }
            finally { Cleanup(d); }
        }
    }
}
