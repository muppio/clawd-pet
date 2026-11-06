#!/usr/bin/env node
// Pins the Windows app that /pet downloads. After a GitHub release is published and the
// "Windows app" workflow has attached ClawdPet-win-x64.zip and VERSION to it, run:
//
//   node windows/pin-release.mjs v0.3.0 [owner/repo]
//
// It downloads both, and writes app/dist-windows/release.json with the zip's URL, SHA-256,
// size and build id. Commit that file: /pet only installs a download that matches it.
import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const [tag, repo = "muppio/clawd-pet"] = process.argv.slice(2);
if (!tag) {
  console.error("usage: node windows/pin-release.mjs <tag> [owner/repo]");
  process.exit(1);
}
const base = `https://github.com/${repo}/releases/download/${tag}`;

async function get(name) {
  const res = await fetch(`${base}/${name}`);
  if (!res.ok) throw new Error(`${name}: HTTP ${res.status}. Has the Windows workflow attached it to ${tag} yet?`);
  return Buffer.from(await res.arrayBuffer());
}

try {
  const zip = await get("ClawdPet-win-x64.zip");
  const buildId = (await get("VERSION")).toString("utf8").replace(/^﻿/, "").trim();
  if (!/^[0-9a-f]{16}$/i.test(buildId)) throw new Error(`VERSION doesn't look like a build id: "${buildId}"`);
  const release = {
    buildId,
    url: `${base}/ClawdPet-win-x64.zip`,
    sha256: createHash("sha256").update(zip).digest("hex"),
    size: zip.length,
  };
  const out = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "app", "dist-windows", "release.json");
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, JSON.stringify(release, null, 2) + "\n");
  console.log(`Pinned ${tag}: build ${buildId}, ${(zip.length / 1048576).toFixed(1)} MB, sha256 ${release.sha256}`);
} catch (err) {
  console.error(err.message || err);
  process.exit(1);
}
