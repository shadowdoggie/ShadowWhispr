# ShadowWhispr — working notes

WPF app, `net10.0-windows`, .NET 10. Windows-only at runtime; the checkout lives in WSL.

## Build and run from WSL

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

`app-log.txt` next to the exe (`Services/AppLog.cs`, rotates to `app-log.old.txt`).
Read it after launching instead of asking Dylan for console output.

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
