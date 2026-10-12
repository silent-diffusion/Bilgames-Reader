# Intro motion graphic

A 93-second introduction to Bilgames Reader, built in the app's own design language
(`design/tokens.json`: Manrope and Literata, amber accent, dark theme with a light-theme beat).
Voice tones are deliberately left out for now.

| File | What it is |
|---|---|
| `bilgames-intro.html` | The animation. Self-contained HTML/CSS/JS; fonts come from Google Fonts. Autoplays and loops, pauses with the ⏸ button, and the chapter bar at the bottom jumps between scenes. **Hear a sample** plays `sample.mp3` in sync with the transcript in the *Listen* scene. Use it on a website as an `<iframe>` or embed it directly. |
| `bilgames-intro.mp4` | The same animation rendered at 1600×900, 24 fps (silent). |
| `bilgames-intro.gif` | 960 px wide, 12 fps, for the README. |
| `sample.mp3` | 27 s of narration made by the app's own engine: Heart (narrator), Emma (Rabbit), Bella (Alice), George (Cat), Fenrir (Hatter). Public-domain text. |
| `render.mjs` | Renders the HTML frame by frame to the MP4 and GIF. |

Every number and line in the animation comes from the app's own analysis of
`tests/corpus/alice.epub` (`Bilgames.Inspect … analyze 1`): 1,047 dialogue lines, 16 speakers, 289 lines
to review, chapter 1 analysed in 903 ms.

## Regenerating

The page exposes a deterministic timeline (`window.__seek(ms)`, `window.__duration`), so a
headless browser can step through it exactly.

```powershell
npm install playwright ffmpeg-static; npx playwright install chromium
node docs/intro/render.mjs docs/intro/bilgames-intro.html docs/intro 24 1600 960 12
```

Arguments: page, output folder, video fps, video width, GIF width, GIF fps. To change the audio
sample, edit the lines in `tools/Bilgames.IntroSample/Program.cs`, run it, convert the WAV to MP3,
and paste the start times it writes (`sample.json`) into the `SAMPLE` list in the HTML.
