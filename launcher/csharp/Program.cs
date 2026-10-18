// WolfTeam Diriliş launcher entry point (WolfTeamLauncher.exe; started by the bootstrap WolfTeamDirilis.exe, BootLayout.cs).
//   WolfTeamLauncher.exe                    normal UI
//   WolfTeamLauncher.exe --selftest         load WolfTeamDirilis.ini, probe the server ports, fetch manifest + news, compare the
//                                           local files; prints to stdout, exit code 0 = all good, 1 = something failed.
//                                           No login and no game start.
//   extra flags: --apply-updates (with --selftest: also download what differs), --config <file>, --gamedir <dir>
//   from the bootstrap: --gamedir <install>, --confirm-pipe <handle> (Handover.cs), --rolled-back (a new version did
//   not start and this one runs instead)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("WolfTeam Diriliş başlatıcısı")]
[assembly: System.Reflection.AssemblyProduct("WolfTeam Diriliş")]
[assembly: System.Reflection.AssemblyVersion(WolfTeamDirilis.Program.FileVersion)]
[assembly: System.Reflection.AssemblyFileVersion(WolfTeamDirilis.Program.FileVersion)]
// Full version-resource metadata: an unsigned EXE with blank CompanyName/Copyright is a common AV/SmartScreen
// heuristic. Values match the installer's Publisher/Website (SetupCore.cs).
[assembly: System.Reflection.AssemblyCompany("WolfTeam Diriliş")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 WolfTeam Diriliş — hayran yapımı, ticari olmayan özel sunucu — wolf2team.com")]
[assembly: System.Reflection.AssemblyDescription("WolfTeam Diriliş başlatıcısı")]

namespace WolfTeamDirilis
{
    internal static class Program
    {
        public const string Version = "1.6.1";
        /// <summary>Version padded to four parts: the version resource (ops\build-setup.ps1 checks that they match).</summary>
        public const string FileVersion = "1.6.1.0";

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        /// <summary>The arguments the launcher was started with (for a restart through the bootstrap).</summary>
        public static string[] Args = new string[0];

        [STAThread]
        private static int Main(string[] args)
        {
            Args = args;
            Tls.Enable();
            bool selftest = false, apply = false;
            string configPath = null, gameDir = null, pipe = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--selftest") selftest = true;
                else if (a == "--apply-updates") apply = true;
                else if (a == BootLayout.RolledBackFlag) Handover.RolledBack = true;
                else if (a == BootLayout.ConfirmPipeFlag && i + 1 < args.Length) pipe = args[++i];
                else if (a == BootLayout.ConfigFlag && i + 1 < args.Length) configPath = args[++i];
                else if (a == BootLayout.GameDirFlag && i + 1 < args.Length) gameDir = args[++i];
            }
            string exe = Application.ExecutablePath;
            if (gameDir == null) gameDir = BootLayout.InstallDirOf(exe);
            if (pipe != null) Handover.Open(pipe);
            // A network game folder is not supported (gate 1.4e, as in the setup and the bootstrap): also for a launcher
            // started directly or with --gamedir.
            if (BootLayout.IsNetworkPath(Path.GetFullPath(gameDir)))
            {
                if (selftest) { TextWriter w = OpenStdout(); w.WriteLine("[layout]   FAIL " + BootLayout.NetworkText); w.WriteLine("RESULT FAIL"); }
                else MessageBox.Show(BootLayout.NetworkText, "WolfTeam Diriliş", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            // Letters outside the system ANSI code page in the game folder: the game would stop with "axProtect
            // initialization Failed" (BootLayout.AnsiPathText). Also for a launcher started directly or with --gamedir.
            string ansi = BootLayout.AnsiPathProblem(Path.GetFullPath(gameDir), BootLayout.SystemAnsi());
            if (ansi != null)
            {
                if (selftest) { TextWriter w = OpenStdout(); w.WriteLine("[layout]   FAIL " + ansi.Replace("\r\n", " ")); w.WriteLine("RESULT FAIL"); return 1; }
                // Started by a bootstrap (a 1.4 bootstrap does not check the folder itself): this launcher version works,
                // only the folder does not. Its start is confirmed so the version is not rejected and no older launcher
                // (without this check) is started instead.
                bool supervised = Handover.Connected;
                Handover.ConfirmNow();
                MessageBox.Show(ansi, "WolfTeam Diriliş", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return supervised ? 0 : 1;
            }

            if (selftest) return SelfTest(gameDir, configPath, apply, exe);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            LauncherConfig cfg = LauncherConfig.Load(gameDir, configPath);
            Application.Run(new MainForm(cfg, exe));
            return 0;
        }

        private static TextWriter OpenStdout()
        {
            // A /target:winexe process has no console of its own. When stdout is redirected (pipe or file) we write
            // there; otherwise attach to the console of the shell that started us.
            Stream s = Console.OpenStandardOutput();
            if (s == Stream.Null && AttachConsole(-1)) s = Console.OpenStandardOutput();
            StreamWriter w = new StreamWriter(s, new UTF8Encoding(false));
            w.AutoFlush = true;
            return w;
        }

        private static int SelfTest(string gameDir, string configPath, bool apply, string exe)
        {
            TextWriter o = OpenStdout();
            bool allOk = true;
            o.WriteLine("WolfTeam Dirilis launcher " + Version + " selftest");
            LauncherConfig cfg = LauncherConfig.Load(gameDir, configPath);
            o.WriteLine("[config]   " + (cfg.FileFound ? "OK   " : "WARN ") + (cfg.FileFound ? LauncherConfig.FileName + " loaded" : LauncherConfig.FileName + " not found, defaults in use"));
            foreach (string w in cfg.Warnings) o.WriteLine("[config]   WARN " + w);
            foreach (string n in cfg.LogNotes) o.WriteLine("[config]   NOTE " + n);
            o.WriteLine("           ServerHost=" + cfg.ServerHost + " GamePort=" + cfg.GamePort);
            o.WriteLine("           LoginUrl=" + LauncherConfig.HostPort(cfg.LoginUrl) + " UpdateBaseUrl=" + LauncherConfig.Redact(cfg.UpdateBaseUrl)
                + " NewsUrl=" + LauncherConfig.Redact(cfg.NewsUrl));
            bool https = cfg.UpdateBaseUrl != null && cfg.UpdateBaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
            o.WriteLine("[tls]      " + (Tls.Available ? "OK   TLS 1.2 available" : "FAIL TLS 1.2 not available on this PC")
                + (https ? ", updates over HTTPS" : ", updates over plain HTTP (no launcher\\endpoints.ini)")
                + (cfg.TlsFallbackUsed ? ", login/news fall back to HTTP" : ""));
            allOk &= Tls.Available;
            BootState boot = null;
            try { boot = BootState.Read(BootLayout.StatePath(cfg.GameDir)); }
            catch (IOException) { }
            o.WriteLine("[layout]   " + (File.Exists(BootLayout.BootstrapPath(cfg.GameDir)) ? "bootstrap present" : "no bootstrap")
                + ", launcher file " + (File.Exists(BootLayout.StagedLauncher(cfg.GameDir)) ? "present" : "missing")
                + (boot == null ? ", no bootstrap state yet" : ", current=" + (boot.Current == null ? "-" : boot.Current.Substring(0, 16))
                    + (boot.Trial != null ? " trial=" + boot.Trial.Substring(0, 16) : "") + " failed=" + boot.Failed.Count));
            bool gameExe = File.Exists(cfg.GameExePath);
            o.WriteLine("[game]     " + (gameExe ? "OK   " : "FAIL ") + LauncherConfig.GameExeName + (gameExe ? " present" : " missing in game folder"));
            allOk &= gameExe;

            bool[] up = Net.ProbeServer(cfg, 3000);
            o.WriteLine("[server]   " + (up[0] ? "OK   " : "FAIL ") + cfg.ServerHost + ":" + cfg.GamePort + (up[0] ? " open" : " no connection"));
            o.WriteLine("[server]   " + (up[1] ? "OK   " : "FAIL ") + cfg.LoginHost + ":" + cfg.LoginPort + (up[1] ? " open" : " no connection"));
            allOk &= up[0] && up[1];

            if (cfg.UpdateBaseUrl == null)
            {
                o.WriteLine("[manifest] FAIL UpdateBaseUrl not set");
                allOk = false;
            }
            else
            {
                string stage = "[manifest] ";
                try
                {
                    bool ok = true;
                    // Fetch, plan (it writes the hash cache), apply and verify as one transaction under the install's
                    // update lock - also without --apply-updates, so no hash is cached for a file another launcher replaces.
                    Updater.WithUpdateLock(cfg.GameDir, delegate { o.WriteLine("[update]   waiting: " + Updater.WaitingText); },
                        delegate { ok = CheckManifest(o, cfg, apply, ref stage); });
                    allOk &= ok;
                }
                catch (UpdateException e)
                {
                    o.WriteLine(stage + "FAIL " + e.Message);
                    allOk = false;
                }
            }
            if (Updater.IsIncomplete(cfg.GameDir))
            {
                o.WriteLine("[update]   FAIL an earlier update is unfinished; the launcher will not start the game until it completes");
                allOk = false;
            }
            return NewsAndResult(o, cfg, allOk);
        }

        private static bool CheckManifest(TextWriter o, LauncherConfig cfg, bool apply, ref string stage)
        {
            bool allOk = true;
            Updater up2 = new Updater(cfg);
            Manifest m = up2.FetchManifest();
            long total = 0;
            foreach (ManifestEntry e in m.Files) total += e.Size;
            o.WriteLine("[manifest] OK   version=" + m.Version + " files=" + m.Files.Count + " seeds=" + m.Seeds.Count + " bytes=" + total);
            List<ManifestEntry> todo = up2.Plan(m);
            long need = 0;
            foreach (ManifestEntry e in todo) need += e.Size;
            o.WriteLine("[files]    " + (todo.Count == 0 ? "OK   up to date" : "INFO " + todo.Count + " file(s) differ, at most " + need + " bytes to download"));
            int shown = 0;
            foreach (ManifestEntry e in todo)
            {
                if (shown++ >= 10) { o.WriteLine("           ..."); break; }
                o.WriteLine("           " + e.Path + (e.IsLauncher ? " (launcher: the bootstrap starts the new version as a trial)"
                    : e.BlockHashes != null ? " (block update: only changed blocks are downloaded)" : ""));
            }
            if (apply)
            {
                stage = "[update]   ";
                int lastPct = -1;
                up2.Progress = delegate(UpdateProgress p)
                {
                    if (p.Percent / 10 != lastPct / 10) { lastPct = p.Percent; o.WriteLine("           %" + p.Percent + " " + p.File); }
                };
                up2.Apply(todo);
                List<ManifestEntry> again = up2.Plan(m);
                o.WriteLine("[update]   " + (again.Count == 0 ? "OK   applied, files now match the manifest" : "FAIL " + again.Count + " file(s) still differ"));
                allOk &= again.Count == 0;
            }
            return allOk;
        }

        private static int NewsAndResult(TextWriter o, LauncherConfig cfg, bool allOk)
        {
            if (cfg.NewsUrl == null)
            {
                o.WriteLine("[news]     FAIL NewsUrl not set");
                allOk = false;
            }
            else
            {
                try
                {
                    List<NewsItem> items = News.Fetch(cfg.NewsUrl);
                    o.WriteLine("[news]     OK   " + items.Count + " item(s)");
                    foreach (NewsItem it in items) o.WriteLine("           " + it.Date + " " + it.Title + (it.ImageUrl != null ? " [image]" : ""));
                }
                catch (Exception e)
                {
                    if (!(e is System.Net.WebException || e is IOException || e is InvalidDataException
                          || e is ArgumentException || e is InvalidOperationException)) throw;
                    o.WriteLine("[news]     FAIL " + Net.DescribeFailure(e));
                    allOk = false;
                }
            }
            o.WriteLine(allOk ? "RESULT OK" : "RESULT FAIL");
            o.Flush();
            return allOk ? 0 : 1;
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            CultureInfo tr = CultureInfo.GetCultureInfo("tr-TR");
            if (bytesPerSecond < 1024 * 1024)
                return ((int)(bytesPerSecond / 1024)).ToString(tr) + " KB/sn";
            return (bytesPerSecond / (1024 * 1024)).ToString("0.0", tr) + " MB/sn";
        }
    }
}
