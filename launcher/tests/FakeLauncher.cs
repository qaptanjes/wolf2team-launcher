// A stand-in launcher for the real-process bootstrap tests (LauncherTests: Boot*). Its behaviour comes from a marker the
// test appends to a copy of this exe ("WTFAKE:<mode>;" in the last 64 bytes; a PE file runs with appended bytes), so
// every mode has its own SHA-256 like a real launcher version:
//   ok          byte 1, byte 2, keeps running (WT_FAKE_STAY_MS, default 20000), exit 0
//   hang        never signals, keeps running
//   half        byte 1, then its "UI" hangs (no byte 2), keeps running
//   close       byte 1, then exits 0 (the player closed the window at once)
//   signalcrash byte 1, byte 2, then exits 5
//   crash       exits 3 at once
//   update      plays a launcher that downloaded a new version: copies <gamedir>\next.bin over launcher\WolfTeamLauncher.exe,
//               confirms, starts the bootstrap with --after-update and exits 0 at once
// Each start appends "<mode> <arguments>" to <gamedir>\fake-log.txt.
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace WolfTeamDirilis.Tests
{
    internal static class FakeLauncher
    {
        private static int Main(string[] args)
        {
            string mode = Mode();
            string gameDir = null, pipe = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == BootLayout.GameDirFlag) gameDir = args[i + 1];
                if (args[i] == BootLayout.ConfirmPipeFlag) pipe = args[i + 1];
            }
            // Started without --gamedir (as a 1.2 launcher would be): logs next to itself, so a test sees that start too.
            if (gameDir == null) gameDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            if (gameDir != null)
            {
                try { File.AppendAllText(Path.Combine(gameDir, "fake-log.txt"), mode + " " + string.Join(" ", args) + "\n"); }
                catch (IOException) { }
            }
            if (mode == "crash") return 3;
            int stay = 20000;
            int.TryParse(Environment.GetEnvironmentVariable("WT_FAKE_STAY_MS") ?? "", out stay);
            if (stay <= 0) stay = 20000;
            AnonymousPipeClientStream p = null;
            try { if (pipe != null) p = new AnonymousPipeClientStream(PipeDirection.Out, pipe); }
            catch (IOException) { }
            catch (ArgumentException) { }
            if (mode == "hang") { Thread.Sleep(stay); return 0; }
            if (mode == "update")
            {
                File.Copy(Path.Combine(gameDir, "next.bin"), BootLayout.StagedLauncher(gameDir), true);
                Write(p, BootLayout.SignalStarted);
                Write(p, BootLayout.SignalStable);
                if (p != null) p.Dispose();
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(BootLayout.BootstrapPath(gameDir), BootLayout.AfterUpdateFlag);
                psi.UseShellExecute = false;
                using (System.Diagnostics.Process.Start(psi)) { }
                return 0;
            }
            Thread.Sleep(200);
            Write(p, BootLayout.SignalStarted);
            if (mode == "close") return 0;
            if (mode == "half") { Thread.Sleep(stay); return 0; }
            Thread.Sleep(300);
            Write(p, BootLayout.SignalStable);
            if (p != null) p.Dispose();
            if (mode == "signalcrash") { Thread.Sleep(100); return 5; }
            Thread.Sleep(stay);
            return 0;
        }

        private static void Write(Stream p, byte b)
        {
            if (p == null) return;
            try { p.WriteByte(b); p.Flush(); }
            catch (IOException) { }
        }

        private static string Mode()
        {
            byte[] all = File.ReadAllBytes(System.Reflection.Assembly.GetEntryAssembly().Location);
            string tail = Encoding.ASCII.GetString(all, Math.Max(0, all.Length - 64), Math.Min(64, all.Length));
            int i = tail.LastIndexOf("WTFAKE:", StringComparison.Ordinal);
            if (i < 0) return "ok";
            int end = tail.IndexOf(';', i);
            return end < 0 ? "ok" : tail.Substring(i + 7, end - i - 7);
        }
    }
}
