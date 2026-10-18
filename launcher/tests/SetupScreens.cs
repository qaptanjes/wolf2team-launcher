// Renders every wizard step to PNG without showing a window (DrawToBitmap on a form that is never shown).
// usage: SetupTests.exe --screenshots <out dir>   (art: %WTD_ASSETS%\bg.png, default C:\tools\re\dist\launcher-assets)
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using WolfTeamDirilis.Setup;

namespace WolfTeamDirilis.Tests
{
    internal static class SetupScreens
    {
        public static int Render(string outDir)
        {
            Application.EnableVisualStyles();
            Directory.CreateDirectory(outDir);
            string assets = Environment.GetEnvironmentVariable("WTD_ASSETS") ?? @"C:\tools\re\dist\launcher-assets";
            Payload p = new Payload();
            string bg = Path.Combine(assets, "bg.png");
            if (File.Exists(bg)) p.Add("bg.png", File.ReadAllBytes(bg));
            string self = Application.ExecutablePath;

            Shot(outDir, "setup-1-hosgeldin.png", p, self, delegate(SetupForm f) { });
            Shot(outDir, "setup-2-kullanim-sartlari.png", p, self, delegate(SetupForm f)
            {
                f.GoTo(SetupForm.PageTerms);
                Tick t = (Tick)typeof(SetupForm).GetField("acceptTerms", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
                t.Checked = true;
            });
            Shot(outDir, "setup-3-klasor.png", p, self, delegate(SetupForm f) { f.GoTo(SetupForm.PageFolder); });
            Shot(outDir, "setup-3b-klasor-hata.png", p, self, delegate(SetupForm f)
            {
                typeof(SetupForm).GetField("folder", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f, @"C:\Program Files\WolfTeam");
                f.GoTo(SetupForm.PageFolder);
                ((Label)FindPage(f).Controls["error"]).Text = InstallPaths.Check(@"C:\Program Files\WolfTeam", SystemFolders.Current());
            });
            Shot(outDir, "setup-4-secenekler.png", p, self, delegate(SetupForm f) { f.GoTo(SetupForm.PageOptions); });
            Shot(outDir, "setup-5-kuruluyor.png", p, self, delegate(SetupForm f)
            {
                f.GoTo(SetupForm.PageInstall);
                f.ShowInstallProgress(62, "Kısayollar oluşturuluyor…");
            });
            Shot(outDir, "setup-5b-oyun-acik.png", p, self, delegate(SetupForm f)
            {
                f.GoTo(SetupForm.PageInstall);
                f.ShowInstallError("Bu klasördeki oyun ya da başlatıcı açık (Wolfteam.bin). Kapatıp Tekrar dene'ye bas.", true);
            });
            Shot(outDir, "setup-6-bitti.png", p, self, delegate(SetupForm f) { f.GoTo(SetupForm.PageDone); });

            using (UninstallConfirm c = new UninstallConfirm(@"C:\Oyunlar\WolfTeam Dirilis"))
                Save(c, Path.Combine(outDir, "kaldir-onay.png"));
            Console.WriteLine("screenshots written to " + outDir);
            return 0;
        }

        private static Panel FindPage(SetupForm f)
        {
            return (Panel)typeof(SetupForm).GetField("page", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
        }

        private delegate void Prep(SetupForm f);

        private static void Shot(string dir, string name, Payload p, string self, Prep prep)
        {
            SetupOptions o = SetupOptions.Parse("");
            // Never the real registry entry: the welcome page must not depend on this PC.
            o.RegistryKey = @"Software\WolfTeamDirilisTest\screens-none";
            using (SetupForm f = new SetupForm(o, p, self))
            {
                prep(f);
                Save(f, Path.Combine(dir, name));
            }
        }

        private static void Save(Form f, string path)
        {
            using (Bitmap bmp = Runner.Snapshot(f)) bmp.Save(path, ImageFormat.Png); // never shown on screen
        }
    }
}
