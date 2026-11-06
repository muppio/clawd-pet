import { expect, test } from 'claude-code/testing'
import type { On } from 'claude-code'

// Nothing sits beneath the mod in a test: these hooks stand in for the engine
// (the filesystem, processes, the environment), and record what the mod did.

type Status = { state: string; title: string; detail: string; project: string }

type Fake = {
  writes: Status[]
  statusPaths: string[]
  runs: string[][]
  windows: boolean // a Windows PC instead of a Mac
  running: boolean
  installed: boolean
  codexOut: boolean
  bundled: string // the prebuilt app's build id ('' = this copy ships none)
  installedBuild: string // the installed app's build id
}

const WINDOWS_ENV: Record<string, string> = {
  OS: 'Windows_NT',
  USERPROFILE: 'C:\\Users\\tester',
  LOCALAPPDATA: 'C:\\Users\\tester\\AppData\\Local',
}

function engine(on: On, overrides: Partial<Fake> = {}): Fake {
  const fake: Fake = {
    writes: [], statusPaths: [], runs: [], windows: false, running: false, installed: true, codexOut: true,
    bundled: '', installedBuild: '', ...overrides,
  }
  on('env.get', (_, e) => ({ value: fake.windows ? WINDOWS_ENV[e.name] : '/Users/tester' }))
  on('session.cwd', () => ({ value: fake.windows ? 'C:\\Users\\tester\\code\\my-app' : '/Users/tester/code/my-app' }))
  on('command.register', () => ({ value: { command: 'pet' } }))
  on('ui.toast', () => ({ value: undefined }))
  on('clock.sleep', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('tool.call', () => ({ result: {} }))
  on('turn.start', (_, e) => ({ turnId: e.turnId }))
  on('turn.complete', (_, e) => ({ text: e.answer }))
  on('classic.Notification', () => ({}))
  on('fs.write', (_, e) => {
    if (/[\\/]\.clawd-pet[\\/]status\.json$/.test(e.path)) {
      fake.statusPaths.push(e.path)
      fake.writes.push(JSON.parse(e.text) as Status)
    }
    return { value: undefined }
  })
  on('fs.read', (_, e) => {
    if (e.path.endsWith('/app/dist/VERSION')) return { value: fake.bundled }
    if (e.path.endsWith('/Contents/Resources/build-id')) return { value: fake.installedBuild }
    if (e.path.endsWith('\\app\\dist-windows\\VERSION')) return { value: fake.bundled }
    if (e.path.endsWith('C:\\Users\\tester\\AppData\\Local\\ClawdPet\\build-id')) return { value: fake.installedBuild }
    return { value: '' }
  })
  on('fs.exists', (_, e) => ({ value: e.path.endsWith('ClawdPet.exe') ? fake.installed : false }))
  on('process.run', (_, e) => {
    const argv = [...e.argv]
    fake.runs.push(argv)
    const ok = (stdout = '') => ({
      value: { exitCode: 0, stdout, stderr: '', isStdoutTruncated: false, isStderrTruncated: false },
    })
    const fail = { value: { exitCode: 1, stdout: '', stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
    if (argv[0] === 'pgrep') return fake.running ? ok('123') : fail
    if (argv[0] === 'test') return fake.installed ? ok() : fail
    if (argv[0] === 'tasklist.exe') {
      // tasklist exits 0 either way; only its output says whether Clawd runs.
      return ok(fake.running
        ? '"ClawdPet.exe","4242","Console","1","52,124 K"\r\n'
        : 'INFO: No tasks are running which match the specified criteria.\r\n')
    }
    if (argv[1] === '--codex-check') return ok(fake.codexOut ? 'out\n' : 'missing\n')
    return ok()
  })
  return fake
}

const start = { cwd: '/Users/tester/code/my-app', surface: 'desktop', isInteractive: true } as const

test('a turn walks Clawd through working, the tool, then done', async ($, on) => {
  const fake = engine(on)
  await $.session.start(start)

  await $.turn.start({ text: 'Fix the login bug on the settings page', turnId: 't1' })
  await $.tool.call({ tool: 'Bash', command: 'npm test -- --watch=false' })
  await $.tool.call({ tool: 'Edit', file_path: '/Users/tester/code/my-app/src/login.ts', old_string: 'a', new_string: 'b' })
  await $.turn.complete({ answer: 'Fixed.', durationMs: 10, isAborted: false, turnId: 't1', reason: 'answer' })

  expect(fake.writes.map(w => w.state)).toEqual(['working', 'working', 'working', 'done'])
  expect(fake.writes[0]?.title).toBe('Fix the login bug on the settings page')
  expect(fake.writes[0]?.project).toBe('my-app')
  expect(fake.writes[1]?.detail).toBe('Running npm test -- --watch=false')
  expect(fake.writes[2]?.detail).toBe('Editing login.ts')
  expect(fake.writes[3]?.detail).toBe('All done')
})

test('a stopped turn goes idle and a subagent turn writes nothing', async ($, on) => {
  const fake = engine(on)
  await $.session.start(start)

  await $.turn.start({ text: 'look around', turnId: 'sub', agentId: 'agent-1' })
  expect(fake.writes).toEqual([])

  await $.turn.start({ text: 'Refactor', turnId: 't2' })
  await $.turn.complete({ answer: '', durationMs: 5, isAborted: true, turnId: 't2', reason: 'aborted' })
  expect(fake.writes.map(w => w.state)).toEqual(['working', 'idle'])
})

test('ending a session keeps "Done" up but clears an unfinished turn', async ($, on) => {
  const fake = engine(on)
  on('session.end', () => ({ sessionId: 's1' }))
  await $.session.start(start)

  await $.turn.start({ text: 'Ship it', turnId: 't3' })
  await $.turn.complete({ answer: 'Shipped.', durationMs: 5, isAborted: false, turnId: 't3', reason: 'answer' })
  await $.session.end({ reason: 'other' } as never)
  expect(fake.writes.map(w => w.state)).toEqual(['working', 'done'])

  await $.turn.start({ text: 'Half-way', turnId: 't4' })
  await $.session.end({ reason: 'other' } as never)
  expect(fake.writes.map(w => w.state)).toEqual(['working', 'done', 'working', 'idle'])
})

test('a permission prompt raises "needs you"', async ($, on) => {
  const fake = engine(on)
  await $.session.start(start)

  await $.classic.Notification({ message: 'Claude needs your permission to use Bash', notification_type: 'permission_prompt' })
  await $.classic.Notification({ message: 'Claude is waiting for your input', notification_type: 'idle_prompt' })

  expect(fake.writes.map(w => w.state)).toEqual(['needs'])
  expect(fake.writes[0]?.detail).toBe('Claude needs your permission to use Bash')
})

test('/pet opens Clawd when he is away, and sends him home when he is out', async ($, on) => {
  const fake = engine(on, { running: false })
  await $.session.start(start)

  const opened = await $.command.run({ command: 'pet', args: '' })
  expect(opened.text).toContain('Clawd is on your screen')
  expect(fake.runs).toContainEqual(['open', '/Users/tester/Applications/Clawd Pet.app'])

  fake.running = true
  const closed = await $.command.run({ command: 'pet', args: '' })
  expect(closed.text).toContain('Clawd went home')
  expect(fake.runs).toContainEqual(['pkill', '-x', 'ClawdPet'])
})

test('/pet installs the prebuilt app the first time, no Xcode needed', async ($, on) => {
  const fake = engine(on, { running: false, installed: false, bundled: 'abc123', installedBuild: '' })
  await $.session.start(start)

  const opened = await $.command.run({ command: 'pet', args: '' })
  expect(opened.text).toContain('Clawd is on your screen')
  expect(fake.runs.some(r => r[0] === 'ditto' && (r[3] ?? '').endsWith('/app/dist/ClawdPet.zip'))).toBe(true)
  expect(fake.runs.some(r => (r[1] ?? '').endsWith('/app/build.sh'))).toBe(false)
  expect(fake.runs).toContainEqual(['open', '/Users/tester/Applications/Clawd Pet.app'])
})

test('/pet updates an older install and leaves a current one alone', async ($, on) => {
  const fake = engine(on, { running: false, bundled: 'new-build', installedBuild: 'old-build' })
  await $.session.start(start)

  await $.command.run({ command: 'pet', args: '' })
  expect(fake.runs.filter(r => r[0] === 'ditto').length).toBe(1)

  fake.installedBuild = 'new-build'
  await $.command.run({ command: 'pet', args: '' })
  expect(fake.runs.filter(r => r[0] === 'ditto').length).toBe(1)
})

test('/pet builds from source when this copy ships no prebuilt app', async ($, on) => {
  const fake = engine(on, { running: false, installed: false })
  await $.session.start(start)

  await $.command.run({ command: 'pet', args: '' })
  expect(fake.runs.some(r => r[0] === '/bin/zsh' && (r[1] ?? '').endsWith('/app/build.sh'))).toBe(true)
})

test('/pet fight spars only when the Codex pet is out', async ($, on) => {
  const fake = engine(on, { running: true, codexOut: false })
  await $.session.start(start)

  const missing = await $.command.run({ command: 'pet', args: 'fight' })
  expect(missing.text).toContain("Codex pet isn't on screen")
  expect(fake.runs).not.toContainEqual(['open', 'clawdpet://fight'])

  fake.codexOut = true
  const ding = await $.command.run({ command: 'pet', args: 'fight' })
  expect(ding.text).toContain('Ding ding')
  expect(fake.runs).toContainEqual(['open', 'clawdpet://fight'])
})

test('/pet rebuild runs the build, and unknown options are explained', async ($, on) => {
  const fake = engine(on)
  await $.session.start(start)

  const rebuilt = await $.command.run({ command: 'pet', args: 'rebuild' })
  expect(rebuilt.text).toContain('rebuilt')
  expect(fake.runs.some(r => (r[1] ?? '').endsWith('/app/build.sh'))).toBe(true)

  const unknown = await $.command.run({ command: 'pet', args: 'dance' })
  expect(unknown.text).toContain('Unknown option')
})

// MARK: Windows

const winStart = { cwd: 'C:\\Users\\tester\\code\\my-app', surface: 'desktop', isInteractive: true } as const
const WIN_APP = 'C:\\Users\\tester\\AppData\\Local\\ClawdPet'
const WIN_EXE = `${WIN_APP}\\ClawdPet.exe`
const MAC_ONLY = ['open', 'pgrep', 'pkill', 'ditto', 'test', 'mkdir', 'rm', '/bin/zsh']

/** The PowerShell script a run passed with -Command, or '' for anything else. */
function script(run: string[]): string {
  return run[0] === 'powershell.exe' && run.includes('-Command') ? run[run.length - 1] ?? '' : ''
}

test('on Windows, a turn writes status.json under %USERPROFILE% with Windows paths', async ($, on) => {
  const fake = engine(on, { windows: true })
  await $.session.start(winStart)

  await $.turn.start({ text: 'Fix the login bug', turnId: 'w1' })
  await $.tool.call({ tool: 'Edit', file_path: 'C:\\Users\\tester\\code\\my-app\\src\\login.ts', old_string: 'a', new_string: 'b' })
  await $.turn.complete({ answer: 'Fixed.', durationMs: 10, isAborted: false, turnId: 'w1', reason: 'answer' })

  // The tests run on a Mac, where the engine reads C:\ as relative and puts its folder in front.
  expect(fake.statusPaths[0]?.endsWith('C:\\Users\\tester\\.clawd-pet\\status.json')).toBe(true)
  expect(fake.writes.map(w => w.state)).toEqual(['working', 'working', 'done'])
  expect(fake.writes[0]?.project).toBe('my-app')
  expect(fake.writes[1]?.detail).toBe('Editing login.ts')
})

test('on Windows, /pet starts ClawdPet.exe and sends him home with taskkill', async ($, on) => {
  const fake = engine(on, { windows: true, running: false })
  await $.session.start(winStart)

  const opened = await $.command.run({ command: 'pet', args: '' })
  expect(opened.text).toContain('Clawd is on your screen')
  expect(opened.text).toContain('Ctrl+Alt+C')
  expect(fake.runs.some(r => script(r) === `Start-Process -FilePath '${WIN_EXE}'`)).toBe(true)

  fake.running = true
  const closed = await $.command.run({ command: 'pet', args: '' })
  expect(closed.text).toContain('Clawd went home')
  expect(fake.runs).toContainEqual(['taskkill.exe', '/IM', 'ClawdPet.exe', '/F'])
  expect(fake.runs.some(r => MAC_ONLY.includes(r[0] ?? ''))).toBe(false)
})

test('on Windows, /pet installs the bundled app into %LOCALAPPDATA% once, no .NET needed', async ($, on) => {
  const fake = engine(on, { windows: true, running: false, installed: false, bundled: 'win-1', installedBuild: '' })
  await $.session.start(winStart)

  const opened = await $.command.run({ command: 'pet', args: '' })
  expect(opened.text).toContain('Clawd is on your screen')
  const unpacks = () => fake.runs.filter(r => script(r).includes('Expand-Archive'))
  expect(unpacks().length).toBe(1)
  expect(script(unpacks()[0] ?? [])).toContain("\\app\\dist-windows\\ClawdPet-win-x64.zip' -DestinationPath")
  expect(script(unpacks()[0] ?? [])).toContain(`-DestinationPath '${WIN_APP}'`)
  expect(fake.runs.some(r => r.includes('-File'))).toBe(false)

  fake.installed = true
  fake.installedBuild = 'win-1'
  await $.command.run({ command: 'pet', args: '' })
  expect(unpacks().length).toBe(1)
})

test('on Windows, /pet fight checks for the Codex pet before sending Clawd', async ($, on) => {
  const fake = engine(on, { windows: true, running: true, codexOut: false })
  await $.session.start(winStart)

  const missing = await $.command.run({ command: 'pet', args: 'fight' })
  expect(missing.text).toContain("Codex pet isn't on screen")
  expect(fake.runs).toContainEqual([WIN_EXE, '--codex-check'])
  expect(fake.runs.some(r => script(r).includes('clawdpet://fight'))).toBe(false)

  fake.codexOut = true
  const ding = await $.command.run({ command: 'pet', args: 'fight' })
  expect(ding.text).toContain('Ding ding')
  expect(fake.runs.some(r => script(r) === `Start-Process -FilePath '${WIN_EXE}' -ArgumentList 'clawdpet://fight'`)).toBe(true)
})

test('on Windows, /pet rebuild and a missing app build from windows/ with build.ps1', async ($, on) => {
  const fake = engine(on, { windows: true, running: false, installed: false })
  await $.session.start(winStart)
  const builds = () => fake.runs.filter(r => r[0] === 'powershell.exe' && (r[r.length - 1] ?? '').endsWith('\\windows\\build.ps1'))

  await $.command.run({ command: 'pet', args: '' })
  expect(builds().length).toBe(1)

  const rebuilt = await $.command.run({ command: 'pet', args: 'rebuild' })
  expect(rebuilt.text).toContain('rebuilt')
  expect(builds().length).toBe(2)
  expect(fake.runs.some(r => MAC_ONLY.includes(r[0] ?? ''))).toBe(false)
})
