// Clawd Pet's drawing code, shared by the website (index.html) and the brand studio
// (brand/studio.html), so every picture of Clawd and the Codex pet comes from one place.
window.ClawdArt = (() => {
  "use strict";
  const here = document.currentScript ? document.currentScript.src : location.href;
  const art = { reduce: false }; // set reduce to true to hold every animation still

  const C = { clawd: "#D77757", light: "#EC9978", dark: "#B05B3F", eye: "#171312", blush: "#FF99A3" };
  const CODEX = { body: "#5D88F2", dark: "#1F2360" }; // the Codex pet's blue and its screen
  const POP = "#FFCC52";
  const COMP = "#2F2F33";
  const MONO = '"JetBrains Mono", ui-monospace, Menlo, monospace';
  const PIXEL = '"Jersey 10", "Courier New", monospace';

  // ---------- canvas helpers ----------
  function prep(cv) {
    const dpr = Math.max(1, Math.round(window.devicePixelRatio || 1));
    const r = cv.getBoundingClientRect();
    const w = Math.max(1, Math.round(r.width)), h = Math.max(1, Math.round(r.height));
    if (cv.width !== w * dpr || cv.height !== h * dpr) { cv.width = w * dpr; cv.height = h * dpr; }
    const ctx = cv.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.imageSmoothingEnabled = false;
    ctx.clearRect(0, 0, w, h);
    return { ctx, w, h };
  }
  function rr(ctx, x, y, w, h, r) {
    ctx.beginPath();
    if (!(w > 0 && h > 0)) return; // too narrow to draw: an empty path fills nothing
    r = Math.max(0, Math.min(r, w / 2, h / 2));
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  // ---------- Clawd: a port of the app's ClawdPainter ----------
  function paintClawd(ctx, px, look, t, x0, y0) {
    const frame = Math.floor(t * 8);
    const ox = 1, oy = 3;
    const mood = look.mood;
    const walking = look.walk || mood === "thinking" || mood === "working";
    let bob = 0;
    if (mood === "happy") bob = -Math.round(Math.abs(Math.sin(t * 7.5)) * 2.4);
    else if (mood === "thinking" || mood === "working" || mood === "sleepy" || mood === "dizzy") bob = 0;
    else if (mood === "listening" || mood === "alert") bob = Math.sin(t * 5) > 0 ? -1 : 0;
    else bob = Math.sin(t * 2.2) > 0.55 ? -1 : 0;
    if (look.calm) bob = 0;
    const fill = (c, r, w, h, color) => {
      ctx.fillStyle = color;
      ctx.fillRect(Math.round(x0 + (ox + c) * px), Math.round(y0 + (oy + bob + r) * px), w * px, h * px);
    };
    const sw = (11 + bob) * px;
    ctx.fillStyle = "rgba(0,0,0,0.28)";
    ctx.beginPath();
    ctx.ellipse(x0 + (ox + 7) * px, y0 + (oy + 9) * px + px * 0.2, sw / 2, px * 0.55, 0, 0, Math.PI * 2);
    ctx.fill();
    [3, 5, 8, 10].forEach((c, i) => {
      const lifted = walking && ((i % 2 === 0) === (frame % 2 === 0));
      fill(c, 7, 1, 1, C.clawd);
      if (!lifted) fill(c, 8, 1, 1, C.dark);
    });
    let L = 3, Rr = 3;
    if (mood === "listening" || mood === "alert") { L = 2; Rr = 2; }
    else if (mood === "happy") { L = frame % 2 === 0 ? 2 : 3; Rr = frame % 2 === 0 ? 3 : 2; }
    else if (mood === "speaking") { Rr = Math.floor(frame / 2) % 2 === 0 ? 3 : 2; }
    else if (mood === "sleepy" || mood === "dizzy") { L = 4; Rr = 4; }
    else if (look.hover) { Rr = Math.floor(frame / 2) % 2 === 0 ? 2 : 3; }
    fill(0, L, 2, 1, C.clawd); fill(0, L + 1, 2, 1, C.dark);
    fill(12, Rr, 2, 1, C.clawd); fill(12, Rr + 1, 2, 1, C.dark);
    fill(2, 0, 10, 7, C.clawd);
    fill(3, 0, 8, 1, C.light);
    fill(2, 1, 1, 3, C.light);
    fill(2, 6, 10, 1, C.dark);
    const blink = (t % 3.9) < 0.14;
    const lx = 4, rx = 9, E = C.eye;
    switch (mood) {
      case "happy":
        for (const x of [lx, rx]) { fill(x - 1, 3, 1, 1, E); fill(x, 2, 1, 1, E); fill(x + 1, 3, 1, 1, E); }
        break;
      case "sleepy":
        fill(lx - 1, 3, 2, 1, E); fill(rx, 3, 2, 1, E);
        break;
      case "dizzy":
        for (const x of [lx, rx]) { fill(x - 1, 1, 1, 1, E); fill(x + 1, 1, 1, 1, E); fill(x, 2, 1, 1, E); fill(x - 1, 3, 1, 1, E); fill(x + 1, 3, 1, 1, E); }
        break;
      case "listening":
      case "alert":
        if (blink) { fill(lx, 3, 1, 1, E); fill(rx, 3, 1, 1, E); } else { fill(lx, 1, 1, 3, E); fill(rx, 1, 1, 3, E); }
        break;
      case "thinking":
      case "working":
        if (blink) { fill(lx + 1, 2, 1, 1, E); fill(rx + 1, 2, 1, 1, E); } else { fill(lx + 1, 1, 1, 2, E); fill(rx + 1, 1, 1, 2, E); }
        break;
      default: {
        const ph = t % 9;
        const dx = look.lookDX !== undefined ? look.lookDX : (look.hover ? 0 : (ph < 1.4 ? -1 : (ph > 5 && ph < 6.3 ? 1 : 0)));
        if (blink) { fill(lx + dx, 3, 1, 1, E); fill(rx + dx, 3, 1, 1, E); } else { fill(lx + dx, 2, 1, 2, E); fill(rx + dx, 2, 1, 2, E); }
      }
    }
    if (mood === "speaking" && look.mouth) fill(6, 5, 2, 1, E);
    if (mood === "happy" || look.hover) { fill(3, 4, 1, 1, C.blush); fill(10, 4, 1, 1, C.blush); }
  }

  function paintGlyph(ctx, px, mood, t, x0, y0) {
    const s = px / 6, f = Math.floor(t * 9);
    const txt = (str, x, y, size, color, weight) => {
      ctx.font = `${weight || 700} ${size * s}px ui-sans-serif, system-ui, sans-serif`;
      ctx.fillStyle = color;
      ctx.textBaseline = "top";
      ctx.textAlign = "left";
      ctx.fillText(str, x0 + x * s, y0 + y * s);
    };
    ctx.save();
    if (mood === "thinking" || mood === "working") {
      const seq = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];
      txt(seq[f % seq.length], 78, -2, 18, C.clawd);
    } else if (mood === "alert") {
      txt("!", 84, Math.sin(t * 8) > 0 ? -5 : -2, 20, POP, 900);
    } else if (mood === "sleepy") {
      const d = (t * 5) % 14;
      ctx.globalAlpha = 0.75;
      txt("z", 80 + d * 0.4, 10 - d, 10, "#8790A3", 800);
      if (d > 4) txt("Z", 86 + d * 0.3, 2 - d * 0.6, 13, "#8790A3", 800);
    } else if (mood === "happy") {
      ctx.globalAlpha = f % 4 < 2 ? 1 : 0.35;
      txt("✦", 82, 2, 13, POP);
    } else if (mood === "dizzy") {
      for (let i = 0; i < 3; i++) {
        const a = t * 5 + i * 2.1;
        txt("★", 43 + Math.cos(a) * 28, 6 + Math.sin(a) * 5, 10, POP);
      }
    }
    ctx.restore();
  }

  // The real Codex pet: frames from the default pet's sprite sheet in the Codex app.
  // codex-pet.webp holds five of its rows, 144×156 per frame. Frame timings follow
  // the Codex pet's own animation table; idle holds its first frame between blinks.
  const codexImg = new Image();
  codexImg.decoding = "async";
  codexImg.src = new URL("codex-pet.webp", here).href;
  const CODEX_CELL = { w: 144, h: 156, feet: 200 / 208 };
  const CODEX_ANIMS = {
    idle: { row: 0, still: 0, ms: [2600, 110, 110, 140, 140, 320] },
    wave: { row: 1, still: 2, ms: [140, 140, 140, 280] },
    happy: { row: 2, still: 2, ms: [140, 140, 140, 140, 280] },
    dizzy: { row: 3, still: 2, ms: [140, 140, 140, 140, 140, 140, 140, 240] },
    pleased: { row: 4, still: 1, ms: [150, 150, 150, 150, 150, 280] },
  };
  function codexFrame(anim, t) {
    if (art.reduce) return anim.still;
    const total = anim.ms.reduce((a, b) => a + b, 0);
    let x = ((t * 1000) % total + total) % total;
    for (let i = 0; i < anim.ms.length; i++) { if (x < anim.ms[i]) return i; x -= anim.ms[i]; }
    return 0;
  }
  // cx: its centre; ground: where its feet stand; h: the drawn height of one frame.
  function paintCodex(ctx, mood, t, cx, ground, h) {
    const anim = CODEX_ANIMS[mood] || CODEX_ANIMS.idle;
    const w = h * CODEX_CELL.w / CODEX_CELL.h;
    ctx.fillStyle = "rgba(0,0,0,0.28)";
    ctx.beginPath();
    ctx.ellipse(cx, ground, w * 0.3, Math.max(2, h * 0.035), 0, 0, Math.PI * 2);
    ctx.fill();
    if (!codexImg.complete || !codexImg.naturalWidth) return;
    ctx.save();
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(codexImg, codexFrame(anim, t) * CODEX_CELL.w, anim.row * CODEX_CELL.h, CODEX_CELL.w, CODEX_CELL.h,
      cx - w / 2, ground - h * CODEX_CELL.feet, w, h);
    ctx.restore();
  }

  // The cartoon brawl, ported from the app's BrawlCloud.
  const POPS = ["POW!", "BONK!", "ZAP!", "WHAM!", "✳", "</>", "BAM!", "KAPOW!", "{ }", "★"];
  function rnd(a, b, seed) {
    let x = (Math.imul(a, 73856093) ^ Math.imul(b, 19349663) ^ Math.imul(seed, 83492791)) >>> 0;
    x ^= x >>> 13;
    x = Math.imul(x, 0x5bd1e995) >>> 0;
    x = (x ^ (x >>> 15)) >>> 0; // keep it unsigned, or the index can go negative
    return (x % 10000) / 10000;
  }
  function paintCloud(ctx, t, cx, cy, s, seed) {
    const f = Math.floor(t * 12);
    const poof = Math.max(0, (t - 2.85) / 0.6);
    const grow = Math.min(1, t / 0.18);
    const alpha = 1 - Math.min(1, poof);
    if (alpha <= 0) return;
    const scale = (0.4 + 0.6 * grow) * (1 + poof * 0.5) * s;
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.translate(cx + Math.sin(t * 31) * 5 * s, cy + Math.cos(t * 23) * 3 * s);
    ctx.scale(scale, scale);
    for (let i = 0; i < 6; i++) {
      const a = (i / 6 + rnd(Math.floor(f / 2), i, seed) * 0.14) * Math.PI * 2;
      const codex = i % 2 === 1;
      ctx.save();
      ctx.translate(Math.cos(a) * 72, Math.sin(a) * 50);
      ctx.rotate(a - Math.PI / 2);
      ctx.fillStyle = codex ? CODEX.body : C.clawd;
      ctx.fillRect(-6, -15, 12, 22);
      ctx.fillStyle = codex ? CODEX.dark : C.dark;
      ctx.fillRect(-7, 7, 14, 8);
      ctx.restore();
    }
    for (let i = 0; i < 11; i++) {
      const a = (i / 11) * Math.PI * 2 + t * 0.9;
      const r = 38 + 10 * Math.sin(t * 11 + i * 1.7);
      const d = 58 + (i % 4) * 9 + 6 * Math.sin(t * 13 + i);
      ctx.beginPath();
      ctx.arc(Math.cos(a) * r * 1.35, Math.sin(a) * r * 0.8, d / 2, 0, Math.PI * 2);
      ctx.fillStyle = i % 3 === 0 ? "#CDCFD5" : "#F5F5F7";
      ctx.fill();
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = "rgba(0,0,0,0.12)";
      ctx.stroke();
    }
    ctx.beginPath();
    ctx.ellipse(0, 0, 60, 41, 0, 0, Math.PI * 2);
    ctx.fillStyle = "#F7F7F9";
    ctx.fill();
    for (let k = 0; k < 3; k++) {
      const period = 0.42, phase = t + k * 0.14;
      const cyc = Math.floor(phase / period);
      const local = (phase % period) / period;
      const word = POPS[Math.floor(rnd(cyc, k + 7, seed) * POPS.length) % POPS.length];
      const a = rnd(cyc, k + 31, seed) * Math.PI * 2;
      const sc = local < 0.25 ? (local / 0.25) * 1.25 : 1.25 - (local - 0.25) * 0.35;
      const op = local > 0.8 ? (1 - local) / 0.2 : 1;
      ctx.save();
      ctx.globalAlpha = alpha * op;
      ctx.translate(Math.cos(a) * 84, Math.sin(a) * 54 - 10);
      ctx.rotate(((rnd(cyc, k, seed) - 0.5) * 40 * Math.PI) / 180);
      ctx.scale(sc, sc);
      ctx.font = `400 25px ${PIXEL}`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.lineJoin = "round";
      ctx.lineWidth = 5;
      ctx.strokeStyle = "#1B1512";
      ctx.strokeText(word, 0, 0);
      ctx.fillStyle = k === 1 ? C.clawd : POP;
      ctx.fillText(word, 0, 0);
      ctx.restore();
    }
    ctx.restore();
  }

  function paintTag(ctx, text, cx, bottom, s, W) {
    ctx.save();
    const size = Math.max(10.5, 12.5 * s);
    ctx.font = `600 ${size}px ${MONO}`;
    const w = ctx.measureText(text).width + 22 * Math.max(s, 0.8), h = size * 2;
    let x = cx - w / 2;
    x = Math.max(8, Math.min(W - w - 8, x));
    const y = bottom - h;
    rr(ctx, x, y, w, h, h / 2);
    ctx.fillStyle = COMP;
    ctx.shadowColor = "rgba(0,0,0,0.3)";
    ctx.shadowBlur = 10;
    ctx.shadowOffsetY = 3;
    ctx.fill();
    ctx.shadowColor = "transparent";
    ctx.strokeStyle = "rgba(255,255,255,0.12)";
    ctx.lineWidth = 1;
    ctx.stroke();
    ctx.fillStyle = "#F3F3F5";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(text, x + w / 2, y + h / 2 + 0.5);
    ctx.restore();
  }

  function paintBar(ctx, x, y, w, h, s, text, textX) {
    ctx.save();
    rr(ctx, x, y, w, h, 24 * s);
    ctx.fillStyle = COMP;
    ctx.shadowColor = "rgba(0,0,0,0.30)";
    ctx.shadowBlur = 18;
    ctx.shadowOffsetY = 6;
    ctx.fill();
    ctx.shadowColor = "transparent";
    ctx.strokeStyle = "rgba(255,255,255,0.10)";
    ctx.lineWidth = 1;
    ctx.stroke();
    if (text) {
      ctx.font = `500 ${Math.max(11, 13.5 * s)}px ${MONO}`;
      if (textX + ctx.measureText(text).width < x + w - 14 * s) {
        ctx.fillStyle = "rgba(255,255,255,0.44)";
        ctx.textBaseline = "middle";
        ctx.fillText(text, textX, y + 26 * s);
      }
    }
    ctx.restore();
  }

  return Object.assign(art, { C, CODEX, POP, COMP, MONO, PIXEL, prep, rr, paintClawd, paintGlyph, paintCodex, codexImg, paintCloud, paintTag, paintBar });
})();
