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
| `tools/Bilgames.Inspect` | Developer CLI: `dotnet run --project tools/Bilgames.Inspect -- book.epub [structure\|text N\|analyze N]`; also `-- x gpu` (apps using GPU memory) and `-- x phonemes "text"` (what the speech front end hears). |
| `build/release.ps1` | Builds and packages the one-click installer. |

## Build and run

Requires the .NET 8 SDK on Windows 10 1809 or later.

```powershell
dotnet test tests/Bilgames.Core.Tests
dotnet build src/Bilgames.App
src/Bilgames.App/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/BilgamesReader.exe
```

### CPU and GPU

Speech runs on ONNX Runtime with DirectML. With *Prefer the GPU* on (Settings › Processing), the
engine uses the most capable DirectX 12 GPU whenever it has free video memory, falls back to the CPU
segment-by-segment if memory runs short or the GPU errors, and checks again every 20 s to move back —
generation never stops. DirectML can't run Kokoro's depthwise ConvTranspose layers, so the GPU copy of
the model is rewritten on first use (`ModelPatcher`: zero-insertion upsample + grouped Conv,
numerically equivalent) and uses full precision (half precision overflows on DirectML).

When the GPU is preferred but too full, the Generate tab and Settings › Processing list the apps
holding its video memory (largest first), read from the same Windows performance counters Task
Manager uses (`GpuMemoryUsage`, PDH `\GPU Process Memory(*)\Dedicated Usage`).

### Speakers

The Cast tab can **add** a character the analysis missed (they get an unused voice and colour) and
**merge** names that are the same person. Merged names are kept as aliases and added speakers are
kept as user-added, so finding speakers again re-applies both. Typing a new name in Review's
"Someone else…" or a line's "Reassign…" picker adds that speaker on the spot.

### Bookmarks and highlights

In the player, **B** bookmarks the moment being heard and **H** highlights the line being read.
Selecting text in the transcript offers four highlight colours, a note, and copy. Right-clicking a
line offers bookmark, highlight, play from here, and edit or remove for an existing highlight. They
are stored in `books/{id}/annotations.json` and survive deleting the audio. The bookmark button in
the player header lists them, plays from any of them, and exports them as Markdown, plain text or
CSV (`AnnotationExport`).

### Listening while generating

Any chapter can be played immediately: the player asks the job to generate that chapter first and
plays it in small chunks as sentences land (`ChapterStream`), on the same timeline as the final file,
then switches to the saved chapter at the same spot once it's finished.

Data lives in `%LOCALAPPDATA%\BilgamesReader`. Set `BILGAMES_DATA` to another folder to try things
without touching your real library. Passing `.epub` paths on the command line imports them.

## Releasing an update

```powershell
./build/release.ps1 -Version 0.3.0            # builds artifacts/releases/BilgamesReader-win-Setup.exe
./build/release.ps1 -Version 0.3.0 -Publish   # also uploads a GitHub release (asks first; uses `gh auth token`)
```

`Setup.exe` installs per-user (no admin) with a Start menu entry and an uninstaller. Installed copies
update from the GitHub Releases of `silent-diffusion/Bilgames-Reader`: Settings → *Check for updates*
(or the opt-in *check when the app starts*, off by default). The app only goes online when the user
clicks something that needs it (checking for updates, installing one, downloading a voice model), and
nothing from the user's books is ever sent.

Bump `<Version>` in `Directory.Build.props` along with the release so the About/version line matches.

## Licenses

Kokoro-82M model and voices: Apache-2.0. KokoroSharp: MIT. MisakiSharp: Apache-2.0. Manrope and
Literata fonts: SIL Open Font License (see `src/Bilgames.App/Assets/Fonts`).
