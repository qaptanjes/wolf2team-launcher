// WolfTeamDirilis-Setup.exe entry point.
//   (no arguments)                 install wizard
//   /S [/nodesktop] [/nostartmenu] [/launch] [/D=<folder>]   silent install; exit code 0 = ok
//   /uninstall [/S] [/keepprofiles]                            uninstall (the copy in the install folder,
//                                                              WolfTeamDirilis-Kaldir.exe, is what Windows runs)
// Exit codes: 0 ok, 1 error, 2 nothing installed, 3 the game or launcher is running, 4 cancelled.
// The uninstaller cannot delete the exe it runs from, so it copies itself to %TEMP% and continues there (/inner); the
// temp copy removes itself at the end.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WolfTeamDirilis.Setup
{
    internal static class SetupProgram
    {
        private const string Title = "WolfTeam Diriliş Kurulumu";

        [STAThread]
        private static int Main()
        {
            SetupOptions o = SetupOptions.Parse(CommandLineTail(Environment.CommandLine));
            string self = Application.ExecutablePath;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (o.Uninstall) return o.Inner ? UninstallInner(o, self) : UninstallStart(o, self);
                if (o.Silent) return SilentInstall(o, self);
                try { Application.Run(new SetupForm(o, Payload.FromResources(), self)); }
                finally { DeleteSelfLater(self); } // the bootstrap's repair copy (%TEMP%\WolfTeamDirilis-Onar-*.exe)
                return 0;
            }
            catch (InstallException e)
            {
                if (!o.Silent) MessageBox.Show(e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return e.Running ? 3 : 1;
            }
        }

        internal static string CommandLineTail(string cmd)
        {
            cmd = cmd.TrimStart();
            int end;
            if (cmd.StartsWith("\""))
            {
                end = cmd.IndexOf('"', 1);
                end = end < 0 ? cmd.Length : end + 1;
            }
            else
            {
                end = cmd.IndexOf(' ');
                if (end < 0) end = cmd.Length;
            }
            return cmd.Substring(end).Trim();
        }

        private static int SilentInstall(SetupOptions o, string self)
        {
            Installer inst = new Installer(o, Payload.FromResources(), self);
            if (string.IsNullOrEmpty(o.Dir))
                o.Dir = inst.ExistingInstall() ?? InstallPaths.DefaultDir(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            inst.Install(null);
            if (o.Launch) SetupForm.StartLauncher(Path.GetFullPath(o.Dir));
            return 0;
        }

        /// <summary>First stage: find the install, confirm, check running processes, continue from a temp copy.</summary>
        private static int UninstallStart(SetupOptions o, string self)
        {
            Installer inst = new Installer(o, null, self);
            string dir = Path.GetDirectoryName(self);
            if (!File.Exists(Path.Combine(dir, InstallPaths.MarkerName))) dir = inst.ExistingInstall();
            if (dir == null)
            {
                if (!o.Silent) MessageBox.Show("Bu bilgisayarda kurulu bir WolfTeam Diriliş bulunamadı.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            if (!o.Silent)
            {
                using (UninstallConfirm c = new UninstallConfirm(dir))
                {
                    if (c.ShowDialog() != DialogResult.OK) return 4;
                    o.KeepProfiles = c.KeepProfiles;
                }
            }
            while (true)
            {
                List<string> running = ProcessCheck.RunningUnder(dir, Process.GetCurrentProcess().Id);
                if (running.Count == 0) break;
                if (o.Silent) return 3;
                DialogResult r = MessageBox.Show("Oyun ya da başlatıcı açık (" + string.Join(", ", running.ToArray()) + "). Kapatıp Yeniden Dene'ye bas.",
                    Title, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.Retry) return 4;
            }
            string temp = Path.Combine(Path.GetTempPath(), "WolfTeamDirilis-Kaldir-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(self, temp, true);
            ProcessStartInfo psi = new ProcessStartInfo(temp, o.ToInnerArgs(Process.GetCurrentProcess().Id, dir));
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
            return 0;
        }

        /// <summary>Second stage, from %TEMP%: wait for the first stage to exit, remove everything, delete ourselves.</summary>
        private static int UninstallInner(SetupOptions o, string self)
        {
            if (o.ParentPid > 0)
            {
                try
                {
                    using (Process p = Process.GetProcessById(o.ParentPid)) p.WaitForExit(20000);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            int code = 0;
            try
            {
                if (string.IsNullOrEmpty(o.Dir)) throw new InstallException("Kaldırılacak klasör belli değil.");
                List<string> failed = new Installer(o, null, self).RemoveEverything(Path.GetFullPath(o.Dir));
                if (failed.Count > 0)
                {
                    code = 1;
                    if (!o.Silent)
                    {
                        MessageBox.Show("WolfTeam Diriliş kaldırıldı ama bazı dosyalar silinemedi (açık olabilirler):\r\n\r\n"
                                        + string.Join("\r\n", failed.GetRange(0, Math.Min(8, failed.Count)).ToArray())
                                        + "\r\n\r\nBunları elle silebilirsin.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else if (!o.Silent)
                {
                    MessageBox.Show(o.KeepProfiles ? "WolfTeam Diriliş kaldırıldı. Oyun ayarların (Profiles klasörü) duruyor."
                        : "WolfTeam Diriliş kaldırıldı.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (InstallException e)
            {
                code = 1;
                if (!o.Silent) MessageBox.Show(e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            DeleteSelfLater(self);
            return code;
        }

        private static void DeleteSelfLater(string self)
        {
            // Only a copy made in %TEMP% under one of our names deletes itself: the uninstaller's second stage, or the
            // repair the bootstrap offered (launcher\bootstrap\Bootstrap.cs OfferRepair).
            string name = Path.GetFileName(self);
            if (!InstallPaths.Under(self, Path.GetTempPath().TrimEnd('\\'))
                || !(name.StartsWith("WolfTeamDirilis-Kaldir-") || name.StartsWith("WolfTeamDirilis-Onar-"))) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"" + self + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>"WolfTeam Diriliş kaldırılsın mı?" with the keep-settings option.</summary>
    internal sealed class UninstallConfirm : Form
    {
        private readonly Tick keep = new Tick("Oyun ayarlarımı sakla (Profiles klasörü)", false);

        public bool KeepProfiles
        {
            get { return keep.Checked; }
        }

        public UninstallConfirm(string dir)
        {
            Text = "WolfTeam Diriliş'i kaldır";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(520, 250);
            BackColor = Theme.PanelSolid;
            Label h = new Label();
            h.Text = "WolfTeam Diriliş kaldırılsın mı?";
            h.Font = Theme.Heading;
            h.ForeColor = Theme.Text;
            h.Bounds = new Rectangle(28, 22, 470, 32);
            Label b = new Label();
            b.Text = "Şu klasör, içindeki oyun dosyalarıyla birlikte silinir:\r\n" + dir + "\r\n\r\nKısayollar ve Programlar ve Özellikler kaydı da kaldırılır.";
            b.Font = Theme.Body;
            b.ForeColor = Theme.Muted;
            b.UseMnemonic = false;
            b.Bounds = new Rectangle(28, 60, 470, 90);
            keep.Bounds = new Rectangle(28, 152, 470, 30);
            Plate yes = new Plate("Kaldır", true);
            yes.Bounds = new Rectangle(520 - 28 - 150, 194, 150, 42);
            Plate no = new Plate("Vazgeç", false);
            no.Bounds = new Rectangle(520 - 28 - 150 - 12 - 110, 194, 110, 42);
            yes.Activated += delegate { DialogResult = DialogResult.OK; };
            no.Activated += delegate { DialogResult = DialogResult.Cancel; };
            Controls.AddRange(new Control[] { h, b, keep, no, yes });
            no.TabIndex = 0;
            yes.TabIndex = 1;
            keep.TabIndex = 2;
            ActiveControl = no;
        }
    }
}
