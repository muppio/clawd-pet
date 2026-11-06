# Changelog

All notable changes to Clawd Pet are listed here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
- Native Windows app for Windows 10 and 11 (x64), in `windows/`, with the same features. Sparring may not find the Codex pet on Windows yet, because its window hasn't been measured.
