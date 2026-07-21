<div align="center">

<img src="https://shadowwhispr.shadowdog.cat/assets/og.png" alt="ShadowWhispr — Hold a key. Speak. It types for you." width="100%">

<h1>ShadowWhispr</h1>

**Hold a key. Speak. Release. It types for you — anywhere on Windows.**

Local, offline voice typing powered by NVIDIA Parakeet v3. Your voice never leaves your PC.

<br>

[![Website](https://img.shields.io/badge/website-shadowwhispr.shadowdog.cat-8b7ff0?style=flat-square)](https://shadowwhispr.shadowdog.cat)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square&logo=windows&logoColor=white)](#-requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#-requirements)
[![Python](https://img.shields.io/badge/Python-3.12-3776AB?style=flat-square&logo=python&logoColor=white)](#-requirements)
[![Runs offline](https://img.shields.io/badge/speech-100%25%20local-5eead4?style=flat-square)](#-privacy)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-brightgreen?style=flat-square)](LICENSE)

[**🌐 Website**](https://shadowwhispr.shadowdog.cat) · [**⬇ Download**](https://github.com/shadowdoggie/ShadowWhispr/releases/latest) · [**🐛 Report a bug**](https://github.com/shadowdoggie/ShadowWhispr/issues)

</div>

---

## ✨ What it is

ShadowWhispr turns your voice into text in **whatever window is focused** — your chat box, a document, an email, your code editor. You hold your hotkey, talk, let go, and the words appear where your cursor already is. No copy-pasting, no separate window, no thinking about it.

Speech recognition runs **entirely on your own machine** using NVIDIA's Parakeet v3 model. Nothing is uploaded. The internet is optional.

🌐 **See it in action:** [**shadowwhispr.shadowdog.cat**](https://shadowwhispr.shadowdog.cat)

<div align="center">

### ⌨️ &nbsp; Hold your hotkey &nbsp; → &nbsp; 🎙️ &nbsp; Speak &nbsp; → &nbsp; 📝 &nbsp; Release &nbsp; → &nbsp; text appears

<br>

<img src="docs/main-window.png" alt="The ShadowWhispr main window: hotkey and microphone settings on top, the optional AI cleanup section below." width="720">

</div>

---

## 🧠 Why I built this

I have **ADHD**, and typing is where a lot of my thoughts go to die.

The idea shows up fully formed in my head — then, somewhere between my brain and the keyboard, half of it evaporates. I lose the thread mid-sentence. I stall on how to phrase things. I open a text box, forget what I was going to write, and close it again. Speaking is *so* much faster than typing for me, but every voice tool I tried added friction: open an app, switch to it, wait, copy the result, paste it back. Each of those little steps is another place for an ADHD brain to get derailed, bored, and give up.

So I built the thing I actually needed: **zero friction after setup.** Set your hotkey once, and from then on it's a single reflex — hold, talk, release, done. No window to find. No thought required. The text lands exactly where I was already looking. It's designed to get out of my way completely, because the moment a tool asks me to *think about using it*, I've already lost.

There was one more gap that pushed me to build it: **the other voice tools I found don't let you connect your AI providers through their real logins.** They expect you to paste in an API key and pay per word on top of subscriptions you already have. I didn't want that. ShadowWhispr connects to Claude, ChatGPT/Codex, and Google Antigravity through their **official OAuth logins**, so the AI cleanup just uses the plans you're already paying for — no extra keys, no extra billing.

If your brain works like mine, I hope it helps you get the words out too. 💜

---

## 🚀 Features

| | |
|---|---|
| 🔒 **Fully local recognition** | NVIDIA Parakeet v3 downloads once and is cached on your PC. Works offline forever after. |
| 📦 **No Python to install** | The app ships its own Python runtime. Nothing is added to your system, and an existing Python setup is never touched. |
| 🖊️ **Types anywhere** | Text appears in whatever input is focused — chat, docs, email, code. No copy-pasting. |
| ⚡ **Your hotkey, your rules** | Click the hotkey field and press any key or modifier combo. Extended keys **F13–F24** supported. |
| ✌️ **Two hotkeys, two modes** | Set a second hotkey that types the **raw** transcript with AI cleanup skipped. Pick the treatment with your finger, not the settings screen. |
| 🎙️ **Pick your microphone** | Choose which mic ShadowWhispr records from, or follow the Windows default. The choice is remembered — even if the mic is unplugged for a while. |
| 📬 **No waiting between messages** | Start dictating the next message while the previous one is still transcribing or being cleaned up. Each one is queued and pasted into the field it was dictated into. |
| 🔔 **Lives in the tray** | Closing the window keeps ShadowWhispr listening from the system tray. Optionally start it with Windows — **off by default**. |
| 🤖 **Optional AI cleanup** | Polish rough dictation with your **existing** Claude, ChatGPT/Codex, or Google Antigravity login. Off by default. |

---

## 🔧 Run it on your PC

```powershell
.\scripts\run.ps1
```

The **first run** installs the local speech environment and downloads the Parakeet model. That model is cached on your PC, so every run after that is fast and offline.

> **Tip:** Prefer a proper install? Grab the installer from the [latest release](https://github.com/shadowdoggie/ShadowWhispr/releases/latest).

**First launch (installed app):** the very first time you open ShadowWhispr, it offers a one-time **"Set up speech now"** step. That downloads the local speech engine (~2–3 GB) — no admin needed, and no Python to install, because the app brings its own. Progress is shown **inside the app** — a step name, a progress bar, and live megabytes during the big model download — with no console window to read. If anything goes wrong you get a plain-English message and an **Open setup log** button. After that, launches are instant and offline.

### 🔔 Tray and startup

Closing the ShadowWhispr window doesn't stop it — it keeps running in the system tray so your hotkey still works. **Quit ShadowWhispr** from the tray icon's menu stops it for real, and the tray's tooltip always tells you which hotkeys are armed. You can turn the tray behaviour off with a checkbox.

**Start with Windows** is a separate checkbox and is **off by default**. Turning it on adds a normal per-user startup entry (no admin, no scheduled task) that launches ShadowWhispr straight into the tray; unticking it removes the entry again.

### Windows may show “Unknown publisher”

> [!IMPORTANT]
> The official ShadowWhispr installer does **not** contain viruses or malware. Windows shows “Unknown publisher” because this free project does not use a paid code-signing certificate—not because Windows detected a threat. Download only from this repository's [official releases](https://github.com/shadowdoggie/ShadowWhispr/releases), and use the included SHA-256 checksum if you want to verify the installer file.

---

## 🤖 Optional AI cleanup

Dictation is rarely perfect on the first pass. ShadowWhispr can optionally send your transcribed text through an AI model to tidy up punctuation, filler words, and phrasing — using a subscription you **already have**:

- **Claude** (Anthropic)
- **ChatGPT / Codex** (OpenAI)
- **Google Antigravity**

Login and Logout run through each provider's official CLI tool. ShadowWhispr never sees or stores your credentials — they live inside the provider's own tool.

### ⭐ Recommended models for the best experience

For the cleanest results, these are the models I'd reach for:

| Model | Notes |
|---|---|
| **GPT 5.6 Sol** | Use **medium** effort reasoning mode |
| **Gemini 3.6 Flash** | — |
| **Claude Opus** | — |

**This is off by default.** With it off, nothing you say ever leaves your machine.

### ⚡ Fast mode (Codex only)

Codex models offer a faster speed tier. Tick **Fast mode** under the model picker to use it:
replies come back about **1.5x faster**, but it uses up your Codex usage allowance quicker than
normal speed. It's **off by default**, and the box only appears for Codex models that offer it.

You can also set a **second hotkey** that always skips cleanup, even while AI cleanup is switched on — hold that one instead and the raw local transcript is typed straight out. Both hotkeys are fully configurable, and the second one is optional (press **Delete** while setting it to clear it).

---

## 🔐 Privacy

Privacy here isn't a setting you switch on — it's how the app works by default.

- 🎧 Microphone audio is written **only** to a temporary WAV file, and that file is **deleted immediately** after transcription.
- 🏠 Raw speech stays **local** unless you explicitly enable AI cleanup.
- 🔑 OAuth credentials remain inside each provider's official CLI — ShadowWhispr never reads or stores them.

---

## 💻 Requirements

- **Windows 10 or 11**
- **NVIDIA GPU (required)** — see note below
- **.NET 10 Desktop Runtime**
- The official provider CLI + an active login for any AI cleanup provider you choose to enable

> [!NOTE]
> **You do not need Python.** ShadowWhispr ships its own Python 3.12 inside the app and uses only that. Nothing is installed system-wide, no interpreter already on your PC is touched or modified, and uninstalling removes it all again.

> [!IMPORTANT]
> **ShadowWhispr runs on NVIDIA hardware only.** Local transcription uses a CUDA build of PyTorch and loads the Parakeet model onto an NVIDIA GPU. **AMD and Intel GPUs are not supported**, and there is no CPU-only fallback — without a working NVIDIA + CUDA setup, the app will not transcribe.

---

## 🗂️ Project layout

```
ShadowWhispr/
├─ src/ShadowWhispr/   # WPF desktop app (.NET 10)
├─ stt/                # Local speech-to-text worker (Python + Parakeet v3)
├─ scripts/            # run / build / setup PowerShell scripts
└─ installer/          # Inno Setup installer definition
```

The bundled Python runtime is not in source control. `scripts/get-bundled-python.ps1` fetches a pinned, checksum-verified [python-build-standalone](https://github.com/astral-sh/python-build-standalone) build into `python/` — automatically during a build, and on first setup in a source checkout.

The landing page source is maintained separately in the [ShadowWhispr website repository](https://github.com/shadowdoggie/shadow-whispr-website).

---

## 📄 License

Licensed under the [**GNU AGPL-3.0**](LICENSE). © Dylan.

Free and open source — you're welcome to use, study, and build on it. But if you distribute it or run a modified version (including as a web service), you **must** keep it open under this same license and credit the original. In plain terms: you can't take this code and pass it off as your own closed product. 💜

<div align="center">
<br>

**Stop typing. Start talking.**

[![Download ShadowWhispr](https://img.shields.io/badge/⬇%20Download%20for%20Windows-8b7ff0?style=for-the-badge)](https://github.com/shadowdoggie/ShadowWhispr/releases/latest)

[🌐 shadowwhispr.shadowdog.cat](https://shadowwhispr.shadowdog.cat)

</div>
