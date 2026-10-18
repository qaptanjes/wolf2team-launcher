// A tiny HTTP/1.1 file server on 127.0.0.1 for the updater tests (GET, "Range: bytes=N-" and "bytes=A-B", keep-alive).
// HttpListener would need a URL reservation (admin) on Windows; a TcpListener does not.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WolfTeamDirilis.Tests
{
    internal sealed class TestHttpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private volatile bool stopping;
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        /// <summary>Paths served with these bytes instead (a corrupt mirror), keyed like Files.</summary>
        public readonly Dictionary<string, byte[]> Corrupt = new Dictionary<string, byte[]>();
        public readonly List<string> Log = new List<string>();
        /// <summary>Every request target with its query string.</summary>
        public readonly List<string> Queries = new List<string>();
        /// <summary>Paths answered with this HTTP status and no body (e.g. 500 for a broken server).</summary>
        public readonly Dictionary<string, int> Status = new Dictionary<string, int>();
        /// <summary>When > 0, a response body is cut after this many bytes (connection dropped) once per path.</summary>
        public int DropAfter;
        /// <summary>Milliseconds to wait before each answer (a slow server).</summary>
        public int DelayMs;
        private readonly HashSet<string> dropped = new HashSet<string>();
        /// <summary>Ignore Range headers and always send the whole file with 200 (a server without range support).</summary>
        public bool IgnoreRange;
        /// <summary>Accepted TCP connections (keep-alive: one connection serves many requests).</summary>
        public int Connections;
        /// <summary>Body bytes sent in total.</summary>
        public long BytesSent;
        private readonly List<TcpClient> open = new List<TcpClient>();

        public TestHttpServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public string BaseUrl
        {
            get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/pub/"; }
        }

        private void Loop()
        {
            while (!stopping)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                Interlocked.Increment(ref Connections);
                lock (open) open.Add(c);
                // A thread per connection: an idle keep-alive connection must not block a new one.
                Thread t = new Thread(delegate() { Serve(c); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void Serve(TcpClient c)
        {
            try
            {
                NetworkStream ns = c.GetStream();
                StreamReader r = new StreamReader(ns, Encoding.ASCII);
                while (!stopping && Handle(ns, r)) { }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally { c.Close(); }
        }

        /// <summary>Answers one request; true when the connection stays open for the next one.</summary>
        private bool Handle(NetworkStream ns, StreamReader r)
        {
            string first = r.ReadLine();
            if (first == null) return false;
            long from = -1, to = -1;
            string line;
            while (!string.IsNullOrEmpty(line = r.ReadLine()))
            {
                if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] ft = line.Substring(13).Split('-');
                    from = long.Parse(ft[0]);
                    if (ft.Length > 1 && ft[1].Length > 0) to = long.Parse(ft[1]);
                }
            }
            if (IgnoreRange) from = to = -1;
            string target = first.Split(' ')[1];
            int q = target.IndexOf('?');
            // Like nginx serving static files: the query is not part of the file name (the launcher adds ?h= / ?t=).
            lock (Queries) Queries.Add(target);
            string path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            lock (Log) Log.Add(path + (from >= 0 ? " range=" + from + (to >= 0 ? "-" + to : "") : ""));
            byte[] body;
            bool corrupt;
            int status;
            lock (Files)
            {
                if (Status.TryGetValue(path, out status))
                {
                    Write(ns, "HTTP/1.1 " + status + " Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", null, 0, 0);
                    return false;
                }
                corrupt = Corrupt.TryGetValue(path, out body);
                if (!corrupt && !Files.TryGetValue(path, out body)) body = null;
            }
            if (body == null)
            {
                Write(ns, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", null, 0, 0);
                return false;
            }
            if (from > body.Length || (from >= 0 && from == body.Length && to >= 0))
            {
                Write(ns, "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + body.Length
                          + "\r\nContent-Length: 0\r\n\r\n", null, 0, 0);
                return true;
            }
            // Like nginx, "bytes=A-B" is always answered with 206; the old tests only send "bytes=N-" with N > 0.
            bool partial = from > 0 || to >= 0;
            int start = from > 0 ? (int)from : 0;
            int end = to >= 0 ? (int)Math.Min(to, body.Length - 1) : body.Length - 1;
            int len = end - start + 1;
            string head = (partial ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + start + "-" + end + "/" + body.Length + "\r\n"
                                   : "HTTP/1.1 200 OK\r\n")
                          + "Content-Length: " + len + "\r\n\r\n";
            int send = len;
            lock (dropped)
            {
                if (DropAfter > 0 && len > DropAfter && dropped.Add(path)) send = DropAfter;
            }
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            Write(ns, head, body, start, send);
            Interlocked.Add(ref BytesSent, send);
            return send == len; // a dropped response closes the connection
        }

        private static void Write(NetworkStream ns, string head, byte[] body, int off, int len)
        {
            byte[] h = Encoding.ASCII.GetBytes(head);
            ns.Write(h, 0, h.Length);
            if (body != null && len > 0) ns.Write(body, off, len);
            ns.Flush();
        }

        public void Dispose()
        {
            stopping = true;
            listener.Stop();
            // Like a server that went away: keep-alive connections are closed as well.
            lock (open) foreach (TcpClient c in open) c.Close();
        }
    }
}
