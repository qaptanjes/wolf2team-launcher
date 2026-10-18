// Owner-drawn controls of the installer, in the launcher's visual language (Theme.cs): the chamfered red plate for
// the main action, an outlined plate for secondary actions, a check row and a thin progress bar. They sit on the solid
// content panel, so no transparency is needed.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WolfTeamDirilis.Setup
{
    internal abstract class SetupControl : Control
    {
        protected bool Hover, Pressed;
        public event EventHandler Activated;

        protected SetupControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.PanelSolid;
            TabStop = true;
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled) { Pressed = true; Focus(); Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = Pressed && ClientRectangle.Contains(e.Location) && Enabled;
            Pressed = false;
            Invalidate();
            base.OnMouseUp(e);
            if (fire) Fire();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Enabled)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Fire();
            }
            base.OnKeyDown(e);
        }

        public void Fire()
        {
            EventHandler h = Activated;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected bool KeyboardFocus
        {
            get { return Focused && ShowFocusCues; }
        }
    }

    internal sealed class Plate : SetupControl
    {
        private readonly bool primary;

        public Plate(string text, bool primary)
        {
            this.primary = primary;
            Text = text;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            AccessibleName = Text;
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            RectangleF r = new RectangleF(3, 3, Width - 7, Height - 7);
            using (GraphicsPath path = Theme.Chamfer(r.X, r.Y, r.Width, r.Height, 9))
            {
                if (primary)
                {
                    Color top, bot, edge;
                    if (!Enabled) { top = Theme.Hex("#44484E"); bot = Theme.Hex("#2C2F33"); edge = Theme.Hex("#5A5F66"); }
                    else if (Pressed) { top = Theme.Hex("#7E0D06"); bot = Theme.Hex("#5A0904"); edge = Theme.Hex("#B3150B"); }
                    else if (Hover) { top = Theme.Hex("#EA2B12"); bot = Theme.Hex("#9A1208"); edge = Theme.Hex("#FF8A5C"); }
                    else { top = Theme.Hex("#C51A0D"); bot = Theme.Hex("#7E0D06"); edge = Theme.RedEdge; }
                    using (LinearGradientBrush lg = new LinearGradientBrush(r, top, bot, 90f)) g.FillPath(lg, path);
                    using (Pen p = new Pen(edge, 1)) g.DrawPath(p, path);
                }
                else
                {
                    Color fill = !Enabled ? Theme.PanelSolid : Pressed ? Theme.Hex("#0B0908") : Hover ? Theme.Hex("#2A211A") : Theme.Hex("#1E1813");
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (Pen p = new Pen(Enabled ? Theme.FieldLine : Theme.PanelLine, 1)) g.DrawPath(p, path);
                }
            }
            Color text = !Enabled ? Theme.Faint : primary ? Theme.Text : Theme.Text;
            RectangleF tr = r;
            if (Pressed) tr.Offset(0, 1);
            Theme.DrawText(g, Text, primary ? Theme.Heading : Theme.Label, text, tr, StringAlignment.Center, StringAlignment.Center);
            if (KeyboardFocus)
            {
                using (GraphicsPath ring = Theme.Chamfer(1, 1, Width - 3, Height - 3, 10))
                using (Pen p = new Pen(Theme.Amber, 2)) g.DrawPath(p, ring);
            }
        }
    }

    internal sealed class Tick : SetupControl
    {
        private bool on;
        public event EventHandler CheckedChanged;

        public Tick(string text, bool isChecked)
        {
            Text = text;
            on = isChecked;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.CheckButton;
            Activated += delegate { Checked = !Checked; };
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                AccessibleDescription = on ? "işaretli" : "işaretsiz";
                Invalidate();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            int box = 20, y = (Height - box) / 2;
            Rectangle br = new Rectangle(2, y, box, box);
            using (SolidBrush b = new SolidBrush(on ? Theme.Amber : Theme.Field)) g.FillRectangle(b, br);
            using (Pen p = new Pen(on ? Theme.Amber : Hover ? Theme.Muted : Theme.FieldLine, 1)) g.DrawRectangle(p, br);
            if (on)
            {
                using (Pen p = new Pen(Theme.CheckMark, 2.4f))
                    g.DrawLines(p, new PointF[] { new PointF(br.X + 5, br.Y + 10), new PointF(br.X + 9, br.Y + 14), new PointF(br.X + 15, br.Y + 6) });
            }
            Theme.DrawText(g, Text, Theme.Body, Enabled ? Theme.Text : Theme.Faint, new RectangleF(box + 12, 0, Width - box - 14, Height),
                StringAlignment.Near, StringAlignment.Center);
            if (KeyboardFocus)
            {
                using (Pen p = new Pen(Theme.Amber, 2)) g.DrawRectangle(p, br.X - 2, br.Y - 2, br.Width + 4, br.Height + 4);
            }
        }
    }

    internal sealed class Bar : Control
    {
        private int percent;

        public Bar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.PanelSolid;
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        public int Percent
        {
            get { return percent; }
            set
            {
                percent = Math.Max(0, Math.Min(100, value));
                AccessibleName = "Kurulum %" + percent;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Track)) e.Graphics.FillRectangle(b, 0, 0, Width, Height);
            using (SolidBrush b = new SolidBrush(Theme.Amber)) e.Graphics.FillRectangle(b, 0, 0, Width * percent / 100, Height);
        }
    }
}
