#!/usr/bin/env node
// Renders Clawd Pet's brand pictures into docs/brand/ from studio.html, frame by frame,
// in a headless Chrome of its own (your screen isn't touched).
//
//   node docs/brand/render.mjs            render everything
//   node docs/brand/render.mjs spar logo  render just those
//
// Needs Node 22+, Google Chrome (or CHROME=/path/to/chrome) and ffmpeg.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const CHROME = process.env.CHROME || {
  darwin: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  win32: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
}[process.platform] || "google-chrome";
const FPS = 20;

// Each animated scene becomes a GIF for the README (960 wide); spar also becomes an MP4 for posts.
const JOBS = {
  spar: { kind: "anim", dpr: 2, gif: "spar.gif", mp4: "spar.mp4" },
  talk: { kind: "anim", dpr: 2, gif: "talk.gif" },
  status: { kind: "anim", dpr: 2, gif: "status.gif" },
  moods: { kind: "still", dpr: 2, png: "moods.png" },
  social: { kind: "still", dpr: 1, png: "social-preview.png" },
  logo: { kind: "logo" },
};

// Clawd's idle pose on his 14 by 9 grid: [x, y, width, height, colour].
const CLAWD = [
  [2, 0, 10, 7, "#D77757"], [3, 0, 8, 1, "#EC9978"], [2, 1, 1, 3, "#EC9978"], [2, 6, 10, 1, "#B05B3F"],
  [0, 3, 2, 1, "#D77757"], [0, 4, 2, 1, "#B05B3F"], [12, 3, 2, 1, "#D77757"], [12, 4, 2, 1, "#B05B3F"],
  ...[3, 5, 8, 10].flatMap((x) => [[x, 7, 1, 1, "#D77757"], [x, 8, 1, 1, "#B05B3F"]]),
  [4, 2, 1, 2, "#171312"], [9, 2, 1, 2, "#171312"],
];
function logoSVG() {
  const rects = CLAWD.map(([x, y, w, h, c]) => `<rect x="${x}" y="${y}" width="${w}" height="${h}" fill="${c}"/>`).join("");
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="-1 -4 16 16" width="512" height="512" shape-rendering="crispEdges"><title>Clawd Pet</title>${rects}</svg>\n`;
}

function sleep(ms) { return new Promise((ok) => setTimeout(ok, ms)); }

async function connect(url) {
  const ws = new WebSocket(url);
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = () => fail(new Error("could not reach Chrome")); });
  let next = 0;
  const pending = new Map(), waiters = [];
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id);
      pending.delete(m.id);
      if (m.error) p.fail(new Error(m.error.message)); else p.ok(m.result);
    } else for (const w of [...waiters]) if (w.method === m.method) { waiters.splice(waiters.indexOf(w), 1); w.ok(m.params); }
  };
  return {
    send: (method, params = {}) => new Promise((ok, fail) => { const id = ++next; pending.set(id, { ok, fail }); ws.send(JSON.stringify({ id, method, params })); }),
    once: (method) => new Promise((ok) => waiters.push({ method, ok })),
    close: () => ws.close(),
  };
}

async function main() {
  const want = process.argv.slice(2);
  for (const w of want) if (!JOBS[w]) throw new Error(`unknown asset "${w}"; try: ${Object.keys(JOBS).join(", ")}`);
  const jobs = Object.entries(JOBS).filter(([name]) => !want.length || want.includes(name));
  execFileSync("ffmpeg", ["-version"], { stdio: "ignore" });

  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "clawd-studio-"));
  const chrome = spawn(CHROME, ["--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`,
    "--hide-scrollbars", "--no-first-run", "--no-default-browser-check", "--force-color-profile=srgb", "about:blank"], { stdio: "ignore" });
  try {
    let port;
    for (let i = 0; i < 150 && !port; i++) {
      try { port = fs.readFileSync(path.join(profile, "DevToolsActivePort"), "utf8").split("\n")[0]; } catch { await sleep(100); }
    }
    if (!port) throw new Error("Chrome didn't start");
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    const c = await connect(targets.find((t) => t.type === "page").webSocketDebuggerUrl);
    await c.send("Page.enable");

    const open = async (url, width, height, dpr, clear) => {
      await c.send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: dpr, mobile: false });
      await c.send("Emulation.setDefaultBackgroundColorOverride", clear ? { color: { r: 0, g: 0, b: 0, a: 0 } } : {});
      const loaded = c.once("Page.loadEventFired");
      await c.send("Page.navigate", { url });
      await loaded;
    };
    const run = async (expression) => {
      const r = await c.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
      return r.result.value;
    };
    const shot = async (file) => {
      const r = await c.send("Page.captureScreenshot", { format: "png" });
      fs.writeFileSync(file, Buffer.from(r.data, "base64"));
    };
    const frameAt = async (name, t) => run(`(async () => { studio.render(${JSON.stringify(name)}, ${t}); await new Promise((r) => requestAnimationFrame(() => r())); return true; })()`);
    const studio = pathToFileURL(path.join(here, "studio.html")).href + "?render";

    for (const [name, job] of jobs) {
      const started = Date.now();
      if (job.kind === "logo") {
        const svg = path.join(here, "logo.svg");
        fs.writeFileSync(svg, logoSVG());
        await open(pathToFileURL(svg).href, 1024, 1024, 1, true);
        await c.send("Emulation.setDeviceMetricsOverride", { width: 512, height: 512, deviceScaleFactor: 2, mobile: false });
        await shot(path.join(here, "logo.png"));
        console.log(`logo: logo.svg, logo.png (${Date.now() - started} ms)`);
        continue;
      }
      await open(studio, 100, 100, 1, false);
      const sc = await run(`studio.ready.then(() => { const s = studio.scenes[${JSON.stringify(name)}]; return { w: s.w, h: s.h, dur: s.dur, still: s.still }; })`);
      await open(studio, sc.w, sc.h, job.dpr, false);
      await run("studio.ready");
      if (job.kind === "still") {
        await frameAt(name, sc.still);
        await shot(path.join(here, job.png));
        console.log(`${name}: ${job.png} (${Date.now() - started} ms)`);
        continue;
      }
      const frames = fs.mkdtempSync(path.join(os.tmpdir(), `clawd-${name}-`));
      const count = Math.round(sc.dur * FPS);
      for (let i = 0; i < count; i++) {
        await frameAt(name, i / FPS);
        await shot(path.join(frames, String(i).padStart(4, "0") + ".png"));
      }
      const input = ["-y", "-loglevel", "error", "-framerate", String(FPS), "-i", path.join(frames, "%04d.png")];
      const made = [];
      if (job.gif) {
        execFileSync("ffmpeg", [...input, "-vf",
          `scale=${sc.w}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=256:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle`,
          "-loop", "0", path.join(here, job.gif)]);
        made.push(job.gif);
      }
      if (job.mp4) {
        execFileSync("ffmpeg", [...input, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", "-preset", "slow", "-movflags", "+faststart", path.join(here, job.mp4)]);
        made.push(job.mp4);
      }
      fs.rmSync(frames, { recursive: true, force: true });
      console.log(`${name}: ${made.join(", ")} from ${count} frames (${Date.now() - started} ms)`);
    }
    c.close();
  } finally {
    chrome.kill();
    await sleep(300);
    fs.rmSync(profile, { recursive: true, force: true });
  }
}

main().catch((err) => { console.error(err.message || err); process.exit(1); });
