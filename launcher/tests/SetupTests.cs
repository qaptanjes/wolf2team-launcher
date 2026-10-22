// Setup tests: install-folder rules, command line, shortcut and uninstall plans, the safe remover, and a full
// install + uninstall into temp folders (shortcut folders, registry key and %APPDATA% redirected; nothing of the real
// user profile is touched).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using WolfTeamDirilis.Setup;

namespace WolfTeamDirilis.Tests
{
    internal static class SetupTestMain
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--screenshots") return SetupScreens.Render(args[1]);
            // General fixtures use Turkish paths; refusal tests supply their own explicit code page.
            string previous = Environment.GetEnvironmentVariable(BootLayout.AnsiCodePageTestVariable);
            try
            {
                Environment.SetEnvironmentVariable(BootLayout.AnsiCodePageTestVariable, "1254");
                return Runner.Run(typeof(SetupTests));
            }
            finally { Environment.SetEnvironmentVariable(BootLayout.AnsiCodePageTestVariable, previous); }
        }
    }

    public static class SetupTests
    {
        private static SystemFolders Env()
        {
            SystemFolders f = new SystemFolders();
            f.Windows = @"C:\Windows";
            f.ProgramFiles = new string[] { @"C:\Program Files", @"C:\Program Files (x86)" };
            f.Protected = new string[] { @"C:\Users\ali", @"C:\Users\ali\Desktop", @"C:\Users\ali\Documents", @"C:\Users\ali\Downloads" };
            return f;
        }

        // ------------------------------------------------------------------ folder rules

        public static void TestDefaultFolderIsUserWritableAndAscii()
        {
            // ASCII since 1.5: the game cannot start from "Diriliş" on an English Windows (cp1252, axProtect error).
            Assert.Equal(@"C:\Oyunlar\WolfTeam Dirilis", InstallPaths.DefaultDir("C:"), "default");
            Assert.Equal(@"D:\Oyunlar\WolfTeam Dirilis", InstallPaths.DefaultDir(@"D:\"), "other drive");
            foreach (char c in InstallPaths.DefaultDir("C:")) Assert.True(c < 128, "ASCII only: " + c);
            Assert.Equal(null, InstallPaths.Check(InstallPaths.DefaultDir("C:"), Env()), "the default passes the checks");
        }

        private static readonly Encoding Cp1252 = Encoding.GetEncoding(1252), Cp1254 = Encoding.GetEncoding(1254);

        public static void TestFoldersTheGameCannotOpenUnderTheAnsiCodePageAreRefused()
        {
            // Evidence: setup 1.4 on an English Windows (cp1252) into C:\Oyunlar\WolfTeam Diriliş -> "axProtect
            // initialization Failed. file not found 0"; the same files in an ASCII folder start.
            string e = InstallPaths.AnsiProblem(@"C:\Oyunlar\WolfTeam Diriliş", Cp1252);
            Assert.True(e != null && e.Contains(@"C:\Oyunlar\WolfTeam Dirilis") && e.Contains("axProtect"), "cp1252 refuses ş: " + e);
            Assert.True(InstallPaths.AnsiProblem(@"D:\Oyunlarım\WT", Cp1252) != null, "cp1252 refuses ı");
            Assert.True(InstallPaths.AnsiProblem(@"D:\Oyunlar\WT", Cp1252) == null, "ASCII under cp1252");
            Assert.True(InstallPaths.AnsiProblem(@"D:\x\Diriliş", Cp1252).Contains(@"D:\Oyunlar\WolfTeam Dirilis"), "suggests the same drive");
            Assert.Equal(null, InstallPaths.AnsiProblem(@"C:\Oyunlar\WolfTeam Diriliş", Cp1254), "Turkish Windows keeps working");
            Assert.Equal(null, InstallPaths.AnsiProblem(@"D:\Oyunlarım\Kurt Takımı Ğ", Cp1254), "all Turkish letters under cp1254");
            Assert.Equal(null, InstallPaths.AnsiProblem(@"C:\Oyunlar\Café", Cp1252), "é exists in cp1252");
            Assert.True(InstallPaths.AnsiProblem(@"C:\Oyunlar\Игры", Cp1254) != null, "Cyrillic under cp1254");
            Assert.Equal(null, InstallPaths.AnsiProblem(@"C:\Oyunlar\Игры", Encoding.GetEncoding(1251)), "Cyrillic under cp1251");
            Assert.True(InstallPaths.AnsiProblem(@"C:\Oyunlar\WolfTeam Diriliş", Encoding.GetEncoding(932)) != null, "Japanese Windows");
            Assert.Equal(null, InstallPaths.AnsiProblem(@"C:\Oyunlar\WolfTeam Dirilis", Encoding.GetEncoding(932)), "ASCII under cp932");
        }

        public static void TestTheInstallerRefusesSuchAFolderButStillRemovesAnOldInstall()
        {
            using (Sandbox sb = new Sandbox())
            {
                string self = Process.GetCurrentProcess().MainModule.FileName;
                string ascii = Path.Combine(Path.GetTempPath(), "wtd-test ansi " + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    // An install from setup 1.4 on a Turkish Windows (cp1254) ...
                    string old = Path.Combine(sb.Root, "Oyunlar", "WolfTeam Diriliş");
                    Installer turkish = new Installer(sb.Options(old), FakePayload(), self);
                    turkish.Ansi = Cp1254;
                    turkish.Install(null);
                    Assert.True(File.Exists(Path.Combine(old, InstallPaths.MarkerName)), "cp1254: installed into Diriliş");

                    // ... is refused (repair) under cp1252, and so is a new folder, before anything is written ...
                    string fresh = Path.Combine(sb.Root, "Oyunlar", "Yeni Diriliş");
                    Installer english = new Installer(sb.Options(fresh), FakePayload(), self);
                    english.Ansi = Cp1252;
                    InstallException e = Assert.Throws<InstallException>(delegate { english.Validate(old); }, "cp1252 repair");
                    Assert.True(e.Message.Contains("axProtect"), e.Message);
                    Assert.Throws<InstallException>(delegate { english.Install(null); }, "cp1252 new install");
                    Assert.False(Directory.Exists(fresh), "nothing written");

                    // ... an ASCII folder is fine under cp1252 ...
                    string asciiDir = Path.Combine(Path.Combine(ascii, "Oyunlar"), InstallPaths.FolderName);
                    Installer ok = new Installer(sb.Options(asciiDir), FakePayload(), self);
                    ok.Ansi = Cp1252;
                    Assert.Equal(asciiDir, ok.Validate(asciiDir), "ASCII under cp1252");

                    // ... and the old install can still be removed on the English Windows.
                    SetupOptions u = sb.Options(null);
                    u.Uninstall = true;
                    Installer remover = new Installer(u, null, self);
                    remover.Ansi = Cp1252;
                    remover.RemoveEverything(old);
                    Assert.False(Directory.Exists(old), "old install removed");
                }
                finally { Runner.DeleteTree(ascii); }
            }
        }

        public static void TestTurkishLettersAndSpacesAreFine()
        {
            Assert.Equal(null, InstallPaths.Check(@"C:\Oyunlar\WolfTeam Diriliş", Env()), "default");
            Assert.Equal(null, InstallPaths.Check(@"D:\Oyunlarım\Kurt Takımı Ğ", Env()), "turkish");
            Assert.Equal(null, InstallPaths.Check(@"C:\Users\ali\Games\WT", Env()), "below the profile is fine");
        }

        public static void TestRejectedFolders()
        {
            string[] bad =
            {
                "", "Oyunlar\\WT", @"C:\", @"D:", @"\\server\share\WT", @"C:\Windows\WT", @"C:\Program Files\WolfTeam",
                @"c:\program files (x86)\WT", @"C:\Users\ali", @"C:\Users\ali\Desktop", @"C:\Users\ali\Documents",
                @"C:\Oyun<lar\WT", @"C:\Oyunlar\" + new string('x', 140)
            };
            foreach (string b in bad) Assert.True(InstallPaths.Check(b, Env()) != null, "should be refused: " + b);
        }

        public static void TestProgramFilesGetsAnExplanation()
        {
            string e = InstallPaths.Check(@"C:\Program Files\WolfTeam", Env());
            Assert.True(e.Contains("Program Files") && e.Contains("C:\\Oyunlar"), e);
        }

        public static void TestFolderKinds()
        {
            string d = Runner.TempDir("kinds");
            try
            {
                string sub = Path.Combine(d, "x");
                Assert.Equal(FolderKind.Missing, InstallPaths.Classify(sub), "missing");
                Directory.CreateDirectory(sub);
                Assert.Equal(FolderKind.Empty, InstallPaths.Classify(sub), "empty");
                File.WriteAllText(Path.Combine(sub, "notes.txt"), "x");
                Assert.Equal(FolderKind.Foreign, InstallPaths.Classify(sub), "someone else's files");
                File.WriteAllText(Path.Combine(sub, "Wolfteam.bin"), "x");
                Assert.Equal(FolderKind.OldPackage, InstallPaths.Classify(sub), "an unzipped package");
                File.WriteAllText(Path.Combine(sub, InstallPaths.MarkerName), "x");
                Assert.Equal(FolderKind.OurInstall, InstallPaths.Classify(sub), "installed by us");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestDiskSpace()
        {
            Assert.Equal(null, InstallPaths.SpaceProblem(5L << 30), "5 GB free");
            string e = InstallPaths.SpaceProblem(1L << 30);
            Assert.True(e != null && e.Contains("4 GB"), e);
        }

        // ------------------------------------------------------------------ command line

        public static void TestSilentInstallWithAFolderThatHasSpaces()
        {
            SetupOptions o = SetupOptions.Parse(@"/S /nodesktop /D=C:\Oyun lar\Kurt Ğ");
            Assert.True(o.Silent && !o.Uninstall && !o.Desktop && o.StartMenu, "flags");
            Assert.Equal(@"C:\Oyun lar\Kurt Ğ", o.Dir, "the rest of the line is the folder");
        }

        public static void TestUninstallSwitches()
        {
            SetupOptions o = SetupOptions.Parse("/uninstall /S /keepprofiles");
            Assert.True(o.Uninstall && o.Silent && o.KeepProfiles, "uninstall flags");
            Assert.Equal(null, o.Dir, "no dir");
            SetupOptions n = SetupOptions.Parse("");
            Assert.True(!n.Silent && n.Desktop && n.StartMenu && !n.Uninstall, "defaults");
        }

        public static void TestQuotedTestOverrides()
        {
            SetupOptions o = SetupOptions.Parse("/S \"/desktopdir=C:\\T mp\\d\" /regkey=Software\\X /D=C:\\A");
            Assert.Equal(@"C:\T mp\d", o.DesktopDir, "desktop override");
            Assert.Equal(@"Software\X", o.RegistryKey, "registry override");
        }

        // ------------------------------------------------------------------ plans

        public static void TestShortcutPlan()
        {
            List<ShortcutSpec> both = ShortcutPlan.Build(@"C:\Oyunlar\WT", @"C:\U\Desktop", @"C:\U\Programs", true, true);
            Assert.Equal(2, both.Count, "two shortcuts");
            Assert.Equal(@"C:\U\Desktop\WolfTeam Diriliş.lnk", both[0].LinkPath, "desktop link");
            Assert.Equal(@"C:\U\Programs\WolfTeam Diriliş\WolfTeam Diriliş.lnk", both[1].LinkPath, "start menu link");
            Assert.Equal(@"C:\Oyunlar\WT\WolfTeamDirilis.exe", both[0].Target, "target");
            Assert.Equal(@"C:\Oyunlar\WT", both[0].WorkingDir, "working dir");
            Assert.Equal(1, ShortcutPlan.Build(@"C:\G", @"C:\D", @"C:\P", false, true).Count, "start menu only");
        }

        public static void TestUninstallEntry()
        {
            Dictionary<string, object> v = UninstallEntry.Values(@"C:\Oyunlar\WT", "1.1", 2048, new DateTime(2026, 9, 30));
            Assert.Equal("WolfTeam Diriliş", v["DisplayName"], "name");
            Assert.Equal("\"C:\\Oyunlar\\WT\\WolfTeamDirilis-Kaldir.exe\" /uninstall", v["UninstallString"], "uninstall");
            Assert.Equal("\"C:\\Oyunlar\\WT\\WolfTeamDirilis-Kaldir.exe\" /uninstall /S", v["QuietUninstallString"], "quiet");
            Assert.Equal(@"C:\Oyunlar\WT", v["InstallLocation"], "location");
            Assert.Equal("20260930", v["InstallDate"], "date");
            Assert.Equal(1, v["NoModify"], "no modify");
            Assert.Equal(2048, v["EstimatedSize"], "size");
        }

        // ------------------------------------------------------------------ remover

        public static void TestRemoverKeepsProfilesWhenAsked()
        {
            string d = Runner.TempDir("rm");
            try
            {
                string game = Path.Combine(d, "game");
                Directory.CreateDirectory(Path.Combine(game, "Profiles"));
                Directory.CreateDirectory(Path.Combine(game, "rez\\deep"));
                File.WriteAllText(Path.Combine(game, InstallPaths.MarkerName), "x");
                File.WriteAllText(Path.Combine(game, "Profiles\\Player.txt"), "mine");
                File.WriteAllText(Path.Combine(game, "rez\\deep\\a.dat"), "a");
                File.SetAttributes(Path.Combine(game, "rez\\deep\\a.dat"), FileAttributes.ReadOnly);
                List<string> failed = Remover.RemoveInstall(game, true);
                Assert.Equal(0, failed.Count, "no failures: " + string.Join(",", failed));
                Assert.True(File.Exists(Path.Combine(game, "Profiles\\Player.txt")), "profiles kept");
                Assert.False(Directory.Exists(Path.Combine(game, "rez")), "game files gone");
                Assert.False(File.Exists(Path.Combine(game, InstallPaths.MarkerName)), "marker gone");
                // A later install into the same folder must accept the kept Profiles folder.
                Assert.Equal(FolderKind.OldPackage, InstallPaths.Classify(game), "only Profiles left: installable again");
                Assert.Throws<InstallException>(delegate { Remover.RemoveInstall(game, false); }, "no marker: never removed");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestRemoverDoesNotFollowJunctions()
        {
            string d = Runner.TempDir("junction");
            try
            {
                string game = Path.Combine(d, "game"), outside = Path.Combine(d, "outside");
                Directory.CreateDirectory(game);
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
                File.WriteAllText(Path.Combine(game, InstallPaths.MarkerName), "x");
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Path.Combine(game, "link") + "\" \"" + outside + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) p.WaitForExit();
                Assert.True(Directory.Exists(Path.Combine(game, "link")), "junction made");
                Remover.RemoveInstall(game, false);
                Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "the junction target survives");
                Assert.False(Directory.Exists(game), "install folder gone");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestRemoverRefusesAFolderWithoutOurMarker()
        {
            string d = Runner.TempDir("nomarker");
            try
            {
                File.WriteAllText(Path.Combine(d, "important.docx"), "x");
                Assert.Throws<InstallException>(delegate { Remover.RemoveInstall(d, false); }, "no marker");
                Assert.True(File.Exists(Path.Combine(d, "important.docx")), "untouched");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestProcessesRunningFromTheFolderAreFound()
        {
            string here = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            List<string> names = ProcessCheck.RunningUnder(here);
            Assert.True(names.Count >= 1, "this test exe runs from " + here);
            Assert.Equal(0, ProcessCheck.RunningUnder(Path.Combine(here, "nothing-runs-here")).Count, "empty folder");
        }

        // ------------------------------------------------------------------ install + uninstall end to end

        private sealed class Sandbox : IDisposable
        {
            public readonly string Root = Runner.TempDir("sandbox");
            public readonly string RegKey = @"Software\WolfTeamDirilisTest\" + Guid.NewGuid().ToString("N");

            public SetupOptions Options(string dir)
            {
                SetupOptions o = SetupOptions.Parse("/S");
                o.Dir = dir;
                o.DesktopDir = Path.Combine(Root, "Desktop");
                o.StartMenuDir = Path.Combine(Root, "Programs");
                o.RegistryKey = RegKey;
                o.AppDataDir = Path.Combine(Root, "AppData");
                Directory.CreateDirectory(o.DesktopDir);
                Directory.CreateDirectory(o.StartMenuDir);
                return o;
            }

            public void Dispose()
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\WolfTeamDirilisTest", false); }
                catch (ArgumentException) { }
                Runner.DeleteTree(Root);
            }
        }

        private static Payload FakePayload()
        {
            Payload p = new Payload();
            p.Add("WolfTeamDirilis.exe", Encoding.ASCII.GetBytes("MZ fake bootstrap"));
            p.Add("launcher\\WolfTeamLauncher.exe", Encoding.ASCII.GetBytes("MZ fake launcher"));
            p.Add("launcher\\endpoints.ini", Encoding.UTF8.GetBytes("UpdateBaseUrl=https://example.org/d/client/\r\n"));
            p.Add("bg.png", new byte[] { 1, 2, 3 });
            p.Add("WolfTeamDirilis.ini", Encoding.UTF8.GetBytes("UpdateBaseUrl=http://127.0.0.1/x/\r\n"));
            p.Add("KURULUM.txt", Encoding.UTF8.GetBytes("kurulum"));
            return p;
        }

        public static void TestInstallThenUninstallLeavesNothingBehind()
        {
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "Oyunlar", "WolfTeam Diriliş");
                SetupOptions o = sb.Options(dir);
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(o, FakePayload(), self).Install(null);

                foreach (string f in new string[] { "WolfTeamDirilis.exe", "bg.png", "WolfTeamDirilis.ini", "KURULUM.txt",
                                                    Installer.UninstallerName, InstallPaths.MarkerName })
                    Assert.True(File.Exists(Path.Combine(dir, f)), "installed " + f);
                Assert.Equal("UpdateBaseUrl=http://127.0.0.1/x/\r\n", File.ReadAllText(Path.Combine(dir, "WolfTeamDirilis.ini")), "ini copied as is");
                string desk = Path.Combine(o.DesktopDir, "WolfTeam Diriliş.lnk");
                Assert.True(File.Exists(desk), "desktop shortcut");
                Assert.Equal(Path.Combine(dir, "WolfTeamDirilis.exe"), Shortcuts.ReadTarget(desk), "shortcut target");
                Assert.True(File.Exists(Path.Combine(o.StartMenuDir, "WolfTeam Diriliş\\WolfTeam Diriliş.lnk")), "start menu shortcut");
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(sb.RegKey))
                {
                    Assert.True(k != null, "uninstall key");
                    Assert.Equal(dir, (string)k.GetValue("InstallLocation"), "InstallLocation");
                }

                // the launcher's own per-install files in %APPDATA% go too
                Directory.CreateDirectory(o.AppDataDir);
                File.WriteAllText(Path.Combine(o.AppDataDir, "user.txt"), "ali");
                File.WriteAllText(Path.Combine(dir, "wolf.xfs"), "downloaded later by the launcher");

                SetupOptions u = sb.Options(null);
                u.Uninstall = true;
                new Installer(u, null, self).RemoveEverything(dir);
                Assert.False(Directory.Exists(dir), "install folder gone");
                Assert.False(File.Exists(desk), "desktop shortcut gone");
                Assert.False(Directory.Exists(Path.Combine(o.StartMenuDir, "WolfTeam Diriliş")), "start menu folder gone");
                Assert.False(Directory.Exists(o.AppDataDir), "launcher settings gone");
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(sb.RegKey)) Assert.True(k == null, "uninstall key gone");
            }
        }

        public static void TestRemoverRefusesAnInstallFolderReachedThroughAJunction()
        {
            // Codex review #1: /D= naming a junction whose target has a marker must not empty the target.
            string d = Runner.TempDir("rootjunction");
            try
            {
                string target = Path.Combine(d, "Belgeler"), link = Path.Combine(d, "Oyun");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, InstallPaths.MarkerName), "x");
                File.WriteAllText(Path.Combine(target, "tez.docx"), "keep");
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) p.WaitForExit();
                Assert.Throws<InstallException>(delegate { Remover.RemoveInstall(link, false); }, "junction root");
                Assert.True(File.Exists(Path.Combine(target, "tez.docx")), "target untouched");
            }
            finally { Runner.DeleteTree(d); }
        }

        public static void TestUninstallRemovesOnlyItsOwnRegistryEntryAndLauncherFiles()
        {
            // Codex review #2: an entry without InstallLocation is not ours; only the launcher's files leave %APPDATA%.
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "Oyun");
                SetupOptions o = sb.Options(dir);
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(o, FakePayload(), self).Install(null);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(sb.RegKey)) k.DeleteValue("InstallLocation");
                Directory.CreateDirectory(o.AppDataDir);
                File.WriteAllText(Path.Combine(o.AppDataDir, "user.txt"), "ali");
                string g = InstallLock.KeyOf(dir);
                File.WriteAllText(Path.Combine(o.AppDataDir, "hashcache-" + g + ".tsv"), "x");
                File.WriteAllText(Path.Combine(o.AppDataDir, "verified-" + g + ".json"), "{}");
                File.WriteAllText(Path.Combine(o.AppDataDir, "seed-once-" + g + ".txt"), "config.xfs\tid1");
                File.WriteAllText(Path.Combine(o.AppDataDir, "tez.docx"), "not the launcher's");
                SetupOptions u = sb.Options(null);
                u.Uninstall = true;
                new Installer(u, null, self).RemoveEverything(dir);
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(sb.RegKey)) Assert.True(k != null, "foreign-looking entry kept");
                Assert.False(File.Exists(Path.Combine(o.AppDataDir, "user.txt")), "launcher file removed");
                Assert.False(File.Exists(Path.Combine(o.AppDataDir, "hashcache-" + g + ".tsv")), "cache removed");
                Assert.False(File.Exists(Path.Combine(o.AppDataDir, "verified-" + g + ".json")), "last verified list removed");
                Assert.False(File.Exists(Path.Combine(o.AppDataDir, "seed-once-" + g + ".txt")), "replace-once record removed");
                Assert.True(File.Exists(Path.Combine(o.AppDataDir, "tez.docx")), "other file kept");
            }
        }

        /// <summary>Holds the install lock of {dir} (launcher\install.lock, what a running bootstrap holds) while {body} runs.</summary>
        private static void WhileHandingOver(string dir, Action body)
        {
            string path = InstallLock.LockPath(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) body();
        }

        public static void TestSetupAndUninstallWaitForALauncherSelfUpdate()
        {
            // Codex approval gate (launcher 1.3) #5: a repair or an uninstall must not run while the launcher's helper
            // (running from %APPDATA%, invisible to the folder process check) replaces or restores the exe.
            int before = InstallLock.TimeoutMs;
            InstallLock.TimeoutMs = 500;
            try
            {
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                    File.WriteAllText(Path.Combine(dir, "WolfTeamDirilis.exe"), "the bootstrap of the install");
                    WhileHandingOver(dir, delegate
                    {
                        InstallException e = Assert.Throws<InstallException>(delegate
                        {
                            new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        }, "repair while a bootstrap holds the lock");
                        Assert.True(e.Message.Contains("Başlatıcı şu an"), e.Message);
                        Assert.Equal("the bootstrap of the install", File.ReadAllText(Path.Combine(dir, "WolfTeamDirilis.exe")), "nothing written");
                    });
                    new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                    Assert.Equal("MZ fake bootstrap", File.ReadAllText(Path.Combine(dir, "WolfTeamDirilis.exe")), "repairs once the lock is free");
                    SetupOptions u = sb.Options(null);
                    u.Uninstall = true;
                    WhileHandingOver(dir, delegate
                    {
                        Assert.Throws<InstallException>(delegate { new Installer(u, null, self).RemoveEverything(dir); }, "uninstall during a hand-over");
                        Assert.True(File.Exists(Path.Combine(dir, "WolfTeamDirilis.exe")), "nothing removed");
                    });
                    new Installer(u, null, self).RemoveEverything(dir);
                    Assert.False(Directory.Exists(dir), "removed once the lock is free");
                }
            }
            finally { InstallLock.TimeoutMs = before; }
        }

        public static void TestSetupAndUninstallWaitForAnUpdateAndRecheckProcesses()
        {
            // Gate 1.4b #2: maintenance takes install.lock, then update.lock, then checks the processes again, and holds
            // both through every write.
            int before = InstallLock.TimeoutMs, settle = MaintenanceLock.SettleMs;
            InstallLock.TimeoutMs = 500;
            MaintenanceLock.SettleMs = 300;
            try
            {
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                    string exe = Path.Combine(dir, "WolfTeamDirilis.exe");
                    File.WriteAllText(exe, "the bootstrap of the install");
                    string upd = BootLayout.UpdateLockPath(dir);
                    SetupOptions u = sb.Options(null);
                    u.Uninstall = true;

                    // 1. a launcher's update holds update.lock (the bootstrap already confirmed and let install.lock go)
                    using (new FileStream(upd, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        InstallException e = Assert.Throws<InstallException>(delegate
                        {
                            new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        }, "repair during an update");
                        Assert.True(e.Message.Contains("Başlatıcı şu an"), e.Message);
                        Assert.Equal("the bootstrap of the install", File.ReadAllText(exe), "nothing written");
                        Assert.Throws<InstallException>(delegate { new Installer(u, null, self).RemoveEverything(dir); }, "uninstall during an update");
                        Assert.True(File.Exists(exe), "nothing removed");
                    }

                    // 2. a launcher started after the first check, from the folder, and still runs: refused after the locks
                    string fake = Path.Combine(dir, "launcher\\running-launcher.exe");
                    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake, true); // any program running from the folder
                    Process running = null;
                    MaintenanceLock.AfterLocks = delegate(string d)
                    {
                        if (running != null) return;
                        ProcessStartInfo psi = new ProcessStartInfo(fake, "/d /c ping -n 30 127.0.0.1 >nul");
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        running = Process.Start(psi);
                        System.Threading.Thread.Sleep(300);
                    };
                    try
                    {
                        InstallException e2 = Assert.Throws<InstallException>(delegate
                        {
                            new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        }, "a launcher that started meanwhile");
                        Assert.True(e2.Running, "reported as running: " + e2.Message);
                        Assert.Equal("the bootstrap of the install", File.ReadAllText(exe), "nothing written");
                        Assert.Throws<InstallException>(delegate { new Installer(u, null, self).RemoveEverything(dir); }, "uninstall too");
                        Assert.True(File.Exists(exe), "nothing removed");
                        // and the locks were let go: a bootstrap can start again
                        using (new FileStream(InstallLock.LockPath(dir), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        using (new FileStream(upd, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    }
                    finally
                    {
                        MaintenanceLock.AfterLocks = null;
                        if (running != null)
                        {
                            if (!running.HasExited) running.Kill(); // only the test's own process, started above
                            running.WaitForExit(10000);
                        }
                    }

                    // 2b. only a waiting bootstrap (e.g. the restart after an update) is left: refused as "busy, retry",
                    // not as "close the launcher"; the locks are let go so it can go on
                    string bootCopy = Path.Combine(dir, "WolfTeamDirilis.exe");
                    string bootBefore = File.ReadAllText(bootCopy);
                    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), bootCopy, true);
                    running = null;
                    MaintenanceLock.AfterLocks = delegate(string d)
                    {
                        if (running != null) return;
                        ProcessStartInfo psi = new ProcessStartInfo(bootCopy, "/d /c ping -n 30 127.0.0.1 >nul");
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        running = Process.Start(psi);
                        System.Threading.Thread.Sleep(300);
                    };
                    try
                    {
                        InstallException e3 = Assert.Throws<InstallException>(delegate
                        {
                            new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        }, "a waiting bootstrap");
                        Assert.Equal(InstallLock.BusyText, e3.Message, "busy, retry");
                    }
                    finally
                    {
                        MaintenanceLock.AfterLocks = null;
                        if (running != null)
                        {
                            if (!running.HasExited) running.Kill(); // only the test's own process, started above
                            running.WaitForExit(10000);
                        }
                    }
                    System.Threading.Thread.Sleep(300);
                    File.WriteAllText(bootCopy, bootBefore);

                    // 3. during the writes both locks are held: no bootstrap start and no launcher update gets in
                    bool installHeld = false, updateHeld = false;
                    Installer.Fault = delegate(string step)
                    {
                        if (step != Payload.Endpoints) return;
                        try { using (new FileStream(InstallLock.LockPath(dir), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                        catch (IOException) { installHeld = true; }
                        try { using (new FileStream(upd, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                        catch (IOException) { updateHeld = true; }
                    };
                    try { new Installer(sb.Options(dir), FakePayload(), self).Install(null); }
                    finally { Installer.Fault = null; }
                    Assert.True(installHeld && updateHeld, "both locks held while writing");
                    Assert.Equal("MZ fake bootstrap", File.ReadAllText(exe), "repaired once free");
                    new Installer(u, null, self).RemoveEverything(dir);
                    Assert.False(Directory.Exists(dir), "uninstalled once free, lock files included");
                }
            }
            finally
            {
                InstallLock.TimeoutMs = before;
                MaintenanceLock.SettleMs = settle;
            }
        }

        public static void TestASetupRightAfterAnUninstallKeepsEverything()
        {
            // Gate 1.4b #2 (re-review): the uninstall holds its maintenance lock through the shortcuts, the uninstall entry
            // and the settings; after the lock only empty folders go. A setup into the same folder right then keeps all.
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                SetupOptions u = sb.Options(null);
                u.Uninstall = true;
                bool reinstalled = false;
                Installer.AfterUninstallUnlocked = delegate(string d)
                {
                    new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                    reinstalled = true;
                };
                List<string> failed;
                try { failed = new Installer(u, null, self).RemoveEverything(dir); }
                finally { Installer.AfterUninstallUnlocked = null; }
                Assert.True(reinstalled, "the setup ran at the boundary");
                Assert.Equal(0, failed.Count, "uninstall reports nothing failed: " + string.Join(", ", failed.ToArray()));
                Assert.True(File.Exists(Path.Combine(dir, InstallPaths.MarkerName)), "the new install's marker stays");
                Assert.Equal("MZ fake bootstrap", File.ReadAllText(Path.Combine(dir, "WolfTeamDirilis.exe")), "its bootstrap stays");
                Assert.True(File.Exists(Path.Combine(dir, "launcher\\WolfTeamLauncher.exe")), "its launcher stays");
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sb.RegKey))
                    Assert.True(k != null, "its uninstall entry stays");
                Assert.True(Directory.GetFiles(Path.Combine(sb.Root, "Desktop"), "*.lnk").Length > 0, "its shortcut stays");
                new Installer(u, null, self).RemoveEverything(dir);
                Assert.False(Directory.Exists(dir), "and a later uninstall removes it all");
            }
        }

        public static void TestAFailedFreshInstallNeverRemovesAnotherSetupsWork()
        {
            // Gate 1.4b #2 (re-review 3): a failed fresh install cleans up inside its maintenance lock; afterwards only
            // empty folders go, and without the lock nothing but empty folders.
            int before = InstallLock.TimeoutMs;
            InstallLock.TimeoutMs = 500;
            try
            {
                // 1. A fails (its desktop shortcut cannot be made) and lets the lock go; B installs right then.
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    SetupOptions a = sb.Options(dir);
                    a.DesktopDir = Path.Combine(sb.Root, "not-a-folder");
                    File.WriteAllText(a.DesktopDir, "a file where the desktop folder should be");
                    bool bRan = false;
                    Installer.AfterFailedInstallUnlocked = delegate(string d)
                    {
                        new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        bRan = true;
                    };
                    try
                    {
                        Assert.Throws<InstallException>(delegate { new Installer(a, FakePayload(), self).Install(null); }, "A fails");
                    }
                    finally { Installer.AfterFailedInstallUnlocked = null; }
                    Assert.True(bRan, "B ran at the boundary");
                    Assert.True(File.Exists(Path.Combine(dir, InstallPaths.MarkerName)), "B's marker stays");
                    Assert.Equal("MZ fake bootstrap", File.ReadAllText(Path.Combine(dir, "WolfTeamDirilis.exe")), "B's bootstrap stays");
                    Assert.True(File.Exists(Path.Combine(dir, "launcher\\WolfTeamLauncher.exe")), "B's launcher stays");
                }

                // 2. A cannot get the lock (B holds it, B's work is in the folder A just made): A removes nothing.
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    FileStream held = null;
                    Installer.BeforeLock = delegate(string d)
                    {
                        string lp = InstallLock.LockPath(d);
                        Directory.CreateDirectory(Path.GetDirectoryName(lp));
                        held = new FileStream(lp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        File.WriteAllText(Path.Combine(d, "B-is-writing.txt"), "B");
                    };
                    try
                    {
                        InstallException e = Assert.Throws<InstallException>(delegate { new Installer(sb.Options(dir), FakePayload(), self).Install(null); }, "busy");
                        Assert.True(e.Message.Contains("Başlatıcı şu an"), e.Message);
                        Assert.True(File.Exists(Path.Combine(dir, "B-is-writing.txt")), "B's work stays");
                        Assert.True(File.Exists(InstallLock.LockPath(dir)), "B's lock file stays");
                    }
                    finally
                    {
                        Installer.BeforeLock = null;
                        if (held != null) held.Dispose();
                    }
                }

                // 2b. B installs completely into the folder A just made, before A takes the lock; A then fails: A
                // removes nothing of B (ownership is checked again under the lock)
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    SetupOptions a = sb.Options(dir);
                    a.DesktopDir = Path.Combine(sb.Root, "not-a-folder");
                    File.WriteAllText(a.DesktopDir, "x");
                    Installer.BeforeLock = delegate(string d)
                    {
                        Installer.BeforeLock = null;
                        new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        File.WriteAllText(Path.Combine(d, "Wolfteam.bin"), "B's game file");
                    };
                    try
                    {
                        Assert.Throws<InstallException>(delegate { new Installer(a, FakePayload(), self).Install(null); }, "A fails");
                    }
                    finally { Installer.BeforeLock = null; }
                    Assert.True(File.Exists(Path.Combine(dir, InstallPaths.MarkerName)), "B's marker stays");
                    Assert.Equal("B's game file", File.ReadAllText(Path.Combine(dir, "Wolfteam.bin")), "B's game file stays");
                    Assert.True(File.Exists(Path.Combine(dir, "WolfTeamDirilis.exe")), "B's bootstrap stays");
                }

                // 2b'. the same, but the ownership check itself fails (the launcher folder cannot be listed): an error
                // means "not ours", B's files survive
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    SetupOptions a = sb.Options(dir);
                    a.DesktopDir = Path.Combine(sb.Root, "not-a-folder");
                    File.WriteAllText(a.DesktopDir, "x");
                    string launcherDir = Path.Combine(dir, "launcher");
                    System.Security.AccessControl.FileSystemAccessRule deny = new System.Security.AccessControl.FileSystemAccessRule(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User,
                        System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
                    Installer.BeforeLock = delegate(string d)
                    {
                        Installer.BeforeLock = null;
                        Directory.CreateDirectory(launcherDir);
                        File.WriteAllText(Path.Combine(launcherDir, "B-work.txt"), "B");
                        File.WriteAllText(Path.Combine(d, "z-B-root.txt"), "B's root file"); // listed after "launcher"
                        System.Security.AccessControl.DirectorySecurity sec = Directory.GetAccessControl(launcherDir);
                        sec.AddAccessRule(deny);
                        Directory.SetAccessControl(launcherDir, sec);
                    };
                    try
                    {
                        Assert.Throws<InstallException>(delegate { new Installer(a, FakePayload(), self).Install(null); }, "A fails");
                        Assert.Equal("B's root file", File.ReadAllText(Path.Combine(dir, "z-B-root.txt")), "B's root file survives");
                        Assert.Equal("B", File.ReadAllText(Path.Combine(launcherDir, "B-work.txt")), "B's launcher-folder file survives");
                    }
                    finally
                    {
                        Installer.BeforeLock = null;
                        if (Directory.Exists(launcherDir))
                        {
                            System.Security.AccessControl.DirectorySecurity sec = Directory.GetAccessControl(launcherDir);
                            sec.RemoveAccessRule(deny);
                            Directory.SetAccessControl(launcherDir, sec);
                        }
                    }
                }

                // 2c. a busy install leaves a file under the old probe name alone (no probe before the lock)
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                    string probe = Path.Combine(dir, "yazma-denemesi.tmp");
                    File.WriteAllText(probe, "someone's file");
                    using (new FileStream(InstallLock.LockPath(dir), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Throws<InstallException>(delegate { new Installer(sb.Options(dir), FakePayload(), self).Install(null); }, "busy");
                    }
                    Assert.Equal("someone's file", File.ReadAllText(probe), "unchanged");
                    Assert.Equal(0, Directory.GetFiles(dir, "yazma-denemesi-*.tmp").Length, "no probe left");
                }

                // 3. a failed fresh install alone still leaves no folder behind
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    SetupOptions a = sb.Options(dir);
                    a.DesktopDir = Path.Combine(sb.Root, "not-a-folder");
                    File.WriteAllText(a.DesktopDir, "x");
                    Assert.Throws<InstallException>(delegate { new Installer(a, FakePayload(), self).Install(null); }, "fails");
                    Assert.False(Directory.Exists(dir), "no half folder");
                }
            }
            finally { InstallLock.TimeoutMs = before; }
        }

        public static void TestTheSetupVersionResourceMatchesTheVersion()
        {
            string four = SetupInfo.Version;
            for (int i = SetupInfo.Version.Split('.').Length; i < 4; i++) four += ".0";
            Assert.Equal(four, SetupInfo.FileVersion, "SetupInfo.FileVersion is SetupInfo.Version in four parts");
            string self = Process.GetCurrentProcess().MainModule.FileName; // built with SetupAssemblyInfo.cs
            Assert.Equal(four, FileVersionInfo.GetVersionInfo(self).FileVersion, "the version resource");
        }

        public static void TestThisSetupIdentifiesAsARepairSetup()
        {
            // The version resource (SetupAssemblyInfo.cs) and the recorded hash the bootstrap checks before it offers the
            // repair (gate 1.4d #4).
            string self = Process.GetCurrentProcess().MainModule.FileName; // built from the setup sources
            Assert.True(BootLayout.IsRepairSetupFile(self), "setup " + SetupInfo.Version + " is offered for repair");
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                string recorded = BootLayout.RecordedSetupHash(dir);
                Assert.Equal(Installer.Sha256Hex(self), recorded, "the marker records this setup's SHA-256");
                Assert.Equal(recorded, Installer.Sha256Hex(Path.Combine(dir, Installer.UninstallerName)), "= the setup copy");
            }
        }

        public static void TestAProcessOfOursThatCannotBeInspectedCountsAsRunning()
        {
            // Gate 1.4d #1: fail closed. A WolfTeam process the setup may not inspect (another Windows user's) could be
            // playing from this folder: maintenance refuses. Other processes it cannot inspect are not ours.
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                string elsewhere = Path.Combine(sb.Root, "other");
                Directory.CreateDirectory(elsewhere);
                string fake = Path.Combine(elsewhere, "WolfTeamLauncher.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake, true);
                ProcessStartInfo psi = new ProcessStartInfo(fake, "/d /c ping -n 30 127.0.0.1 >nul");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                try
                {
                    int pid = p.Id;
                    Assert.Equal(0, ProcessCheck.RunningUnder(dir, -1).Count, "inspectable and elsewhere: not running here");
                    // the real query for every other process, a failed one (null) for this one
                    ProcessCheck.ImagePathOverride = delegate(int id) { return id == pid ? null : ProcessCheck.QueryImage(id); };
                    int settle = MaintenanceLock.SettleMs;
                    MaintenanceLock.SettleMs = 200;
                    try
                    {
                        List<string> running = ProcessCheck.RunningUnder(dir, -1);
                        Assert.True(running.Count == 1 && running[0].StartsWith("WolfTeamLauncher"), "counted: " + string.Join(", ", running.ToArray()));
                        InstallException e = Assert.Throws<InstallException>(delegate
                        {
                            new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        }, "repair refused");
                        Assert.True(e.Running, e.Message);
                    }
                    finally
                    {
                        ProcessCheck.ImagePathOverride = null;
                        MaintenanceLock.SettleMs = settle;
                    }
                }
                finally
                {
                    if (!p.HasExited) p.Kill(); // only the test's own process, started above
                    p.WaitForExit(10000);
                }
            }
        }

        public static void TestUninstallRemovesOnlyThisInstallsSelfUpdateFiles()
        {
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                SetupOptions o = sb.Options(dir);
                new Installer(o, FakePayload(), self).Install(null);
                string mine = InstallLock.KeyOf(Path.Combine(dir, "WolfTeamDirilis.exe")),
                       other = InstallLock.KeyOf(Path.Combine(sb.Root, "Başka\\WolfTeamDirilis.exe"));
                string mineDir = InstallLock.KeyOf(dir), otherDir = InstallLock.KeyOf(Path.Combine(sb.Root, "Başka"));
                Directory.CreateDirectory(o.AppDataDir);
                File.WriteAllText(Path.Combine(o.AppDataDir, "verified-" + mineDir + ".json"), "{}");
                File.WriteAllText(Path.Combine(o.AppDataDir, "verified-" + otherDir + ".json"), "{}");
                File.WriteAllText(Path.Combine(o.AppDataDir, "hashcache-" + otherDir + ".tsv"), "x");
                File.WriteAllText(Path.Combine(o.AppDataDir, "user.txt"), "ali");
                Directory.CreateDirectory(o.AppDataDir);
                foreach (string n in new string[] { "selfupdate-{0}.txt", "selfupdate-trial-{0}.txt", "selfupdate-failed-{0}.txt", "selfupdate-helper-{0}.exe" })
                {
                    File.WriteAllText(Path.Combine(o.AppDataDir, string.Format(n, mine)), "x");
                    File.WriteAllText(Path.Combine(o.AppDataDir, string.Format(n, other)), "x");
                }
                SetupOptions u = sb.Options(null);
                u.Uninstall = true;
                new Installer(u, null, self).RemoveEverything(dir);
                Assert.False(File.Exists(Path.Combine(o.AppDataDir, "selfupdate-trial-" + mine + ".txt")), "own trial removed");
                Assert.False(File.Exists(Path.Combine(o.AppDataDir, "selfupdate-helper-" + mine + ".exe")), "own helper removed");
                Assert.True(File.Exists(Path.Combine(o.AppDataDir, "selfupdate-trial-" + other + ".txt")), "another install's trial kept");
                Assert.True(File.Exists(Path.Combine(o.AppDataDir, "selfupdate-" + other + ".txt")), "another install's record kept");
                // approval gate 1.3 #4: the other install's verified list and hash cache stay, and so does the shared user name
                Assert.False(File.Exists(Path.Combine(o.AppDataDir, "verified-" + mineDir + ".json")), "own verified list removed");
                Assert.True(File.Exists(Path.Combine(o.AppDataDir, "verified-" + otherDir + ".json")), "other verified list kept");
                Assert.True(File.Exists(Path.Combine(o.AppDataDir, "hashcache-" + otherDir + ".tsv")), "other hash cache kept");
                Assert.True(File.Exists(Path.Combine(o.AppDataDir, "user.txt")), "user name kept while another install remains");
            }
        }

        public static void TestARepairKeepsTheLauncherVersionsAndResetsOnlyALostState()
        {
            // Codex 1.4 round 2 #2: a repair must not destroy the confirmed fallback version; only a lost state is reset.
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                string version = Path.Combine(dir, "launcher\\versions\\aaaaaaaaaaaaaaaa\\WolfTeamLauncher.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(version));
                File.WriteAllText(version, "confirmed launcher");
                BootState s = new BootState();
                s.Current = new string('a', 64);
                s.MarkFailed(new string('b', 64));
                File.WriteAllText(BootLayout.StatePath(dir), s.Text());
                File.WriteAllText(BootLayout.StateBackupPath(dir), s.Text());
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                Assert.True(File.Exists(version), "the confirmed version stays");
                Assert.Equal(s.Text(), File.ReadAllText(BootLayout.StatePath(dir)), "the state stays");
                File.WriteAllText(BootLayout.StatePath(dir), "garbage");
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                Assert.True(File.Exists(version), "the copy still holds the state: nothing reset");
                File.WriteAllText(BootLayout.StateBackupPath(dir), "garbage");
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                Assert.False(File.Exists(BootLayout.StatePath(dir)) || File.Exists(BootLayout.StateBackupPath(dir)), "a lost state is removed");
                Assert.False(Directory.Exists(Path.Combine(dir, "launcher\\versions")), "and the versions: a genuine first start");
                Assert.True(File.Exists(Path.Combine(dir, "launcher\\WolfTeamLauncher.exe")), "the new launcher file is in place");
            }
        }

        private sealed class SetupKilled : Exception { }

        public static void TestASetupMigrationFrom12KilledAtAnyStepLeavesAWorkingShortcut()
        {
            // Gate 1.4 #2: 1.2 installs migrate only with setup 1.4. Killed after any step, the shortcut's exe is the 1.2
            // launcher or the bootstrap (never missing), and the next setup run completes the migration.
            // the order of the staged install (gate 1.4e): stage all, unpin, the bootstrap first, then the launcher file...
            List<string> steps = new List<string> { "staged", "unpinned", "WolfTeamDirilis.exe", "launcher\\WolfTeamLauncher.exe",
                Installer.UninstallerName, InstallPaths.MarkerName, Payload.Endpoints, "bg.png", "WolfTeamDirilis.ini", "KURULUM.txt" };
            foreach (string step in steps)
            {
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    Directory.CreateDirectory(dir);
                    string exe = Path.Combine(dir, "WolfTeamDirilis.exe");
                    File.WriteAllText(exe, "MZ the 1.2 launcher");
                    File.WriteAllText(Path.Combine(dir, "Wolfteam.bin"), "game");
                    string stop = step;
                    Installer.Fault = delegate(string s) { if (s == stop) throw new SetupKilled(); };
                    try
                    {
                        new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                        throw new TestFailure("not killed at " + step);
                    }
                    catch (SetupKilled) { }
                    finally { Installer.Fault = null; }
                    string now = File.ReadAllText(exe);
                    Assert.True(now == "MZ the 1.2 launcher" || now == "MZ fake bootstrap", step + ": the shortcut's exe is a whole launcher");
                    Assert.Equal(steps.IndexOf(step) < steps.IndexOf("WolfTeamDirilis.exe") ? "MZ the 1.2 launcher" : "MZ fake bootstrap", now,
                        step + ": 1.2 until the bootstrap move, the bootstrap after it");
                    if (steps.IndexOf(step) > steps.IndexOf("WolfTeamDirilis.exe"))
                        Assert.Equal("MZ fake launcher", File.ReadAllText(Path.Combine(dir, "launcher\\WolfTeamLauncher.exe")), step + ": the launcher file right after the bootstrap");
                    if (steps.IndexOf(step) < steps.IndexOf("WolfTeamDirilis.exe"))
                        Assert.False(File.Exists(Path.Combine(dir, "launcher\\WolfTeamLauncher.exe")), step + ": nothing moved in before the bootstrap");
                    new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                    Assert.Equal("MZ fake bootstrap", File.ReadAllText(exe), step + ": the next run completes");
                    Assert.False(File.Exists(exe + ".old"), step + ": no 1.2 copy is kept (never a fallback, gate 1.4b #1)");
                }
            }
        }

        public static void TestA12StartDuringTheMigrationIsBlockedOrAbortsIt()
        {
            // Gate 1.4e: a 1.2 launcher honours no lock. After the final process check it is pinned (Windows cannot start
            // it); a start that slips in after the pin is let go makes the bootstrap move fail, and nothing has changed.
            // Stand-in for the 1.2 launcher: a copy of cmd.exe; with WT_TEST_REAL12_EXE set, also the real 1.2 binary.
            List<string> stand = new List<string> { Path.Combine(Environment.SystemDirectory, "cmd.exe") };
            string real12 = Environment.GetEnvironmentVariable("WT_TEST_REAL12_EXE");
            if (!string.IsNullOrEmpty(real12))
            {
                if (!File.Exists(real12)) throw new TestFailure("WT_TEST_REAL12_EXE is set but missing: " + real12);
                stand.Add(real12);
            }
            foreach (string old in stand)
            {
                using (Sandbox sb = new Sandbox())
                {
                    string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    Directory.CreateDirectory(dir);
                    string exe = Path.Combine(dir, "WolfTeamDirilis.exe");
                    File.Copy(old, exe);
                    File.WriteAllText(Path.Combine(dir, "Wolfteam.bin"), "game");
                    bool blocked = false;
                    Installer.Fault = delegate(string s)
                    {
                        if (s != "staged") return;
                        // the player clicks the shortcut while the setup is paused before the bootstrap move
                        ProcessStartInfo psi = new ProcessStartInfo(exe, "/d /c exit 0");
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        try
                        {
                            using (Process p = Process.Start(psi)) { if (!p.WaitForExit(10000)) p.Kill(); }
                        }
                        catch (System.ComponentModel.Win32Exception e) { blocked = e.NativeErrorCode == 32; } // ERROR_SHARING_VIOLATION only
                    };
                    try { new Installer(sb.Options(dir), FakePayload(), self).Install(null); }
                    finally { Installer.Fault = null; }
                    Assert.True(blocked, Path.GetFileName(old) + ": the old launcher cannot start while the setup holds it");
                    Assert.Equal("MZ fake bootstrap", File.ReadAllText(exe), "migrated");
                }
            }

            // a start that slips in between the unpin and the bootstrap move: the move fails, nothing has changed
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                Directory.CreateDirectory(dir);
                string exe = Path.Combine(dir, "WolfTeamDirilis.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
                byte[] before = File.ReadAllBytes(exe);
                File.WriteAllText(Path.Combine(dir, "Wolfteam.bin"), "game");
                Process running = null;
                Installer.Fault = delegate(string s)
                {
                    if (s != "unpinned") return;
                    ProcessStartInfo psi = new ProcessStartInfo(exe, "/d /c ping -n 30 127.0.0.1 >nul");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    running = Process.Start(psi);
                    System.Threading.Thread.Sleep(300);
                };
                try
                {
                    InstallException e = Assert.Throws<InstallException>(delegate { new Installer(sb.Options(dir), FakePayload(), self).Install(null); }, "aborted");
                    Assert.True(e.Running, e.Message);
                    Assert.True(running != null, "the old launcher started in the gap");
                    Assert.Equal(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(exe)), "the old launcher is untouched");
                    Assert.False(File.Exists(Path.Combine(dir, "launcher\\WolfTeamLauncher.exe")), "no launcher file moved in");
                    Assert.False(File.Exists(Path.Combine(dir, "launcher\\endpoints.ini")), "no endpoints moved in");
                    Assert.Equal(0, Directory.GetFiles(dir, "*.kurulum-tmp", SearchOption.AllDirectories).Length, "no temp file left");
                }
                finally
                {
                    Installer.Fault = null;
                    if (running != null)
                    {
                        if (!running.HasExited) running.Kill(); // only the test's own process, started above
                        running.WaitForExit(10000);
                    }
                }
            }
        }

        public static void TestAnInterruptedUpgradeFrom12LeavesAWorkingLauncher()
        {
            // Codex 1.4 round 4 / gate 1.4e: while the old launcher is in use (opened by someone) the setup cannot pin it
            // and changes nothing; the shortcut keeps starting it. Once free, the bootstrap replaces it; no copy is kept.
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                string exe = Path.Combine(dir, "WolfTeamDirilis.exe"), launcher = Path.Combine(dir, "launcher\\WolfTeamLauncher.exe");
                File.WriteAllText(exe, "MZ the 1.2 launcher");
                File.Delete(launcher);
                using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read)) // in use: cannot be pinned
                {
                    InstallException e = Assert.Throws<InstallException>(delegate { new Installer(sb.Options(dir), FakePayload(), self).Install(null); }, "stopped");
                    Assert.True(e.Running, e.Message);
                }
                Assert.Equal("MZ the 1.2 launcher", File.ReadAllText(exe), "the shortcut still starts the 1.2 launcher");
                Assert.False(File.Exists(launcher), "nothing was written");
                Assert.Equal(0, Directory.GetFiles(dir, "*.kurulum-tmp", SearchOption.AllDirectories).Length, "no temp file");
                File.WriteAllText(exe + ".old", "MZ a 1.2 copy from a pre-release setup");
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                Assert.Equal("MZ fake bootstrap", File.ReadAllText(exe), "the bootstrap once the exe is free");
                Assert.False(File.Exists(exe + ".old"), "an old 1.2 copy is removed");
            }
        }

        public static void TestARepairThatCannotReplaceTheBootstrapLeavesTheOldOne()
        {
            // Codex 1.4 round 3 #1: the files are replaced with one atomic move, never deleted first.
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                string boot = Path.Combine(dir, "WolfTeamDirilis.exe");
                File.WriteAllText(boot, "the working bootstrap");
                using (new FileStream(boot, FileMode.Open, FileAccess.Read, FileShare.Read)) // in use: it cannot be replaced
                {
                    Assert.Throws<InstallException>(delegate { new Installer(sb.Options(dir), FakePayload(), self).Install(null); }, "repair fails");
                }
                Assert.Equal("the working bootstrap", File.ReadAllText(boot), "the old bootstrap is still there");
                Assert.False(File.Exists(boot + ".kurulum-tmp"), "no temp file left");
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                Assert.Equal("MZ fake bootstrap", File.ReadAllText(boot), "replaced once it is free");
            }
        }

        public static void TestARepairGivesItsOwnRejectedLauncherANewTrial()
        {
            // Codex 1.4 round 5 #2: a first start killed before confirmation rejected the only launcher; the same setup's
            // repair must let it be tried again, keeping confirmed versions and other rejections.
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam Diriliş");
                string self = Process.GetCurrentProcess().MainModule.FileName;
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                string h;
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                    h = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(BootLayout.StagedLauncher(dir)))).Replace("-", "").ToLowerInvariant();
                string other = new string('c', 64), confirmed = new string('a', 64);
                BootState s = new BootState();
                s.Current = confirmed;
                s.MarkFailed(other);
                s.MarkFailed(h);
                File.WriteAllText(BootLayout.StatePath(dir), s.Text());
                File.WriteAllText(BootLayout.StateBackupPath(dir), s.Text());
                new Installer(sb.Options(dir), FakePayload(), self).Install(null);
                BootState t = BootState.Read(BootLayout.StatePath(dir));
                Assert.False(t.Failed.Contains(h), "the bundled launcher may be tried again");
                Assert.True(t.Failed.Contains(other) && t.Current == confirmed, "other rejections and the confirmed version stay");
                Assert.Equal(t.Text(), BootState.Read(BootLayout.StateBackupPath(dir)).Text(), "the copy too");
            }
        }

        public static void TestInstallRefusesAFolderWithSomeoneElsesFiles()
        {
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "Belgeler");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "tez.docx"), "x");
                InstallException e = Assert.Throws<InstallException>(delegate
                {
                    new Installer(sb.Options(dir), FakePayload(), Process.GetCurrentProcess().MainModule.FileName).Install(null);
                }, "foreign folder");
                Assert.True(e.Message.Contains("boş"), e.Message);
                Assert.Equal(1, Directory.GetFileSystemEntries(dir).Length, "nothing written");
            }
        }

        public static void TestUpgradingAnUnzippedPackageRemovesTheOldBatchStarter()
        {
            using (Sandbox sb = new Sandbox())
            {
                string dir = Path.Combine(sb.Root, "WolfTeam");
                Directory.CreateDirectory(dir);
                foreach (string f in new string[] { "Wolfteam.bin", "OYNA.bat", "WolfTeamLauncher.ps1", "wolf.xfs" })
                    File.WriteAllText(Path.Combine(dir, f), "old");
                new Installer(sb.Options(dir), FakePayload(), Process.GetCurrentProcess().MainModule.FileName).Install(null);
                Assert.False(File.Exists(Path.Combine(dir, "OYNA.bat")), "OYNA.bat removed");
                Assert.False(File.Exists(Path.Combine(dir, "WolfTeamLauncher.ps1")), "old PowerShell launcher removed");
                Assert.True(File.Exists(Path.Combine(dir, "wolf.xfs")), "game files kept (the launcher checks them)");
                Assert.True(File.Exists(Path.Combine(dir, InstallPaths.MarkerName)), "now our install");
            }
        }
    }
}
