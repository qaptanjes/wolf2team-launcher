// Custom-painted controls. Each one paints the matching slice of the form's composed background bitmap first, so the
// art shows through without WinForms transparency tricks and without flicker.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WolfTeamDirilis
{
    internal class SkinControl : Control
    {
        /// <summary>The form's composed background (same coordinates as the form client area).</summary>
        public static Bitmap Backdrop;

        protected bool Hover;
        protected bool Pressed;

        public event EventHandler Activated;

        public SkinControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop != null)
            {
                e.Graphics.DrawImage(Backdrop, new Rectangle(0, 0, Width, Height), new Rectangle(Left, Top, Width, Height),
                    GraphicsUnit.Pixel);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Theme.PanelSolid)) e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        protected virtual bool Clickable
        {
            get { return true; }
        }

        protected virtual bool HitTest(Point p)
        {
            return ClientRectangle.Contains(p);
        }

        protected bool KeyboardFocus
        {
            get { return Focused && ShowFocusCues; }
        }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e) { Hover = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = HitTest(e.Location);
            if (h != Hover) { Hover = h; Invalidate(); }
            Cursor = h && Clickable ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitTest(e.Location) && Clickable)
            {
                Pressed = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = Pressed && HitTest(e.Location) && Clickable;
            Pressed = false;
            Invalidate();
            base.OnMouseUp(e);
            if (fire) Fire();
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }

        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Enter || keyData == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && Clickable)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Fire();
            }
            base.OnKeyDown(e);
        }

        protected void Fire()
        {
            EventHandler h = Activated;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected void DrawFocusRect(Graphics g, Rectangle r)
        {
            using (Pen p = new Pen(Theme.Amber, 2)) g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
        }
    }

    internal enum StartState { Ready, LoggingIn, Checking, Updating, Launching, Blocked }

    /// <summary>The chamfered red "OYUNU BAŞLAT" plate (spec sec. 5). The control is 5 px larger on each side than
    /// the plate so the focus ring fits; clicks outside the plate path are ignored.</summary>
    internal sealed class StartButton : SkinControl
    {
        public const int Pad = 5;
        private StartState state = StartState.Blocked;
        private int percent = -1;
        private string sub = "";

        public StartButton()
        {
            AccessibleName = "Oyunu başlat";
            AccessibleRole = AccessibleRole.PushButton;
        }

        public StartState State
        {
            get { return state; }
        }

        public void SetState(StartState s, int pct)
        {
            SetState(s, pct, "");
        }

        /// <summary><paramref name="subText"/>: a third line under the percentage while updating (remaining time).</summary>
        public void SetState(StartState s, int pct, string subText)
        {
            subText = subText ?? "";
            if (s == state && pct == percent && subText == sub) return;
            state = s;
            percent = pct;
            sub = subText;
            AccessibleDescription = s == StartState.Ready ? null : Label();
            Invalidate();
        }

        protected override bool Clickable
        {
            get { return state == StartState.Ready; }
        }

        private RectangleF Plate
        {
            get { return new RectangleF(Pad, Pad, Width - 2 * Pad - 1, Height - 2 * Pad - 1); }
        }

        protected override bool HitTest(Point p)
        {
            RectangleF r = Plate;
            using (GraphicsPath path = Theme.Chamfer(r.X, r.Y, r.Width, r.Height, 16)) return path.IsVisible(p);
        }

        private string Label()
        {
            switch (state)
            {
                case StartState.LoggingIn: return "GİRİŞ YAPILIYOR";
                case StartState.Checking: return "KONTROL EDİLİYOR";
                case StartState.Updating: return "GÜNCELLENİYOR";
                case StartState.Launching: return "OYUN AÇILIYOR";
                default: return "OYUNU BAŞLAT";
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            RectangleF r = Plate;
            Color top, bot, edge, text;
            bool busy = state != StartState.Ready;
            if (busy) { top = Theme.Hex("#44484E"); bot = Theme.Hex("#2C2F33"); edge = Theme.Hex("#5A5F66"); text = Theme.SlateText; }
            else if (Pressed) { top = Theme.Hex("#7E0D06"); bot = Theme.Hex("#5A0904"); edge = Theme.Hex("#B3150B"); text = Theme.Hex("#E8D8D0"); }
            else if (Hover) { top = Theme.Hex("#EA2B12"); bot = Theme.Hex("#9A1208"); edge = Theme.Hex("#FF8A5C"); text = Color.White; }
            else { top = Theme.Hex("#C51A0D"); bot = Theme.Hex("#7E0D06"); edge = Theme.RedEdge; text = Theme.Text; }

            using (GraphicsPath path = Theme.Chamfer(r.X, r.Y, r.Width, r.Height, 16))
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(r, top, bot, 90f)) g.FillPath(lg, path);
                using (Pen hi = new Pen(Theme.Alpha(edge, 150), 1)) g.DrawLine(hi, r.X + 17, r.Y + 2, r.Right - 2, r.Y + 2);
                using (Pen outline = new Pen(Theme.Alpha(edge, busy ? 255 : 200), 1)) g.DrawPath(outline, path);
            }
            if (KeyboardFocus && !busy)
            {
                using (GraphicsPath ring = Theme.Chamfer(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6, 17))
                using (Pen p = new Pen(Theme.Amber, 2)) g.DrawPath(p, ring);
            }
            if (!busy)
            {
                RectangleF tr = r;
                if (Pressed) tr.Offset(0, 2);
                Theme.DrawText(g, Label(), Theme.Button, text, tr, StringAlignment.Center, StringAlignment.Center);
            }
            else if ((state == StartState.Updating || state == StartState.Checking) && percent >= 0)
            {
                float cy = r.Y + r.Height / 2 - (sub.Length > 0 ? 10 : 0);
                Theme.DrawText(g, Label(), Theme.ButtonSmall, text, new RectangleF(r.X, cy - 30, r.Width, 32),
                    StringAlignment.Center, StringAlignment.Center);
                Theme.DrawText(g, "%" + percent, Theme.ButtonSub, Theme.Amber, new RectangleF(r.X, cy + 4, r.Width, 22),
                    StringAlignment.Center, StringAlignment.Center);
                if (sub.Length > 0)
                    Theme.DrawText(g, sub, Theme.Small, Theme.SlateText, new RectangleF(r.X + 6, cy + 28, r.Width - 12, 20),
                        StringAlignment.Center, StringAlignment.Center);
            }
            else if (state == StartState.Blocked)
            {
                // The play plate, greyed: visible but not usable until the files are checked.
                Theme.DrawText(g, Label(), Theme.Button, Theme.Alpha(text, 150), r, StringAlignment.Center, StringAlignment.Center);
            }
            else
            {
                Theme.DrawText(g, Label(), Theme.ButtonSmall, text, r, StringAlignment.Center, StringAlignment.Center);
            }
        }
    }

    /// <summary>"Tekrar dene": shown next to the footer after a failed update check or download.</summary>
    internal sealed class RetryButton : SkinControl
    {
        public RetryButton()
        {
            Text = "Tekrar dene";
            AccessibleName = "Güncellemeyi tekrar dene";
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            RectangleF r = new RectangleF(2, 2, Width - 5, Height - 5);
            Color fill = Pressed ? Theme.Hex("#5A0904") : Hover ? Theme.Hex("#9A1208") : Theme.Hex("#7E0D06");
            using (GraphicsPath path = Theme.Chamfer(r.X, r.Y, r.Width, r.Height, 8))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                using (Pen p = new Pen(Hover ? Theme.Hex("#FF8A5C") : Theme.RedEdge, 1)) g.DrawPath(p, path);
            }
            Theme.DrawText(g, Text, Theme.Label, Theme.Text, r, StringAlignment.Center, StringAlignment.Center);
            if (KeyboardFocus) DrawFocusRect(g, ClientRectangle);
        }
    }

    internal enum BadgeState { Checking, Online, LoginDown, Offline }

    internal sealed class ServerBadge : SkinControl
    {
        private BadgeState state = BadgeState.Checking;
        private readonly ToolTip tip;

        public ServerBadge(ToolTip tip)
        {
            this.tip = tip;
            AccessibleRole = AccessibleRole.PushButton;
            SetState(BadgeState.Checking);
        }

        public void SetState(BadgeState s)
        {
            state = s;
            string t = Text2();
            AccessibleName = t;
            using (Graphics g = CreateGraphics())
                Width = (int)Math.Ceiling(g.MeasureString(t, Theme.Badge).Width) + 34;
            string detail = null;
            switch (s)
            {
                case BadgeState.Online: detail = "Oyun sunucusu ve giriş servisi çalışıyor."; break;
                case BadgeState.LoginDown: detail = "Oyun sunucusu açık ama giriş servisi yanıt vermiyor."; break;
                case BadgeState.Offline: detail = "Oyun sunucusuna bağlanılamadı."; break;
            }
            tip.SetToolTip(this, detail == null ? "Tıkla: yeniden kontrol et" : detail + " Tıkla: yeniden kontrol et");
            Invalidate();
        }

        private string Text2()
        {
            switch (state)
            {
                case BadgeState.Online: return "Sunucu: Çevrimiçi";
                case BadgeState.LoginDown: return "Sunucu: Giriş kapalı";
                case BadgeState.Offline: return "Sunucu: Çevrimdışı";
                default: return "Sunucu: Kontrol ediliyor…";
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            Color dot;
            switch (state)
            {
                case BadgeState.Online: dot = Theme.Green; break;
                case BadgeState.LoginDown: dot = Theme.Amber; break;
                case BadgeState.Offline: dot = Theme.Danger; break;
                default: dot = Theme.Faint; break;
            }
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Hover ? 150 : 110, 0, 0, 0))) g.FillRectangle(b, r);
            using (Pen p = new Pen(Color.FromArgb(160, 0, 0, 0))) g.DrawRectangle(p, r);
            using (SolidBrush b = new SolidBrush(Theme.Alpha(dot, 60))) g.FillEllipse(b, 7, 6, 14, 14);
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, 10, 9, 8, 8);
            Theme.DrawText(g, Text2(), Theme.Badge, Theme.Text, new RectangleF(25, 0, Width - 26, Height),
                StringAlignment.Near, StringAlignment.Center);
            if (KeyboardFocus) DrawFocusRect(g, ClientRectangle);
        }
    }

    internal sealed class ChromeButton : SkinControl
    {
        private readonly bool close;

        public ChromeButton(bool close)
        {
            this.close = close;
            AccessibleName = close ? "Kapat" : "Küçült";
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            if (Hover || Pressed)
            {
                using (SolidBrush b = new SolidBrush(close ? Theme.CloseHover : Color.FromArgb(40, 255, 255, 255)))
                    g.FillRectangle(b, ClientRectangle);
            }
            using (Pen p = new Pen(Theme.Text, 2))
            {
                int cx = Width / 2, cy = Height / 2 + 1;
                if (close)
                {
                    g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy + 6);
                    g.DrawLine(p, cx + 6, cy - 6, cx - 6, cy + 6);
                }
                else
                {
                    g.DrawLine(p, cx - 6, cy, cx + 6, cy);
                }
            }
            if (KeyboardFocus) DrawFocusRect(g, ClientRectangle);
        }
    }

    internal sealed class TextLink : SkinControl
    {
        private readonly Font font, hoverFont;
        private readonly Color color;
        private readonly StringAlignment align;

        public TextLink(string text, Font font, Font hoverFont, Color color, StringAlignment align)
        {
            Text = text;
            this.font = font;
            this.hoverFont = hoverFont;
            this.color = color;
            this.align = align;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.Link;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            Theme.DrawText(g, Text, Hover ? hoverFont : font, Hover ? Theme.Text : color, new RectangleF(2, 0, Width - 4, Height),
                align, StringAlignment.Center);
            if (KeyboardFocus) DrawFocusRect(g, ClientRectangle);
        }
    }

    internal sealed class CheckRow : SkinControl
    {
        private bool isChecked;

        public CheckRow(string text)
        {
            Text = text;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.CheckButton;
            Activated += delegate { Checked = !Checked; };
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                isChecked = value;
                AccessibleDescription = value ? "işaretli" : "işaretsiz";
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            Rectangle box = new Rectangle(0, 3, 15, 15);
            if (isChecked)
            {
                using (SolidBrush b = new SolidBrush(Theme.Amber)) g.FillRectangle(b, box);
                using (Pen p = new Pen(Theme.CheckMark, 2))
                    g.DrawLines(p, new Point[] { new Point(3, 10), new Point(6, 14), new Point(12, 6) });
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillRectangle(b, box);
                using (Pen p = new Pen(Hover ? Theme.Amber : Theme.FieldLine)) g.DrawRectangle(p, 0, 3, 14, 14);
            }
            Theme.DrawText(g, Text, Theme.Small, Theme.Text, new RectangleF(22, 0, Width - 22, Height),
                StringAlignment.Near, StringAlignment.Center);
            if (KeyboardFocus)
            {
                using (Pen p = new Pen(Theme.Amber, 2)) g.DrawRectangle(p, -1 + 1, 2, 16, 16);
            }
        }
    }

    internal enum MessageKind { Error, Info, Success }

    /// <summary>One-line message strip; the full text is in the tooltip and Ctrl+C copies it.</summary>
    internal sealed class MessageStrip : SkinControl
    {
        private readonly ToolTip tip;
        private MessageKind kind;

        /// <summary>Strip size in MainForm: 498 x 34, text inset 12 px left and 8 px right.</summary>
        public const int TextWidth = 478;
        public const int TextHeight = 34;

        /// <summary>The body font when the text fits on one line; otherwise the small font on (up to) two lines, so a long
        /// error is readable without its tooltip.</summary>
        public static Font FontFor(Graphics g, string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return Theme.Body;
            int oneLine = (int)Math.Ceiling(Theme.Body.GetHeight(g)) + 1;
            return NewsList.Fits(g, text, Theme.Body, width, oneLine) ? Theme.Body : Theme.Small;
        }

        public MessageStrip(ToolTip tip)
        {
            this.tip = tip;
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
            Visible = false;
        }

        protected override bool Clickable
        {
            get { return false; }
        }

        public void Show(string text, MessageKind k)
        {
            Text = text;
            kind = k;
            AccessibleName = text;
            tip.SetToolTip(this, text);
            Visible = true;
            Invalidate();
        }

        public void Clear()
        {
            Text = "";
            Visible = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C && Text.Length > 0)
            {
                try { Clipboard.SetText(Text); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            Color bar = kind == MessageKind.Error ? Theme.Danger : kind == MessageKind.Success ? Theme.Green : Theme.Amber;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(b, ClientRectangle);
            using (SolidBrush b = new SolidBrush(bar)) g.FillRectangle(b, 0, 0, 3, Height);
            Font f = FontFor(g, Text, Width - 20);
            if (f == Theme.Body)
                Theme.DrawText(g, Text, f, Theme.Text, new RectangleF(12, 0, Width - 20, Height), StringAlignment.Near, StringAlignment.Center);
            else
                Theme.DrawWrappedCentered(g, Text, f, Theme.Text, new RectangleF(12, 0, Width - 20, Height));
            if (Focused) DrawFocusRect(g, ClientRectangle);
        }
    }

    internal enum FooterMode { Idle, Indeterminate, Progress }

    /// <summary>Left part of the footer: status line, percent, speed and the amber progress bar.</summary>
    internal sealed class Footer : SkinControl
    {
        private FooterMode mode = FooterMode.Idle;
        private string line = "";
        private Color dot = Theme.Faint;
        private int percent;
        private string speed = "";
        private readonly Timer anim = new Timer();
        private DateTime animStart = DateTime.UtcNow;

        public Footer()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            anim.Interval = 30;
            anim.Tick += delegate { Invalidate(new Rectangle(0, 25, Width, 12)); };
        }

        protected override bool Clickable
        {
            get { return false; }
        }

        public void SetIdle(string text, Color dotColor)
        {
            mode = FooterMode.Idle;
            line = text;
            dot = dotColor;
            anim.Stop();
            AccessibleName = text;
            Invalidate();
        }

        public void SetIndeterminate(string text)
        {
            mode = FooterMode.Indeterminate;
            line = text;
            speed = "";
            AccessibleName = text;
            if (SystemInformation.UIEffectsEnabled && !anim.Enabled) { animStart = DateTime.UtcNow; anim.Start(); }
            Invalidate();
        }

        public void SetProgress(string text, int pct, string speedText)
        {
            mode = FooterMode.Progress;
            line = text;
            percent = Math.Max(0, Math.Min(100, pct));
            speed = speedText ?? "";
            anim.Stop();
            AccessibleName = text + " %" + percent;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }

        // The control sits at (0, 618); spec coordinates are converted with y - 618.
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            if (mode == FooterMode.Idle)
            {
                using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, 20, 18, 8, 8);
                Theme.DrawText(g, line, Theme.Small, Theme.Muted, new RectangleF(34, 12, 700, 20), StringAlignment.Near, StringAlignment.Center);
                return;
            }
            Theme.DrawText(g, line, Theme.Small, Theme.Text, new RectangleF(20, 5, 520, 18), StringAlignment.Near, StringAlignment.Center);
            using (SolidBrush track = new SolidBrush(Theme.Track)) g.FillRectangle(track, 20, 29, 740, 6);
            using (SolidBrush fill = new SolidBrush(Theme.Amber))
            {
                if (mode == FooterMode.Progress)
                {
                    Theme.DrawText(g, "%" + percent, Theme.Label, Theme.Amber, new RectangleF(560, 5, 70, 18), StringAlignment.Far, StringAlignment.Center);
                    Theme.DrawText(g, speed, Theme.Small, Theme.Muted, new RectangleF(640, 5, 120, 18), StringAlignment.Far, StringAlignment.Center);
                    g.FillRectangle(fill, 20, 29, 740 * percent / 100, 6);
                }
                else if (anim.Enabled)
                {
                    double t = ((DateTime.UtcNow - animStart).TotalMilliseconds % 1200) / 1200.0;
                    int x = 20 + (int)(t * (740 + 120)) - 120;
                    int x0 = Math.Max(20, x), x1 = Math.Min(760, x + 120);
                    if (x1 > x0) g.FillRectangle(fill, x0, 29, x1 - x0, 6);
                }
                else
                {
                    g.FillRectangle(fill, 20, 29, 370, 6); // reduced motion: static half bar
                }
            }
        }
    }

    internal enum NewsState { Loading, Empty, Failed, Items }

    /// <summary>Scrollable news list inside the news panel.</summary>
    internal sealed class NewsList : SkinControl
    {
        /// <summary>Height of a one-line title.</summary>
        public const int TitleLine = 22;

        /// <summary>Height of a title wrapped to <paramref name="width"/> (at least one line).</summary>
        public static int TitleHeight(Graphics g, string title, int width)
        {
            if (string.IsNullOrEmpty(title)) return TitleLine;
            int h = (int)Math.Ceiling(g.MeasureString(title, Theme.NewsTitle, width).Height);
            return Math.Max(TitleLine, h);
        }

        /// <summary>True when the whole text fits into width x height when wrapped at words.</summary>
        public static bool Fits(Graphics g, string text, Font font, int width, int height)
        {
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.Word;
                sf.FormatFlags = StringFormatFlags.LineLimit; // a line cut at the bottom does not count as shown
                int chars, lines;
                g.MeasureString(text, font, new SizeF(width, height), sf, out chars, out lines);
                return chars >= text.Length;
            }
        }

        private NewsState state = NewsState.Loading;
        private List<NewsItem> items = new List<NewsItem>();
        private int scroll;
        private int contentHeight;
        private Rectangle retryRect;

        public event EventHandler RetryRequested;

        public NewsList()
        {
            TabStop = false;
            AccessibleName = "Duyurular";
            AccessibleRole = AccessibleRole.List;
        }

        protected override bool Clickable
        {
            get { return state == NewsState.Failed; }
        }

        protected override bool HitTest(Point p)
        {
            return state == NewsState.Failed && retryRect.Contains(p);
        }

        public void SetState(NewsState s)
        {
            state = s;
            if (s != NewsState.Items) items = new List<NewsItem>();
            scroll = 0;
            Invalidate();
        }

        public void SetItems(List<NewsItem> list)
        {
            items = list;
            state = list.Count == 0 ? NewsState.Empty : NewsState.Items;
            scroll = 0;
            Invalidate();
        }

        public void ItemsChanged()
        {
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool retry = state == NewsState.Failed && Pressed && retryRect.Contains(e.Location);
            base.OnMouseUp(e);
            if (retry)
            {
                EventHandler h = RetryRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollBy(-e.Delta / 3);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // Wheel messages go to the focused control; take focus only if nothing typed is focused.
            Form f = FindForm();
            if (f != null && !(f.ActiveControl is TextBox)) Focus();
        }

        public void ScrollBy(int dy)
        {
            int max = Math.Max(0, contentHeight - Height);
            int s = Math.Max(0, Math.Min(max, scroll + dy));
            if (s != scroll) { scroll = s; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            if (state != NewsState.Items)
            {
                string msg = state == NewsState.Loading ? "Duyurular yükleniyor…"
                    : state == NewsState.Empty ? "Henüz duyuru yok." : "Duyurular yüklenemedi.";
                Theme.DrawText(g, msg, Theme.Body, Theme.Muted, new RectangleF(0, 0, Width, Height - 30),
                    StringAlignment.Center, StringAlignment.Center);
                if (state == NewsState.Failed)
                {
                    retryRect = new Rectangle(Width / 2 - 50, Height / 2 + 2, 100, 22);
                    Theme.DrawText(g, "Tekrar dene", Hover ? Theme.SmallUnderline : Theme.Small, Hover ? Theme.Text : Theme.Amber,
                        retryRect, StringAlignment.Center, StringAlignment.Center);
                }
                return;
            }
            const int left = 18, right = 18, dateW = 84;
            int y = 12 - scroll;
            int textW = Width - left - right;
            for (int i = 0; i < items.Count; i++)
            {
                NewsItem it = items[i];
                int tx = left, tw = textW;
                bool hasImg = it.Image != null;
                if (hasImg)
                {
                    g.DrawImage(it.Image, tx, y + 2, 128, 72);
                    using (Pen p = new Pen(Theme.PanelLine)) g.DrawRectangle(p, tx, y + 2, 128, 72);
                    tx += 142;
                    tw -= 142;
                }
                // Long titles wrap (they used to be cut with "…" after about 45 characters).
                int titleH = TitleHeight(g, it.Title, tw - dateW);
                Theme.DrawWrapped(g, it.Title, Theme.NewsTitle, Theme.Text, new RectangleF(tx, y + (titleH == TitleLine ? 1 : 0), tw - dateW, titleH + 2));
                Theme.DrawText(g, it.Date, Theme.Small, Theme.Faint, new RectangleF(tx + tw - dateW, y, dateW, TitleLine),
                    StringAlignment.Far, StringAlignment.Center);
                int bodyH = 0;
                if (it.Text.Length > 0)
                {
                    SizeF sz = g.MeasureString(it.Text, Theme.Body, tw);
                    bodyH = (int)Math.Ceiling(sz.Height);
                    Theme.DrawWrapped(g, it.Text, Theme.Body, Theme.Muted, new RectangleF(tx, y + titleH + 2, tw, bodyH + 2));
                }
                int blockH = Math.Max(titleH + 2 + bodyH, hasImg ? 76 : 0);
                y += blockH + 12;
                if (i < items.Count - 1)
                {
                    using (Pen p = new Pen(Theme.Rule)) g.DrawLine(p, left, y, Width - right, y);
                    y += 12;
                }
            }
            contentHeight = y + scroll;
            if (contentHeight > Height)
            {
                int track = Height - 16;
                int thumb = Math.Max(24, track * Height / contentHeight);
                int ty = 8 + (track - thumb) * scroll / Math.Max(1, contentHeight - Height);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(b, Width - 9, ty, 3, thumb);
            }
        }
    }
}
