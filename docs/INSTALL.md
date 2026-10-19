# Installing Wildmere

Wildmere is free. Download it from the website or from the
[GitHub releases page](https://github.com/tamoghnakabi-wq/wildmere/releases).

It is an independent release, so the macOS build is not notarized by Apple and the
Windows build is not code-signed. Your system will ask you to confirm the first launch.
That is expected; the steps below show exactly what to click.

## System requirements

| | Minimum | Recommended |
|---|---|---|
| **macOS** | macOS 12 Monterey or later, Apple Silicon or Intel Mac with a Metal GPU, 8 GB RAM, about 1 GB free | Apple M1 or newer |
| **Windows** | Windows 10 or 11 (64-bit), DirectX 11 GPU, 8 GB RAM, about 1 GB free | GeForce GTX 1060-class GPU or better |

Wildmere is developed on an Apple Silicon Mac. **Windows and Intel Macs are experimental**
until the website marks them as verified: they have passed automated checks at most.
Reports from those machines are very welcome (see "Problems" below).

## macOS

1. Download `Wildmere-<version>-macOS.zip` and double-click it to unzip (Safari may already
   have unzipped it).
2. Drag **Wildmere.app** into your **Applications** folder.
3. Open Wildmere. macOS says it cannot verify the app and does not open it. Click **Done**
   (not "Move to Bin").
4. Open **System Settings › Privacy & Security**, scroll down to **Security** and click
   **Open Anyway** next to the message about Wildmere. Confirm with your password or Touch ID,
   then choose **Open Anyway** once more. You only need to do this the first time.

On macOS 12 to 14 you can instead Control-click the app in Finder and choose **Open**.
Newer macOS versions require the System Settings step.

**Terminal alternative.** This removes the download-quarantine flag so macOS opens the app
without asking:

```sh
xattr -dr com.apple.quarantine /Applications/Wildmere.app
```

If macOS ever says the app "is damaged and can't be opened", the download was corrupted:
delete it, download again and check the checksum (below).

## Windows

1. Download `Wildmere-<version>-Windows-x64.zip`.
2. Right-click the zip, choose **Extract All…**, and pick a folder such as
   `Documents\Games`. Do not run the game from inside the zip.
3. Open the extracted **Wildmere** folder and run **Wildmere.exe**. Keep all files in that
   folder together: the game needs `Wildmere_Data` and `UnityPlayer.dll` next to the exe.
4. If SmartScreen shows "Windows protected your PC", click **More info**, then **Run anyway**.
   This appears because the build is not code-signed.

Tip: right-click the zip › **Properties** › tick **Unblock** › **OK** before extracting, and
Windows will not ask at all.

## Check your download (optional)

Each download has a SHA-256 checksum, shown next to the download button on the website and
listed in `Wildmere-<version>-SHA256SUMS.txt` on the release page. Compare it with:

macOS (Terminal):

```sh
shasum -a 256 ~/Downloads/Wildmere-<version>-macOS.zip
```

Windows (PowerShell, in your Downloads folder):

```powershell
Get-FileHash .\Wildmere-<version>-Windows-x64.zip -Algorithm SHA256
```

If the value differs, delete the file and download it again.

## Updating and uninstalling

- **Update:** from 0.1.3 on, Wildmere updates itself. It checks for a new version shortly
  after launch (and every few hours while running), downloads it in the background, and
  installs it when you quit, or straight away with **Restart to update** on the title screen.
  Before installing, it checks the update's signature against Wildmere's release key and the
  zip's exact size and SHA-256; anything that doesn't match is discarded. The old version is
  kept until the new one is in place, and restored if the swap fails.
  Turn off automatic downloads in **Settings › Updates** (you can still press **Check now**).
  Versions 0.1.0–0.1.2 can't update themselves: download the current version once by hand.
  Self-updating needs a folder you can write to: on macOS move Wildmere.app into Applications
  (an app opened straight from a download, without moving it, can't replace itself); on
  Windows don't put the Wildmere folder in `C:\Program Files`. The game says when this is
  the problem. You can always update by hand by replacing the app or folder.
- **Uninstall:** delete `Wildmere.app` from Applications, or delete the Wildmere folder on
  Windows. Saved worlds may remain in your user data folder afterwards.

## Problems

Open an issue at <https://github.com/tamoghnakabi-wq/wildmere/issues> with your OS version,
your Mac or PC model (and GPU on Windows), and what happened. On Windows the game's log is
`Player.log` in `%USERPROFILE%\AppData\LocalLow\<company>\Wildmere\`; on macOS it is
`~/Library/Logs/<company>/Wildmere/Player.log`.
