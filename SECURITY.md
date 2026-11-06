# Security

## Reporting a vulnerability

Please don't open a public issue for a security problem. Report it privately instead:

1. Open the repository's **Security** tab.
2. Click **Report a vulnerability**.
3. Describe the problem, how to reproduce it, and what someone could do with it.

This creates a private GitHub security advisory that only you and the maintainer can see. This is a hobby project, so a reply may take a few days.

Clawd Pet is an unofficial fan project and isn't affiliated with Anthropic or OpenAI. Problems in Claude Code, the Claude app, Codex or the Codex pet belong with those companies, not here.

## What Clawd Pet touches

- **The microphone**, only after you tap the mic button or the voice orb. It stops when you stop talking or tap again. Speech is turned into text by macOS's speech recognizer (on the Mac when macOS supports it) or by Windows' offline speech engine.
- **Your own `claude` command.** Chats run it as you, from `~/.clawd-pet/chat`, with web search and web fetch as its only tools. Clawd Pet doesn't store an API key, and the app makes no network requests of its own.
- **`clawdpet://` links.** `clawdpet://ask?q=…` sends its text to Claude as a question. The other links start voice, open a new chat or start a sparring match.
- **Local files.** On a Mac, the app is installed to `~/Applications/Clawd Pet.app`, and its data lives in `~/.clawd-pet`. That includes `status.json`, which the mod writes on every turn with the start of your prompt, the current step and the project folder's name. On Windows, the app is in `%LOCALAPPDATA%\ClawdPet`, its data is in `%USERPROFILE%\.clawd-pet`, and `clawdpet://` and **Open at login** are per-user registry entries.
- **Your screen**, only to find where the Codex pet's window is. Clawd Pet doesn't record or capture your screen.

## Supported versions

Fixes go into the latest release only.
