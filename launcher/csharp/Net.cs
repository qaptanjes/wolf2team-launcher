// Login request, small bounded downloads and TCP port probes. All of these block; callers run them off the UI thread.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace WolfTeamDirilis
{
    internal sealed class LoginResult
    {
        public bool Ok;
        public string Token;
        /// <summary>Turkish, one line, safe to show (no URLs).</summary>
        public string Error;
    }

    /// <summary>TLS 1.2 for the HTTPS addresses (security audit F2/F3). .NET 4.0 knows only SSL3/TLS 1.0 by name; on a PC with
    /// .NET 4.5 or later the value 3072 (Tls12) is accepted, on a plain 4.0 it throws.</summary>
    internal static class Tls
    {
        private static bool available;

        public const string MissingText = "Windows bu bilgisayarda güvenli bağlantıyı (TLS 1.2) desteklemiyor. Güncelleme "
            + "güvenli bağlantı olmadan yapılmaz; Windows Update'i çalıştırıp başlatıcıyı yeniden aç.";

        public static bool Available
        {
            get { return available; }
        }

        public static void Enable()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                available = true;
            }
            catch (NotSupportedException) { available = false; }
            catch (ArgumentException) { available = false; }
        }

        internal static void SetForTests(bool value)
        {
            available = value;
        }

        /// <summary>A failure of the TLS handshake itself (no common protocol or cipher, an untrusted certificate).</summary>
        public static bool IsTlsFailure(Exception e)
        {
            WebException we = e as WebException;
            return we != null && (we.Status == WebExceptionStatus.SecureChannelFailure || we.Status == WebExceptionStatus.TrustFailure);
        }
    }

    internal static class Net
    {
        /// <summary>A URL with "?t=&lt;now&gt;" so no cache between the server and the player answers with an old copy
        /// (the manifest and the news list; the server ignores the query).</summary>
        public static string Fresh(string url)
        {
            return url + (url.IndexOf('?') >= 0 ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public const string StaticUserAgent = "WolfTeamDirilis/1.0";
        private static readonly Regex TokenPattern = new Regex(@"\A[0-9a-fA-F]{32}\z"); // \z: "$" would also accept a trailing newline

        static Net()
        {
            // The login server does not need "Expect: 100-continue"; skipping it saves a round trip.
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 8;
        }

        /// <summary>POST user/pass to login.php exactly like the original loader (form fields, WolfLoader/1.0).</summary>
        public static LoginResult Login(string loginUrl, string user, string pass)
        {
            LoginResult r = new LoginResult();
            byte[] body = Encoding.UTF8.GetBytes("user=" + Uri.EscapeDataString(user) + "&pass=" + Uri.EscapeDataString(pass));
            string text;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(loginUrl);
                req.Method = "POST";
                req.UserAgent = "WolfLoader/1.0";
                req.ContentType = "application/x-www-form-urlencoded";
                req.ContentLength = body.Length;
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.AllowAutoRedirect = false;
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK)
                    {
                        r.Error = "Giriş sunucusu hata verdi (HTTP " + (int)resp.StatusCode + "). Yöneticiye haber ver.";
                        return r;
                    }
                    text = Encoding.UTF8.GetString(ReadLimited(resp.GetResponseStream(), 64 * 1024));
                }
            }
            catch (WebException e)
            {
                r.Error = DescribeLoginError(e);
                return r;
            }
            catch (InvalidDataException)
            {
                // ReadLimited: the answer is larger than any real login answer.
                r.Error = "Sunucudan beklenmeyen yanıt geldi. Yöneticiye haber ver.";
                return r;
            }
            catch (IOException)
            {
                r.Error = "Giriş sunucusuna ulaşılamadı. Bağlantını kontrol edip tekrar dene.";
                return r;
            }
            finally
            {
                Array.Clear(body, 0, body.Length);
            }

            Dictionary<string, object> obj;
            try
            {
                obj = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (ArgumentException) { obj = null; }
            catch (InvalidOperationException) { obj = null; }
            if (obj == null)
            {
                r.Error = "Sunucudan beklenmeyen yanıt geldi. Yöneticiye haber ver.";
                return r;
            }
            object ok;
            if (obj.TryGetValue("ok", out ok) && ok is bool && (bool)ok)
            {
                object token;
                string t = obj.TryGetValue("token", out token) ? token as string : null;
                if (t == null || !TokenPattern.IsMatch(t))
                {
                    r.Error = "Sunucudan beklenmeyen yanıt geldi. Yöneticiye haber ver.";
                    return r;
                }
                r.Ok = true;
                r.Token = t;
                return r;
            }
            object err;
            string msg = obj.TryGetValue("error", out err) ? err as string : null;
            r.Error = TranslateServerError(msg);
            return r;
        }

        /// <summary>The server answers in English; known texts get a Turkish wording, others are shown as-is.</summary>
        public static string TranslateServerError(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return "Kullanıcı adı veya şifre yanlış.";
            msg = msg.Trim().Replace('\r', ' ').Replace('\n', ' ');
            string lower = msg.ToLowerInvariant();
            if (lower.StartsWith("invalid login") || lower.Contains("invalid user") || lower.Contains("wrong password"))
                return "Kullanıcı adı veya şifre yanlış.";
            if (lower.StartsWith("database error")) return "Sunucuda veritabanı hatası var. Yöneticiye haber ver.";
            // LauncherWebServer THROTTLED / BUSY
            if (lower.StartsWith("too many login attempts")) return "Çok fazla giriş denemesi. Birkaç dakika bekleyip tekrar dene.";
            if (lower.StartsWith("server busy")) return "Sunucu şu an yoğun. Biraz sonra tekrar dene.";
            if (lower.StartsWith("account banned until "))
                return "Hesabın yasaklı (bitiş: " + msg.Substring("account banned until ".Length) + ").";
            return msg.Length > 120 ? msg.Substring(0, 120) : msg;
        }

        private static string DescribeLoginError(WebException e)
        {
            if (e.Status == WebExceptionStatus.Timeout)
                return "Giriş sunucusu yanıt vermedi. Biraz sonra tekrar dene.";
            HttpWebResponse resp = e.Response as HttpWebResponse;
            if (e.Status == WebExceptionStatus.ProtocolError && resp != null)
            {
                int code = (int)resp.StatusCode;
                resp.Close();
                return "Giriş sunucusu hata verdi (HTTP " + code + "). Yöneticiye haber ver.";
            }
            return "Giriş sunucusuna ulaşılamadı. Bağlantını kontrol edip tekrar dene.";
        }

        public static HttpWebRequest StaticRequest(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = StaticUserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = false;
            req.AutomaticDecompression = DecompressionMethods.None;
            // Never serve a stale manifest or news list from the WinINet cache.
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            return req;
        }

        /// <summary>GET with a size cap. Throws WebException / IOException / InvalidDataException.</summary>
        public static byte[] DownloadSmall(string url, int maxBytes, int timeoutMs)
        {
            HttpWebRequest req = StaticRequest(url, timeoutMs);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException("HTTP " + (int)resp.StatusCode);
                if (resp.ContentLength > maxBytes) throw new InvalidDataException("too large");
                return ReadLimited(resp.GetResponseStream(), maxBytes);
            }
        }

        public static byte[] ReadLimited(Stream s, int maxBytes)
        {
            MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[16384];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + n > maxBytes) throw new InvalidDataException("too large");
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }

        /// <summary>Short description of a download failure without the URL (the URL may contain the secret).</summary>
        public static string DescribeFailure(Exception e)
        {
            WebException we = e as WebException;
            if (we != null)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (we.Status == WebExceptionStatus.ProtocolError && resp != null)
                {
                    int code = (int)resp.StatusCode;
                    resp.Close();
                    return "HTTP " + code;
                }
                return we.Status.ToString();
            }
            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>TCP connect with a timeout; true when the port accepted the connection.</summary>
        public static bool Probe(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return c.Connected;
                }
            }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>Both probes in parallel. Returns [gameUp, loginUp].</summary>
        public static bool[] ProbeServer(LauncherConfig cfg, int timeoutMs)
        {
            bool[] r = new bool[2];
            Thread t = new Thread(delegate() { r[1] = Probe(cfg.LoginHost, cfg.LoginPort, timeoutMs); });
            t.IsBackground = true;
            t.Start();
            r[0] = Probe(cfg.ServerHost, cfg.GamePort, timeoutMs);
            t.Join(timeoutMs + 2000);
            return r;
        }
    }
}
