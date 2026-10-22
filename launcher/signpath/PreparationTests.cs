using System;
using System.IO;

namespace WolfTeamDirilis
{
    internal static class PreparationTests
    {
        private static void Require(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }

        public static int Main(string[] args)
        {
            string root = Path.Combine(Path.GetTempPath(), "wtd-preparation-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                LauncherConfig missing = LauncherConfig.Load(root, null);
                Require(!missing.FileFound && missing.ServerHost == "build-only.invalid",
                    "Missing INI uses reserved host");
                Require(new Uri(missing.LoginUrl).Host == "build-only.invalid",
                    "Missing INI login cannot default to production");
                Require(missing.UpdateBaseUrl == null && missing.NewsUrl == null &&
                    missing.HomeUrl == null && missing.SupportUrl == null, "Missing INI has no update or external links");
                Directory.CreateDirectory(Path.GetDirectoryName(BootLayout.EndpointsPath(root)));
                File.Copy(Path.Combine(args[0], "endpoints.ini"), BootLayout.EndpointsPath(root));
                File.Copy(Path.Combine(args[0], LauncherConfig.FileName), Path.Combine(root, LauncherConfig.FileName));
                LauncherConfig configured = LauncherConfig.Load(root, null);
                Require(configured.FileFound && configured.ServerHost == "build-only.invalid",
                    "Bundled INI uses reserved host");
                Require(new Uri(configured.LoginUrl).Host == "build-only.invalid" &&
                    new Uri(configured.UpdateBaseUrl).Host == "build-only.invalid" &&
                    new Uri(configured.NewsUrl).Host == "build-only.invalid", "Bundled service URLs use reserved host");
                Require(configured.HomeUrl == null && configured.SupportUrl == null, "Bundled INI has no external links");
                Console.WriteLine("PreparationTests: 6 passed, 0 failed");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
