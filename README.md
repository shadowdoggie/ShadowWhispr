# ShadowWhispr

Hold a hotkey, speak, and release to type into the selected Windows input. Speech recognition runs locally with NVIDIA Parakeet v3. Optional cleanup can use the user's existing Claude, ChatGPT/Codex, Google Antigravity, or Kimi subscription login.

Click the hotkey field and press any key or modifier combination to change it. Extended keys F13 through F24 are supported. Provider Login and Logout buttons use each official provider tool; the installed Kimi tool supports login but does not expose logout.

## Run on this PC

```powershell
.\scripts\run.ps1
```

The first run installs the local speech environment and downloads the Parakeet model. The model is cached on the PC for later runs.

## Privacy

- Microphone audio is written only to a temporary WAV file and deleted immediately after transcription.
- Raw speech stays local unless AI cleanup is enabled.
- OAuth credentials remain inside each provider's official CLI; ShadowWhispr never reads or stores them.

## Requirements

- Windows 10 or 11
- NVIDIA GPU recommended
- .NET 10 Desktop Runtime
- Python 3.12
- The official provider CLI and an active login for any AI cleanup provider you enable
