# Clawd Pet for Windows (developer notes)

The same pet as `app/ClawdPet.swift`, ported to C# / .NET 8 WPF. Users never need this folder: `/pet` downloads the prebuilt app from the GitHub release that `app/dist-windows/release.json` pins, and checks its SHA-256. This page is for building, releasing and finishing the port.

## Files

| File | What it is |
| --- | --- |
| `Program.cs` | Entry point, single instance (a second launch forwards its `clawdpet://` link over a named pipe), HKCU registration for `clawdpet://` and Open at login, opening the Claude desktop app. |
| `PetWindow.cs` | The transparent, always-on-top window: Clawd, the composer, the bubble, the right-click menu, Ctrl+Alt+C, click and drag. |
| `PetModel.cs` | The brain: moods, chat, voice loop, dictation, Claude Code status, sparring, links. Mirrors `PetModel` in the Swift app. |
| `ClawdPainter.cs` | The 16x13 pixel grid, moods and glyphs (a straight port), and `SpriteView`, which redraws only when a pixel changes. |
| `ClaudeRunner.cs` | Runs your own `claude -p` with the same flags as the Mac app, and cuts replies into sentences for speech. |
| `Speech.cs` | Dictation and voice through Windows' offline speech engine (`System.Speech`), and the best installed English voice. |
| `Fight.cs` | Sparring: the fight animation, and `CodexPet`, the one class that finds the Codex pet. |
| `build.ps1` | Publishes a self-contained build into `%LOCALAPPDATA%\ClawdPet` and starts it (`/pet rebuild` runs this). |
| `release.ps1` | Remakes `app/dist-windows/ClawdPet-win-x64.zip` and `VERSION`. The GitHub workflow runs it on every change. |

## Build

On Windows with the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`):

```powershell
.\windows\build.ps1      # build, install into %LOCALAPPDATA%\ClawdPet, start
.\windows\release.ps1    # remake the zip that /pet installs
```

On macOS or Linux, `dotnet build windows/ClawdPet.csproj` checks that it compiles (the project sets `EnableWindowsTargeting`), but the app only runs on Windows.

The `Windows app` workflow (`.github/workflows/windows.yml`) builds on `windows-latest`, uploads `app/dist-windows/` as an artifact, and attaches the zip to a GitHub release when one is published. To ship a Windows build inside the plugin, put the artifact's two files in `app/dist-windows/`.

## How it maps to the Mac app

- **Window.** Borderless, `AllowsTransparency`, topmost, and a tool window (not in Alt+Tab or the taskbar). Windows passes clicks on fully transparent pixels to the app underneath, so only Clawd, the composer and the bubble take clicks. Clawd gets an almost invisible backdrop so the gaps between his legs count too. A left click on Clawd answers `WM_MOUSEACTIVATE` with "don't activate", so it opens Claude without stealing focus. Clicks on the composer or bubble activate the window, so typing lands in Clawd.
- **Idle cost.** `SpriteView` checks his pose 4 to 12 times a second and redraws only when a pixel changes. The status file is checked twice a second, and the Codex pet every 3 seconds.
- **Speech.** `System.Speech` dictation runs on the PC. Natural or Neural voices are used when Windows lists them; otherwise Zira, Mark or David.
- **Settings** live in `%USERPROFILE%\.clawd-pet\settings.json`, next to the `status.json` the mod writes. Errors go to `error.log` in the same folder.
- **Integration.** `clawdpet://` is `HKCU\Software\Classes\clawdpet`, and Open at login is `HKCU\...\CurrentVersion\Run\ClawdPet`. `ClawdPet.exe --uninstall` removes both.

## What to check on Windows

If something looks off on a PC, start with these:

1. **Codex pet detection** (`CodexPet` in `Fight.cs`) is a guess. Bring the Codex pet out and run:
   ```powershell
   & "$env:LOCALAPPDATA\ClawdPet\ClawdPet.exe" --codex-windows | Out-String
   ```
   It lists every Codex/ChatGPT window with its rect and styles, and what the heuristic makes of each. Adjust `Owners` and `PetCenter` to match. Sparring stays greyed out ("open the Codex pet first") whenever detection returns nothing.
2. **Transparency and click-through**: clicks beside Clawd should reach the app underneath. The composer and bubble shadows catch clicks a few pixels past their edges.
3. **Speech**: dictation needs an English speech recognizer (Settings › Time & language › Speech) and microphone access for desktop apps.
4. **Ctrl+Alt+C**: on some layouts this is AltGr+C (for example ć in Polish). While Clawd runs, he takes that key.
5. **`clawdpet://` links and Open at login** (registry), and the Claude app opener for both the classic and Store installs.
