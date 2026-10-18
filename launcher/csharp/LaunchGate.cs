// When the play button may be used, and the Turkish progress texts of the update footer. Pure functions (tested in
// launcher\tests\LauncherTests.cs).
using System;
using System.Globalization;

namespace WolfTeamDirilis
{
    /// <summary>Offline: the update server could not be reached, but the files still match the last list that was
    /// fully verified on this PC (owner decision 2026-09-30).</summary>
    internal enum UpdateState { NotConfigured, Running, Done, ManifestFailed, Failed, Offline }

    internal static class LaunchGate
    {
        /// <summary>The game may start only after the files were checked against the release list (Done). While
        /// checking/downloading and after any failure it stays off: a half-downloaded or unchecked folder must not
        /// start. NotConfigured (no UpdateBaseUrl in the ini) is a developer setup without an update server.</summary>
        public static bool CanPlay(UpdateState s, bool gameExePresent)
        {
            if (!gameExePresent) return false;
            return s == UpdateState.Done || s == UpdateState.NotConfigured || s == UpdateState.Offline;
        }

        public static bool CanRetry(UpdateState s)
        {
            return s == UpdateState.Failed || s == UpdateState.ManifestFailed || s == UpdateState.Offline;
        }
    }

    internal static class ProgressText
    {
        private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", Tr) + " GB";
            // Block updates are often a few MB: "1,8 MB / 3,2 MB" instead of "1 MB / 3 MB".
            if (bytes > 0 && bytes < 10L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", Tr) + " MB";
            return (bytes >> 20).ToString(Tr) + " MB";
        }

        /// <summary>Remaining time for <paramref name="remaining"/> bytes at the current speed; "" while unknown.</summary>
        public static string Eta(long remaining, double bytesPerSecond)
        {
            if (bytesPerSecond < 1 || remaining <= 0) return "";
            double secs = remaining / bytesPerSecond;
            if (secs < 60) return "1 dk'dan az kaldı";
            long min = (long)Math.Round(secs / 60);
            if (min < 60) return "yaklaşık " + min.ToString(Tr) + " dk kaldı";
            return "yaklaşık " + (min / 60).ToString(Tr) + " sa " + (min % 60).ToString(Tr) + " dk kaldı";
        }

        /// <summary>"İndiriliyor 3/120: wolf.xfs %35, 540 MB / 1,9 GB"</summary>
        public static string Line(UpdateProgress p)
        {
            int filePct = p.FileSize <= 0 ? 0 : (int)Math.Min(100, p.FileDone * 100 / p.FileSize);
            return "İndiriliyor " + p.Index + "/" + p.Count + ": " + p.File + " %" + filePct + ", " + Size(p.Done) + " / " + Size(p.Total);
        }
    }
}
