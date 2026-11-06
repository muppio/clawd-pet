# Contributing to Clawd Pet

Thanks for wanting to help. Clawd Pet is a small hobby project, so small, focused changes are the easiest to review and merge.

Clawd Pet is an unofficial fan project. It isn't affiliated with, endorsed by or sponsored by Anthropic or OpenAI. Claude, Claude Code and Clawd belong to Anthropic, and Codex belongs to OpenAI. Please keep it that way in what you add: no official logos or wording that suggests otherwise, and nothing that changes the Codex pet. Clawd only draws next to it.

## How the project is laid out

| Path | What it is |
| --- | --- |
| `app/ClawdPet.swift` | The whole Mac app, in one Swift file: the window, Clawd's pixel art and moods, chat and voice, sparring, and `clawdpet://` links. |
| `app/Info.plist` | The Mac app's bundle settings: version, link scheme, and the microphone and speech prompts. |
| `app/build.sh` | Builds the Mac app from source and launches it. |
| `app/release.sh` | Remakes the prebuilt Mac app that ships with the plugin: `app/dist/ClawdPet.zip` and `app/dist/VERSION`. |
| `hooks/register.ts` | The mod. It adds `/pet`, installs and launches the app, and writes `~/.clawd-pet/status.json` while Claude Code works, so Clawd can react. |
| `tests/pet.test.ts` | Tests for the mod. |
| `.claude-plugin/plugin.json` | The plugin manifest. |
| `windows/` | The Windows port, in C# and WPF. See [`windows/README.md`](windows/README.md). |
| `docs/` | The project website. `docs/art.js` draws Clawd and the Codex pet for it. |
| `docs/brand/` | The logo, the README GIFs and the social preview. `node docs/brand/render.mjs` redraws them from `studio.html` (needs Chrome and ffmpeg). |

The app and the mod only talk through `status.json`. When you chat with Clawd, the app runs your own `claude` command.

## Build and run on a Mac

You need macOS 14 or later and Xcode's command line tools (`xcode-select --install`). The full Xcode app isn't needed.

```bash
app/build.sh               # build for this Mac, install into ~/Applications, launch
app/build.sh --universal   # Apple silicon + Intel in one app
```

`build.sh` quits any running Clawd, replaces `~/Applications/Clawd Pet.app` and opens the new one.

To try the mod from your checkout, start Claude Code from the repo folder with `claude --plugin-dir .`, then type `/pet`.

One thing to know: plain `/pet` reinstalls the prebuilt app whenever your installed app's build id doesn't match `app/dist/VERSION`. So while you're changing the Swift file, launch your build with `app/build.sh` or `/pet rebuild`. Plain `/pet` would put the prebuilt app back.

## Check the mod

Run both of these from the repo folder before you open a PR:

```bash
claude plugin validate .
claude plugin test .
```

`validate` checks the plugin manifest. `test` runs every `*.test.ts` file, which today is `tests/pet.test.ts`. The tests fake the files, processes and environment, so they run on any Mac without the app installed, and they cover the Windows paths too. If you change `hooks/register.ts`, add or update a test.

## Ship a Mac change with `app/release.sh`

People don't build the app themselves. `/pet` unpacks the prebuilt `app/dist/ClawdPet.zip`. So whenever you change `app/ClawdPet.swift` or `app/Info.plist`, run:

```bash
app/release.sh
```

It builds a universal app (Apple silicon + Intel), remakes `app/dist/ClawdPet.zip`, and writes the new build id to `app/dist/VERSION`. Include both files in your PR. `/pet` compares that id with the installed app's and updates everyone's copy on its own. If you skip this step, your Swift change never reaches anyone.

## The Windows build

The Windows port lives in `windows/`. It's C# on .NET 8 with WPF, and it mirrors the Mac app. [`windows/README.md`](windows/README.md) explains each file, how to build it, and what still needs checking.

- On Windows, with the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`), `.\windows\build.ps1` builds Clawd, installs him into `%LOCALAPPDATA%\ClawdPet` and starts him. `.\windows\release.ps1` makes `app\dist-windows\ClawdPet-win-x64.zip` and its `VERSION`. `/pet` installs that local zip in place of the download, which is handy for testing. Both files are ignored by git.
- On macOS or Linux, `dotnet build windows/ClawdPet.csproj` checks that it compiles. The app itself only runs on Windows.
- The **Windows app** workflow (`.github/workflows/windows.yml`) builds the self-contained win-x64 zip on `windows-latest`. It runs on pushes to `main` and on pull requests that touch `windows/`, and uploads the zip as the `clawd-pet-win-x64` artifact. You can download it from the workflow run and try it. When a GitHub release is published, the workflow attaches the zip and its `VERSION` to it.
- **Shipping a Windows change:** publish a release, wait for the workflow to attach the zip, then run `node windows/pin-release.mjs <tag>`. It writes `app/dist-windows/release.json` (the download's URL, SHA-256, size and build id). Commit that file: `/pet` on Windows downloads only what it pins.

**Windows users are a big help.** If you run Clawd on Windows, open an issue with what worked and what didn't. The "What to check on Windows" list in [`windows/README.md`](windows/README.md) says where to look first.

Try to keep the two apps in step. If you change how Clawd behaves on one side, make the same change on the other, or say in your PR that it still needs porting.

## Pull requests

- Keep PRs small and focused: one fix or one feature each. Keep refactors separate from behavior changes.
- Test on a real machine, and say which OS and version you used. A floating window can't be checked by tests alone.
- For anything visual (drawing, animation, bubbles, menus), add a screenshot or a short GIF.
- Make sure `claude plugin validate .` and `claude plugin test .` pass.
- If you changed `app/ClawdPet.swift` or `app/Info.plist`, run `app/release.sh` and include the new `app/dist/ClawdPet.zip` and `app/dist/VERSION`.
- Keep Clawd's lines friendly and short, so they fit in his bubble.
- For a bigger idea, open an issue first, so we can talk it over before you build it.

## Good first contributions

- **New taunts and result lines.** The pre-fight taunts and the win, loss and draw lines are short lists in `startFight` in `app/ClawdPet.swift` and `StartFight` in `windows/PetModel.cs`. Add yours to both.
- **New moods.** Moods are the `Mood` enum in `app/ClawdPet.swift` and `windows/Pet.cs`. On Windows, the pixel art is in `windows/ClawdPainter.cs`. A new mood needs a look and a moment when Clawd shows it.
- **More languages for speech.** Dictation, voice and spoken replies are English only today: `en-US` on the Mac, and an English recognizer and voice on Windows.
- **Keep sparring on target.** Clawd finds the Codex pet from the position Codex saves for it (`FightKit` in `app/ClawdPet.swift`, `CodexPet` in `windows/Fight.cs`). If a Codex update moves things, `ClawdPet --codex-debug` (Mac) or `ClawdPet.exe --codex-windows` (Windows) shows what he found.
- **A Linux port.** There isn't one yet. The Windows port shows how the Mac app maps to another platform, and `/pet` in `hooks/register.ts` would need a Linux branch.

## Issues

Use the **Bug report** or **Idea** form when you open an issue. For security problems, follow [SECURITY.md](SECURITY.md) instead. Everyone here follows the [code of conduct](CODE_OF_CONDUCT.md).

## License

By contributing, you agree that your work is released under the project's [MIT license](LICENSE).
