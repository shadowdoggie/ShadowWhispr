# ShadowWhispr — working notes

Three projects since the Linux port: `src/ShadowWhispr` (WPF, `net10.0-windows`),
`src/ShadowWhispr.Core` (shared, `net10.0`), `src/ShadowWhispr.Linux` (Avalonia,
`net10.0`). Core keeps the `ShadowWhispr.*` namespaces so the WPF app was
extracted without touching its usings.

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
