# Releasing Wildmere

How a build gets from this Mac to players: local Unity build, zip, GitHub release,
website update, then automated download tests on real Windows and macOS machines.

```
tools/build.sh                       Build/mac/Wildmere.app, Build/win/
  -> tools/release/package.sh        dist/*.zip, SHA256SUMS, manifest.json
  -> tools/release/verify-download.sh --local      pre-flight on this Mac
  -> tools/release/publish.sh        GitHub release v<ver>, rewrites website/src/data/release.json
  -> git commit + push               Vercel redeploys the site with download buttons
  -> verify-release.yml (automatic)  downloads + self-test on windows-latest / macos-latest
  -> tools/release/verify-download.sh v<ver>       same check on this Mac
```

Builds are made locally because a Unity licence in CI needs the owner's Unity
credentials (see "Automated builds" at the end for the optional route).

## One-time setup

### GitHub

- Public repository `tamoghnakabi-wq/wildmere`, `main` pushed.
- `gh auth status` must show an account with write access (publish.sh uses `gh`).
- Optional: Settings > General > "Immutable releases". Works with publish.sh, which
  uploads to a draft and publishes only after every digest matches; once published,
  assets can never be swapped (publish.sh refuses to try anyway).

### Vercel (website)

Create a **new, separate** Vercel project for this repo. Do not reuse or touch the
existing `wren-agents` or `overdrive-league` projects.

| Setting | Value |
|---|---|
| Root Directory | `website` |
| Framework preset | Astro (also pinned in `website/vercel.json`) |
| Install / Build / Output | `npm ci` / `npm run build` / `dist` (from `website/vercel.json`) |
| Node.js version | 24.x (the site needs Node >= 22.22; it is developed on Node 26) |
| Environment variables | none needed. Optional `SITE_URL=https://your.domain` for canonical/OG URLs; otherwise Vercel's production domain is used automatically. **Never set `WILDMERE_DEMO`** (the build fails on purpose if you do). |
| Git LFS | leave off. The site ships `.webp`/`.webm` and generates its PNGs at build time. Only turn it on if you commit PNG/JPG/MP4 media to `website/public/` (the repo's `.gitattributes` routes those to LFS). |

Every push to `main` that touches `website/**` redeploys; pull requests get previews.

### Update signing key (in-game auto-update)

The game only installs updates whose manifest (`wildmere-update.json`, attached to every
release) carries a valid RSA-SHA256 signature from the release key.

- Private key: `.secrets/wildmere-update-key.pem` (RSA-3072, gitignored, `chmod 600`).
  **Back it up somewhere safe and offline.** It is never committed, never put in GitHub
  Actions secrets, and never leaves this machine; `publish.sh` signs locally.
- Public key: `tools/release/update-public.pem`, also compiled into
  `Assets/Scripts/Core/Updater.cs` (`kModulus`). The two must match.
- If the private key is lost or leaks: generate a new pair
  (`openssl genrsa -out .secrets/wildmere-update-key.pem 3072` and
  `openssl rsa -in .secrets/wildmere-update-key.pem -pubout -out tools/release/update-public.pem`),
  put the new modulus in `Updater.cs`, and release. Installed copies keep trusting the old key,
  so players have to download that one release by hand.
- The feed is `releases/latest/download/wildmere-update.json`, so publishing a release is
  what ships the update. Drafts and prereleases are never "latest".

Updater tests (run after touching `Updater.cs`):
- `tools/release/test-updater-mac.sh`: local end-to-end on this Mac (good update and
  relaunch, tampered manifest, bad checksum, same version, install on quit).
- Windows: `tools/release/stage-updater-test.sh <prerelease tag>` (the prerelease must
  already hold the current Windows zip), then
  `gh workflow run updater-windows.yml -f tag=<tag>`.

## Cutting a release

1. **Version.** Set `VERSION` (semver, e.g. `0.1.0`). The macOS `Info.plist`
   `CFBundleShortVersionString` must match it: package.sh refuses stale builds.
   Before the first release also set `PlayerSettings.productName` = `Wildmere` and a real
   `companyName` (both are still the URP-template defaults in `ProjectSettings.asset`);
   they decide the macOS executable name, the window title and the save-data folder.

2. **Build** both platforms (`tools/build.sh`, i.e. `BuildScript.BuildMac` and
   `BuildScript.BuildWindows`). The mac app must be ad-hoc signed
   (`codesign --force --deep --sign - Build/mac/Wildmere.app`) or Apple Silicon will not run it.

3. **Package.**

   ```sh
   tools/release/package.sh
   ```

   Checks, and refuses if anything is missing or wrong: the `.app` has `Info.plist`, its
   executable and `Contents/Resources/Data`; version matches `VERSION`; the signature
   verifies; the Windows folder has `Wildmere.exe` (a real PE file), `UnityPlayer.dll`,
   `Wildmere_Data/globalgamemanagers` and `MonoBleedingEdge/` (or `GameAssembly.dll`).
   Writes `dist/Wildmere-<ver>-macOS.zip` (ditto, bundle kept intact),
   `dist/Wildmere-<ver>-Windows-x64.zip` (everything under a top-level `Wildmere/` folder,
   minus Unity's `*_DoNotShip` / `*_ButDontShipItWithYourGame` folders),
   `dist/Wildmere-<ver>-SHA256SUMS.txt` and `dist/manifest.json` (sizes, SHA-256, mac
   architectures). The macOS label on the site comes from `lipo`: a non-Universal build is
   labelled "Apple Silicon" only.

   Zips are written without macOS extended attributes (`--norsrc --noextattr --noqtn --noacl`);
   otherwise `ditto` embeds `._*` AppleDouble files that break the app's signature when the
   zip is opened with anything but Archive Utility. package.sh re-extracts the mac zip with
   plain `unzip` and re-verifies the signature to prove it.

4. **Pre-flight on this Mac.**

   ```sh
   tools/release/verify-download.sh --local            # checksums, layout, signature, self-test
   tools/release/verify-download.sh --local --rosetta  # also run the Intel slice under Rosetta 2
   ```

   The self-test runs `Wildmere.app/Contents/MacOS/Wildmere -batchmode -nographics -selftest -logFile -`
   (with the 2-slot Unity lock from `tools/unity.sh`) and needs exit code 0 plus a
   `WM_SELFTEST PASS` line. Default timeout 600 s (`--timeout N`).

5. **Release notes.** `cp releases/TEMPLATE.md releases/v<ver>.md`, replace every `TODO` line
   (see `releases/README.md`). The same text is the GitHub release body and the website's
   "What's new" block.

6. **Commit and push** the code you built from (publish.sh tags `HEAD` and checks the
   commit exists on GitHub).

7. **Publish.**

   ```sh
   tools/release/publish.sh --dry-run   # every check, prints the gh commands
   tools/release/publish.sh
   ```

   It verifies `dist/` against the manifest, creates release `v<ver>` as a **draft** with the
   two zips + SHA256SUMS (`gh release create v<ver> ... --notes-file releases/v<ver>.md --draft`),
   re-reads the release, compares GitHub's SHA-256 digests with the local files, and only
   then publishes it. That ordering means `verify-release.yml` (triggered by
   `release: published`) never sees a half-uploaded release.

   Re-running is safe: missing assets are uploaded, matching ones are left alone, a
   mismatching asset on a draft is replaced, and a mismatching asset on a **published**
   release aborts (published binaries are never replaced; bump `VERSION` instead).

   Finally it rewrites `website/src/data/release.json` from the real asset URLs, sizes and
   SHA-256, validates it, and prints the commands to commit it:

   ```sh
   git add website/src/data/release.json
   git commit -m "Website: downloads for v<ver>"
   git push origin HEAD
   ```

   Vercel redeploys and the download buttons go live.

8. **Watch the automated download test.**

   ```sh
   gh run list --repo tamoghnakabi-wq/wildmere --workflow verify-release.yml --limit 3
   gh run watch <run-id> --repo tamoghnakabi-wq/wildmere
   gh run download <run-id> --repo tamoghnakabi-wq/wildmere   # logs + Windows screenshot
   ```

   Re-run by hand any time: Actions > "Verify release downloads" > Run workflow > tag, or
   `gh workflow run verify-release.yml --repo tamoghnakabi-wq/wildmere -f tag=v<ver>`.

9. **Test the real download on this Mac** (uses the website's URLs and checksums):

   ```sh
   tools/release/verify-download.sh v<ver>
   tools/release/verify-download.sh v<ver> --gh     # via gh + SHA256SUMS instead
   ```

10. **Mark platforms verified** once someone has actually played the build on that hardware:
    edit `"verified"` in `website/src/data/release.json`, commit, push. Until then the site
    shows Windows and Intel Macs as "Experimental".

## Website data (edit JSON, not components)

All in `website/src/data/`. `npm run build` runs `scripts/check-data.mjs` first and fails
on anything invalid, so a broken link never deploys.

### `release.json`

```json
{
  "version": "0.1.0",
  "date": "2026-10-20",
  "notes": "<markdown, from releases/v0.1.0.md>",
  "repo": "https://github.com/tamoghnakabi-wq/wildmere",
  "checksums": "https://github.com/.../releases/download/v0.1.0/Wildmere-0.1.0-SHA256SUMS.txt",
  "assets": [
    { "platform": "macos", "label": "macOS (Apple Silicon & Intel)", "file": "Wildmere-0.1.0-macOS.zip",
      "url": "https://github.com/tamoghnakabi-wq/wildmere/releases/download/v0.1.0/Wildmere-0.1.0-macOS.zip",
      "size": 123456789, "sha256": "<64 hex>", "verified": { "arm64": true, "x64": false } },
    { "platform": "windows", "label": "Windows 10/11 (64-bit)", "file": "Wildmere-0.1.0-Windows-x64.zip",
      "url": "https://github.com/tamoghnakabi-wq/wildmere/releases/download/v0.1.0/Wildmere-0.1.0-Windows-x64.zip",
      "size": 123456789, "sha256": "<64 hex>", "verified": false }
  ]
}
```

- `assets: []` (as shipped now) shows a disabled "Builds coming soon" state. There is never a
  placeholder link: the checker requires each `url` to be exactly
  `<repo>/releases/download/v<version>/<file>`, a real size and a 64-hex SHA-256.
- `date` may be `null` until there are assets.
- `verified`: `true`/`false`, or for macOS `{"arm64": bool, "x64": bool}`. Missing or false
  means "Experimental" on the site (Windows; Intel for macOS). publish.sh keeps the flags when
  re-run for the same version and resets them to `false` for a new version.
- The visitor's OS (macOS / Windows) is detected in the browser and its card is highlighted.

### `media.json`

```json
{
  "hero": { "image": "/media/hero.webp", "video": ["/media/hero.webm"], "alt": "...", "caption": "In-game footage, v0.1.0" },
  "gallery": [ { "src": "/media/shots/lake.webp", "caption": "...", "alt": "...", "width": 2560, "height": 1440 } ]
}
```

- `hero: null` uses the code-drawn landscape (labelled "Illustration, not in-game footage").
  With `video`, `image` is the poster and the still shown to reduced-motion visitors.
- `gallery: []` hides the gallery section and its nav link.
- Files live in `website/public/media/`. Only real in-game captures: no stock, AI or
  third-party game imagery. Import them with:

  ```sh
  cd website
  npm run import-shots -- ~/path/to/shots/*.png           # -> public/media/shots/<name>.webp + <name>-sm.webp
  npm run import-shots -- --hero ~/path/to/vista.png      # -> public/media/hero.webp
  npm run import-shots -- --hero-video clip.mov --hero vista.png   # -> hero.webm (VP9, 20 s, silent; needs ffmpeg)
  ```

  then write a real `caption` and a specific `alt` for each entry. WebP/WebM matter: the
  repo's `.gitattributes` sends PNG/JPG/MP4 to Git LFS, which Vercel does not fetch unless
  enabled, and the checker fails the build on LFS pointer files.

  The game can produce captures itself: `Wildmere -shots 5,12,30 -shotdir ~/Desktop/shots -quitafter 35 -seed <n>`.

### `site.json`

Tagline, pitch, the "Every block is yours to move" copy, the three spotlights, the six feature
cards, system requirements and footer credits. **Check the feature copy against the
shipping build before launch**; it was written from the architecture contracts (biomes,
blocks, workbench/furnace, torches and glowcaps, wildlife, clouds/water/ambience, Universal
macOS build), not from playing it. A spotlight's `"image"` can point at a real screenshot to
replace its illustration.

### Previewing

```sh
cd website
npm ci
npm run dev            # http://localhost:4321
npm run build && npm run preview
npm run build:demo     # dist-demo/ with fake assets + gallery, to see the "downloads live" layout
```

Demo builds show a "DEMO DATA" ribbon, disable their download buttons, are `noindex`, and
refuse to build on Vercel.

## Automated builds (optional)

`.github/workflows/build.yml` builds both platforms with GameCI on GitHub's Linux runners,
ad-hoc signs the mac app on a macOS runner, runs `package.sh` and the self-test, and uploads
`wildmere-dist-<ver>` as a workflow artifact. Publishing stays manual (download the artifact
into `dist/` and run `publish.sh`). It is skipped, with a notice, until three repository
secrets exist:

| Secret | Value |
|---|---|
| `UNITY_LICENSE` | the full contents of an activated licence file. On a Mac where Unity Hub is signed in with a Personal licence: `/Library/Application Support/Unity/Unity_lic.ulf` |
| `UNITY_EMAIL` | the Unity ID e-mail |
| `UNITY_PASSWORD` | the Unity ID password |

These are the account owner's credentials, so the owner adds them personally (repository
Settings > Secrets and variables > Actions, or `gh secret set UNITY_LICENSE < Unity_lic.ulf`).
Never commit them or paste them into files. The workflow has not run yet: Unity's
licensing rules for Personal licences in CI change from time to time, and building the
macOS player on Linux has not been tried with this project. Expect to adjust it on the
first run (see https://game.ci/docs/github/activation).

## When something goes wrong

| Situation | Fix |
|---|---|
| publish.sh stopped halfway | run it again; it resumes. |
| wrong file on a **draft** release | re-run `package.sh` then `publish.sh`; mismatching draft assets are replaced. |
| wrong file on a **published** release | do not replace it. Bump `VERSION`, rebuild, release again; mark the bad release as a pre-release or delete it on GitHub by hand. |
| verify-release.yml fails on Windows | download the run's artifact: `logs/selftest.log`, `logs/graphics.log`, `shots/`. The runner has no GPU (software D3D11 / WARP), so a graphics-only failure there is weaker evidence than a self-test failure. |
| site still says "Builds coming soon" | `website/src/data/release.json` was not committed/pushed, or the Vercel build failed the data check (see the deployment log). |
| macOS says the app is "damaged" | the signature broke; re-sign (`codesign --force --deep --sign -`) and re-package. package.sh refuses unsigned builds for this reason. |
