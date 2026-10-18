// Renders the launcher in its update states to PNG without showing a window (test-only; drives private members by
// reflection so the launcher has no preview code). usage: LauncherTests.exe --screenshots <out dir>
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace WolfTeamDirilis.Tests
{
    internal static class LauncherScreens
    {
        private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance;

        public static int Render(string outDir)
        {
            Application.EnableVisualStyles();
            Directory.CreateDirectory(outDir);
            string assets = Environment.GetEnvironmentVariable("WTD_ASSETS") ?? @"C:\tools\re\dist\launcher-assets";
            string game = Runner.TempDir("screens");
            UserSettings.DirOverride = Path.Combine(game, "appdata");
            try
            {
                if (File.Exists(Path.Combine(assets, "bg.png"))) File.Copy(Path.Combine(assets, "bg.png"), Path.Combine(game, "bg.png"));
                File.WriteAllText(Path.Combine(game, LauncherConfig.FileName), "UpdateBaseUrl=http://127.0.0.1:9/pub/\r\n", new UTF8Encoding(false));
                string exe = Path.Combine(game, "WolfTeamDirilis.exe");

                Shot(game, exe, Path.Combine(outDir, "launcher-1-kontrol.png"), delegate(MainForm f)
                {
                    Set(f, "update", UpdateState.Running);
                    Set(f, "updateChecking", true);
                    Get<Footer>(f, "footer").SetIndeterminate("Güncelleme kontrol ediliyor…");
                });
                Shot(game, exe, Path.Combine(outDir, "launcher-2-indiriliyor.png"), delegate(MainForm f)
                {
                    UpdateProgress p = new UpdateProgress();
                    p.File = "wolf.xfs"; p.Index = 3; p.Count = 105; p.FileDone = 35; p.FileSize = 100;
                    p.Done = 540L << 20; p.Total = (long)(1.9 * (1L << 30));
                    Set(f, "update", UpdateState.Running);
                    Set(f, "updateChecking", false);
                    Set(f, "updatePercent", p.Percent);
                    Set(f, "updateEta", ProgressText.Eta(p.Total - p.Done, 5.2 * (1 << 20)));
                    Get<Footer>(f, "footer").SetProgress(ProgressText.Line(p), p.Percent, Program.FormatSpeed(5.2 * (1 << 20)));
                });
                Shot(game, exe, Path.Combine(outDir, "launcher-3-hata-tekrar-dene.png"), delegate(MainForm f)
                {
                    Call(f, "OnUpdateFinished", "wolf.xfs indirilemedi. İnternet bağlantını kontrol edip Tekrar dene'ye bas.", false, 0, false);
                });
                File.WriteAllText(Path.Combine(game, "Wolfteam.bin"), "x");
                Shot(game, exe, Path.Combine(outDir, "launcher-4-hazir.png"), delegate(MainForm f)
                {
                    Call(f, "OnUpdateFinished", null, false, 105, false);
                });
                Shot(game, exe, Path.Combine(outDir, "launcher-5-sunucuya-ulasilamadi.png"), delegate(MainForm f)
                {
                    Call(f, "OnUpdateFinished", null, true, 0, false, true);
                });
                Shot(game, exe, Path.Combine(outDir, "launcher-6-uzun-baslik-ve-hata.png"), delegate(MainForm f)
                {
                    System.Collections.Generic.List<NewsItem> items = new System.Collections.Generic.List<NewsItem>();
                    foreach (string[] t in new string[][]
                    {
                        new string[] { "Eski sunucular geri döndü: Av Bölgesi, Gizli Karargah, Zafer Tepesi, Yıldırım Ordusu", "2026-09-30",
                            "Sunucu seçiminde artık eski WolfTeam gibi 4 sunucu var; her sunucunun kendi lobisi ve odaları var." },
                        new string[] { "Başlatıcı 1.3", "2026-10-01", "Başlatıcı kendini güncelleyince hemen yeniden açılır." },
                    })
                    {
                        NewsItem it = new NewsItem();
                        it.Title = t[0]; it.Date = t[1]; it.Text = t[2];
                        items.Add(it);
                    }
                    Get<NewsList>(f, "news").SetItems(items);
                    Call(f, "OnUpdateFinished", "Önceki güncelleme yarım kaldı ve güncelleme listesi alınamadı. Oyun bu hâliyle açılamaz; "
                         + "bağlantını kontrol edip Tekrar dene'ye bas.", false, 0, false);
                });
                Console.WriteLine("screenshots written to " + outDir);
                return 0;
            }
            finally
            {
                UserSettings.DirOverride = null;
                Runner.DeleteTree(game);
            }
        }

        private delegate void Prep(MainForm f);

        private static void Shot(string game, string exe, string path, Prep prep)
        {
            LauncherConfig cfg = LauncherConfig.Load(game, null);
            using (MainForm f = new MainForm(cfg, exe))
            {
                Runner.CreateHandles(f);
                Get<ServerBadge>(f, "badge").SetState(BadgeState.Online);
                Call(f, "LoadNews");
                prep(f);
                Call(f, "RefreshStartButton");
                using (Bitmap bmp = Runner.Snapshot(f)) bmp.Save(path, ImageFormat.Png);
            }
        }

        private static T Get<T>(MainForm f, string field)
        {
            return (T)typeof(MainForm).GetField(field, Any).GetValue(f);
        }

        private static void Set(MainForm f, string field, object value)
        {
            typeof(MainForm).GetField(field, Any).SetValue(f, value);
        }

        private static void Call(MainForm f, string method, params object[] args)
        {
            foreach (MethodInfo m in typeof(MainForm).GetMethods(Any))
            {
                if (m.Name == method && m.GetParameters().Length == args.Length) { m.Invoke(f, args); return; }
            }
            throw new MissingMethodException(method);
        }
    }
}
