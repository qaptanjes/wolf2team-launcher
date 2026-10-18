// Colour and font tokens from the launcher design spec (C:\tools\re\out\launcher-spec.md, sec. 3).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WolfTeamDirilis
{
    internal static class Theme
    {
        public static readonly Color Ink = Hex("#000000");
        public static readonly Color Band = Hex("#7B0808");
        public static readonly Color Panel = Color.FromArgb(228, Hex("#15110E"));
        public static readonly Color PanelSolid = Hex("#15110E");
        public static readonly Color PanelLine = Hex("#3B2D22");
        public static readonly Color Rule = Hex("#2B221B");
        public static readonly Color Text = Hex("#F2ECE4");
        public static readonly Color Muted = Hex("#A99D90");
        public static readonly Color Faint = Hex("#7A6F64");
        public static readonly Color RedEdge = Hex("#F05A3C");
        public static readonly Color SlateText = Hex("#A7ACB3");
        public static readonly Color Amber = Hex("#F2A033");
        public static readonly Color Green = Hex("#47C46B");
        public static readonly Color Danger = Hex("#EF4B3C");
        public static readonly Color Field = Hex("#0B0908");
        public static readonly Color FieldLine = Hex("#7A6755");
        public static readonly Color Track = Hex("#2A231E");
        public static readonly Color CloseHover = Hex("#C42B1C");
        public static readonly Color CheckMark = Hex("#1A1208");

        public static readonly Font Button = MakeFont("Bahnschrift SemiBold Condensed", 34, FontStyle.Regular);
        public static readonly Font ButtonSmall = MakeFont("Bahnschrift SemiBold Condensed", 26, FontStyle.Regular);
        public static readonly Font ButtonSub = MakeFont("Bahnschrift SemiCondensed", 15, FontStyle.Regular);
        public static readonly Font Heading = MakeFont("Bahnschrift SemiBold", 19, FontStyle.Regular);
        public static readonly Font PageHeading = MakeFont("Bahnschrift SemiBold", 27, FontStyle.Regular);
        public static readonly Font NewsTitle = MakeFont("Bahnschrift SemiBold", 16, FontStyle.Regular);
        public static readonly Font Body = MakeFont("Segoe UI", 13, FontStyle.Regular);
        public static readonly Font Small = MakeFont("Segoe UI", 12, FontStyle.Regular);
        public static readonly Font Tiny = MakeFont("Segoe UI", 10, FontStyle.Regular);
        /// <summary>The owner's signature, small and quiet at the bottom of the launcher and the installer.</summary>
        public const string Signature = "Developed by Jesus";
        public static readonly Font SmallUnderline = MakeFont("Segoe UI", 12, FontStyle.Underline);
        public static readonly Font Label = MakeFont("Segoe UI Semibold", 12, FontStyle.Regular);
        public static readonly Font FieldText = MakeFont("Segoe UI", 14, FontStyle.Regular);
        public static readonly Font Badge = MakeFont("Segoe UI Semibold", 13, FontStyle.Regular);
        public static readonly Font HeaderLink = MakeFont("Segoe UI Semibold", 13, FontStyle.Regular);
        public static readonly Font HeaderLinkHover = MakeFont("Segoe UI Semibold", 13, FontStyle.Underline);

        public static Color Hex(string hex)
        {
            string h = hex.TrimStart('#');
            return Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16),
                Convert.ToInt32(h.Substring(4, 2), 16));
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(a, c);
        }

        private static HashSet<string> installed;

        /// <summary>Pixel-sized font; Bahnschrift falls back to Segoe UI Semibold on Windows before 1709.</summary>
        private static Font MakeFont(string family, float px, FontStyle style)
        {
            if (installed == null)
            {
                installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (InstalledFontCollection fonts = new InstalledFontCollection())
                {
                    foreach (FontFamily f in fonts.Families) installed.Add(f.Name);
                }
            }
            string name = installed.Contains(family) ? family : "Segoe UI Semibold";
            try
            {
                return new Font(name, px, style, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
                return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel);
            }
        }

        public static void DrawText(Graphics g, string s, Font font, Color color, RectangleF r,
            StringAlignment h, StringAlignment v)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(color))
            {
                sf.Alignment = h;
                sf.LineAlignment = v;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(s, font, b, r, sf);
            }
        }

        public static void DrawWrapped(Graphics g, string s, Font font, Color color, RectangleF r)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(color))
            {
                sf.Trimming = StringTrimming.Word;
                g.DrawString(s, font, b, r, sf);
            }
        }

        /// <summary>Word-wrapped, vertically centred, "…" when even that does not fit.</summary>
        public static void DrawWrappedCentered(Graphics g, string s, Font font, Color color, RectangleF r)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(color))
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisWord;
                g.DrawString(s, font, b, r, sf);
            }
        }

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
        }

        /// <summary>Plate with the top-left and bottom-right corners cut by c pixels.</summary>
        public static GraphicsPath Chamfer(float x, float y, float w, float h, float c)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddPolygon(new PointF[]
            {
                new PointF(x + c, y), new PointF(x + w, y), new PointF(x + w, y + h - c),
                new PointF(x + w - c, y + h), new PointF(x, y + h), new PointF(x, y + c)
            });
            return p;
        }
    }
}
