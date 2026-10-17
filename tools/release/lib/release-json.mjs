#!/usr/bin/env node
// JSON helpers for the release scripts (plain Node, no dependencies).
//
//   manifest <manifest.json> <version>          validate; print "platform<TAB>file<TAB>size<TAB>sha256" lines
//   compare  <gh-release.json> <manifest.json>  print "OK|MISSING|MISMATCH|NODIGEST<TAB>file" per expected asset
//   write    <gh-release.json> <manifest.json> <notes.md> <repo-url> <out release.json>
//   expect   <release.json> <tag>               print "file<TAB>url<TAB>sha256" from the website data
import { readFileSync, writeFileSync, existsSync } from 'node:fs';

const [cmd, ...args] = process.argv.slice(2);
const read = (p) => JSON.parse(readFileSync(p, 'utf8'));
const fail = (m) => {
  console.error(`release-json: ${m}`);
  process.exit(1);
};
const PLATFORMS = ['macos', 'windows'];

function loadManifest(p, version) {
  if (!existsSync(p)) fail(`${p} not found; run tools/release/package.sh first`);
  const m = read(p);
  if (version && m.version !== version) fail(`manifest is for ${m.version} but VERSION is ${version}; re-run package.sh`);
  if (!Array.isArray(m.assets)) fail('manifest has no assets');
  for (const pl of PLATFORMS) {
    if (!m.assets.some((a) => a.platform === pl)) fail(`manifest is missing the ${pl} asset; package both platforms (package.sh without --only)`);
  }
  for (const a of m.assets) {
    if (!/^[0-9a-f]{64}$/.test(a.sha256 ?? '')) fail(`bad sha256 for ${a.file}`);
    if (!Number.isInteger(a.size) || a.size <= 0) fail(`bad size for ${a.file}`);
  }
  return m;
}

function ghAssets(p) {
  const r = read(p);
  return { release: r, byName: new Map((r.assets ?? []).map((a) => [a.name, a])) };
}

switch (cmd) {
  case 'manifest': {
    const m = loadManifest(args[0], args[1]);
    for (const a of m.assets) console.log([a.platform, a.file, a.size, a.sha256].join('\t'));
    break;
  }
  case 'compare': {
    const { byName } = ghAssets(args[0]);
    const m = loadManifest(args[1]);
    for (const a of m.assets) {
      const g = byName.get(a.file);
      let s = 'OK';
      if (!g || (g.state && g.state !== 'uploaded')) s = 'MISSING';
      else if (g.size !== a.size) s = 'MISMATCH';
      else if (g.digest) s = g.digest === `sha256:${a.sha256}` ? 'OK' : 'MISMATCH';
      else s = 'NODIGEST';
      console.log(`${s}\t${a.file}`);
    }
    break;
  }
  case 'write': {
    const [ghFile, manFile, notesFile, repoUrl, outFile] = args;
    const { release, byName } = ghAssets(ghFile);
    const m = loadManifest(manFile);
    const version = m.version;
    const base = `${repoUrl}/releases/download/v${version}/`;
    let previous = null;
    if (existsSync(outFile)) {
      try {
        previous = read(outFile);
      } catch {
        previous = null;
      }
    }
    const assets = PLATFORMS.map((pl) => {
      const a = m.assets.find((x) => x.platform === pl);
      const g = byName.get(a.file);
      if (!g) fail(`GitHub release has no asset named ${a.file}`);
      if (g.size !== a.size) fail(`${a.file}: GitHub size ${g.size} != local ${a.size}`);
      if (g.digest && g.digest !== `sha256:${a.sha256}`) fail(`${a.file}: GitHub digest ${g.digest} != local sha256 ${a.sha256}`);
      if (g.url !== base + a.file) fail(`${a.file}: unexpected download URL ${g.url} (expected ${base + a.file})`);
      // keep the lead's "verified" flags only for the same version: a new build is unverified
      const prev = previous?.version === version ? previous.assets?.find((x) => x.platform === pl) : null;
      const verified = prev?.verified ?? (pl === 'macos' ? { arm64: false, x64: false } : false);
      return { platform: pl, label: a.label, file: a.file, url: g.url, size: a.size, sha256: a.sha256, verified };
    });
    const sums = [...byName.values()].find((g) => /-SHA256SUMS\.txt$/.test(g.name));
    const notes = readFileSync(notesFile, 'utf8').replace(/<!--[\s\S]*?-->/g, '').trim();
    const date = (release.publishedAt || new Date().toISOString()).slice(0, 10);
    const out = { version, date, notes, repo: repoUrl, checksums: sums ? sums.url : null, assets };
    writeFileSync(outFile, JSON.stringify(out, null, 2) + '\n');
    console.log(`release-json: wrote ${outFile} (v${version}, ${assets.length} assets, verified flags ${previous?.version === version ? 'kept' : 'reset to false'})`);
    break;
  }
  case 'expect': {
    const [relFile, tag] = args;
    const r = read(relFile);
    if (`v${r.version}` !== tag) fail(`website release.json is v${r.version}, not ${tag}`);
    if (!r.assets?.length) fail('website release.json lists no assets');
    for (const a of r.assets) console.log([a.file, a.url, a.sha256].join('\t'));
    break;
  }
  default:
    fail(`unknown command "${cmd ?? ''}" (manifest | compare | write | expect)`);
}
