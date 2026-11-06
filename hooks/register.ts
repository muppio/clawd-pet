import type { EngineInterface, Register } from 'claude-code'

// Clawd Pet: /pet summons or dismisses Clawd (the native Clawd Pet.app on a
// Mac, ClawdPet.exe on Windows), and every turn writes a tiny status file he
// reads, so he walks and sparkles while Claude works, waves "!" when Claude
// needs you, and hops when it's done.

type PetState = 'working' | 'needs' | 'done' | 'error' | 'idle'

const PROCESS = 'ClawdPet'
const WIN_EXE = 'ClawdPet.exe'
const CODEX_MISSING =
  "The Codex pet isn't on screen, so Clawd has no one to spar with. Open the Codex app and type /pet there to bring it out, then try /pet fight again."

function clip(text: string, max: number): string {
  const one = text.replace(/\s+/g, ' ').trim()
  return one.length > max ? `${one.slice(0, max - 1)}…` : one
}

function baseName(path: string): string {
  return path.split(/[\\/]/).pop() || path
}

function describe(tool: string, args: Record<string, unknown>): string {
  const str = (key: string): string => (typeof args[key] === 'string' ? (args[key] as string) : '')
  switch (tool) {
    case 'Bash':
      return `Running ${clip(str('command'), 60)}`
    case 'Edit':
    case 'Write':
    case 'MultiEdit':
      return `Editing ${baseName(str('file_path'))}`
    case 'NotebookEdit':
      return `Editing ${baseName(str('notebook_path'))}`
    case 'Read':
      return `Reading ${baseName(str('file_path'))}`
    case 'Grep':
    case 'Glob':
      return 'Searching the code'
    case 'WebSearch':
      return `Searching the web for ${clip(str('query'), 40)}`
    case 'WebFetch':
      return 'Reading a web page'
    case 'Agent':
    case 'Task':
      return 'Handing work to a subagent'
    default:
      return tool.startsWith('mcp__') ? `Using ${tool.split('__').slice(1).join(' › ')}` : `Using ${tool}`
  }
}

let home = ''
let windows: boolean | undefined
let title = ''
let project = ''
let lastState: PetState = 'idle'

/** Windows sets OS=Windows_NT for every process; macOS doesn't set OS at all. */
async function isWindows($: EngineInterface): Promise<boolean> {
  if (windows === undefined) windows = (await $.env.get('OS')) === 'Windows_NT'
  return windows
}

/** The user's home: $HOME on a Mac, %USERPROFILE% on Windows (where the app reads status.json). */
async function ensureHome($: EngineInterface): Promise<string> {
  if (!home) {
    home = (await isWindows($))
      ? ((await $.env.get('USERPROFILE')) || (await $.env.get('HOME')) || '')
      : ((await $.env.get('HOME')) ?? '')
  }
  return home
}

async function write($: EngineInterface, state: PetState, detail = ''): Promise<void> {
  const dir = await ensureHome($)
  if (!dir) return
  lastState = state
  const file = (await isWindows($)) ? `${dir}\\.clawd-pet\\status.json` : `${dir}/.clawd-pet/status.json`
  try {
    await $.fs.write(
      file,
      JSON.stringify({ state, title, detail, project, ts: Date.now() }),
    )
  } catch {
    // Clawd is a nice-to-have: never get in Claude's way.
  }
}

async function isRunning($: EngineInterface): Promise<boolean> {
  return (await $.process.run(['pgrep', '-x', PROCESS])).exitCode === 0
}

/** Builds Clawd Pet.app from the source bundled in this mod (needs Xcode's command line tools). */
async function build($: EngineInterface): Promise<string | undefined> {
  $.ui.toast('Building Clawd… (about a minute the first time)')
  const ran = await $.process.run(['/bin/zsh', `${$.plugin.root}/app/build.sh`], { timeoutMs: 600_000 })
  if (ran.exitCode === 0) return undefined
  const tail = `${ran.stderr}\n${ran.stdout}`.trim().split('\n').slice(-4).join('\n')
  return tail.includes('swiftc') || tail.includes('xcrun')
    ? `Building Clawd needs Apple's command line tools. Run \`xcode-select --install\`, then /pet again.\n${tail}`
    : `Clawd's build failed:\n${tail}`
}

async function readText($: EngineInterface, path: string): Promise<string> {
  try {
    return (await $.fs.read(path)).trim()
  } catch {
    return ''
  }
}

/** Unpacks the prebuilt app that ships with this mod (Apple silicon + Intel, no Xcode needed). */
async function installPrebuilt($: EngineInterface): Promise<string | undefined> {
  const dir = await ensureHome($)
  await $.process.run(['pkill', '-x', PROCESS])
  await $.process.run(['pkill', '-x', 'ClaudePet']) // a build from before the rename
  await $.process.run(['mkdir', '-p', `${dir}/Applications`])
  await $.process.run(['rm', '-rf', `${dir}/Applications/Clawd Pet.app`, `${dir}/Applications/Claude Pet.app`])
  const ran = await $.process.run(['ditto', '-x', '-k', `${$.plugin.root}/app/dist/ClawdPet.zip`, `${dir}/Applications`])
  return ran.exitCode === 0 ? undefined : `Couldn't install Clawd: ${ran.stderr.trim()}`
}

/**
 * Opens the app. The first time, and whenever this mod ships a newer build,
 * it installs the prebuilt app first; with no prebuilt app it builds from source.
 */
async function launch($: EngineInterface): Promise<string | undefined> {
  const dir = await ensureHome($)
  const app = `${dir}/Applications/Clawd Pet.app`
  const bundled = await readText($, `${$.plugin.root}/app/dist/VERSION`)
  const installed = await readText($, `${app}/Contents/Resources/build-id`)
  if (bundled && installed !== bundled) {
    const failed = await installPrebuilt($)
    if (failed) return failed
  } else if ((await $.process.run(['test', '-d', app])).exitCode !== 0) {
    return build($) // build.sh opens it when done
  }
  const opened = await $.process.run(['open', app])
  return opened.exitCode === 0 ? undefined : `Couldn't open Clawd Pet.app: ${opened.stderr.trim()}`
}

// MARK: Windows: ClawdPet.exe in %LOCALAPPDATA%\ClawdPet, from app/dist-windows/

/** A PowerShell string literal. */
function psQuote(text: string): string {
  return `'${text.replace(/'/g, "''")}'`
}

function powershell(script: string): string[] {
  return ['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script]
}

async function winDir($: EngineInterface): Promise<string> {
  const local = (await $.env.get('LOCALAPPDATA')) || `${await ensureHome($)}\\AppData\\Local`
  return `${local}\\ClawdPet`
}

async function winExe($: EngineInterface): Promise<string> {
  return `${await winDir($)}\\${WIN_EXE}`
}

async function winIsRunning($: EngineInterface): Promise<boolean> {
  const ran = await $.process.run(['tasklist.exe', '/FI', `IMAGENAME eq ${WIN_EXE}`, '/FO', 'CSV', '/NH'])
  return ran.stdout.toLowerCase().includes(WIN_EXE.toLowerCase())
}

async function winQuit($: EngineInterface): Promise<void> {
  await $.process.run(['taskkill.exe', '/IM', WIN_EXE, '/F'])
}

/** Builds and starts ClawdPet.exe from the source in windows/ (needs the .NET 8 SDK). */
async function winBuild($: EngineInterface): Promise<string | undefined> {
  $.ui.toast('Building Clawd… (a few minutes the first time)')
  const script = `${$.plugin.root}\\windows\\build.ps1`
  const ran = await $.process.run(
    ['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script],
    { timeoutMs: 600_000 },
  )
  if (ran.exitCode === 0) return undefined
  const tail = `${ran.stderr}\n${ran.stdout}`.trim().split(/\r?\n/).slice(-4).join('\n')
  return tail.includes('.NET') || tail.includes('dotnet')
    ? `Building Clawd needs the .NET 8 SDK. Run \`winget install Microsoft.DotNet.SDK.8\`, then /pet rebuild.\n${tail}`
    : `Clawd's build failed:\n${tail}`
}

/** Unpacks the prebuilt app that ships with this mod (self-contained, no .NET needed). */
async function winInstallPrebuilt($: EngineInterface): Promise<string | undefined> {
  const dir = await winDir($)
  const zip = `${$.plugin.root}\\app\\dist-windows\\ClawdPet-win-x64.zip`
  await winQuit($)
  const ran = await $.process.run(
    powershell(
      [
        "$ErrorActionPreference = 'Stop'",
        `Get-Process -Name ${PROCESS} -ErrorAction SilentlyContinue | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue`,
        `if (Test-Path -LiteralPath ${psQuote(dir)}) { Remove-Item -LiteralPath ${psQuote(dir)} -Recurse -Force }`,
        `Expand-Archive -LiteralPath ${psQuote(zip)} -DestinationPath ${psQuote(dir)} -Force`,
      ].join('; '),
    ),
    { timeoutMs: 180_000 },
  )
  return ran.exitCode === 0 ? undefined : `Couldn't install Clawd: ${ran.stderr.trim()}`
}

async function winLaunch($: EngineInterface): Promise<string | undefined> {
  const dir = await winDir($)
  const exe = await winExe($)
  const bundled = await readText($, `${$.plugin.root}\\app\\dist-windows\\VERSION`)
  const installed = await readText($, `${dir}\\build-id`)
  if (bundled && installed !== bundled) {
    const failed = await winInstallPrebuilt($)
    if (failed) return failed
  } else if (!(await $.fs.exists(exe).catch(() => false))) {
    return winBuild($) // build.ps1 starts it when done
  }
  const opened = await $.process.run(powershell(`Start-Process -FilePath ${psQuote(exe)}`))
  return opened.exitCode === 0 ? undefined : `Couldn't start Clawd: ${opened.stderr.trim()}`
}

async function windowsCommand($: EngineInterface, arg: string): Promise<{ text: string }> {
  if (arg === 'rebuild') {
    const failed = await winBuild($)
    return { text: failed ?? 'Clawd is rebuilt and back on your screen.' }
  }

  if (arg === 'fight' || arg === 'spar') {
    if (!(await winIsRunning($))) {
      const failed = await winLaunch($)
      if (failed) return { text: failed }
      await $.clock.sleep(1500)
    }
    const exe = await winExe($)
    const check = await $.process.run([exe, '--codex-check'])
    if (check.stdout.trim() !== 'out') return { text: CODEX_MISSING }
    // A second ClawdPet.exe hands the link to the running one and exits.
    await $.process.run(powershell(`Start-Process -FilePath ${psQuote(exe)} -ArgumentList ${psQuote('clawdpet://fight')}`))
    return { text: 'Ding ding! 🥊 Clawd is off to spar with the Codex pet.' }
  }

  if (arg !== '') {
    return { text: `Unknown option "${arg}". Try /pet, /pet fight or /pet rebuild.` }
  }

  if (await winIsRunning($)) {
    await winQuit($)
    return { text: 'Clawd went home. Type /pet to bring him back.' }
  }
  const failed = await winLaunch($)
  return {
    text: failed ?? 'Clawd is on your screen. Click him to open Claude, or press Ctrl+Alt+C to type to him.',
  }
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    home = ''
    windows = undefined
    await ensureHome($)
    await $.command.register({
      name: 'pet',
      description: 'Summon or dismiss Clawd, your Claude buddy beside the Codex pet (fight: spar with Codex, rebuild: reinstall)',
      argumentHint: '[fight | rebuild]',
    })
    return next(e)
  })

  on('command.run', { command: 'pet' }, async ($, e) => {
    const arg = e.args.trim().toLowerCase()
    if (await isWindows($)) return windowsCommand($, arg)

    if (arg === 'rebuild') {
      const failed = await build($)
      return { text: failed ?? 'Clawd is rebuilt and back on your screen.' }
    }

    if (arg === 'fight' || arg === 'spar') {
      if (!(await isRunning($))) {
        const failed = await launch($)
        if (failed) return { text: failed }
        await $.clock.sleep(1500)
      }
      const dir = await ensureHome($)
      const check = await $.process.run([`${dir}/Applications/Clawd Pet.app/Contents/MacOS/${PROCESS}`, '--codex-check'])
      if (check.stdout.trim() !== 'out') return { text: CODEX_MISSING }
      await $.process.run(['open', 'clawdpet://fight'])
      return { text: 'Ding ding! 🥊 Clawd is off to spar with the Codex pet.' }
    }

    if (arg !== '') {
      return { text: `Unknown option "${arg}". Try /pet, /pet fight or /pet rebuild.` }
    }

    if (await isRunning($)) {
      await $.process.run(['pkill', '-x', PROCESS])
      await $.process.run(['pkill', '-x', 'ClaudePet']) // a build from before the rename
      return { text: 'Clawd went home. Type /pet to bring him back.' }
    }
    const failed = await launch($)
    return {
      text: failed ?? 'Clawd is on your screen. Click him to open Claude, or press ⌃⌥C to type to him.',
    }
  })

  on('turn.start', async ($, e, next) => {
    if (!e.agentId) {
      title = clip(e.text, 90)
      project = baseName(await $.session.cwd())
      await write($, 'working', 'Thinking…')
    }
    return next(e)
  })

  on('tool.call', async ($, e, next) => {
    await write($, 'working', describe(String(e.tool), e as unknown as Record<string, unknown>))
    return next(e)
  }).catch(($, e, next) => next(e))

  on('turn.complete', async ($, e, next) => {
    const agentId = (e as { agentId?: string }).agentId
    if (!agentId) {
      if (e.reason === 'answer') await write($, 'done', 'All done')
      else if (e.reason === 'aborted') await write($, 'idle', 'Stopped')
      else await write($, 'error', 'Hit a problem')
    }
    return next(e)
  })

  on('classic.Notification', async ($, e, next) => {
    if (e.notification_type === 'permission_prompt') await write($, 'needs', clip(e.message, 80))
    return next(e)
  }).catch(($, e, next) => next(e))

  on('session.end', async ($, e, next) => {
    // Only clear a turn the session left unfinished; "Done ✓" stays up.
    if (lastState === 'working' || lastState === 'needs') await write($, 'idle')
    return next(e)
  })
}
