using System.Reflection;

// 版本号的唯一事实来源。
//
// csproj 里设了 <GenerateAssemblyInfo>false</GenerateAssemblyInfo>，因此那边的
// <AssemblyVersion>/<FileVersion> 一律不生效——改那里打出来的包版本号纹丝不动，
// 而且没有任何报错。踩过一次：安装包文件名是 2.1.0、里面的 exe 却报 2.0.5，
// 更新检查会一直告诉已是最新版的用户"有新版本"。改版本请只改这里。
//
// 另外三处要同步改（Build-Installer.ps1 会校验第一处，不一致直接中止构建）：
//   Setup/UEModManager.iss           MyAppVersion    安装包显示与卸载项
//   cf-workers/.../wrangler.toml     LATEST_VERSION  服务端回答"最新是多少"
//   打包命令                          -Version        产物文件名

[assembly: AssemblyTitle("UEModManager")]
[assembly: AssemblyProduct("UEModManager")]
[assembly: AssemblyCompany("Ai-chan Studio")]

// 跟着构建配置走。此前手写死 "Debug"，Release 安装包里的 exe 也标着 Debug。
#if DEBUG
[assembly: AssemblyConfiguration("Debug")]
#else
[assembly: AssemblyConfiguration("Release")]
#endif

[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]
[assembly: AssemblyInformationalVersion("2.1.0")]
