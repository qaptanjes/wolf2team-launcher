// The install wizard: Hoş geldin, Kullanım şartları, Kurulum klasörü, Seçenekler, Kuruluyor, Bitti.
// 780 x 500 borderless window: a rail with the launcher art and the step list on the left, the page on the right,
// the actions in a bar at the bottom. The install itself runs on a background thread (Installer).
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WolfTeamDirilis.Setup
{
    internal sealed class SetupForm : Form
    {
        public const int W = 780, H = 500, Rail = 250;
        private static readonly string[] Steps = { "Hoş geldin", "Kullanım şartları", "Kurulum klasörü", "Seçenekler", "Kuruluyor", "Bitti" };
        internal const int PageWelcome = 0, PageTerms = 1, PageFolder = 2, PageOptions = 3, PageInstall = 4, PageDone = 5;

        public const string Terms =
            "1. WolfTeam Diriliş, hayranların kurduğu ticari olmayan bir özel sunucudur. WolfTeam adı ve oyun içeriği hak sahiplerine "
            + "aittir; bu proje onlarla bağlantılı değildir.\r\n\r\n"
            + "2. Oyun ücretsizdir. Hesabını kimseyle paylaşma. Bu şifreyi başka yerde kullanma: başlatıcı, eski oyunla uyum için "
            + "giriş bilgisini şifrelenmemiş bağlantıyla gönderir.\r\n\r\n"
            + "3. Hile, bot, oyun dosyalarını değiştirmek ve başka oyuncuları rahatsız etmek yasaktır. Kurallara uymayan hesaplar "
            + "uyarı olmadan kapatılabilir.\r\n\r\n"
            + "4. Başlatıcı her açılışta oyun dosyalarını sunucudaki listeyle karşılaştırır; eksik ya da değişmiş dosyaları yeniden indirir.\r\n\r\n"
            + "5. Kurulum yalnızca seçtiğin klasöre, masaüstü ve Başlat menüsü kısayollarına ve Programlar ve Özellikler listesine yazar. "
            + "Kaldırmak için Ayarlar > Uygulamalar'da WolfTeam Diriliş'i seç.\r\n\r\n"
            + "6. Hesabın ve oyun içi eşyaların için kalıcılık garantisi yoktur; hizmet haber verilmeden değişebilir ya da sona erebilir.\r\n\r\n"
            + "7. Kişisel verilerin (kullanıcı adı, e-posta, IP adresi) yalnızca hesabını çalıştırmak ve kötüye kullanımı önlemek için "
            + "tutulur. Ayrıntılar wolf2team.com adresindeki Kurallar ve KVKK sayfasında.\r\n\r\n"
            + "8. Program olduğu gibi sunulur; kullanımından doğan zararlardan geliştiriciler sorumlu tutulamaz.";

        private readonly SetupOptions options;
        private readonly Payload payload;
        private readonly string selfExe;
        private readonly Installer probe;
        private readonly string existing;
        private readonly Bitmap railArt;

        private readonly Panel page = new Panel();
        private readonly Plate next = new Plate("İleri", true);
        private readonly Plate back = new Plate("Geri", false);
        private readonly Plate cancel = new Plate("Vazgeç", false);
        private readonly CloseGlyph close = new CloseGlyph();

        private int current = -1;
        private bool installing, installed;
        private Tick acceptTerms, desktopTick, startMenuTick, launchTick;
        private TextBox folderBox;
        private Label folderError, freeLabel, installStatus;
        private Bar installBar;
        private string folder;

        public SetupForm(SetupOptions options, Payload payload, string selfExe)
        {
            this.options = options;
            this.payload = payload;
            this.selfExe = selfExe;
            probe = new Installer(options, payload, selfExe);
            existing = probe.ExistingInstall();
            folder = options.Dir ?? existing ?? InstallPaths.DefaultDir(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));

            Text = "WolfTeam Diriliş Kurulumu";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.PanelSolid;
            DoubleBuffered = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(selfExe); }
            catch (ArgumentException) { }
            catch (IOException) { }
            railArt = ComposeRail(payload != null && payload.Has("bg.png") ? payload.Get("bg.png") : null);

            page.Bounds = new Rectangle(Rail + 36, 44, W - Rail - 72, 370);
            page.BackColor = Theme.PanelSolid;
            cancel.Bounds = new Rectangle(Rail + 30, H - 64, 110, 44);
            back.Bounds = new Rectangle(W - 36 - 190 - 12 - 110, H - 64, 110, 44);
            next.Bounds = new Rectangle(W - 36 - 190, H - 64, 190, 44);
            close.Bounds = new Rectangle(W - 40, 6, 32, 28);
            Controls.AddRange(new Control[] { page, next, back, cancel, close });
            next.TabIndex = 50;
            back.TabIndex = 51;
            cancel.TabIndex = 52;
            close.TabIndex = 53;

            next.Activated += delegate { OnNext(); };
            back.Activated += delegate { if (current > 0 && current < PageInstall) GoTo(current - 1); };
            cancel.Activated += delegate { Close(); };
            close.Activated += delegate { Close(); };
            GoTo(PageWelcome);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000 | 0x00080000; // WS_MINIMIZEBOX | WS_SYSMENU: taskbar menu and Alt+F4
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && railArt != null) railArt.Dispose();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ painting

        private static Bitmap ComposeRail(byte[] png)
        {
            Bitmap bmp = new Bitmap(Rail, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Theme.Prepare(g);
                g.Clear(Theme.Ink);
                if (png != null)
                {
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(png))
                        using (Image img = Image.FromStream(ms))
                        {
                            // The wolf art sits in the left part of the launcher backdrop; take a rail-shaped slice of it.
                            float scale = (float)H / Math.Min(img.Height, 616);
                            int srcW = (int)(Rail / scale);
                            int srcX = Math.Max(0, Math.Min(img.Width - srcW, 120));
                            g.DrawImage(img, new Rectangle(0, 0, Rail, H), new Rectangle(srcX, 0, srcW, Math.Min(img.Height, 616)), GraphicsUnit.Pixel);
                        }
                    }
                    catch (ArgumentException) { }
                }
                using (LinearGradientBrush shade = new LinearGradientBrush(new Rectangle(0, 0, Rail, H),
                    Color.FromArgb(120, 0, 0, 0), Color.FromArgb(235, 0, 0, 0), 90f))
                    g.FillRectangle(shade, 0, 0, Rail, H);
                // The step list needs a calm ground: the art fades out behind it.
                int listTop = H - 34 - Steps.Length * 34 - 24;
                using (LinearGradientBrush fade = new LinearGradientBrush(new Rectangle(0, listTop - 40, Rail, 41),
                    Color.FromArgb(0, 0, 0, 0), Color.FromArgb(225, 8, 6, 5), 90f))
                    g.FillRectangle(fade, 0, listTop - 40, Rail, 40);
                using (SolidBrush calm = new SolidBrush(Color.FromArgb(225, 8, 6, 5))) g.FillRectangle(calm, 0, listTop, Rail, H - listTop);
                using (SolidBrush band = new SolidBrush(Theme.Band)) g.FillRectangle(band, Rail - 3, 0, 3, H);
                Theme.DrawText(g, Theme.Signature, Theme.Tiny, Theme.Faint, new RectangleF(24, H - 26, Rail - 40, 16), StringAlignment.Near, StringAlignment.Center);
                Theme.DrawText(g, "WolfTeam Diriliş", Theme.Heading, Theme.Text, new RectangleF(24, 28, Rail - 40, 30), StringAlignment.Near, StringAlignment.Center);
                Theme.DrawText(g, "Kurulum " + SetupInfo.Version, Theme.Small, Theme.Muted, new RectangleF(24, 58, Rail - 40, 20), StringAlignment.Near, StringAlignment.Center);
            }
            return bmp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            g.DrawImageUnscaled(railArt, 0, 0);
            // Step list: a real sequence, so it is numbered. Done steps get a check, the current one the red marker.
            int y = H - 34 - Steps.Length * 34;
            for (int i = 0; i < Steps.Length; i++, y += 34)
            {
                bool now = i == current, done = i < current;
                if (now)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Band, 200))) g.FillRectangle(b, 0, y, Rail - 3, 30);
                    using (SolidBrush b = new SolidBrush(Theme.RedEdge)) g.FillRectangle(b, 0, y, 4, 30);
                }
                Color c = now ? Theme.Text : done ? Theme.Muted : Theme.Hex("#8C8177");
                Rectangle badge = new Rectangle(24, y + 5, 20, 20);
                using (Pen p = new Pen(c, 1.4f)) g.DrawEllipse(p, badge);
                if (done)
                {
                    using (Pen p = new Pen(c, 2f))
                        g.DrawLines(p, new PointF[] { new PointF(29, y + 15), new PointF(33, y + 19), new PointF(39, y + 11) });
                }
                else
                {
                    Theme.DrawText(g, (i + 1).ToString(), Theme.Small, c, new RectangleF(badge.X, badge.Y, badge.Width, badge.Height),
                        StringAlignment.Center, StringAlignment.Center);
                }
                Theme.DrawText(g, Steps[i], now ? Theme.Label : Theme.Small, c, new RectangleF(54, y, Rail - 60, 30), StringAlignment.Near, StringAlignment.Center);
            }
            using (Pen p = new Pen(Theme.Rule)) g.DrawLine(p, Rail + 30, H - 80, W - 30, H - 80);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && (e.X < Rail || e.Y < 40))
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !installing) { e.Handled = true; Close(); return; }
            if (e.KeyCode == Keys.Enter && next.Enabled && !(ActiveControl is SetupControl) && !(ActiveControl is TextBox && ((TextBox)ActiveControl).Multiline))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OnNext();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (installing) { e.Cancel = true; return; }
            if (!installed && e.CloseReason == CloseReason.UserClosing && current != PageDone)
            {
                DialogResult r = MessageBox.Show(this, "Kurulumdan çıkılsın mı? WolfTeam Diriliş kurulmayacak.", "WolfTeam Diriliş Kurulumu",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) e.Cancel = true;
            }
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ pages

        private Label Heading(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.PageHeading;
            l.ForeColor = Theme.Text;
            l.BackColor = Theme.PanelSolid;
            l.Bounds = new Rectangle(0, 0, page.Width, 40);
            l.UseMnemonic = false;
            page.Controls.Add(l);
            return l;
        }

        private Label Body(string text, int y, int h, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.Body;
            l.ForeColor = color;
            l.BackColor = Theme.PanelSolid;
            l.Bounds = new Rectangle(0, y, page.Width, h);
            l.UseMnemonic = false;
            page.Controls.Add(l);
            return l;
        }

        internal void GoTo(int p)
        {
            current = p;
            page.SuspendLayout();
            foreach (Control c in page.Controls) c.Dispose();
            page.Controls.Clear();
            back.Visible = p > PageWelcome && p < PageInstall;
            cancel.Visible = p < PageDone;
            cancel.Enabled = p != PageInstall;
            next.Enabled = true;
            next.Visible = true;
            switch (p)
            {
                case PageWelcome: BuildWelcome(); break;
                case PageTerms: BuildTerms(); break;
                case PageFolder: BuildFolder(); break;
                case PageOptions: BuildOptions(); break;
                case PageInstall: BuildInstall(); break;
                default: BuildDone(); break;
            }
            page.ResumeLayout();
            Invalidate(new Rectangle(0, 0, Rail, H));
            if (next.Enabled) next.Focus();
        }

        private void BuildWelcome()
        {
            Heading("WolfTeam Diriliş'e hoş geldin");
            Body("Bu program WolfTeam Diriliş başlatıcısını bilgisayarına kurar ve masaüstüne bir kısayol ekler.\r\n\r\n"
                 + "Oyun dosyaları (yaklaşık 2 GB) kurulumdan sonra, başlatıcı ilk açıldığında indirilir. Sonraki güncellemeleri de "
                 + "başlatıcı kendisi yapar; indirme bitince OYUNU BAŞLAT düğmesi açılır.\r\n\r\n"
                 + "Kurulum için yönetici izni gerekmez.", 56, 170, Theme.Muted);
            if (existing != null)
            {
                Label note = Body("Bu bilgisayarda zaten kurulu: " + existing + "\r\nDevam edersen başlatıcı onarılır ve güncellenir; "
                                  + "oyun dosyaların ve ayarların korunur.", 236, 64, Theme.Amber);
                note.Name = "existing";
            }
            next.Text = "İleri";
        }

        private void BuildTerms()
        {
            Heading("Kullanım şartları");
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Theme.Field;
            t.ForeColor = Theme.Text;
            t.Font = Theme.Body;
            t.Text = Terms;
            t.Bounds = new Rectangle(0, 52, page.Width, 250);
            t.TabIndex = 0;
            t.AccessibleName = "Kullanım şartları metni";
            page.Controls.Add(t);
            acceptTerms = new Tick("Kullanım şartlarını okudum, kabul ediyorum", false);
            acceptTerms.Bounds = new Rectangle(0, 316, page.Width, 32);
            acceptTerms.TabIndex = 1;
            acceptTerms.CheckedChanged += delegate { next.Enabled = acceptTerms.Checked; };
            page.Controls.Add(acceptTerms);
            next.Text = "İleri";
            next.Enabled = false;
        }

        private void BuildFolder()
        {
            Heading("Kurulum klasörü");
            Body("Başlatıcı ve oyun dosyaları bu klasöre yazılır. Önerilen klasörde kalmanı öneririz: başlatıcı güncellemeleri "
                 + "yönetici izni istemeden yazabilir. Program Files seçilemez.", 52, 64, Theme.Muted);
            folderBox = new TextBox();
            folderBox.BorderStyle = BorderStyle.FixedSingle;
            folderBox.BackColor = Theme.Field;
            folderBox.ForeColor = Theme.Text;
            folderBox.Font = Theme.FieldText;
            folderBox.Text = folder;
            folderBox.Bounds = new Rectangle(0, 132, page.Width - 124, 30);
            folderBox.TabIndex = 0;
            folderBox.AccessibleName = "Kurulum klasörü";
            folderBox.TextChanged += delegate { folder = folderBox.Text; folderError.Text = ""; UpdateFree(); };
            page.Controls.Add(folderBox);
            Plate browse = new Plate("Gözat…", false);
            browse.Bounds = new Rectangle(page.Width - 114, 126, 114, 40);
            browse.TabIndex = 1;
            browse.Activated += delegate { Browse(); };
            page.Controls.Add(browse);
            freeLabel = Body("", 176, 24, Theme.Muted);
            folderError = Body("", 206, 90, Theme.Danger);
            folderError.Name = "error";
            UpdateFree();
            next.Text = "İleri";
        }

        private void UpdateFree()
        {
            if (freeLabel == null) return;
            string root = null;
            try { root = Path.GetPathRoot(folder.Trim()); }
            catch (ArgumentException) { }
            long free = string.IsNullOrEmpty(root) ? -1 : Installer.FreeBytes(root);
            freeLabel.Text = free < 0 ? "Gereken alan: en az 4 GB" : "Boş alan: " + InstallPaths.FormatBytes(free) + " (en az 4 GB gerekiyor)";
            freeLabel.ForeColor = free >= 0 && free < InstallPaths.RequiredBytes ? Theme.Danger : Theme.Muted;
        }

        private void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "WolfTeam Diriliş'in kurulacağı klasörü seç. Seçtiğin klasörün içinde \"" + InstallPaths.FolderName
                                + "\" klasörü oluşturulur.";
                d.ShowNewFolderButton = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string picked = d.SelectedPath;
                // Picking a parent such as D:\Oyunlar installs into D:\Oyunlar\WolfTeam Dirilis, never into the parent itself.
                // An install folder itself (also one from setup 1.4 named "WolfTeam Diriliş") is taken as it is.
                string name = Path.GetFileName(picked.TrimEnd('\\'));
                if (!string.Equals(name, InstallPaths.FolderName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "WolfTeam Diriliş", StringComparison.OrdinalIgnoreCase))
                    picked = Path.Combine(picked, InstallPaths.FolderName);
                folderBox.Text = picked;
            }
        }

        private void BuildOptions()
        {
            Heading("Seçenekler");
            Body("Kurulacak klasör:", 52, 22, Theme.Muted);
            Label f = Body(folder.Trim(), 74, 44, Theme.Text);
            f.Font = Theme.Label;
            desktopTick = new Tick("Masaüstüne kısayol oluştur", options.Desktop);
            desktopTick.Bounds = new Rectangle(0, 132, page.Width, 32);
            desktopTick.TabIndex = 0;
            desktopTick.CheckedChanged += delegate { options.Desktop = desktopTick.Checked; };
            startMenuTick = new Tick("Başlat menüsüne kısayol ekle", options.StartMenu);
            startMenuTick.Bounds = new Rectangle(0, 170, page.Width, 32);
            startMenuTick.TabIndex = 1;
            startMenuTick.CheckedChanged += delegate { options.StartMenu = startMenuTick.Checked; };
            page.Controls.Add(desktopTick);
            page.Controls.Add(startMenuTick);
            Body("Kısayol başlatıcıyı açar. Başlatıcı ilk açılışta oyun dosyalarını indirir.", 220, 44, Theme.Muted);
            next.Text = existing != null ? "Onar" : "Kur";
        }

        private void BuildInstall()
        {
            Heading("Kuruluyor");
            installStatus = Body("Hazırlanıyor…", 60, 24, Theme.Text);
            installBar = new Bar();
            installBar.Bounds = new Rectangle(0, 92, page.Width, 8);
            page.Controls.Add(installBar);
            Label hint = Body("Bu adım birkaç saniye sürer. Oyun dosyaları sonra başlatıcı tarafından indirilir.", 116, 44, Theme.Muted);
            hint.Name = "hint";
            next.Text = "Kuruluyor…";
            next.Enabled = false;
        }

        private void BuildDone()
        {
            Heading("Kurulum tamamlandı");
            Body((options.Desktop ? "Masaüstündeki WolfTeam Diriliş kısayolu" : "Başlat menüsündeki WolfTeam Diriliş kısayolu")
                 + " başlatıcıyı açar.\r\n\r\n"
                 + "İlk açılışta oyun dosyaları indirilir (yaklaşık 2 GB). İndirme bitince başlatıcı \"Güncelleme tamamlandı\" der ve "
                 + "OYUNU BAŞLAT düğmesi açılır. Hesabın yoksa wolf2team.com adresinden kayıt ol.", 56, 150, Theme.Muted);
            launchTick = new Tick("WolfTeam Diriliş'i başlat", true);
            launchTick.Bounds = new Rectangle(0, 220, page.Width, 32);
            Label sign = Body(Theme.Signature, 346, 18, Theme.Faint);
            sign.Font = Theme.Tiny;
            launchTick.TabIndex = 0;
            page.Controls.Add(launchTick);
            next.Text = "Bitir";
        }

        // ------------------------------------------------------------------ actions

        private void OnNext()
        {
            if (!next.Enabled) return;
            switch (current)
            {
                case PageWelcome: GoTo(PageTerms); break;
                case PageTerms: if (acceptTerms.Checked) GoTo(PageFolder); break;
                case PageFolder: if (CheckFolder()) GoTo(PageOptions); break;
                case PageOptions: StartInstall(); break;
                case PageInstall: StartInstall(); break; // "Tekrar dene" after an error
                case PageDone: Finish(); break;
            }
        }

        private bool CheckFolder()
        {
            try
            {
                probe.Validate(folder);
                folder = Path.GetFullPath(folder.Trim()).TrimEnd('\\');
                return true;
            }
            catch (InstallException e)
            {
                folderError.Text = e.Message;
                folderBox.Focus();
                return false;
            }
        }

        private void StartInstall()
        {
            if (current != PageInstall) GoTo(PageInstall);
            installing = true;
            next.Enabled = false;
            next.Text = "Kuruluyor…";
            back.Visible = false;
            cancel.Enabled = false;
            options.Dir = folder;
            Installer inst = new Installer(options, payload, selfExe);
            Thread t = new Thread(delegate()
            {
                string error = null;
                bool running = false;
                try
                {
                    inst.Install(delegate(int pct, string text) { Ui(delegate { installBar.Percent = pct; installStatus.Text = text + "…"; }); });
                }
                catch (InstallException e)
                {
                    error = e.Message;
                    running = e.Running;
                }
                Ui(delegate { OnInstalled(error, running); });
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA); // ShellLink COM
            t.Start();
        }

        private void OnInstalled(string error, bool running)
        {
            installing = false;
            if (error == null)
            {
                installed = true;
                GoTo(PageDone);
                return;
            }
            ShowInstallError(error, running);
        }

        /// <summary>The install page after a failure: the reason, "Tekrar dene", and Geri to pick another folder.</summary>
        internal void ShowInstallError(string error, bool running)
        {
            installStatus.Text = "Kurulum yapılamadı";
            installStatus.ForeColor = Theme.Danger;
            installBar.Percent = 0;
            Control hint = page.Controls["hint"];
            if (hint != null)
            {
                hint.Text = error;
                hint.ForeColor = Theme.Text;
                hint.Height = 120;
            }
            next.Text = "Tekrar dene";
            next.Enabled = true;
            back.Visible = !running;
            cancel.Enabled = true;
            back.Activated -= BackToFolder;
            back.Activated += BackToFolder;
            next.Focus();
        }

        private void BackToFolder(object sender, EventArgs e)
        {
            back.Activated -= BackToFolder;
            if (current == PageInstall) GoTo(PageFolder);
        }

        internal void ShowInstallProgress(int pct, string text)
        {
            installBar.Percent = pct;
            installStatus.Text = text;
        }

        private void Finish()
        {
            if (launchTick != null && launchTick.Checked) StartLauncher(folder);
            Close();
        }

        internal static void StartLauncher(string dir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(dir, SetupInfo.LauncherExe));
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Win32Exception) { }
            catch (FileNotFoundException) { }
        }

        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>The window's close cross (the wizard has no system title bar).</summary>
    internal sealed class CloseGlyph : SetupControl
    {
        public CloseGlyph()
        {
            AccessibleName = "Kapat";
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            if (Hover || Pressed)
                using (SolidBrush b = new SolidBrush(Theme.CloseHover)) g.FillRectangle(b, ClientRectangle);
            int cx = Width / 2, cy = Height / 2;
            using (Pen p = new Pen(Theme.Text, 1.6f))
            {
                g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy + 6);
                g.DrawLine(p, cx + 6, cy - 6, cx - 6, cy + 6);
            }
            if (KeyboardFocus)
                using (Pen p = new Pen(Theme.Amber, 2)) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
        }
    }
}
