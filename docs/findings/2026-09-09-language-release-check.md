# English and Chinese release check — 2026-09-09

The app now uses one persisted language setting across first-run storage setup,
email sign-in, settings and the main window. Static XAML labels and dynamic
counts/status labels share an embedded English catalog.

## Language selection

- An existing saved preference wins on restart and upgrade.
- Before a preference is saved, an installed copy uses the language selected
  in the installer. A portable copy uses Chinese on Chinese Windows systems
  and English on other Windows systems.
- Players can switch language in the first-run storage window, the sign-in
  window, or Settings → General → Language.
- Switching keeps email/code input, unsaved game paths, game identifiers,
  selected sorting and profile data. Built-in game/category labels translate;
  custom names, file paths and user descriptions are preserved.

## UI and packaging

All 19 window XAML files use the shared localization bindings. Dialog buttons,
file pickers, import/conflict prompts, deployment explanations and storage
migration messages have English text. The main toolbar wraps as a group at
narrow widths, and longer labels wrap or truncate within their allocated space.

The installer offers English and Simplified Chinese, with a license in each
language, translated tasks and runtime guidance. Its initial-language marker
does not overwrite a language preference already saved by the app. Application
identity and user-data retention behavior are unchanged. The English quick start
is linked from the README and included in the distribution.

Original donation images and credits retain their original names/text. Windows
file-picker chrome and operating-system error details follow the OS. The bundled
legacy migration and full-data-cleanup scripts still use Chinese; the English
quick start identifies them explicitly.

## Validation

| Check | Result |
| --- | --- |
| Release solution build with warnings treated as errors | 0 warnings, 0 errors |
| .NET regression tests | 1848 passed; 1 manual snapshot generator skipped |
| Actual XAML load and language binding checks | All 19 windows |
| Main toolbar bounds | 1024, 1280 and 1440 pixels in English; 1024 in Chinese |
| Language persistence and repeated switching | Passed with isolated preferences |
| Input preservation and dynamic mod/profile labels | Passed with real WPF controls/templates |
| Installer smoke checks | 106 passed in isolated installation directories |

Installer checks cover both languages, rendered caption sizing, license
selection, first-launch markers, missing-runtime guidance, default autostart
off, explicit autostart changes, cross-language upgrades, installed-file SHA256
matches and user-data preservation after uninstall. A separate AppId, shortcut
and Run entry were used for these checks.

Language integration tests use a network handler that rejects requests; they do
not send verification emails or change the account protocol. Production user
configuration was not used for the checks.

GitHub remains a source distribution. Download links lead to the official
website; compiled installers and portable archives are kept outside Git.
