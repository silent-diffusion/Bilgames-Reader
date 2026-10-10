# Bilgames Reader

On-device EPUB → audiobook app for Windows. Import a DRM-free EPUB, let it find who speaks each
line, pick a voice per character, and generate a chapter-aware audiobook — entirely offline after a
one-time voice model download. Product spec: [ReadMe.html](ReadMe.html); design handoff:
[design/README.md](design/README.md).

## What's in the box

| Path | What it is |
|---|---|
| `src/Bilgames.Core` | Platform-free engine: safe EPUB parsing, chapter detection, dialogue attribution, synthesis planning, job coordinator, WAV/M4B chapter writer. |
| `src/Bilgames.Tts.Kokoro` | Kokoro-82M text-to-speech on ONNX Runtime (via KokoroSharp), plus the model downloader. |
| `src/Bilgames.App` | WinUI 3 app: adaptive compact / medium / expanded layout, player, settings, updates. |
| `tests/Bilgames.Core.Tests` | Unit tests (archive safety, attribution rules, planner, M4B chapters). |
| `tools/Bilgames.Inspect` | Developer CLI: `dotnet run --project tools/Bilgames.Inspect -- book.epub [structure\|text N\|analyze N]`. |
| `build/release.ps1` | Builds and packages the one-click installer. |

## Build and run

Requires the .NET 8 SDK on Windows 10 1809 or later.

```powershell
dotnet test tests/Bilgames.Core.Tests
dotnet build src/Bilgames.App
src/Bilgames.App/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/BilgamesReader.exe
```

Data lives in `%LOCALAPPDATA%\BilgamesReader`. Set `BILGAMES_DATA` to another folder to try things
without touching your real library. Passing `.epub` paths on the command line imports them.

## Releasing an update

```powershell
./build/release.ps1 -Version 0.2.0            # builds artifacts/releases/BilgamesReader-win-Setup.exe
./build/release.ps1 -Version 0.2.0 -Publish   # also uploads a GitHub release (asks first; uses `gh auth token`)
```

`Setup.exe` installs per-user (no admin) with a Start menu entry and an uninstaller. Installed copies
update from the GitHub Releases of `silent-diffusion/Bilgames-Reader`: Settings → *Check for updates*
(or the opt-in *check when the app starts*). Every network request — update checks, update downloads,
voice model downloads — asks for confirmation first, and nothing from the user's books is ever sent.

Bump `<Version>` in `Directory.Build.props` along with the release so the About/version line matches.

## Licenses

Kokoro-82M model and voices: Apache-2.0. KokoroSharp: MIT. MisakiSharp: Apache-2.0. Manrope and
Literata fonts: SIL Open Font License (see `src/Bilgames.App/Assets/Fonts`).
