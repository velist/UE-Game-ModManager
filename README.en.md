# Ai-chan Mod Manager

<p align="center">
  <a href="README.md">简体中文</a> &nbsp; · &nbsp; <strong>English</strong>
</p>

<p align="center">
  <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/docs/assets/readme/hero-en.png" width="100%" alt="Ai-chan Mod Manager: multiple engines, local storage, offline use">
</p>

<p align="center">
  <a href="https://www.modmanger.com/#download"><img src="https://img.shields.io/badge/Release-2.1.0-25cce1?style=flat-square&labelColor=15171b" alt="Version 2.1.0"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20%C2%B7%20x64-94b5c0?style=flat-square&labelColor=15171b" alt="Windows 10 / 11 x64">
  <img src="https://img.shields.io/badge/.NET-8%20Desktop-94b5c0?style=flat-square&labelColor=15171b" alt=".NET 8 Desktop Runtime">
  <a href="https://github.com/velist/UE-Game-ModManager/actions/workflows/ci.yml"><img src="https://github.com/velist/UE-Game-ModManager/actions/workflows/ci.yml/badge.svg?branch=main" alt="Build and tests"></a>
</p>

<p align="center">
  <a href="https://www.modmanger.com/#download"><strong>Download from the website</strong></a>
  &nbsp; · &nbsp; <a href="Setup/bundled/Quickstart.en.md">Quick start</a>
  &nbsp; · &nbsp; <a href="https://github.com/velist/UE-Game-ModManager/issues">Report an issue</a>
  &nbsp; · &nbsp; <a href="#community">Join the community</a>
  &nbsp; · &nbsp; <a href="#support-development">Support development</a>
</p>

A **mod manager for games across multiple engines**, bringing imports, categories, profiles, deployment and recovery into one place. Mod files and configuration are stored locally. Core mod management works offline, without an account.

## Download and get started

Get the latest version from **[modmanger.com](https://www.modmanger.com/#download)**.

Requires **Windows 10 / 11 (64-bit)** and the **[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)**. The installer checks for the runtime and provides a download link if it is missing.

1. **Choose mod storage.** On first launch, choose a repository for your mods. It can be separate from the application folder.
2. **Choose a game.** Select a built-in preset or add a custom game, then confirm the game and mod paths.
3. **Import and enable.** Import a ZIP archive, an extracted folder or supported mod files. Review the results, enable your mods and launch the game.

**English and Simplified Chinese are supported.** Choose a language in the installer, the first-run storage window, the sign-in window, or **Settings → General → Language**. Your choice is saved across restarts. A portable copy without a saved preference starts in Chinese on Chinese Windows systems and in English on other systems.

**Email sign-in is available:** enter your email address and sign in with the verification code you receive. You can also use core mod management without signing in.

## Games and engine support

Built-in presets cover **10 games across 11 entries**, including a separate CNS preset for Stellar Blade. You can also add custom games and select an engine profile.

<table>
  <tr>
    <td align="center" width="33%">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/black-myth-wukong.png" width="52" height="52" alt=""><br>
      <strong>Black Myth: Wukong</strong><br>
      <sub>Unreal Engine</sub>
    </td>
    <td align="center" width="33%">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/stellar-blade.png" width="52" height="52" alt=""><br>
      <strong>Stellar Blade</strong><br>
      <sub>Unreal Engine</sub>
    </td>
    <td align="center" width="33%">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/stellar-blade.png" width="52" height="52" alt=""><br>
      <strong>Stellar Blade · CNS</strong><br>
      <sub>Separate CNS preset</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/clair-obscur-expedition-33.png" width="52" height="52" alt=""><br>
      <strong>Clair Obscur: Expedition 33</strong><br>
      <sub>Unreal Engine</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/wuchang-fallen-feathers.png" width="52" height="52" alt=""><br>
      <strong>WUCHANG: Fallen Feathers</strong><br>
      <sub>Unreal Engine</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/borderlands-4.png" width="52" height="52" alt=""><br>
      <strong>Borderlands 4</strong><br>
      <sub>Unreal Engine</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/diablo-iv.png" width="52" height="52" alt=""><br>
      <strong>Diablo IV</strong><br>
      <sub>Diablo IV engine profile</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/resident-evil-requiem.png" width="52" height="52" alt=""><br>
      <strong>Resident Evil Requiem</strong><br>
      <sub>RE Engine</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/pragmata.png" width="52" height="52" alt=""><br>
      <strong>PRAGMATA</strong><br>
      <sub>RE Engine</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/death-stranding-2.png" width="52" height="52" alt=""><br>
      <strong>Death Stranding 2</strong><br>
      <sub>Decima</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/slay-the-spire-2.png" width="52" height="52" alt=""><br>
      <strong>Slay the Spire 2</strong><br>
      <sub>Godot</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/website/assets/brand.png" width="52" height="52" alt=""><br>
      <strong>Add your game</strong><br>
      <sub>Choose an engine · Set paths · Manage mods</sub>
    </td>
  </tr>
</table>

**Engine profiles for custom games:** Unreal Engine, Unity, RE Engine, Godot, Decima, Diablo IV, and generic rules. Profiles define recognized file formats and default paths. Follow each mod author's instructions for required loaders, dependencies and compatible game versions.

[Game presets](UEModManager/Services/GameConfigService.cs) · [Engine rules](UEModManager/Models/EngineProfile.cs) · [Game icon credits](UEModManager/Assets/GameIcons/SOURCES.md)

## Manage your mods in one place

| Import and organize | Enable and save profiles | Track and recover |
| --- | --- | --- |
| Import ZIP archives, extracted folders and supported mod files | Enable or disable individual mods or a selection | View installation history and recovery results |
| Categorize, search and organize with drag and drop | Save different mod combinations as profiles | Recover from interrupted operations |
| Choose where to store your mod repository | Check file overrides and load-order conflicts | Export diagnostic bundles for bug reports |

Conflict checks use file and load-order rules; they do not inspect resources inside PAK archives. Available checks depend on the engine profile.

Extract RAR / 7z archives with WinRAR or 7-Zip first, then import the extracted folder or mod files.

## English interface previews

<table>
  <tr>
    <td width="58%" valign="top">
      <strong>First launch · Mod storage</strong><br><br>
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/docs/assets/readme/storage-setup-en.png" width="100%" alt="English storage setup with drive capacity, a recommended drive and a separate mod repository path">
    </td>
    <td width="42%" valign="top">
      <strong>Sign in with email</strong><br><br>
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/docs/assets/readme/login-en.png" width="100%" alt="English email sign-in with a six-digit verification code and a language switch">
    </td>
  </tr>
</table>

<details>
<summary><strong>Local data, upgrades and privacy</strong></summary>

### Where is my data stored?

| Data | Default location |
| --- | --- |
| Game settings and indexes | `%LOCALAPPDATA%\UEModManager\` |
| UI preferences and account data | `%APPDATA%\UEModManager\` |
| Mod repository | `%LOCALAPPDATA%\UEModManager\Repository\`, customizable |
| Deployment backups | `%LOCALAPPDATA%\UEModManager\Backups\Deployments\`, customizable |
| Logs | `%LOCALAPPDATA%\UEModManager\Logs\` |

Check Settings for the actual paths in use. Installed and portable copies both use the current Windows user's data directories. Close the manager before upgrading. A normal uninstall keeps user data, the repository and backups. When moving to another computer, back up both user-data directories and any separate repository, generated-output and backup folders.

The bundled legacy migration and full-data-cleanup scripts use Chinese instructions. Run the cleanup script only when you intend to delete your user data.

### Online features

Accounts support email sign-in. Core mod management also works offline. Account features currently do not sync mod files or profiles between computers.

If you opt into update checks and anonymous statistics, the app sends a random device identifier, app version and Windows version. An email hash may be included when you sign in. These reports do not include your plain-text email address, computer name, file paths, game folder or mod list. You can disable the option in **Settings → General** to stop its update checks and statistics requests.

Sign-in, registration and password recovery send the account information needed for those actions. See the [English license and privacy terms](Setup/LICENSE.en.txt) and the [website privacy page (Chinese)](https://www.modmanger.com/help#privacy).

</details>

<details>
<summary><strong>Build from source and contribute</strong></summary>

Building the desktop app requires Windows and the .NET 8 SDK. Workers tests require Node.js 22 or later.

```powershell
dotnet build UEModManager.sln --configuration Release
dotnet test UEModManager.sln --configuration Release
```

To build the installer, install Inno Setup 6.7 or later and run:

```powershell
./Build-Installer.ps1 -Configuration Release
```

For a custom Inno Setup location, pass its folder, `Compil32.exe` or `ISCC.exe` with `-InnoPath`. Each build uses a fresh publish directory and checks the version and package contents.

Further developer documentation is currently in Chinese: [documentation index](docs/README.md), [architecture overview](docs/architecture/overview.md), and [changelog](CHANGELOG.md).

Bug reports, suggestions and pull requests are welcome. Open an [issue](https://github.com/velist/UE-Game-ModManager/issues) with the app version, reproduction steps and error details. Review any diagnostic bundle before sharing it.

</details>

## Community

**[Join the Ai-chan Mod Manager QQ group](https://qm.qq.com/q/5PeqQxiszC)** · Group ID: **147223127**

Group name in QQ: **爱酱MOD管理器交流群**. Scan the code with QQ, or use the link above. You can also report issues in English on [GitHub Issues](https://github.com/velist/UE-Game-ModManager/issues); include your app version, steps to reproduce and any error messages.

<p>
  <a href="https://qm.qq.com/q/5PeqQxiszC"><img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/website/assets/qq-group-147223127.png" width="240" height="240" alt="Ai-chan Mod Manager QQ group QR code, group ID 147223127"></a>
</p>

## Support development

If the manager helps you, you can support its development and maintenance through WeChat Pay or Alipay. Thank you for your support.

| WeChat Pay | Alipay |
| :---: | :---: |
| <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/捐赠支持/微信支付.jpg" alt="WeChat Pay donation QR code" width="240"> | <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/捐赠支持/支付宝.jpg" alt="Alipay donation QR code" width="240"> |

---

<p align="center">
  Ai-chan Studio · <a href="https://www.modmanger.com">modmanger.com</a><br>
  Free for personal, noncommercial use under the <a href="Setup/LICENSE.en.txt">software license</a>.<br>
  <a href="https://github.com/velist/UE-Game-ModManager/issues">Report an issue</a> · mr.xzuo@foxmail.com
</p>
