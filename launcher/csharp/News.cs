// news.json: [ { "title": "...", "date": "2026-09-29", "text": "...", "image": "news/x.jpg" } ]
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace WolfTeamDirilis
{
    internal sealed class NewsItem
    {
        public string Title;
        public string Date;
        public string Text;
        /// <summary>Absolute URL, already checked to be on the news host; null when none.</summary>
        public string ImageUrl;
        public Image Image;
    }

    internal static class News
    {
        public const int MaxItems = 20;
        public const int MaxJsonBytes = 256 * 1024;
        public const int MaxImageBytes = 2 * 1024 * 1024;

        public static List<NewsItem> Fetch(string newsUrl)
        {
            byte[] data = Net.DownloadSmall(Net.Fresh(newsUrl), MaxJsonBytes, 8000);
            return Parse(Encoding.UTF8.GetString(data), newsUrl);
        }

        public static List<NewsItem> Parse(string json, string newsUrl)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = MaxJsonBytes * 2;
            object root = js.DeserializeObject(json.TrimStart('﻿'));
            IEnumerable list = root as object[];
            if (list == null)
            {
                Dictionary<string, object> d = root as Dictionary<string, object>;
                object items;
                if (d != null && d.TryGetValue("items", out items)) list = items as object[];
            }
            if (list == null) throw new InvalidDataException("news.json: expected a list");
            List<NewsItem> result = new List<NewsItem>();
            foreach (object o in list)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                NewsItem it = new NewsItem();
                it.Title = Clean(Str(d, "title"), 120);
                it.Text = Clean(Str(d, "text"), 2000);
                it.Date = FormatDate(Str(d, "date"));
                it.ImageUrl = ResolveImage(newsUrl, Str(d, "image"));
                if (it.Title.Length == 0 && it.Text.Length == 0) continue;
                result.Add(it);
                if (result.Count >= MaxItems) break;
            }
            return result;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        /// <summary>Plain text only: control characters removed, length capped.</summary>
        private static string Clean(string s, int max)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n') sb.Append('\n');
                else if (!char.IsControl(c)) sb.Append(c);
            }
            string r = sb.ToString().Trim();
            return r.Length > max ? r.Substring(0, max) : r;
        }

        private static string FormatDate(string s)
        {
            DateTime d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            return Clean(s, 12);
        }

        /// <summary>Relative to news.json, or absolute http(s) on the same host. Anything else is dropped.</summary>
        public static string ResolveImage(string newsUrl, string image)
        {
            if (string.IsNullOrWhiteSpace(image)) return null;
            Uri baseUri, u;
            if (!Uri.TryCreate(newsUrl, UriKind.Absolute, out baseUri)) return null;
            if (!Uri.TryCreate(baseUri, image.Trim(), out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            if (!string.Equals(u.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) return null;
            return u.AbsoluteUri;
        }

        /// <summary>Downloads and centre-crops the image to 128 x 72. Returns null on any failure.</summary>
        public static Image FetchThumb(string url)
        {
            try
            {
                byte[] data = Net.DownloadSmall(url, MaxImageBytes, 8000);
                using (MemoryStream ms = new MemoryStream(data))
                using (Image src = Image.FromStream(ms))
                {
                    const int W = 128, H = 72;
                    float scale = Math.Max((float)W / src.Width, (float)H / src.Height);
                    float sw = W / scale, sh = H / scale;
                    RectangleF from = new RectangleF((src.Width - sw) / 2f, (src.Height - sh) / 2f, sw, sh);
                    Bitmap dst = new Bitmap(W, H);
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new RectangleF(0, 0, W, H), from, GraphicsUnit.Pixel);
                    }
                    return dst;
                }
            }
            catch (Exception)
            {
                // A broken image must never take the news list down; the item renders without it.
                return null;
            }
        }
    }
}
