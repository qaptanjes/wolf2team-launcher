// Main window: 1020 x 666 borderless, layout per launcher-spec.md sec. 4. All network work runs on background
// threads; results come back through BeginInvoke.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WolfTeamDirilis
{
    internal sealed class MainForm : Form
    {
        private static readonly Rectangle UserFrame = new Rectangle(504, 466, 252, 30);
        private static readonly Rectangle PassFrame = new Rectangle(504, 520, 252, 30);

        private readonly LauncherConfig cfg;
        private readonly string exePath;
        private readonly Bitmap backdrop;
        private readonly ToolTip tip = new ToolTip();

        private readonly ServerBadge badge;
        private readonly ChromeButton minButton, closeButton;
        private readonly TextLink refreshLink;
        private readonly NewsList news;
        private readonly TextBox userBox, passBox;
        private readonly CheckRow remember;
        private readonly StartButton start;
        private readonly RetryButton retry;
        private readonly MessageStrip strip;
        private readonly Footer footer;
        private readonly System.Windows.Forms.Timer probeTimer = new System.Windows.Forms.Timer();

        private enum Busy { None, LoggingIn, Launching }

        private Busy busy = Busy.None;
        private UpdateState update = UpdateState.NotConfigured;
        private int updatePercent = -1;
        private bool updateChecking;
        private string updateEta = "";
        private int probing;   // 1 while a probe runs (Interlocked)
        private int newsGeneration;

        public MainForm(LauncherConfig cfg, string exePath)
        {
            this.cfg = cfg;
            this.exePath = exePath;

            Text = "WolfTeam Diriliş";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1020, 666);
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Theme.PanelSolid;
            KeyPreview = false;
            try { Icon = Icon.ExtractAssociatedIcon(exePath); }
            catch (ArgumentException) { }
            catch (IOException) { }

            backdrop = ComposeBackdrop(Path.Combine(cfg.GameDir, "bg.png"));
            SkinControl.Backdrop = backdrop;
            tip.InitialDelay = 400;

            // Tab order (spec sec. 9): user, password, remember, start, Yenile, badge, minimize, close.
            userBox = MakeTextBox(UserFrame, "Kullanıcı adı", false);
            passBox = MakeTextBox(PassFrame, "Şifre", true);
            remember = new CheckRow("Kullanıcı adımı hatırla");
            remember.Bounds = new Rectangle(504, 554, 240, 21);
            start = new StartButton();
            start.Bounds = new Rectangle(778 - StartButton.Pad, 440 - StartButton.Pad, 212 + 2 * StartButton.Pad, 138 + 2 * StartButton.Pad);
            refreshLink = new TextLink("Yenile", Theme.Small, Theme.SmallUnderline, Theme.Muted, StringAlignment.Far);
            refreshLink.Bounds = new Rectangle(908, 121, 66, 24);
            badge = new ServerBadge(tip);
            badge.Bounds = new Rectangle(16, 27, badge.Width, 26);
            minButton = new ChromeButton(false);
            minButton.Bounds = new Rectangle(952, 6, 30, 26);
            closeButton = new ChromeButton(true);
            closeButton.Bounds = new Rectangle(984, 6, 30, 26);
            news = new NewsList();
            news.Bounds = new Rectangle(493, 155, 496, 272);
            strip = new MessageStrip(tip);
            strip.Bounds = new Rectangle(492, 580, 498, MessageStrip.TextHeight); // two small lines fit (long errors)
            footer = new Footer();
            footer.Bounds = new Rectangle(0, 618, 770, 48);
            retry = new RetryButton();
            retry.Bounds = new Rectangle(636, 624, 126, 36);
            retry.Visible = false;

            Control[] order = { userBox, passBox, remember, start, refreshLink, badge, minButton, closeButton };
            for (int i = 0; i < order.Length; i++) order[i].TabIndex = i;
            news.TabIndex = 20;
            strip.TabIndex = 21;
            Controls.AddRange(order);
            retry.TabIndex = 4;
            Controls.Add(retry); // before the footer: on top of it
            Controls.Add(news);
            Controls.Add(strip);
            Controls.Add(footer);
            AddHeaderLink("Ana Sayfa", cfg.HomeUrl, 220);
            AddHeaderLink("Destek", cfg.SupportUrl, 330);

            start.Activated += delegate { OnStart(); };
            retry.Activated += delegate { if (busy == Busy.None && LaunchGate.CanRetry(update)) { strip.Clear(); StartUpdate(); } };
            remember.Activated += delegate { if (!remember.Checked) UserSettings.ForgetUser(); };
            refreshLink.Activated += delegate { LoadNews(); };
            news.RetryRequested += delegate { LoadNews(); };
            badge.Activated += delegate { StartProbe(); };
            minButton.Activated += delegate { WindowState = FormWindowState.Minimized; };
            closeButton.Activated += delegate { Close(); };
            probeTimer.Interval = 30000;
            probeTimer.Tick += delegate { StartProbe(); };

            string saved = UserSettings.LoadUser();
            remember.Checked = true;
            if (saved != null) userBox.Text = saved;
            footer.SetIdle("", Theme.Faint);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000 | 0x00080000; // WS_MINIMIZEBOX | WS_SYSMENU: taskbar click minimizes, Alt+F4 works
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (userBox.Text.Length > 0) passBox.Select(); else userBox.Select();
            foreach (string w in cfg.Warnings) ShowMessage(w, MessageKind.Info);
            foreach (string n in cfg.LogNotes) LauncherLog.Write(cfg.GameDir, "config: " + n);
            if (Handover.RolledBack)
                ShowMessage("Başlatıcının yeni sürümü açılamadı; bu sürümle devam ediliyor. Yöneticiye haber ver.", MessageKind.Info);
            if (cfg.TlsFallbackUsed)
            {
                ShowMessage(Tls.MissingText, MessageKind.Info);
                LauncherLog.TlsFallback(cfg);
            }
            // With an update address the update brings Wolfteam.bin (a fresh install starts with an empty folder).
            if (cfg.UpdateBaseUrl == null && !File.Exists(cfg.GameExePath))
                ShowMessage("Wolfteam.bin bulunamadı. Başlatıcıyı oyun klasöründen çalıştır.", MessageKind.Error);
            StartProbe();
            probeTimer.Start();
            LoadNews();
            StartUpdate();
            ConfirmHandOver();
        }

        /// <summary>A launcher started by the bootstrap confirms its start only after the start finished (probe, news and
        /// update started; also when the server is unreachable) and its message loop runs: the first tick of a timer is that
        /// proof. A second tick 3 s later sends the second signal (a UI that hangs in between never does); the bootstrap
        /// then checks that the launcher did not crash (Bootstrap.cs ProcessLauncherRunner).</summary>
        private void ConfirmHandOver()
        {
            if (!Handover.Connected) return;
            System.Windows.Forms.Timer confirm = new System.Windows.Forms.Timer();
            int ticks = 0;
            confirm.Interval = 1500;
            confirm.Tick += delegate
            {
                ticks++;
                if (IsDisposed) { confirm.Stop(); confirm.Dispose(); return; }
                if (ticks == 1)
                {
                    Handover.Signal(BootLayout.SignalStarted);
                    confirm.Interval = 3000;   // the second signal proves the message loop still runs later
                    return;
                }
                confirm.Stop();
                confirm.Dispose();
                Handover.Signal(BootLayout.SignalStable);
            };
            confirm.Start();
        }

        /// <summary>A player who closes the window before the confirmation timer ticked had a working launcher: confirmed
        /// now, so the bootstrap does not take the version for a broken one.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!e.Cancel && Handover.Connected) Handover.ConfirmNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                probeTimer.Dispose();
                tip.Dispose();
                if (backdrop != null) backdrop.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ painting

        private static Bitmap ComposeBackdrop(string artPath)
        {
            Bitmap bmp = new Bitmap(1020, 666);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Theme.Prepare(g);
                bool art = false;
                try
                {
                    if (File.Exists(artPath))
                    {
                        // Read into memory so the file stays unlocked (the updater may replace it).
                        using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(artPath)))
                        using (Image img = Image.FromStream(ms))
                        {
                            g.DrawImage(img, new Rectangle(0, 0, 1020, 616), new Rectangle(0, 0, Math.Min(1020, img.Width), Math.Min(616, img.Height)), GraphicsUnit.Pixel);
                            art = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is ArgumentException || e is OutOfMemoryException || e is UnauthorizedAccessException)) throw;
                }
                if (!art)
                {
                    using (SolidBrush b = new SolidBrush(Theme.PanelSolid)) g.FillRectangle(b, 0, 0, 1020, 616);
                    using (SolidBrush b = new SolidBrush(Theme.Band)) g.FillRectangle(b, 0, 0, 1020, 80);
                    Theme.DrawText(g, "WolfTeam Diriliş", Theme.Heading, Theme.Text, new RectangleF(0, 0, 1020, 80), StringAlignment.Center, StringAlignment.Center);
                }
                Rectangle vr = new Rectangle(440, 80, 580, 536);
                using (LinearGradientBrush vg = new LinearGradientBrush(vr, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), 0f))
                    g.FillRectangle(vg, vr);

                // footer
                using (SolidBrush b = new SolidBrush(Theme.Ink)) g.FillRectangle(b, 0, 616, 1020, 50);
                using (Pen p = new Pen(Theme.Band, 2)) g.DrawLine(p, 0, 617, 1020, 617);
                Theme.DrawText(g, "WolfTeam Diriliş", Theme.Label, Theme.Text, new RectangleF(780, 619, 220, 17), StringAlignment.Far, StringAlignment.Center);
                Theme.DrawText(g, "Başlatıcı " + Program.Version, Theme.Small, Theme.Faint, new RectangleF(780, 635, 220, 15), StringAlignment.Far, StringAlignment.Center);
                Theme.DrawText(g, Theme.Signature, Theme.Tiny, Theme.Faint, new RectangleF(780, 650, 220, 14), StringAlignment.Far, StringAlignment.Center);

                // news panel
                PanelBox(g, new Rectangle(492, 110, 498, 318));
                using (SolidBrush b = new SolidBrush(Theme.Band)) g.FillRectangle(b, 492, 110, 4, 44);
                Theme.DrawText(g, "Duyurular", Theme.Heading, Theme.Text, new RectangleF(510, 120, 200, 26), StringAlignment.Near, StringAlignment.Center);
                using (Pen p = new Pen(Theme.Rule)) g.DrawLine(p, 493, 154, 988, 154);

                // login panel
                PanelBox(g, new Rectangle(492, 440, 276, 138));
                Theme.DrawText(g, "Kullanıcı adı", Theme.Label, Theme.Muted, new RectangleF(504, 448, 200, 16), StringAlignment.Near, StringAlignment.Center);
                Theme.DrawText(g, "Şifre", Theme.Label, Theme.Muted, new RectangleF(504, 502, 200, 16), StringAlignment.Near, StringAlignment.Center);
                using (SolidBrush b = new SolidBrush(Theme.Field))
                {
                    g.FillRectangle(b, UserFrame);
                    g.FillRectangle(b, PassFrame);
                }
            }
            return bmp;
        }

        private static void PanelBox(Graphics g, Rectangle r)
        {
            using (SolidBrush b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Theme.PanelLine)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.DrawImage(backdrop, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            DrawFieldFrame(e.Graphics, UserFrame, userBox.Focused);
            DrawFieldFrame(e.Graphics, PassFrame, passBox.Focused);
        }

        private static void DrawFieldFrame(Graphics g, Rectangle r, bool focused)
        {
            if (focused)
            {
                using (Pen p = new Pen(Theme.Amber, 2)) g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            }
            else
            {
                using (Pen p = new Pen(Theme.FieldLine, 1)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }

        private TextBox MakeTextBox(Rectangle frame, string name, bool password)
        {
            TextBox t = new TextBox();
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Theme.Field;
            t.ForeColor = Theme.Text;
            t.Font = Theme.FieldText;
            t.MaxLength = password ? 64 : 32;
            t.UseSystemPasswordChar = password;
            t.AccessibleName = name;
            t.Location = new Point(frame.X + 8, frame.Y + 8);
            t.Width = frame.Width - 16;
            t.Top = frame.Y + (frame.Height - t.Height) / 2;
            t.ShortcutsEnabled = !password;
            t.GotFocus += delegate { Invalidate(frame); };
            t.LostFocus += delegate { Invalidate(frame); };
            t.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true; // no "ding"
                    OnStart();
                }
            };
            return t;
        }

        private void AddHeaderLink(string text, string url, int x)
        {
            if (string.IsNullOrEmpty(url)) return;
            TextLink l = new TextLink(text, Theme.HeaderLink, Theme.HeaderLinkHover, Theme.Text, StringAlignment.Near);
            l.Bounds = new Rectangle(x, 28, 100, 24);
            l.TabIndex = 30 + x;
            l.Activated += delegate
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Win32Exception) { }
            };
            Controls.Add(l);
        }

        // ------------------------------------------------------------------ window dragging

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            bool header = e.Y < 80;
            bool art = e.X < 480 && e.Y < 616;
            if (e.Button == MouseButtons.Left && (header || art))
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
            }
        }

        // ------------------------------------------------------------------ helpers

        private void Ui(Action a)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { } // form closed meanwhile
        }

        private static void Background(ThreadStart work)
        {
            Thread t = new Thread(work);
            t.IsBackground = true;
            t.Start();
        }

        private void ShowMessage(string text, MessageKind kind)
        {
            strip.Show(text, kind);
        }

        private void RefreshStartButton()
        {
            switch (busy)
            {
                case Busy.LoggingIn: start.SetState(StartState.LoggingIn, -1); break;
                case Busy.Launching: start.SetState(StartState.Launching, -1); break;
                default:
                    if (update == UpdateState.Running)
                        start.SetState(updateChecking ? StartState.Checking : StartState.Updating, updatePercent, updateChecking ? "" : updateEta);
                    else if (LaunchGate.CanPlay(update, File.Exists(cfg.GameExePath))) start.SetState(StartState.Ready, -1);
                    else start.SetState(StartState.Blocked, -1);
                    break;
            }
            retry.Visible = busy == Busy.None && LaunchGate.CanRetry(update);
            footer.Width = retry.Visible ? retry.Left - 6 : 770; // side by side, never overlapping
            bool inputs = busy == Busy.None;
            userBox.ReadOnly = !inputs;
            passBox.ReadOnly = !inputs;
            remember.Enabled = inputs;
        }

        // ------------------------------------------------------------------ server status

        private void StartProbe()
        {
            if (Interlocked.Exchange(ref probing, 1) == 1) return;
            Background(delegate
            {
                bool[] up = Net.ProbeServer(cfg, 3000);
                Interlocked.Exchange(ref probing, 0);
                Ui(delegate
                {
                    badge.SetState(!up[0] ? BadgeState.Offline : up[1] ? BadgeState.Online : BadgeState.LoginDown);
                });
            });
        }

        // ------------------------------------------------------------------ news

        private void LoadNews()
        {
            int gen = Interlocked.Increment(ref newsGeneration);
            if (cfg.NewsUrl == null)
            {
                news.SetItems(Placeholder());
                return;
            }
            news.SetState(NewsState.Loading);
            string url = cfg.NewsUrl;
            Background(delegate
            {
                List<NewsItem> items = null;
                try { items = News.Fetch(url); }
                catch (Exception e)
                {
                    if (!(e is System.Net.WebException || e is IOException || e is InvalidDataException
                          || e is ArgumentException || e is InvalidOperationException)) throw;
                }
                Ui(delegate
                {
                    if (gen != newsGeneration) return;
                    if (items == null) news.SetState(NewsState.Failed); else news.SetItems(items);
                });
                if (items == null) return;
                foreach (NewsItem it in items)
                {
                    if (it.ImageUrl == null) continue;
                    Image img = News.FetchThumb(it.ImageUrl);
                    if (img == null) continue;
                    NewsItem target = it;
                    Ui(delegate
                    {
                        if (gen != newsGeneration) { img.Dispose(); return; }
                        target.Image = img;
                        news.ItemsChanged();
                    });
                }
            });
        }

        private static List<NewsItem> Placeholder()
        {
            NewsItem it = new NewsItem();
            it.Title = "WolfTeam Diriliş'e hoş geldin";
            it.Date = "";
            it.Text = "Duyurular burada görünecek. Hesabın yoksa yöneticiden iste.";
            return new List<NewsItem> { it };
        }

        // ------------------------------------------------------------------ update

        /// <summary>Runs manifest check + download in the background. The play button stays off until it succeeds.</summary>
        private void StartUpdate()
        {
            if (cfg.UpdateBaseUrl == null)
            {
                if (Updater.IsIncomplete(cfg.GameDir))
                {
                    // An interrupted update cannot be finished without an update address: do not start a mixed install.
                    OnUpdateFinished("Önceki güncelleme yarım kaldı ve güncelleme adresi ayarlı değil. Yöneticiye haber ver.",
                        false, 0, false);
                    return;
                }
                update = UpdateState.NotConfigured;
                footer.SetIdle("Güncelleme adresi ayarlı değil.", Theme.Amber);
                RefreshStartButton();
                return;
            }
            update = UpdateState.Running;
            updatePercent = -1;
            updateChecking = true;
            updateEta = "";
            footer.SetIndeterminate("Güncelleme kontrol ediliyor…");
            RefreshStartButton();
            Updater up = new Updater(cfg);
            int lastTick = 0;
            up.Progress = delegate(UpdateProgress p)
            {
                int now = Environment.TickCount;
                if (now - lastTick < 150 && p.Done < p.Total) return;
                lastTick = now;
                bool checking = p.Checking;
                string file = p.File;
                int idx = p.Index, count = p.Count, pct = p.Percent;
                double bps = p.BytesPerSecond;
                string line = checking ? "Dosyalar kontrol ediliyor " + idx + "/" + count + ": " + file : ProgressText.Line(p);
                string eta = checking ? "" : ProgressText.Eta(p.Total - p.Done, bps);
                Ui(delegate
                {
                    updatePercent = pct;
                    updateChecking = checking;
                    updateEta = eta;
                    footer.SetProgress(line, pct, checking || bps < 1 ? "" : Program.FormatSpeed(bps));
                    RefreshStartButton();
                });
            };
            Background(delegate
            {
                string error = null;
                bool manifestFailed = false;
                int updated = 0;
                bool selfPending = false;
                bool offline = false;
                // Before the file check: the early package's muted music (runs once; skipped while the game runs).
                if (MusicFix.Run(cfg.GameDir, MusicFix.IsRunning(cfg.GameExePath)) == MusicFixResult.Changed)
                    Ui(delegate { ShowMessage("Oyun müziği açıldı: ses seviyesi varsayılana (%50) getirildi.", MessageKind.Info); });
                try
                {
                Updater.WithUpdateLock(cfg.GameDir, delegate { Ui(delegate { footer.SetIndeterminate(Updater.WaitingText); }); }, delegate
                {
                try
                {
                    Manifest m = up.FetchManifest();
                    List<ManifestEntry> todo = up.Plan(m);
                    updated = todo.Count;
                    foreach (ManifestEntry e in todo) selfPending |= e.IsLauncher;
                    if (todo.Count > 0) Ui(delegate { updateChecking = false; footer.SetProgress("İndirme başlıyor…", 0, ""); });
                    up.Apply(todo); // also with an empty list: that clears an earlier "update incomplete" marker
                    up.SaveVerified(m); // every file matches this list now: the fallback when the server is down

                }
                catch (UpdateException e)
                {
                    error = e.Message;
                    manifestFailed = e.ManifestUnavailable; // false when an earlier update is unfinished
                    // Only when the server did not answer (never after a failed download): play the last verified
                    // version if the files still match it.
                    if (e.Unreachable && e.ManifestUnavailable)
                    {
                        Ui(delegate { footer.SetIndeterminate("Sunucuya ulaşılamadı; dosyalar son doğrulanan sürümle karşılaştırılıyor…"); });
                        if (up.VerifyLastVerified()) offline = true;
                    }
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is UnauthorizedAccessException || e is System.Net.WebException)) throw;
                    error = "Güncelleme yapılamadı. İnternet bağlantını kontrol edip Tekrar dene'ye bas.";
                }
                });
                }
                catch (UpdateException e) { error = e.Message; } // the update lock could not be taken (not a contention)
                bool configReplaced = up.ConfigReplaced;
                Ui(delegate
                {
                    OnUpdateFinished(offline ? null : error, manifestFailed, updated, selfPending, offline);
                    if (configReplaced && update == UpdateState.Done && !selfPending) ShowMessage(ConfigReplacedText, MessageKind.Info);
                });
            });
        }

        /// <summary>After the updater replaced config.xfs (ConfigRepair.cs): one calm line, the details are in launcher.log.</summary>
        public const string LaunchWhileUpdatingText = "Başka bir başlatıcı penceresi oyunu güncelliyor; bitince tekrar dene.";

        public const string ConfigReplacedText = "Oyun ayarları (tuşlar, imleç) varsayılana getirildi; eski ayar dosyan yedeklendi.";

        private void OnUpdateFinished(string error, bool manifestFailed, int updated, bool selfPending)
        {
            OnUpdateFinished(error, manifestFailed, updated, selfPending, false);
        }

        private void OnUpdateFinished(string error, bool manifestFailed, int updated, bool selfPending, bool offline)
        {
            updatePercent = -1;
            updateEta = "";
            if (offline && File.Exists(cfg.GameExePath))
            {
                update = UpdateState.Offline;
                footer.SetIdle("Son doğrulanan sürüm. Oyun hazır.", Theme.Amber);
                ShowMessage("Güncelleme sunucusuna ulaşılamadı; son doğrulanan sürümle oynuyorsun.", MessageKind.Info);
            }
            else if (error == null)
            {
                update = UpdateState.Done;
                if (!File.Exists(cfg.GameExePath))
                {
                    footer.SetIdle("Wolfteam.bin eksik.", Theme.Danger);
                    ShowMessage("Güncelleme listesinde Wolfteam.bin yok. Yöneticiye haber ver.", MessageKind.Error);
                }
                else
                {
                    footer.SetIdle(updated > 0 ? "Güncelleme tamamlandı. Oyun hazır." : "Oyun dosyaları güncel. Oyun hazır.", Theme.Green);
                    if (updated > 0 && selfPending && Handover.StagedDiffersFromRunning(cfg.GameDir, exePath)
                        && File.Exists(BootLayout.BootstrapPath(cfg.GameDir)))
                    {
                        // The bootstrap starts the new version as a trial (and rolls it back itself if it does not start).
                        footer.SetIdle("Başlatıcı güncellendi, yeniden başlatılıyor…", Theme.Green);
                        ShowMessage("Başlatıcının yeni sürümü indirildi; başlatıcı yeniden açılıyor…", MessageKind.Success);
                        BeginInvoke((Action)RestartForSelfUpdate);
                    }
                    else if (updated > 0)
                    {
                        ShowMessage(selfPending ? "Güncelleme tamamlandı. Başlatıcının yeni sürümü sonraki açılışta devreye girer."
                            : "Güncelleme tamamlandı.", MessageKind.Success);
                    }
                }
            }
            else
            {
                // Every failure keeps the play button off: the folder was not verified against the release list.
                update = manifestFailed ? UpdateState.ManifestFailed : UpdateState.Failed;
                footer.SetIdle(manifestFailed ? "Güncelleme kontrol edilemedi." : "Güncelleme yarım kaldı.", Theme.Danger);
                ShowMessage(error, MessageKind.Error);
            }
            RefreshStartButton();
            if ((update == UpdateState.Done || update == UpdateState.Offline) && start.State == StartState.Ready)
            {
                if (userBox.Text.Length > 0) passBox.Select(); else userBox.Select();
            }
            else if (retry.Visible)
            {
                retry.Select();
            }
        }

        private void RestartForSelfUpdate()
        {
            if (busy != Busy.None || IsDisposed) return;
            Handover.ConfirmNow();
            if (Handover.RestartViaBootstrap(cfg.GameDir, Program.Args))
            {
                Close();
                return;
            }
            footer.SetIdle("Güncelleme tamamlandı. Oyun hazır.", Theme.Green);
            ShowMessage("Güncelleme tamamlandı. Başlatıcının yeni sürümü sonraki açılışta devreye girer.", MessageKind.Success);
        }

        // ------------------------------------------------------------------ login + game start

        private void OnStart()
        {
            if (busy != Busy.None || !LaunchGate.CanPlay(update, File.Exists(cfg.GameExePath))) return;
            string user = userBox.Text.Trim();
            string pass = passBox.Text;
            if (user.Length == 0)
            {
                ShowMessage("Kullanıcı adını yaz.", MessageKind.Error);
                userBox.Select();
                return;
            }
            if (user.IndexOfAny(new char[] { ' ', '\t', '"' }) >= 0)
            {
                ShowMessage("Kullanıcı adında boşluk veya tırnak olamaz.", MessageKind.Error);
                userBox.Select();
                return;
            }
            if (pass.Length == 0)
            {
                ShowMessage("Şifreni yaz.", MessageKind.Error);
                passBox.Select();
                return;
            }
            if (cfg.InsecureLogin && !insecureLoginAccepted)
            {
                // Security audit F3: the password never goes over plain http silently; only after an explicit yes.
                DialogResult answer = MessageBox.Show(this, InsecureLoginQuestion, "WolfTeam Diriliş", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    LauncherLog.Write(cfg.GameDir, "insecure login declined by the player");
                    ShowMessage(Tls.Available ? "Giriş yapılmadı: giriş bağlantısı şifreli değil ve onay verilmedi."
                        : Tls.MissingText, MessageKind.Error);
                    return;
                }
                LauncherLog.Write(cfg.GameDir, "insecure (http) login accepted by the player" + (Tls.Available ? "" : ", no TLS 1.2"));
                insecureLoginAccepted = true;
            }
            strip.Clear();
            DoLogin(user, pass);
        }

        private bool insecureLoginAccepted;

        public const string InsecureLoginQuestion = "Giriş bağlantısı şifreli değil (HTTPS değil). Giriş yaparsan kullanıcı adın ve "
            + "şifren şifrelenmeden gönderilir ve aynı ağdaki biri görebilir.\n\nYine de şifresiz bağlantıyla giriş yapılsın mı?";

        private void DoLogin(string user, string pass)
        {
            if (!File.Exists(cfg.GameExePath))
            {
                busy = Busy.None;
                passBox.Text = "";
                RefreshStartButton();
                ShowMessage("Wolfteam.bin bulunamadı. Başlatıcıyı oyun klasöründen çalıştır.", MessageKind.Error);
                return;
            }
            busy = Busy.LoggingIn;
            RefreshStartButton();
            string url = cfg.LoginUrl;
            Background(delegate
            {
                LoginResult r = Net.Login(url, user, pass);
                Ui(delegate { OnLoginFinished(user, r); });
            });
        }

        private void OnLoginFinished(string user, LoginResult r)
        {
            passBox.Text = ""; // cleared after every attempt
            if (!r.Ok)
            {
                busy = Busy.None;
                RefreshStartButton();
                ShowMessage(r.Error, MessageKind.Error);
                passBox.Select();
                return;
            }
            if (remember.Checked) UserSettings.SaveUser(user); else UserSettings.ForgetUser();
            busy = Busy.Launching;
            RefreshStartButton();
            ShowMessage("Giriş başarılı, oyun açılıyor…", MessageKind.Info);
            string error = LaunchGame(user, r.Token);
            if (error != null)
            {
                busy = Busy.None;
                RefreshStartButton();
                ShowMessage(error, MessageKind.Error);
                return;
            }
            WindowState = FormWindowState.Minimized;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 3000;
            t.Tick += delegate { t.Stop(); Close(); };
            t.Start();
        }

        /// <summary>Starts "&lt;gamedir&gt;\Wolfteam.bin &lt;user&gt; &lt;token&gt; 1 0" from the game folder. Returns a
        /// Turkish error or null.</summary>
        private string LaunchGame(string user, string token)
        {
            string path = cfg.GameExePath;
            if (!File.Exists(path)) return "Wolfteam.bin bulunamadı. Başlatıcıyı oyun klasöründen çalıştır.";
            // Under the update lock: another launcher window that is updating (and may be replacing config.xfs) is not
            // raced; once the game process exists, a later update sees it running and leaves config.xfs alone.
            FileStream held;
            if (!Updater.TryHoldUpdateLock(cfg.GameDir, out held)) return LaunchWhileUpdatingText;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.GetFullPath(path), user + " " + token + " 1 0");
                psi.WorkingDirectory = cfg.GameDir;
                psi.UseShellExecute = false;
                Process.Start(psi);
                return null;
            }
            catch (Win32Exception e) { return LaunchProblem(e); }
            catch (InvalidOperationException e) { return LaunchProblem(e); }
            finally { if (held != null) held.Dispose(); }
        }

        /// <summary>Turkish text for a failed game start (the exception texts can be English).</summary>
        internal static string LaunchProblem(Exception e)
        {
            Win32Exception w = e as Win32Exception;
            if (w != null)
                return "Oyun başlatılamadı (Windows hata kodu " + w.NativeErrorCode + "). Virüs programı Wolfteam.bin'i engellemiş olabilir; "
                       + "başlatıcıyı kapatıp tekrar aç.";
            return "Oyun başlatılamadı. Wolfteam.bin açılamıyor; başlatıcıyı kapatıp tekrar aç.";
        }
    }
}
