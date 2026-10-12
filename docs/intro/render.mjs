// Renders docs/intro/bilgames-intro.html frame by frame (deterministic timeline) into MP4 and GIF.
import { chromium } from 'playwright';
import ffmpeg from 'ffmpeg-static';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const html = process.argv[2];
const outDir = process.argv[3];
const fps = +(process.argv[4] || 24);
const width = +(process.argv[5] || 1600);
fs.mkdirSync(outDir, { recursive: true });
const frames = path.join(outDir, 'frames'); fs.rmSync(frames, { recursive: true, force: true }); fs.mkdirSync(frames);

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width, height: Math.round(width * 9 / 16) }, deviceScaleFactor: 1 });
await page.goto('file:///' + html.replace(/\\/g, '/'));
await page.evaluate(() => document.fonts.ready);
await page.waitForTimeout(500);
const duration = await page.evaluate(() => window.__duration);
const n = Math.ceil(duration / 1000 * fps);
console.log(`duration ${duration} ms → ${n} frames at ${fps} fps, ${width}px wide`);
for (let i = 0; i < n; i++) {
  await page.evaluate(ms => window.__seek(ms), i * 1000 / fps);
  await page.screenshot({ path: path.join(frames, `f${String(i).padStart(5, '0')}.png`), type: 'png' });
  if (i % 100 === 0) console.log(`frame ${i}/${n}`);
}
await browser.close();

const run = args => new Promise((res, rej) => { const p = spawn(ffmpeg, args, { stdio: 'inherit' }); p.on('exit', c => c ? rej(new Error('ffmpeg ' + c)) : res()); });
const inp = ['-y', '-loglevel', 'error', '-framerate', String(fps), '-i', path.join(frames, 'f%05d.png')];
await run([...inp, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '21', '-preset', 'slow', '-movflags', '+faststart', path.join(outDir, 'bilgames-intro.mp4')]);
console.log('mp4 done');
const gifW = +(process.argv[6] || 960), gifFps = +(process.argv[7] || 12);
await run([...inp, '-vf', `fps=${gifFps},scale=${gifW}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle`, path.join(outDir, 'bilgames-intro.gif')]);
console.log('gif done');
for (const f of ['bilgames-intro.mp4', 'bilgames-intro.gif']) console.log(f, (fs.statSync(path.join(outDir, f)).size / 1e6).toFixed(1), 'MB');
