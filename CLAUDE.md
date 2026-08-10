# ShadowWhispr — working notes

Three projects since the Linux port: `src/ShadowWhispr` (WPF, `net10.0-windows`),
`src/ShadowWhispr.Core` (shared, `net10.0`), `src/ShadowWhispr.Linux` (Avalonia,
`net10.0`). Core keeps the `ShadowWhispr.*` namespaces so the WPF app was
extracted without touching its usings.

## Windows UI shell (Midnight Aurora)

`MainWindow.xaml` is no longer one long maximized scrolling page. The window
opens at 1180x820 (`MinWidth` 1000, `MinHeight` 660, **not** maximized) and is a
left sidebar plus a left-tabbed `TabControl` (`SectionTabs`) with four sections:
**Dictation**, **AI Cleanup**, **Transcript**, **Settings**. Each `TabItem`'s
`Header` is the section name and its `Tag` is the one-line subtitle; the
persistent header strip binds to both, and carries the auto-save pill
(`SaveStatusPill` / `SaveDot` / `SaveStatus`). The sidebar holds the brand mark,
the engine status pill (`EngineDot` / `EngineStatus`) and the **Pause dictation**
button (`PauseButton`), which stays in sync with the tray menu item through
`SetDictationPaused`.

Palette: violet-black surfaces, an iris `#7C6BF2` -> aurora cyan `#46D9F5`
signature gradient, mint `#4FE0C0` for good status, coral `#FF6B85` for
warnings. Section/nav glyphs are hand-stroked `Canvas` paths inside `Viewbox`es,
not a font or image set. `scripts/generate-icon.py` regenerates the matching app
icon (a luminous whisper wave on a dark tile) into `src/ShadowWhispr/icon.ico`;
it no longer draws the old gold microphone.

When adding UI, put it inside the right section rather than appending to the
bottom of a page, and reuse the shared styles (`Card`, `SettingRow`,
`SectionTitle`, `SectionCaption`, `FieldLabel`, `Hint`, `Pill`,
`SecondaryButton`) instead of inlining colours.

## Start with Windows

`src/ShadowWhispr/Services/StartupService.cs` owns a single `HKCU\...\Run`
value named `ShadowWhispr` — no admin, no scheduled task. `Apply(bool enabled,
bool startMinimized = true)` writes `"<exe>" --tray` when minimized and `"<exe>"`
when not; `StartsMinimized()` reports whether the existing entry carries
`--tray`, defaulting to `true` so entries written before the option existed keep
behaving exactly as they did. The registry is the source of truth on load: the
UI reads `IsEnabled()` / `StartsMinimized()` and only falls back to
`AppSettings.StartMinimized` (default `true`) while autostart is off.

In the Settings section, `StartMinimizedCheck` is nested under
`StartWithWindowsCheck` and binds `IsEnabled` to it, so it greys out while
autostart is off; `StartMinimizedToggled` only ever rewrites an existing entry
and can never switch autostart on by itself. `StartupStatus` shows the one-line
`DescribeAutostart` summary, and both handlers snap the checkboxes back to the
real registry state if Windows refuses the write.

## Linux build and run (native, on the CachyOS desktop)

The .NET 10 SDK is installed via pacman. Build/run directly:

```bash
dotnet run --project src/ShadowWhispr.Linux -c Release
```

The WPF app also *compiles* here for regression checks (cannot run):

```bash
dotnet build src/ShadowWhispr/ShadowWhispr.csproj -c Release /p:EnableWindowsTargeting=true
```

Linux runtime pieces: global hotkeys read /dev/input (user must be in the
`input` group), pasting uses a /dev/uinput virtual keyboard (udev rule in
`packaging/linux/`), audio via parec/paplay, clipboard via wl-clipboard. The
speech engine lives in `~/.local/share/ShadowWhispr` (venv + model + stt copy),
built by `scripts/setup-stt.sh` using uv with `--index-strategy
unsafe-best-match` (uv's default index strategy cannot resolve the pinned
requirements against the PyTorch extra index). The GNOME tray icon needs the
AppIndicator extension.

## Windows build and run from WSL

There is no .NET SDK in WSL — drive the Windows one through interop:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" publish src/ShadowWhispr/ShadowWhispr.csproj \
  -c Release -r win-x64 --self-contained false -o 'C:\Users\Dylan\dev\ShadowWhispr-run'
```

Then launch it with `powershell.exe -NoProfile -Command "Start-Process 'C:\Users\Dylan\dev\ShadowWhispr-run\ShadowWhispr.exe'"`.

`C:\Users\Dylan\dev\ShadowWhispr-run` already contains junctions to the installed
copy under `%LOCALAPPDATA%\Programs\ShadowWhispr`:

| Junction | Why |
|---|---|
| `.venv` | 4.9 GB Python environment |
| `speech-model` | 2.4 GB Parakeet v3 weights |
| `python` | bundled 3.12 runtime |

`ParakeetService.FindProjectRoot` walks up from the exe looking for a folder with
both `stt\` and `.venv\Scripts\python.exe`, so the publish folder satisfies it and
the app skips first-run speech setup. Recreate a junction with
`cmd.exe /c mklink /J <link> <target>`.

**These junctions are the real install.** Anything that writes to `.venv` or
`speech-model` mutates the copy Dylan uses day to day — never point setup code at
this folder.

## Logs

`app-log.txt` next to the exe (`Core/Services/AppLog.cs`, rotates to
`app-log.old.txt`; falls back to `~/.local/state/ShadowWhispr` when the exe dir
is read-only). Read it after launching instead of asking Dylan for console
output. Linux speech setup logs to `~/.local/share/ShadowWhispr/setup-log.txt`.

## Tests

`dotnet.exe test tests/ShadowWhispr.Tests/ShadowWhispr.Tests.csproj`. The
`AiProviderDiscoveryTests` need the Claude/Codex/Gemini CLIs installed and signed
in, and fail in WSL-launched runs — CI excludes them with
`--filter "FullyQualifiedName!~AiProviderDiscoveryTests"`.

## Releases

Publishing a GitHub release triggers `.github/workflows/release.yml`, which builds
the Inno Setup installer, attaches it with a SHA-256 checksum, and prepends the
"Unknown publisher" notice. Auto-update is on by default in the app, so a published
release reaches existing users — confirm the version with Dylan before publishing.
