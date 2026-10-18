// Version resource of WolfTeamDirilis-Setup.exe (and of its copy WolfTeamDirilis-Kaldir.exe in the install folder). The
// bootstrap's repair offer runs that copy only when this title and a version of at least BootLayout.MinRepairSetupVersion
// identify it as a setup that installs the bootstrap (an older setup would put a 1.2-style launcher back; gate 1.4b).
[assembly: System.Reflection.AssemblyTitle(WolfTeamDirilis.BootLayout.SetupTitle)]
[assembly: System.Reflection.AssemblyProduct("WolfTeam Diriliş")]
[assembly: System.Reflection.AssemblyVersion(WolfTeamDirilis.Setup.SetupInfo.FileVersion)]
[assembly: System.Reflection.AssemblyFileVersion(WolfTeamDirilis.Setup.SetupInfo.FileVersion)]
// Full version-resource metadata: a blank CompanyName/Copyright on an unsigned installer is a classic AV/SmartScreen
// heuristic contributor. Values match SetupInfo.Publisher/Website.
[assembly: System.Reflection.AssemblyCompany("WolfTeam Diriliş")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 WolfTeam Diriliş — hayran yapımı, ticari olmayan özel sunucu — wolf2team.com")]
[assembly: System.Reflection.AssemblyDescription("WolfTeam Diriliş kurulumu")]
