# ShadowWhispr — rules for AI agents

Hard-won constraints from real broken releases. A change that violates one of these
will look reasonable and then break on users' machines — do not "simplify" them away.

## Speech engine (cost seven broken releases, v0.1.1–v0.1.8)

- The app **ships its own Python** (`{app}\python`, pinned CPython from
  python-build-standalone, SHA-256 verified by `scripts/get-bundled-python.ps1`, not
  committed to git). Setup must NEVER search the machine for an interpreter or install
  one system-wide — interpreter detection was deleted in v0.5.0 after being the single
  largest source of setup failures. Do not reintroduce it.
- Do not use the python.org **embeddable** zip as the runtime: it has neither pip nor
  venv. python-build-standalone has both and is relocatable.
- The speech model MUST load from the plain `{app}\speech-model` folder (real files),
  NEVER via the Hugging Face hub cache. Do not reintroduce `from_pretrained` by repo
  id — the hub cache's symlink/xet layout repeatedly ended up unreadable when launched
  from the app. Avoid any design that needs Hugging Face at app runtime.
- `scripts/setup-stt.ps1` and `scripts/get-bundled-python.ps1` must stay **pure
  ASCII**: Windows PowerShell 5.1 reads unsigned UTF-8 as ANSI and an em dash inside a
  double-quoted string breaks parsing. Scan for bytes > 127 before shipping.
- `stt/requirements.txt` pins every transitive dependency; setuptools must stay <82
  for the pinned torch.
- Every requirement must install from a **published wheel**. Never use a
  `package @ git+https://...` pin: pip then shells out to `git`, which most users do
  not have (a v0.5.x install died on a user whose scoop `git` shim could not launch).
  If a feature needs an unreleased upstream commit, wait for the release or vendor a
  prebuilt wheel into the installer - do not make setup depend on the user's tooling.
- Setup writes `.venv\setup-complete` only after actually starting the worker once;
  a missing marker or missing/corrupt speech-model means "setup required". Setup emits
  `##SW## percent|message` and `##SWERR## message` markers the app parses for its
  progress screen. `-DetectOnly` reports what would be used and changes nothing.

## AI providers

- Codex "fast mode" is its **priority service tier**, set with
  `--config service_tier="priority"`. Offer it only for models whose entry in
  Codex's `models_cache.json` lists `"fast"` in `additional_speed_tiers` - Codex
  treats an unsupported tier as a soft warning and *silently omits it from the
  request*, so a wrongly offered switch looks like it works while doing nothing.
- Codex describes the tier as "1.5x speed, increased usage". It states no cost
  multiplier anywhere, so user-facing text must not invent one.
- The app passes `--ignore-user-config`, so the user's `config.toml` never
  affects a run; every setting must be passed explicitly on the command line.

## Tray lifetime (each point was a real bug)

- Live status (idle / listening / busy) is a coloured dot badged onto the **tray
  icon**, drawn at runtime in `TrayIconService`. The old floating bottom-right
  overlay window was removed in v0.5.3 as disruptive - do not bring it back. The
  GetHicon handle behind each drawn icon must be freed with `DestroyIcon`.

- The app keeps running in the tray after the window closes, so the process outlives
  environment changes. Provider CLI lookup must re-read User + Machine PATH from the
  registry (not the process's startup PATH snapshot), pass resolved full paths to
  `Process.Start`, and give children the refreshed PATH.
- The installer must declare `AppMutex` matching `Local\ShadowWhispr.SingleInstance`,
  or upgrades fail on a locked exe while the app sits "closed" in the tray.
- Update installers must wait for the process to actually exit (detached
  `Wait-Process` helper); "install when I close" must force a real exit, not
  hide-to-tray, or the pending install never runs.
- Uninstall must delete the autostart value `HKCU\...\Run\ShadowWhispr`.
- The window is created explicitly in `App.OnStartup` (no `StartupUri`) so a duplicate
  launch never shows a second window or tray icon.

## Release

- Build locally with `scripts/build-installer.ps1 -Version x.y.z`, then
  `gh release create` with a notes file; CI re-builds and clobbers the assets. The
  release workflow's notes step must join `gh` output lines with newlines
  (PowerShell returns arrays).
- Never create or publish a release without Dylan's explicit yes.

## Website

- The `website/` folder is its own separate **private** git repo, not part of this
  one. Deployment is handled there — server details deliberately do not live in this
  public file.

## Logging

- Every new feature, error path, and external call (network, subprocess, file
  download) must write to the app's log file in the same change that adds it. Crashes
  are caught by global handlers that log the full error before the app dies.
