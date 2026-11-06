# Changelog

All notable changes to Clawd Pet are listed here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.2] - 2026-11-06

### Fixed

- Mac: when a spoken reply arrived all at once instead of streaming in, Clawd read only its last sentence aloud. He now reads the whole reply, as he does on Windows.
- With **Spar now and then** on, a match Clawd started on his own while the Codex pet was on another screen showed "Too far to spar" out of nowhere, every 20 to 40 minutes, and kept him from napping. Now he quietly skips that match. He still tells you when you ask for a fight.

## [0.3.1] - 2026-11-06

### Fixed

- Sparring: Clawd now runs to exactly where the Codex pet is, on Windows and on the Mac. He used to guess the pet's spot from the Codex window, whose layout changes with where the pet sits and what's open around it, so he could stop short or head the wrong way. Now he reads the position and size Codex saves for its pet (in `~/.codex/.codex-global-state.json`), and goes there only while a Codex window is showing it.
- When the Codex pet is on a different screen from Clawd, he says so instead of doing nothing.

### Added

- `ClawdPet --codex-debug` on the Mac (`ClawdPet.exe --codex-windows` on Windows) prints what Clawd found when he looked for the Codex pet.

## [0.3.0] - 2026-11-06

First public release.

### Added

- `/pet` in Claude Code summons or dismisses Clawd. The first time, it installs the prebuilt app into `~/Applications`. After that, it installs new builds on its own when the plugin updates.
- `/pet fight` sends Clawd to spar with the Codex pet, which has to be on screen.
- `/pet rebuild` builds the app from the source in `app/`, using Xcode's command line tools.
- Voice-to-voice chat: tap the orb and talk. Claude answers out loud, sentence by sentence, and Clawd keeps listening until you tap the orb again.
- Typing and dictation in the "Work with Claude" composer. Replies stream into Clawd's speech bubble, with formatting.
- Reactions while Claude Code works: Clawd walks in place with a spinner and shows what's happening, raises a "!" when Claude needs your OK, and hops when Claude is done.
- Sparring with the Codex pet in a cartoon dust cloud, with a running score. Matches start when you ask, or now and then on their own if you turn on **Spar now and then**.
- Moods: Clawd blinks, looks around, talks while replies stream in, and naps when things are quiet. Reduce Motion is respected.
- Click Clawd to open the Claude app, drag him anywhere, or press ⌃⌥C from anywhere to type to him. Right-click for mini mode, the model, spoken replies, sparring, **Snap back next to Codex** and **Open at login**.
- Links for Shortcuts or Raycast: `clawdpet://ask?q=…`, `clawdpet://voice`, `clawdpet://new` and `clawdpet://fight`.
- Native macOS app for macOS 14 or later, on Apple silicon and Intel.
- Native Windows app for Windows 10 and 11 (x64), in `windows/`, with the same features. `/pet` downloads it from the GitHub release and checks its SHA-256, so no .NET is needed.
