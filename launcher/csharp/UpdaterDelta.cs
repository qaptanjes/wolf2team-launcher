// Block updates (launcher 1.2): a changed file whose manifest entry has "blocks" ({size, sha256[]}, written by
// tools/launcher_manifest.py for files of 64 MB and more, i.e. wolf.xfs and sound.xfs) is patched in place.
//   1. The local copy is hashed block by block (Plan already did it when it had to hash the file anyway).
//   2. The file is grown or cut to the new size; runs of differing blocks are fetched with HTTP Range requests on the
//      same keep-alive connection. Every block is checked against its SHA-256 before it is written, so a wrong or
//      changed server file never reaches the disk.
//   3. The whole file is hashed and compared with the manifest SHA-256. A mismatch, or a server that answers a Range
//      request with the whole file (200), falls back to the normal whole-file download (.part + replace).
// An interruption leaves a partly patched file behind: the "update incomplete" marker keeps the game closed, and the
// next start hashes the blocks again, so only the blocks that are still missing are downloaded (resume).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace WolfTeamDirilis
{
    internal sealed partial class Updater
    {
        /// <summary>At most this many bytes per Range request (a run of consecutive differing blocks is split).</summary>
        internal long MaxRangeBytes = 32L << 20;

        /// <summary>The blocks of one file that still have to be fetched.</summary>
        internal sealed class DeltaPlan
        {
            /// <summary>Indexes of the blocks that differ, ascending; a block leaves the list once written.</summary>
            public readonly List<int> Pending = new List<int>();
            /// <summary>Bytes of all differing blocks (what the player downloads for this file).</summary>
            public long Bytes;
            /// <summary>Bytes of the blocks already verified and written.</summary>
            public long Fetched;
            public long LocalLength;
        }

        /// <summary>Thrown by ApplyDelta when the whole file has to be downloaded instead.</summary>
        private sealed class DeltaFallback : Exception { }

        private static void RememberLocalBlocks(ManifestEntry e, FileInfo stamp, string[] blocks)
        {
            e.LocalBlocks = blocks;
            e.LocalStampLength = stamp.Length;
            e.LocalStampTicks = stamp.LastWriteTimeUtc.Ticks;
        }

        /// <summary>Delta plans for every entry that can be patched in place: it has a block list and a local copy
        /// (a plain file inside the game folder, not empty). Hashes the local copies that Plan did not hash.</summary>
        internal Dictionary<ManifestEntry, DeltaPlan> PlanDeltas(List<ManifestEntry> todo)
        {
            Dictionary<ManifestEntry, DeltaPlan> plans = new Dictionary<ManifestEntry, DeltaPlan>();
            List<ManifestEntry> candidates = new List<ManifestEntry>();
            long hashTotal = 0;
            foreach (ManifestEntry e in todo)
            {
                if (e.BlockHashes == null || e.IsBootstrap || e.IsSeed) continue;
                if (CrossesReparsePoint(e.FullPath)) continue;
                FileInfo fi = new FileInfo(e.FullPath);
                if (!fi.Exists || fi.Length == 0) continue;
                candidates.Add(e);
                if (!HasFreshLocalBlocks(e, fi)) hashTotal += fi.Length;
            }
            UpdateProgress pr = new UpdateProgress();
            pr.Checking = true;
            pr.Count = candidates.Count;
            pr.Total = hashTotal;
            for (int i = 0; i < candidates.Count; i++)
            {
                ManifestEntry e = candidates[i];
                FileInfo fi = new FileInfo(e.FullPath);
                if (!HasFreshLocalBlocks(e, fi))
                {
                    pr.Index = i + 1;
                    pr.File = e.Path;
                    string[] blocks;
                    try
                    {
                        HashFileAndBlocks(e.FullPath, e.BlockSize, pr, Progress, out blocks);
                    }
                    catch (IOException) { continue; }            // unreadable: the whole-file download handles it
                    catch (UnauthorizedAccessException) { continue; }
                    RememberLocalBlocks(e, fi, blocks);
                }
                DeltaPlan dp = new DeltaPlan();
                dp.LocalLength = fi.Length;
                for (int b = 0; b < e.BlockHashes.Length; b++)
                {
                    if (b < e.LocalBlocks.Length && e.LocalBlocks[b] == e.BlockHashes[b]) continue;
                    dp.Pending.Add(b);
                    dp.Bytes += BlockLength(e, b);
                }
                // A .part left by an earlier whole-file download is kept until the block update succeeded: if it
                // falls back to the whole download again, that download resumes instead of starting from zero.
                plans[e] = dp;
            }
            return plans;
        }

        private static bool HasFreshLocalBlocks(ManifestEntry e, FileInfo fi)
        {
            fi.Refresh();
            return e.LocalBlocks != null && fi.Exists && fi.Length == e.LocalStampLength
                   && fi.LastWriteTimeUtc.Ticks == e.LocalStampTicks;
        }

        private static long BlockLength(ManifestEntry e, int index)
        {
            long start = (long)index * e.BlockSize;
            return Math.Min(e.BlockSize, e.Size - start);
        }

        private void ApplyDelta(ManifestEntry e, DeltaPlan dp, UpdateProgress pr, long doneBefore, SpeedMeter speed)
        {
            string target = e.FullPath;
            if (CrossesReparsePoint(target))
                throw new UpdateException(Short(e.Path) + " yazılamadı: klasörde bağlantı (junction) var.");
            FileStream fs;
            try
            {
                FileAttributes a = File.GetAttributes(target);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, a & ~FileAttributes.ReadOnly);
                // FileShare.None: a running game (or a second launcher) keeps the file open, and then nothing is written.
                fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            }
            catch (IOException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
            catch (UnauthorizedAccessException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
            using (fs)
            {
                try
                {
                    if (fs.Length != e.Size) fs.SetLength(e.Size);
                }
                catch (IOException io) { throw WriteFailure(e, io); }
                byte[] buf = new byte[e.BlockSize];
                using (SHA256 sha = SHA256.Create())
                {
                    while (dp.Pending.Count > 0)
                    {
                        int first = dp.Pending[0], n = 1;
                        long maxBlocks = Math.Max(1, MaxRangeBytes / e.BlockSize);
                        while (n < dp.Pending.Count && dp.Pending[n] == first + n && n < maxBlocks) n++;
                        FetchRun(e, dp, fs, sha, buf, first, n, pr, doneBefore, speed);
                    }
                }
                try { fs.Flush(true); }
                catch (IOException io) { throw WriteFailure(e, io); }
            }

            UpdateProgress check = new UpdateProgress();
            check.Checking = true;
            check.Index = 1;
            check.Count = 1;
            check.File = e.Path;
            check.Total = e.Size;
            string got;
            string[] blocks;
            try { got = HashFileAndBlocks(target, e.BlockSize, check, Progress, out blocks); }
            catch (IOException x) { throw new UpdateException(WriteProblem(Short(e.Path), x)); }
            if (got != e.Sha256)
            {
                RememberLocalBlocks(e, new FileInfo(target), blocks);
                throw new DeltaFallback();
            }
            cache.Put(target, new FileInfo(target), e.Sha256);
            SafeDelete(PartPath(e)); // a whole download of an earlier attempt is not needed any more
        }

        /// <summary>One Range request for blocks first..first+count-1; each block is checked and then written.</summary>
        private void FetchRun(ManifestEntry e, DeltaPlan dp, FileStream fs, SHA256 sha, byte[] buf, int first, int count,
            UpdateProgress pr, long doneBefore, SpeedMeter speed)
        {
            long from = (long)first * e.BlockSize;
            long to = Math.Min((long)(first + count) * e.BlockSize, e.Size) - 1;
            HttpWebRequest req = Net.StaticRequest(FileUrl(e), 20000);
            req.ReadWriteTimeout = 30000;
            req.AddRange(from, to);
            HttpWebResponse resp;
            try
            {
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException we)
            {
                HttpWebResponse r = we.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 416)
                {
                    r.Close();
                    throw new HashMismatch(); // the server copy is shorter than the manifest says: another version
                }
                throw;
            }
            using (resp)
            {
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    req.Abort(); // the whole file follows (Range not supported): do not read it here
                    throw new DeltaFallback();
                }
                long crFrom, crTo, crTotal;
                if (!ParseContentRange(resp.Headers[HttpResponseHeader.ContentRange], out crFrom, out crTo, out crTotal)
                    || crFrom != from || crTo != to || (crTotal >= 0 && crTotal != e.Size))
                {
                    req.Abort();
                    throw new HashMismatch(); // not the requested bytes, or a server file of another size
                }
                Stream src = resp.GetResponseStream();
                DateTime lastReport = DateTime.MinValue;
                for (int b = first; b < first + count; b++)
                {
                    int len = (int)BlockLength(e, b);
                    int got = 0;
                    while (got < len)
                    {
                        int n;
                        try { n = src.Read(buf, got, len - got); }
                        catch (IOException io) { throw new NetworkIOException(io); }
                        if (n <= 0) throw new NetworkIOException(new EndOfStreamException());
                        got += n;
                        speed.Add(n);
                        pr.Done = doneBefore + dp.Fetched + got;
                        pr.FileDone = dp.Fetched + got;
                        DateTime now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds >= 200)
                        {
                            lastReport = now;
                            pr.BytesPerSecond = speed.BytesPerSecond;
                            Report(pr);
                        }
                    }
                    if (ToHex(sha.ComputeHash(buf, 0, len)) != e.BlockHashes[b])
                    {
                        req.Abort(); // never written; the rest of this response is not read
                        throw new HashMismatch();
                    }
                    try
                    {
                        fs.Position = (long)b * e.BlockSize;
                        fs.Write(buf, 0, len);
                    }
                    catch (IOException io) { throw WriteFailure(e, io); }
                    dp.Pending.RemoveAt(0);
                    dp.Fetched += len;
                }
                // Read to the end so the keep-alive connection can serve the next request.
                if (src.Read(buf, 0, 1) > 0)
                {
                    req.Abort();
                    throw new HashMismatch();
                }
            }
        }

        /// <summary>"bytes a-b/total" (total may be "*": -1).</summary>
        internal static bool ParseContentRange(string v, out long from, out long to, out long total)
        {
            from = to = total = -1;
            if (v == null) return false;
            v = v.Trim();
            if (!v.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = v.Substring(6).Trim();
            int dash = rest.IndexOf('-'), slash = rest.IndexOf('/');
            if (dash <= 0 || slash < dash) return false;
            NumberStyles ns = NumberStyles.None;
            CultureInfo inv = CultureInfo.InvariantCulture;
            if (!long.TryParse(rest.Substring(0, dash), ns, inv, out from)) return false;
            if (!long.TryParse(rest.Substring(dash + 1, slash - dash - 1), ns, inv, out to)) return false;
            string t = rest.Substring(slash + 1);
            if (t == "*") return to >= from;
            return long.TryParse(t, ns, inv, out total) && to >= from;
        }
    }
}
