// Clawd Pet — a little Claude Code buddy that floats in the corner of your
// Mac, right beside the Codex pet. Click him to jump to Claude; type, dictate
// or talk voice-to-voice with Claude from the composer he sits on.

import AppKit
import AVFoundation
import Carbon.HIToolbox
import ServiceManagement
import Speech
import SwiftUI

// MARK: - Constants

enum Pet {
    static let claudeBundleID = "com.anthropic.claudefordesktop"
    static let windowSize = CGSize(width: 450, height: 470)
    static let composerSize = CGSize(width: 420, height: 96)
    static let px: CGFloat = 6
    static let gridCols = 16
    static let gridRows = 13
    static let petInset: CGFloat = 14 // pet canvas's left edge over the composer
    static let composerTop: CGFloat = 46 // composer starts this far below the pet canvas top
    static let textInset: CGFloat = 112 // composer text starts right of the pet
    static var petCenterX: CGFloat { petInset + CGFloat(gridCols) * px / 2 }
    static var home: URL { FileManager.default.homeDirectoryForCurrentUser }
    static var supportDir: URL { home.appendingPathComponent(".clawd-pet", isDirectory: true) }
    static var statusFile: URL { supportDir.appendingPathComponent("status.json") }
    static var chatDir: URL { supportDir.appendingPathComponent("chat", isDirectory: true) }
}

func onMain(_ work: @escaping @MainActor () -> Void) {
    DispatchQueue.main.async { MainActor.assumeIsolated { work() } }
}

func after(_ seconds: Double, _ work: @escaping @MainActor () -> Void) {
    DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { MainActor.assumeIsolated { work() } }
}

extension Color {
    static let clawd = Color(red: 0.843, green: 0.467, blue: 0.341) // #D77757
    static let clawdLight = Color(red: 0.925, green: 0.600, blue: 0.470)
    static let clawdDark = Color(red: 0.690, green: 0.357, blue: 0.247)
    static let clawdEye = Color(red: 0.09, green: 0.075, blue: 0.07)
    static let blush = Color(red: 1.0, green: 0.60, blue: 0.64)
    static let panelFill = Color(red: 0.192, green: 0.192, blue: 0.204)
    static let panelStroke = Color.white.opacity(0.10)
    static let dim = Color.white.opacity(0.42)
    static let alertYellow = Color(red: 1.0, green: 0.80, blue: 0.32)
}

enum Mood: Equatable { case idle, thinking, working, listening, speaking, happy, alert, sleepy, dizzy }

enum ClaudeModel: String, CaseIterable, Identifiable {
    case opus, sonnet, haiku
    var id: String { rawValue }
    var label: String {
        switch self {
        case .opus: "Opus 5.5"
        case .sonnet: "Sonnet 5.5"
        case .haiku: "Haiku 5.5"
        }
    }

    var blurb: String {
        switch self {
        case .opus: "Smartest"
        case .sonnet: "Fast + smart"
        case .haiku: "Fastest"
        }
    }
}

struct Bubble: Equatable {
    enum Kind { case chat, code, error }
    var id = UUID()
    var kind: Kind
    var status: String
    var prompt = ""
    var body = ""
    var detail = ""
}

struct CodeStatus: Decodable {
    var state: String
    var title: String?
    var detail: String?
    var project: String?
}

/// A system-wide shortcut (⌃⌥C by default) that pops Clawd's composer open.
final class HotKey {
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private let action: () -> Void

    init(keyCode: Int, modifiers: Int, action: @escaping () -> Void) {
        self.action = action
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let me = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, _, userData in
            guard let userData else { return noErr }
            Unmanaged<HotKey>.fromOpaque(userData).takeUnretainedValue().action()
            return noErr
        }, 1, &spec, me, &handler)
        let id = EventHotKeyID(signature: OSType(0x434C_5744), id: 1) // 'CLWD'
        RegisterEventHotKey(UInt32(keyCode), UInt32(modifiers), id, GetApplicationEventTarget(), 0, &ref)
    }

    deinit {
        if let ref { UnregisterEventHotKey(ref) }
        if let handler { RemoveEventHandler(handler) }
    }
}

// MARK: - Clawd, in pixels

struct ClawdLook: Equatable {
    var mood: Mood = .idle
    var hover = false
    var mouthOpen = false
    var calm = false // Reduce Motion: no bobbing or hopping
    var walk = false // step in place (used while he runs across the screen)
    var lookDX: Int? // force his gaze left (-1) or right (+1)
}

enum Ink: Equatable {
    case body, light, dark, eye, blush

    var color: Color {
        switch self {
        case .body: .clawd
        case .light: .clawdLight
        case .dark: .clawdDark
        case .eye: .clawdEye
        case .blush: .blush
        }
    }

    var cg: CGColor { NSColor(color).cgColor }
}

/// One frame of Clawd: his grid pixels plus the width of his ground shadow.
/// Two equal poses draw identically, so views redraw only when this changes.
struct ClawdPose: Equatable {
    struct Pixel: Equatable {
        var x: Int, y: Int, w: Int, h: Int
        var ink: Ink
    }

    var pixels: [Pixel] = []
    var shadowCols = 11
}

/// A glyph over his head: Claude's spinner, "!", z's, a sparkle or dizzy stars.
struct GlyphMark: Equatable {
    enum Tone: Equatable { case clawd, yellow, white }
    enum Weight: Equatable { case bold, heavy, black }
    var text: String
    var x: CGFloat
    var y: CGFloat
    var size: CGFloat
    var weight: Weight = .bold
    var rounded = false
    var tone: Tone
    var opacity: Double = 1

    var color: Color {
        let base: Color = switch tone {
        case .clawd: .clawd
        case .yellow: .alertYellow
        case .white: .white
        }
        return base.opacity(opacity)
    }

    var fontWeight: Font.Weight {
        switch weight {
        case .bold: .bold
        case .heavy: .heavy
        case .black: .black
        }
    }

    var nsFont: NSFont {
        let w: NSFont.Weight = switch weight {
        case .bold: .bold
        case .heavy: .heavy
        case .black: .black
        }
        let font = NSFont.systemFont(ofSize: size, weight: w)
        guard rounded, let d = font.fontDescriptor.withDesign(.rounded) else { return font }
        return NSFont(descriptor: d, size: size) ?? font
    }
}

enum ClawdPainter {
    /// Clawd on a 16x13 grid of pixels. Body is 10x7 with 2x2 arms either side
    /// and four little legs; every pose is a few pixel offsets.
    static func pose(look: ClawdLook, t: Double) -> ClawdPose {
        var pose = ClawdPose()
        let frame = Int(t * 8)
        let ox = 1, oy = 3
        let walking = look.walk || look.mood == .thinking || look.mood == .working

        var bob = 0
        switch look.mood {
        case .happy: bob = -Int((abs(sin(t * 7.5)) * 2.4).rounded())
        case .thinking, .working, .sleepy, .dizzy: bob = 0
        case .listening, .alert: bob = sin(t * 5) > 0 ? -1 : 0
        default: bob = sin(t * 2.2) > 0.55 ? -1 : 0
        }
        if look.calm { bob = 0 }

        func fill(_ c: Int, _ r: Int, _ w: Int = 1, _ h: Int = 1, _ ink: Ink) {
            pose.pixels.append(.init(x: ox + c, y: oy + bob + r, w: w, h: h, ink: ink))
        }

        // The shadow stays on the ground and shrinks while he hops.
        pose.shadowCols = 11 + bob

        // Legs: alternate steps while working.
        for (i, c) in [3, 5, 8, 10].enumerated() {
            let lifted = walking && ((i % 2 == 0) == (frame % 2 == 0))
            fill(c, 7, 1, 1, .body)
            if !lifted { fill(c, 8, 1, 1, .dark) }
        }

        // Arms.
        var leftRow = 3, rightRow = 3
        switch look.mood {
        case .listening, .alert:
            leftRow = 2; rightRow = 2
        case .happy:
            leftRow = frame % 2 == 0 ? 2 : 3
            rightRow = frame % 2 == 0 ? 3 : 2
        case .speaking:
            rightRow = (frame / 2) % 2 == 0 ? 3 : 2
        case .sleepy, .dizzy:
            leftRow = 4; rightRow = 4
        default:
            if look.hover { rightRow = (frame / 2) % 2 == 0 ? 2 : 3 }
        }
        fill(0, leftRow, 2, 1, .body); fill(0, leftRow + 1, 2, 1, .dark)
        fill(12, rightRow, 2, 1, .body); fill(12, rightRow + 1, 2, 1, .dark)

        // Body with a little light and shade.
        fill(2, 0, 10, 7, .body)
        fill(3, 0, 8, 1, .light)
        fill(2, 1, 1, 3, .light)
        fill(2, 6, 10, 1, .dark)

        // Eyes.
        let blink = t.truncatingRemainder(dividingBy: 3.9) < 0.22
        let lx = 4, rx = 9
        switch look.mood {
        case .happy:
            for x in [lx, rx] {
                fill(x - 1, 3, 1, 1, .eye); fill(x, 2, 1, 1, .eye); fill(x + 1, 3, 1, 1, .eye)
            }
        case .sleepy:
            fill(lx - 1, 3, 2, 1, .eye); fill(rx, 3, 2, 1, .eye)
        case .dizzy:
            for x in [lx, rx] {
                fill(x - 1, 1, 1, 1, .eye); fill(x + 1, 1, 1, 1, .eye)
                fill(x, 2, 1, 1, .eye)
                fill(x - 1, 3, 1, 1, .eye); fill(x + 1, 3, 1, 1, .eye)
            }
        case .listening, .alert:
            if blink {
                fill(lx, 3, 1, 1, .eye); fill(rx, 3, 1, 1, .eye)
            } else {
                fill(lx, 1, 1, 3, .eye); fill(rx, 1, 1, 3, .eye)
            }
        case .thinking, .working:
            if blink {
                fill(lx + 1, 2, 1, 1, .eye); fill(rx + 1, 2, 1, 1, .eye)
            } else {
                fill(lx + 1, 1, 1, 2, .eye); fill(rx + 1, 1, 1, 2, .eye)
            }
        default:
            let phase = t.truncatingRemainder(dividingBy: 9)
            let dx = look.lookDX ?? (look.hover ? 0 : (phase < 1.4 ? -1 : (phase > 5 && phase < 6.3 ? 1 : 0)))
            if blink {
                fill(lx + dx, 3, 1, 1, .eye); fill(rx + dx, 3, 1, 1, .eye)
            } else {
                fill(lx + dx, 2, 1, 2, .eye); fill(rx + dx, 2, 1, 2, .eye)
            }
        }

        if look.mood == .speaking, look.mouthOpen { fill(6, 5, 2, 1, .eye) }
        if look.mood == .happy || look.hover {
            fill(3, 4, 1, 1, .blush); fill(10, 4, 1, 1, .blush)
        }
        return pose
    }

    static func rect(_ q: ClawdPose.Pixel, px: CGFloat, dy: CGFloat = 0) -> CGRect {
        CGRect(x: CGFloat(q.x) * px, y: CGFloat(q.y) * px + dy, width: CGFloat(q.w) * px, height: CGFloat(q.h) * px)
    }

    static func shadowRect(_ p: ClawdPose, px: CGFloat, dy: CGFloat = 0) -> CGRect {
        let w = CGFloat(p.shadowCols) * px
        return CGRect(x: 8 * px - w / 2, y: 12 * px - px * 0.35 + dy, width: w, height: px * 1.1)
    }

    /// SwiftUI drawing, for the sparring arena and the app icon.
    static func paint(_ g: GraphicsContext, px: CGFloat, look: ClawdLook, t: Double) {
        let p = pose(look: look, t: t)
        g.fill(Path(ellipseIn: shadowRect(p, px: px)), with: .color(.black.opacity(0.32)))
        for q in p.pixels { g.fill(Path(rect(q, px: px)), with: .color(q.ink.color)) }
    }

    static func glyphs(mood: Mood, t: Double) -> [GlyphMark] {
        let f = Int(t * 9)
        switch mood {
        case .thinking, .working:
            let seq = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"]
            return [GlyphMark(text: seq[f % seq.count], x: 78, y: -4, size: 18, tone: .clawd)]
        case .alert:
            return [GlyphMark(text: "!", x: 82, y: sin(t * 8) > 0 ? -6 : -3, size: 20, weight: .black, rounded: true, tone: .yellow)]
        case .sleepy:
            let d = CGFloat((t * 5).truncatingRemainder(dividingBy: 14)).rounded()
            var marks = [GlyphMark(text: "z", x: 80 + d * 0.4, y: 10 - d, size: 10, weight: .heavy, rounded: true, tone: .white, opacity: 0.55)]
            if d > 4 {
                marks.append(GlyphMark(text: "Z", x: 86 + d * 0.3, y: 2 - d * 0.6, size: 13, weight: .heavy, rounded: true, tone: .white, opacity: 0.55))
            }
            return marks
        case .happy:
            return [GlyphMark(text: "✦", x: 82, y: 0, size: 13, tone: .yellow, opacity: f % 4 < 2 ? 1 : 0.35)]
        case .dizzy:
            return (0 ..< 3).map { i in
                let a = t * 5 + Double(i) * 2.1
                return GlyphMark(text: "★", x: (43 + cos(a) * 28).rounded(), y: (6 + sin(a) * 5).rounded(), size: 10, tone: .yellow)
            }
        default:
            return []
        }
    }
}

/// Glyphs over his head, in SwiftUI (the sparring arena).
struct PetGlyph: View {
    let mood: Mood
    let t: Double

    var body: some View {
        ZStack(alignment: .topLeading) {
            ForEach(Array(ClawdPainter.glyphs(mood: mood, t: t).enumerated()), id: \.offset) { _, g in
                Text(g.text)
                    .font(.system(size: g.size, weight: g.fontWeight, design: g.rounded ? .rounded : .default))
                    .foregroundColor(g.color)
                    .offset(x: g.x, y: g.y)
            }
        }
    }
}

/// Clawd drawn by AppKit, outside SwiftUI's layout: a timer checks his pose a
/// few times a second and the view redraws only when a pixel actually changes,
/// so an always-on pet costs next to nothing while he's just standing there.
final class SpriteView: NSView {
    static let headroom: CGFloat = 18 // room above his head for glyphs
    static let sideroom: CGFloat = 16 // and to his right

    var look = ClawdLook() {
        didSet {
            guard look != oldValue else { return }
            tick(force: true)
            if look.mood != oldValue.mood || look.hover != oldValue.hover { reschedule() }
        }
    }

    private var timer: Timer?
    private var pose = ClawdPose()
    private var marks: [GlyphMark] = []

    override var isFlipped: Bool { true }
    override func hitTest(_: NSPoint) -> NSView? { nil }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        reschedule()
        tick(force: true)
    }

    private var fps: Double {
        if look.hover { return 12 }
        switch look.mood {
        case .idle: return 5
        case .sleepy: return 4
        default: return 12
        }
    }

    private func reschedule() {
        timer?.invalidate()
        timer = nil
        guard window != nil else { return }
        let t = Timer(timeInterval: 1 / fps, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick(force: false) }
        }
        t.tolerance = 0.03
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func tick(force: Bool) {
        let t = Date.timeIntervalSinceReferenceDate
        let p = ClawdPainter.pose(look: look, t: t)
        let g = ClawdPainter.glyphs(mood: look.mood, t: t)
        guard force || p != pose || g != marks else { return }
        pose = p
        marks = g
        needsDisplay = true
    }

    override func draw(_: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let px = Pet.px, dy = Self.headroom
        ctx.setFillColor(NSColor.black.withAlphaComponent(0.32).cgColor)
        ctx.fillEllipse(in: ClawdPainter.shadowRect(pose, px: px, dy: dy))
        for q in pose.pixels {
            ctx.setFillColor(q.ink.cg)
            ctx.fill(ClawdPainter.rect(q, px: px, dy: dy))
        }
        for g in marks {
            let text = NSAttributedString(string: g.text, attributes: [
                .font: g.nsFont,
                .foregroundColor: NSColor(g.color),
            ])
            text.draw(at: CGPoint(x: g.x, y: g.y + dy))
        }
    }
}

struct SpriteRep: NSViewRepresentable {
    var look: ClawdLook

    func makeNSView(context _: Context) -> SpriteView {
        let v = SpriteView()
        v.look = look
        return v
    }

    func updateNSView(_ v: SpriteView, context _: Context) {
        v.look = look
    }
}

// MARK: - Talking to Claude (headless Claude Code)

final class ClaudeRunner {
    enum Event { case status(String), delta(String), done(result: String?, session: String?), failed(String) }

    private var process: Process?
    private var generation = 0
    private static var resolved: (path: String?, env: [String: String])?
    private static let resolveLock = NSLock()

    static let chatPrompt = """
    You are Claude, chatting through Clawd: a small pixel-art buddy that floats in the corner of the \
    user's Mac screen. Keep replies short and friendly, usually one to four sentences. Plain text only: \
    no headings, tables or code fences unless the user asks for code.
    """

    static let voicePrompt = """
    You are Claude, talking out loud through Clawd, a small desktop buddy on the user's Mac. Your reply \
    is read aloud by text-to-speech, so answer in one to three natural spoken sentences: no markdown, \
    lists, URLs, code or emoji.
    """

    /// GUI apps get a bare PATH, so ask the login shell where `claude` lives.
    static func resolve() -> (path: String?, env: [String: String]) {
        resolveLock.lock(); defer { resolveLock.unlock() }
        if let resolved { return resolved }
        var env = ProcessInfo.processInfo.environment
        var found: String?

        let shell = Process()
        shell.executableURL = URL(fileURLWithPath: "/bin/zsh")
        shell.arguments = ["-ilc", "printf '\\n__P__%s\\n__C__%s\\n' \"$PATH\" \"$(command -v claude)\""]
        let out = Pipe()
        shell.standardOutput = out
        shell.standardError = FileHandle.nullDevice
        shell.standardInput = FileHandle.nullDevice
        if (try? shell.run()) != nil {
            let deadline = Date().addingTimeInterval(6)
            while shell.isRunning, Date() < deadline { usleep(50000) }
            if shell.isRunning { shell.terminate() }
            let text = String(data: out.fileHandleForReading.availableData, encoding: .utf8) ?? ""
            for line in text.split(separator: "\n") {
                if line.hasPrefix("__P__") { env["PATH"] = String(line.dropFirst(5)) }
                if line.hasPrefix("__C__") {
                    let c = String(line.dropFirst(5))
                    if c.hasPrefix("/"), FileManager.default.isExecutableFile(atPath: c) { found = c }
                }
            }
        }
        if found == nil {
            let h = Pet.home.path
            let candidates = [
                "\(h)/.npm-global/bin/claude", "\(h)/.local/bin/claude", "\(h)/.claude/local/claude",
                "/opt/homebrew/bin/claude", "/usr/local/bin/claude",
            ]
            found = candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
        }
        let path = env["PATH"] ?? "/usr/bin:/bin"
        if let found {
            let dir = (found as NSString).deletingLastPathComponent
            if !path.split(separator: ":").contains(Substring(dir)) { env["PATH"] = "\(dir):\(path)" }
        }
        let r = (found, env)
        resolved = r
        return r
    }

    func cancel() {
        generation += 1
        if let p = process, p.isRunning { p.terminate() }
        process = nil
    }

    func ask(prompt: String, model: String, resume: String?, voice: Bool, on: @escaping @MainActor (Event) -> Void) {
        cancel()
        let gen = generation
        let emit: (Event) -> Void = { [weak self] ev in
            onMain { if self?.generation == gen { on(ev) } }
        }

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let (path, env) = ClaudeRunner.resolve()
            guard let path else {
                emit(.failed("I couldn't find the claude CLI. Install Claude Code, then try again."))
                return
            }
            try? FileManager.default.createDirectory(at: Pet.chatDir, withIntermediateDirectories: true)

            var args = [
                "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--model", model, "--strict-mcp-config", "--setting-sources", "",
                "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch",
                "--append-system-prompt", voice ? ClaudeRunner.voicePrompt : ClaudeRunner.chatPrompt,
            ]
            if let resume { args += ["--resume", resume] }

            let p = Process()
            p.executableURL = URL(fileURLWithPath: path)
            p.arguments = args
            p.environment = env
            p.currentDirectoryURL = Pet.chatDir
            let stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
            p.standardInput = stdin
            p.standardOutput = stdout
            p.standardError = stderr

            var buffer = Data()
            var sawText = false
            var finished = false
            var session: String?
            var errText = ""

            func handle(_ line: Data) {
                guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { return }
                let type = obj["type"] as? String
                if session == nil, let s = obj["session_id"] as? String { session = s }
                if type == "stream_event", let ev = obj["event"] as? [String: Any] {
                    let evType = ev["type"] as? String
                    if evType == "content_block_start", let block = ev["content_block"] as? [String: Any] {
                        let bt = block["type"] as? String
                        if bt == "text", sawText { emit(.delta("\n\n")) }
                        if bt == "server_tool_use" || bt == "tool_use" {
                            let name = block["name"] as? String ?? ""
                            emit(.status(name.contains("Fetch") || name.contains("fetch") ? "Reading a page…" : "Searching the web…"))
                        }
                    } else if evType == "content_block_delta", let delta = ev["delta"] as? [String: Any],
                              delta["type"] as? String == "text_delta", let text = delta["text"] as? String
                    {
                        sawText = true
                        emit(.delta(text))
                    }
                } else if type == "result" {
                    finished = true
                    let isError = obj["is_error"] as? Bool ?? false
                    let result = obj["result"] as? String
                    if isError {
                        emit(.failed(result?.isEmpty == false ? result! : "Claude couldn't answer that one."))
                    } else {
                        emit(.done(result: result, session: session))
                    }
                }
            }

            stdout.fileHandleForReading.readabilityHandler = { h in
                let chunk = h.availableData
                if chunk.isEmpty { h.readabilityHandler = nil; return }
                buffer.append(chunk)
                while let nl = buffer.firstIndex(of: 0x0A) {
                    let line = buffer.subdata(in: buffer.startIndex ..< nl)
                    buffer.removeSubrange(buffer.startIndex ... nl)
                    if !line.isEmpty { handle(line) }
                }
            }
            stderr.fileHandleForReading.readabilityHandler = { h in
                let chunk = h.availableData
                if chunk.isEmpty { h.readabilityHandler = nil; return }
                errText += String(data: chunk, encoding: .utf8) ?? ""
            }
            p.terminationHandler = { proc in
                // Give the pipes a beat to drain.
                DispatchQueue.global().asyncAfter(deadline: .now() + 0.25) {
                    if !finished, proc.terminationReason != .uncaughtSignal {
                        let tail = errText.split(separator: "\n").last.map(String.init) ?? ""
                        emit(.failed(tail.isEmpty ? "Claude stopped unexpectedly (exit \(proc.terminationStatus))." : tail))
                    }
                }
            }

            do {
                try p.run()
            } catch {
                emit(.failed("Couldn't start Claude: \(error.localizedDescription)"))
                return
            }
            onMain { if self?.generation == gen { self?.process = p } else { p.terminate() } }
            stdin.fileHandleForWriting.write(prompt.data(using: .utf8) ?? Data())
            try? stdin.fileHandleForWriting.close()
        }
    }
}

/// Cuts a streaming reply into speakable sentences.
struct SentenceChunker {
    private var buffer = ""

    mutating func feed(_ text: String) -> [String] {
        buffer += text
        var out: [String] = []
        let pattern = #"[.!?…]+["'”’)\]]*\s+|[:;]\s+|\n+"#
        var searchStart = buffer.startIndex
        while let r = buffer.range(of: pattern, options: .regularExpression, range: searchStart ..< buffer.endIndex) {
            let candidate = String(buffer[buffer.startIndex ..< r.upperBound])
            let isNewline = buffer[r].contains("\n")
            if candidate.count >= 28 || isNewline {
                buffer.removeSubrange(buffer.startIndex ..< r.upperBound)
                let c = Self.clean(candidate)
                if !c.isEmpty { out.append(c) }
                searchStart = buffer.startIndex
            } else {
                searchStart = r.upperBound
            }
        }
        return out
    }

    mutating func flush() -> [String] {
        let c = Self.clean(buffer)
        buffer = ""
        return c.isEmpty ? [] : [c]
    }

    static func clean(_ s: String) -> String {
        var t = s.replacingOccurrences(of: #"\[([^\]]+)\]\([^)]+\)"#, with: "$1", options: .regularExpression)
        t = t.replacingOccurrences(of: #"https?://\S+"#, with: "", options: .regularExpression)
        t = t.replacingOccurrences(of: #"[*_#`>|~]+"#, with: "", options: .regularExpression)
        t = t.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
        return t.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

// MARK: - Voice in, voice out

/// Used only from the main thread (delegate callbacks hop back to it).
final class Speaker: NSObject, AVSpeechSynthesizerDelegate, @unchecked Sendable {
    private let synth = AVSpeechSynthesizer()
    private(set) var pending = 0
    var onIdle: (@MainActor () -> Void)?
    var onWord: (@MainActor () -> Void)?
    var isSpeaking: Bool { pending > 0 }

    private lazy var voice: AVSpeechSynthesisVoice? = {
        let preferred = ["Zoe", "Ava", "Evan", "Nathan", "Joelle", "Samantha"]
        let voices = AVSpeechSynthesisVoice.speechVoices().filter { $0.language == "en-US" }
        func rank(_ v: AVSpeechSynthesisVoice) -> Int {
            let q = v.quality == .premium ? 30 : (v.quality == .enhanced ? 20 : 0)
            let name = preferred.firstIndex { v.name.contains($0) }.map { 10 - $0 } ?? 0
            return q + name
        }
        return voices.max { rank($0) < rank($1) } ?? AVSpeechSynthesisVoice(language: "en-US")
    }()

    override init() {
        super.init()
        synth.delegate = self
    }

    func say(_ text: String) {
        let u = AVSpeechUtterance(string: text)
        u.voice = voice
        u.rate = 0.52
        u.pitchMultiplier = 1.04
        u.postUtteranceDelay = 0.02
        pending += 1
        synth.speak(u)
    }

    func stop() {
        pending = 0
        synth.stopSpeaking(at: .immediate)
    }

    func speechSynthesizer(_: AVSpeechSynthesizer, didFinish _: AVSpeechUtterance) {
        onMain { [weak self] in
            guard let self, self.pending > 0 else { return }
            self.pending -= 1
            if self.pending == 0 { self.onIdle?() }
        }
    }

    func speechSynthesizer(_: AVSpeechSynthesizer, willSpeakRangeOfSpeechString _: NSRange, utterance _: AVSpeechUtterance) {
        onMain { [weak self] in self?.onWord?() }
    }
}

final class Listener {
    private let engine = AVAudioEngine()
    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "en-US"))
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var silenceTimer: Timer?
    private var timeoutTimer: Timer?
    private var latest = ""
    private var finalHandler: (@MainActor (String?) -> Void)?
    private var generation = 0
    private(set) var active = false

    static func authorize(_ done: @escaping @MainActor (Bool) -> Void) {
        SFSpeechRecognizer.requestAuthorization { status in
            guard status == .authorized else { onMain { done(false) }; return }
            AVCaptureDevice.requestAccess(for: .audio) { ok in onMain { done(ok) } }
        }
    }

    func start(
        silence: TimeInterval,
        noSpeechTimeout: TimeInterval,
        partial: @escaping @MainActor (String) -> Void,
        level: @escaping @MainActor (Double) -> Void,
        final: @escaping @MainActor (String?) -> Void
    ) throws {
        stop()
        generation += 1
        let gen = generation
        latest = ""
        finalHandler = final

        let req = SFSpeechAudioBufferRecognitionRequest()
        req.shouldReportPartialResults = true
        req.addsPunctuation = true
        if recognizer?.supportsOnDeviceRecognition == true { req.requiresOnDeviceRecognition = true }
        request = req

        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        input.removeTap(onBus: 0)
        // Deprecated in macOS 27, but its replacement (which throws) only exists
        // on 27+, and Clawd supports macOS 14 and later, so this one stays.
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in
            req.append(buffer)
            guard let ch = buffer.floatChannelData?[0] else { return }
            let n = Int(buffer.frameLength)
            var sum: Float = 0
            for i in 0 ..< n { sum += ch[i] * ch[i] }
            let rms = sqrt(sum / Float(max(n, 1)))
            let db = 20 * log10(max(rms, 1e-6))
            let lvl = Double(min(1, max(0, (db + 52) / 40)))
            onMain { level(lvl) }
        }
        engine.prepare()
        try engine.start()
        active = true

        task = recognizer?.recognitionTask(with: req) { [weak self] result, error in
            let text = result?.bestTranscription.formattedString
            let isFinal = result?.isFinal ?? false
            onMain {
                guard let self, self.generation == gen, self.active else { return }
                if let text, !text.isEmpty {
                    self.latest = text
                    partial(text)
                    self.armSilence(silence)
                }
                if isFinal || (error != nil && result == nil) { self.finish() }
            }
        }

        timeoutTimer = Timer.scheduledTimer(withTimeInterval: noSpeechTimeout, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.generation == gen, self.latest.isEmpty else { return }
                self.finish()
            }
        }
    }

    private func armSilence(_ seconds: TimeInterval) {
        silenceTimer?.invalidate()
        let gen = generation
        silenceTimer = Timer.scheduledTimer(withTimeInterval: seconds, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.generation == gen else { return }
                self.finish()
            }
        }
    }

    /// Stops listening and hands over what was heard.
    func finish() {
        guard active else { return }
        let text = latest.trimmingCharacters(in: .whitespacesAndNewlines)
        let handler = finalHandler
        stop()
        MainActor.assumeIsolated { handler?(text.isEmpty ? nil : text) }
    }

    /// Stops listening and drops what was heard.
    func stop() {
        silenceTimer?.invalidate(); silenceTimer = nil
        timeoutTimer?.invalidate(); timeoutTimer = nil
        if engine.isRunning { engine.stop() }
        engine.inputNode.removeTap(onBus: 0)
        request?.endAudio()
        task?.cancel()
        request = nil
        task = nil
        finalHandler = nil
        active = false
        generation += 1
    }
}

// MARK: - The pet's brain

@MainActor
final class PetModel: ObservableObject {
    @Published var mood: Mood = .idle
    @Published var draft = ""
    @Published var bubble: Bubble?
    @Published var dictating = false
    @Published var voiceMode = false
    @Published var level: Double = 0
    @Published var squish = false
    @Published var mouthOpen = false
    @Published var focusTick = 0
    @Published var clawdAway = false // off sparring with Codex
    @Published var reduceMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
    @Published var sparRandomly: Bool { didSet { defaults.set(sparRandomly, forKey: "sparRandomly") } }
    @Published var composerOpen: Bool { didSet { defaults.set(composerOpen, forKey: "composerOpen") } }
    @Published var speakReplies: Bool { didSet { defaults.set(speakReplies, forKey: "speakReplies") } }
    @Published var model: ClaudeModel { didSet { defaults.set(model.rawValue, forKey: "model") } }

    var hitRects: [CGRect] = []
    var petRect: CGRect = .zero // Clawd himself, window-local, top-left origin
    var dragging = false
    weak var window: NSWindow?

    private let defaults = UserDefaults.standard
    private let runner = ClaudeRunner()
    private let listener = Listener()
    private let speaker = Speaker()
    private var chunker = SentenceChunker()
    private var sessionID: String?
    private var chatBusy = false
    private var streamingText = false
    private var streamDone = true
    private var awaitingFinish = false
    private var replySpoken = false
    private var flashMood: Mood?
    private var flashUntil = Date.distantPast
    private var lastActivity = Date()
    private var code: CodeStatus?
    private var codeUpdated = Date.distantPast
    private var lastStatusMtime: Date?
    private var timers: [Timer] = []
    private var arena: NSPanel?
    private var nextRandomSpar = Date().addingTimeInterval(.random(in: 1200 ... 2400))

    init() {
        Self.carryOverFromClaudePet()
        sparRandomly = defaults.object(forKey: "sparRandomly") as? Bool ?? false
        composerOpen = defaults.object(forKey: "composerOpen") as? Bool ?? true
        speakReplies = defaults.object(forKey: "speakReplies") as? Bool ?? false
        model = ClaudeModel(rawValue: defaults.string(forKey: "model") ?? "") ?? .sonnet
        sessionID = defaults.string(forKey: "sessionID")
    }

    /// Before the rename the app was "Claude Pet" (local.claudepet.ClaudePet):
    /// keep its spot on screen, its settings and the sparring score.
    private static func carryOverFromClaudePet() {
        let d = UserDefaults.standard
        guard !d.bool(forKey: "carriedOverFromClaudePet"),
              let old = UserDefaults(suiteName: "local.claudepet.ClaudePet") else { return }
        let keys = ["originX", "originY", "composerOpen", "speakReplies", "model", "sparRandomly",
                    "score.clawd", "score.codex", "score.draw"]
        for key in keys where d.object(forKey: key) == nil {
            if let value = old.object(forKey: key) { d.set(value, forKey: key) }
        }
        d.set(true, forKey: "carriedOverFromClaudePet")
    }

    var placeholder: String {
        if voiceMode { return listener.active ? "Listening…" : "Voice mode · tap the orb to stop" }
        if dictating { return "Listening… tap the mic to stop" }
        return "Work with Claude"
    }

    func start() {
        try? FileManager.default.createDirectory(at: Pet.supportDir, withIntermediateDirectories: true)
        speaker.onIdle = { [weak self] in self?.speakerIdle() }
        speaker.onWord = { [weak self] in
            guard let self else { return }
            self.mouthOpen = true
            after(0.11) { [weak self] in self?.mouthOpen = false }
        }
        DispatchQueue.global(qos: .utility).async { _ = ClaudeRunner.resolve() }

        timers.append(Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.pollStatus() }
        })
        timers.append(Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.refreshMood()
                self.maybeSparOnItsOwn()
                if Int(Date().timeIntervalSince1970) % 3 == 0 { self.checkCodexPet() }
                let calm = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
                if calm != self.reduceMotion { self.reduceMotion = calm }
            }
        })
        pollStatus()
        flash(.happy, 1.6)
        if !defaults.bool(forKey: "welcomed") {
            defaults.set(true, forKey: "welcomed")
            let hello = Bubble(
                kind: .code,
                status: "Hi, I'm Clawd!",
                body: "**Click me** to open Claude · **⌃⌥C** to type to me · tap the **orb** to talk · **drag** me anywhere · **right-click** for more (try *Spar with Codex* 🥊)"
            )
            bubble = hello
            after(25) { [weak self] in if self?.bubble?.id == hello.id { self?.bubble = nil } }
        }
    }

    // MARK: Mood

    func refreshMood() {
        let now = Date()
        let codeFresh = now.timeIntervalSince(codeUpdated) < 30 * 60
        let next: Mood
        if listener.active { next = .listening }
        else if speaker.isSpeaking { next = .speaking }
        else if chatBusy, streamingText { next = .speaking }
        else if chatBusy { next = .thinking }
        else if let f = flashMood, flashUntil > now { next = f }
        else if codeFresh, code?.state == "working" { next = .working }
        else if codeFresh, code?.state == "needs" { next = .alert }
        else if now.timeIntervalSince(lastActivity) > 300 { next = .sleepy }
        else { next = .idle }
        if next != mood { mood = next }
    }

    func flash(_ m: Mood, _ seconds: Double) {
        flashMood = m
        flashUntil = Date().addingTimeInterval(seconds)
        refreshMood()
        after(seconds + 0.05) { [weak self] in self?.refreshMood() }
    }

    func poke() {
        lastActivity = Date()
        if mood == .sleepy { refreshMood() }
    }

    /// The global shortcut: open the composer and put the cursor in it.
    func summon() {
        poke()
        if !composerOpen { composerOpen = true }
        window?.orderFrontRegardless()
        window?.makeKey()
        after(0.05) { [weak self] in self?.focusTick += 1 }
    }

    private func flapMouth() {
        mouthOpen = true
        after(0.09) { [weak self] in self?.mouthOpen = false }
    }

    // MARK: Clicks

    func petClicked() {
        poke()
        withAnimation(.spring(response: 0.18, dampingFraction: 0.45)) { squish = true }
        after(0.13) { [weak self] in
            withAnimation(.spring(response: 0.3, dampingFraction: 0.5)) { self?.squish = false }
        }
        openClaude()
    }

    func openClaude() {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: Pet.claudeBundleID) else {
            showError("I couldn't find the Claude app on this Mac.")
            return
        }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        NSWorkspace.shared.openApplication(at: url, configuration: config)
    }

    func newChat() {
        runner.cancel()
        speaker.stop()
        chatBusy = false
        sessionID = nil
        defaults.removeObject(forKey: "sessionID")
        bubble = nil
        flash(.happy, 1.2)
    }

    func dismissBubble() {
        bubble = nil
    }

    func copyBubble() {
        guard let b = bubble else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(b.body.isEmpty ? b.detail : b.body, forType: .string)
    }

    func escape() {
        if voiceMode { stopVoice(); return }
        if dictating { stopDictation(); return }
        if chatBusy { runner.cancel(); speaker.stop(); chatBusy = false; bubble = nil; refreshMood(); return }
        if !draft.isEmpty { draft = ""; return }
        bubble = nil
    }

    // MARK: Chat

    func submitDraft() {
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        if dictating { stopDictation() }
        draft = ""
        ask(text, spoken: false)
    }

    func ask(_ text: String, spoken: Bool) {
        lastActivity = Date()
        speaker.stop()
        chunker = SentenceChunker()
        chatBusy = true
        streamingText = false
        streamDone = false
        awaitingFinish = true
        replySpoken = spoken || speakReplies
        bubble = Bubble(kind: .chat, status: "Thinking…", prompt: text)
        refreshMood()

        runner.ask(prompt: text, model: model.rawValue, resume: sessionID, voice: spoken) { [weak self] ev in
            self?.handle(ev)
        }
    }

    private func handle(_ ev: ClaudeRunner.Event) {
        switch ev {
        case let .status(s):
            if bubble?.kind == .chat { bubble?.status = s }
        case let .delta(d):
            if bubble?.kind == .chat {
                bubble?.body += d
                bubble?.status = "Claude"
            }
            if replySpoken {
                for s in chunker.feed(d) { speaker.say(s) }
            } else {
                // Silent replies: he "talks" as the words stream in.
                streamingText = true
                flapMouth()
            }
        case let .done(result, session):
            if let session {
                sessionID = session
                defaults.set(session, forKey: "sessionID")
            }
            if bubble?.kind == .chat {
                if bubble?.body.isEmpty == true, let result {
                    // Nothing streamed: show the whole reply, and read all of it
                    // aloud (feed's full sentences, then flush's remainder below).
                    bubble?.body = result
                    if replySpoken {
                        chunker = SentenceChunker()
                        for s in chunker.feed(result) { speaker.say(s) }
                    }
                }
                bubble?.status = "Claude"
            }
            if replySpoken { for s in chunker.flush() { speaker.say(s) } }
            chatBusy = false
            streamingText = false
            streamDone = true
            if !speaker.isSpeaking { replyFinished() }
        case let .failed(message):
            chatBusy = false
            streamingText = false
            streamDone = true
            awaitingFinish = false
            if message.lowercased().contains("session") { sessionID = nil; defaults.removeObject(forKey: "sessionID") }
            showError(message)
            if voiceMode { stopVoice() }
        }
        refreshMood()
    }

    private func speakerIdle() {
        refreshMood()
        if streamDone { replyFinished() }
    }

    private func replyFinished() {
        guard awaitingFinish else { return }
        awaitingFinish = false
        lastActivity = Date()
        flash(.happy, 2.2)
        if voiceMode {
            after(0.35) { [weak self] in self?.listen() }
        } else if let id = bubble?.id {
            after(75) { [weak self] in
                guard let self, self.bubble?.id == id, !self.chatBusy else { return }
                self.bubble = nil
            }
        }
    }

    func showError(_ message: String) {
        bubble = Bubble(kind: .error, status: "Hmm…", body: message)
        flash(.alert, 2.5)
    }

    // MARK: Voice

    func toggleVoice() {
        if voiceMode { stopVoice(); return }
        Listener.authorize { [weak self] ok in
            guard let self else { return }
            guard ok else {
                self.showError("Clawd needs the microphone and speech recognition. Turn both on for Clawd Pet in System Settings › Privacy & Security.")
                return
            }
            if self.dictating { self.stopDictation() }
            self.voiceMode = true
            self.listen()
        }
    }

    private func listen() {
        guard voiceMode else { return }
        speaker.stop()
        draft = ""
        do {
            try listener.start(
                silence: 1.3,
                noSpeechTimeout: 14,
                partial: { [weak self] text in self?.draft = text },
                level: { [weak self] l in self?.level = l },
                final: { [weak self] text in self?.heard(text) }
            )
        } catch {
            voiceMode = false
            showError("Couldn't start the microphone: \(error.localizedDescription)")
        }
        refreshMood()
    }

    private func heard(_ text: String?) {
        level = 0
        guard voiceMode else { return }
        guard let text, !text.isEmpty else {
            stopVoice()
            return
        }
        draft = ""
        ask(text, spoken: true)
    }

    func stopVoice() {
        voiceMode = false
        listener.stop()
        speaker.stop()
        level = 0
        if draft.isEmpty == false, !chatBusy { draft = "" }
        refreshMood()
    }

    // MARK: Dictation

    func toggleDictation() {
        if dictating { stopDictation(); return }
        Listener.authorize { [weak self] ok in
            guard let self else { return }
            guard ok else {
                self.showError("Dictation needs the microphone and speech recognition. Turn both on for Clawd Pet in System Settings › Privacy & Security.")
                return
            }
            if self.voiceMode { self.stopVoice() }
            let prefix = self.draft.isEmpty ? "" : self.draft + " "
            do {
                try self.listener.start(
                    silence: 2.6,
                    noSpeechTimeout: 10,
                    partial: { [weak self] text in self?.draft = prefix + text },
                    level: { [weak self] l in self?.level = l },
                    final: { [weak self] _ in
                        self?.dictating = false
                        self?.level = 0
                        self?.refreshMood()
                    }
                )
                self.dictating = true
            } catch {
                self.showError("Couldn't start the microphone: \(error.localizedDescription)")
            }
            self.refreshMood()
        }
    }

    func stopDictation() {
        listener.finish()
        dictating = false
        level = 0
        refreshMood()
    }

    // MARK: Claude Code status (written by the clawd-pet mod)

    private func pollStatus() {
        let url = Pet.statusFile
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let mtime = attrs[.modificationDate] as? Date else { return }
        if mtime == lastStatusMtime { return }
        let first = lastStatusMtime == nil
        lastStatusMtime = mtime
        guard let data = try? Data(contentsOf: url),
              let status = try? JSONDecoder().decode(CodeStatus.self, from: data) else { return }
        if first, Date().timeIntervalSince(mtime) > 120 { return }
        applyCode(status)
    }

    private func applyCode(_ s: CodeStatus) {
        code = s
        codeUpdated = Date()
        lastActivity = Date()
        let chatHolds = bubble?.kind == .chat || bubble?.kind == .error || voiceMode || dictating

        func show(_ status: String) {
            guard !chatHolds else { return }
            if bubble?.kind == .code {
                bubble?.status = status
                bubble?.prompt = s.title ?? ""
                bubble?.detail = s.detail ?? ""
            } else {
                bubble = Bubble(kind: .code, status: status, prompt: s.title ?? "", detail: s.detail ?? "")
            }
        }

        let place = (s.project?.isEmpty == false) ? " · \(s.project!)" : ""
        switch s.state {
        case "working":
            show("Claude is working\(place)")
        case "needs":
            show("Claude needs you\(place)")
            flash(.alert, 4)
        case "done":
            show("Done ✓\(place)")
            flash(.happy, 3)
            if let id = bubble?.id, bubble?.kind == .code {
                after(12) { [weak self] in
                    guard let self, self.bubble?.id == id, self.code?.state != "working" else { return }
                    self.bubble = nil
                }
            }
        case "error":
            show("Claude stopped")
        default:
            if bubble?.kind == .code { bubble = nil }
        }
        refreshMood()
    }

    // MARK: Sparring with Codex

    var score: (clawd: Int, codex: Int, draws: Int) {
        (defaults.integer(forKey: "score.clawd"), defaults.integer(forKey: "score.codex"), defaults.integer(forKey: "score.draw"))
    }

    private func maybeSparOnItsOwn() {
        guard sparRandomly, Date() > nextRandomSpar else { return }
        nextRandomSpar = Date().addingTimeInterval(.random(in: 1200 ... 2400))
        let quiet = !chatBusy && !voiceMode && !dictating && mood != .working && mood != .alert
        if quiet { startFight(onlyIfCodexIsOut: true) }
    }

    /// Whether the Codex pet is on screen right now (the menu reads this).
    @Published var codexPetOut = false

    func checkCodexPet() {
        let out = FightKit.codexPetCenter() != nil
        if out != codexPetOut { codexPetOut = out }
    }

    /// Clawd runs over to the Codex pet, they vanish into a cartoon brawl
    /// cloud, and he walks home with the result. Purely for fun, and only when
    /// the Codex pet is actually out: otherwise he says how to summon it.
    func startFight(onlyIfCodexIsOut: Bool = false) {
        guard arena == nil, !voiceMode, let w = window, let screen = w.screen ?? NSScreen.main else { return }
        checkCodexPet()
        guard codexPetOut else {
            if !onlyIfCodexIsOut {
                let note = Bubble(
                    kind: .code,
                    status: "No one to spar with",
                    body: "The Codex pet isn't on screen. Open the **Codex app** and type **/pet** to bring it out, then try again. 🥊"
                )
                bubble = note
                flash(.alert, 2)
                after(12) { [weak self] in if self?.bubble?.id == note.id { self?.bubble = nil } }
            }
            return
        }
        poke()
        let sf = screen.frame
        let toArena = { (p: CGPoint) in CGPoint(x: p.x - sf.minX, y: sf.maxY - p.y) }

        let petW = CGFloat(Pet.gridCols) * Pet.px, petH = CGFloat(Pet.gridRows) * Pet.px
        let stackH = composerOpen ? Pet.composerTop + Pet.composerSize.height : petH
        let home = toArena(CGPoint(x: w.frame.minX + 10 + Pet.petInset, y: w.frame.minY + 10 + stackH))
        let clawdCenter = CGPoint(x: home.x + petW / 2, y: home.y + petH / 2)

        let codex = FightKit.codexPetCenter().map(toArena).flatMap { p -> CGPoint? in
            CGRect(origin: .zero, size: sf.size).insetBy(dx: 20, dy: 20).contains(p) ? p : nil
        }
        guard let target = codex else { // the Codex pet is on another screen
            let far = Bubble(
                kind: .code,
                status: "Too far to spar",
                body: "The Codex pet is on another screen. Drag Clawd over to it, then try again. 🥊"
            )
            bubble = far
            flash(.alert, 2)
            after(10) { [weak self] in if self?.bubble?.id == far.id { self?.bubble = nil } }
            return
        }
        let dir: CGFloat = target.x < clawdCenter.x ? -1 : 1
        let spotY = abs(target.y - clawdCenter.y) > 120 ? target.y - petH / 2 : home.y
        let spot = CGPoint(x: target.x - dir * 74 - petW / 2, y: spotY)
        let cloud = CGPoint(x: (spot.x + petW / 2 + target.x) / 2, y: (spot.y + petH / 2 + target.y) / 2 - 6)

        let roll = Double.random(in: 0 ..< 1)
        let outcome: FightOutcome = roll < 0.45 ? .clawd : (roll < 0.85 ? .codex : .draw)
        let taunt = ["Hey Codex! Spar? ✳", "En garde, robot!", "Tabs vs spaces. Settle it!", "Last to ship buys tokens!", "Code review… in the ring!"].randomElement()!
        let result: String = switch outcome {
        case .clawd: ["Clawd wins! ✳", "Flawless refactor!", "GG, Codex 🤝", "Ship it! 🏆"].randomElement()!
        case .codex: ["Oof. Codex got me.", "Rematch after this build…", "Codex wins this round!"].randomElement()!
        case .draw: ["Draw! Pair-programming it is.", "Tie! Merge conflict resolved.", "Even match. Friends again!"].randomElement()!
        }
        let script = FightScript(
            start: Date(), home: home, spot: spot, cloud: cloud,
            dir: dir, outcome: outcome, taunt: taunt, result: result, seed: Int.random(in: 0 ... 9999)
        )

        let panel = PetPanel(contentRect: sf, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue + 1)
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        let host = NSHostingView(rootView: FightView(s: script))
        host.sizingOptions = []
        host.frame = CGRect(origin: .zero, size: sf.size)
        panel.contentView = host
        panel.orderFrontRegardless()
        arena = panel
        clawdAway = true
        if bubble?.kind != .chat { bubble = nil }

        after(FightScript.duration) { [weak self] in
            guard let self else { return }
            self.arena?.orderOut(nil)
            self.arena = nil
            self.clawdAway = false
            let key: String = switch outcome {
            case .clawd: "score.clawd"
            case .codex: "score.codex"
            case .draw: "score.draw"
            }
            self.defaults.set(self.defaults.integer(forKey: key) + 1, forKey: key)
            let s = self.score
            let b = Bubble(kind: .code, status: "Clawd vs Codex", body: result, detail: "Clawd \(s.clawd) · Codex \(s.codex) · Draws \(s.draws)")
            self.bubble = b
            self.flash(outcome == .codex ? .dizzy : .happy, 2.4)
            after(9) { [weak self] in if self?.bubble?.id == b.id { self?.bubble = nil } }
        }
    }

    // MARK: Links (clawdpet://ask?q=…, clawdpet://voice, clawdpet://new, clawdpet://fight)

    func handle(_ url: URL) {
        guard url.scheme == "clawdpet" else { return }
        let q = URLComponents(url: url, resolvingAgainstBaseURL: false)?
            .queryItems?.first { $0.name == "q" }?.value?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        switch url.host {
        case "ask":
            if let q, !q.isEmpty { ask(q, spoken: false) }
        case "voice":
            toggleVoice()
        case "new":
            newChat()
        case "fight", "spar":
            startFight()
        default:
            break
        }
    }

    // MARK: Window position

    func initialOrigin() -> CGPoint {
        if defaults.object(forKey: "originX") != nil {
            let p = CGPoint(x: defaults.double(forKey: "originX"), y: defaults.double(forKey: "originY"))
            let probe = CGRect(origin: p, size: Pet.windowSize).insetBy(dx: 120, dy: 120)
            if NSScreen.screens.contains(where: { $0.frame.intersects(probe) }) { return p }
        }
        return Self.homeOrigin()
    }

    /// Bottom-right corner, just right of where the Codex pet docks.
    static func homeOrigin() -> CGPoint {
        let s = NSScreen.main ?? NSScreen.screens[0]
        return CGPoint(x: s.frame.maxX - Pet.windowSize.width - 20, y: s.visibleFrame.minY + 10)
    }

    func snapHome() {
        defaults.removeObject(forKey: "originX")
        defaults.removeObject(forKey: "originY")
        window?.setFrameOrigin(Self.homeOrigin())
    }

    func savePosition() {
        guard let o = window?.frame.origin else { return }
        defaults.set(o.x, forKey: "originX")
        defaults.set(o.y, forKey: "originY")
    }

    // MARK: Login item

    var loginEnabled: Bool { SMAppService.mainApp.status == .enabled }

    func setLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            showError("Couldn't change Open at Login: \(error.localizedDescription)")
        }
        objectWillChange.send()
    }
}

// MARK: - Views

struct HitRectsKey: PreferenceKey {
    static let defaultValue: [CGRect] = []
    static func reduce(value: inout [CGRect], nextValue: () -> [CGRect]) { value += nextValue() }
}

extension View {
    /// Marks a region that takes the mouse; everything else clicks through.
    func hitArea() -> some View {
        background(GeometryReader { g in
            Color.clear.preference(key: HitRectsKey.self, value: [g.frame(in: .named("root"))])
        })
    }
}

struct RootView: View {
    @ObservedObject var m: PetModel

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Spacer(minLength: 0)
            if let b = m.bubble {
                BubbleView(m: m, b: b)
                    .hitArea()
                    .transition(.asymmetric(
                        insertion: .scale(scale: 0.85, anchor: .bottomLeading).combined(with: .opacity),
                        removal: .opacity
                    ))
            }
            ZStack(alignment: .topLeading) {
                if m.composerOpen {
                    ComposerView(m: m)
                        .hitArea()
                        .padding(.top, Pet.composerTop)
                        .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .topLeading)))
                }
                PetView(m: m)
                    .hitArea()
                    .padding(.leading, Pet.petInset)
            }
        }
        .padding(.leading, 10)
        .padding(.bottom, 10)
        .frame(width: Pet.windowSize.width, height: Pet.windowSize.height, alignment: .bottomLeading)
        .coordinateSpace(name: "root")
        .onPreferenceChange(HitRectsKey.self) { rects in
            MainActor.assumeIsolated { m.hitRects = rects }
        }
        .onPreferenceChange(PetRectKey.self) { rect in
            MainActor.assumeIsolated { m.petRect = rect }
        }
        .animation(.spring(response: 0.34, dampingFraction: 0.82), value: m.bubble?.id)
        .animation(.spring(response: 0.34, dampingFraction: 0.86), value: m.composerOpen)
    }
}

struct PetView: View {
    @ObservedObject var m: PetModel
    @State private var hover = false

    var body: some View {
        ZStack(alignment: .topLeading) {
            if !m.clawdAway {
                SpriteRep(look: ClawdLook(mood: m.mood, hover: hover, mouthOpen: m.mouthOpen, calm: m.reduceMotion))
                    .frame(
                        width: CGFloat(Pet.gridCols) * Pet.px + SpriteView.sideroom,
                        height: CGFloat(Pet.gridRows) * Pet.px + SpriteView.headroom
                    )
                    .offset(y: -SpriteView.headroom)
                    .allowsHitTesting(false)
            }
        }
        .frame(width: CGFloat(Pet.gridCols) * Pet.px, height: CGFloat(Pet.gridRows) * Pet.px)
        .background(GeometryReader { g in
            Color.clear.preference(key: PetRectKey.self, value: g.frame(in: .named("root")))
        })
        .scaleEffect(x: m.squish ? 1.14 : 1, y: m.squish ? 0.84 : 1, anchor: .bottom)
        .contentShape(Rectangle())
        .onHover { h in
            hover = h
            if h { m.poke() }
        }
        // Left clicks and drags on Clawd are handled by PetPanel.sendEvent.
        .contextMenu { PetMenu(m: m) }
    }
}

struct PetRectKey: PreferenceKey {
    static let defaultValue: CGRect = .zero
    static func reduce(value: inout CGRect, nextValue: () -> CGRect) {
        let n = nextValue()
        if n != .zero { value = n }
    }
}

struct PetMenu: View {
    @ObservedObject var m: PetModel

    var body: some View {
        Button("Open Claude") { m.openClaude() }
        Button("New chat") { m.newChat() }
        Divider()
        if m.codexPetOut {
            Button("Spar with Codex 🥊") { m.startFight() }
        } else {
            Button("Spar with Codex: open the Codex pet first") {}.disabled(true)
        }
        Toggle("Spar now and then", isOn: $m.sparRandomly)
        Divider()
        Button(m.composerOpen ? "Mini mode (hide composer)" : "Show composer") { m.composerOpen.toggle() }
        Toggle("Speak replies", isOn: $m.speakReplies)
        Menu("Model") {
            ForEach(ClaudeModel.allCases) { mm in
                Toggle("\(mm.label) · \(mm.blurb)", isOn: Binding(get: { m.model == mm }, set: { if $0 { m.model = mm } }))
            }
        }
        Divider()
        Button("Snap back next to Codex") { m.snapHome() }
        Toggle("Open at login", isOn: Binding(get: { m.loginEnabled }, set: { m.setLogin($0) }))
        Divider()
        Button("Hide Clawd") { NSApp.terminate(nil) }
    }
}

final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, checked: Bool = false, _ handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: "")
        target = self
        state = checked ? .on : .off
    }

    @available(*, unavailable)
    required init(coder _: NSCoder) { fatalError() }

    @objc private func fire() { handler() }
}

struct ComposerView: View {
    @ObservedObject var m: PetModel
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            TextField("", text: $m.draft, prompt: Text(m.placeholder).foregroundColor(.dim), axis: .vertical)
                .textFieldStyle(.plain)
                .font(.system(size: 14, design: .monospaced))
                .foregroundColor(.white)
                .lineLimit(1 ... 2)
                .focused($focused)
                .onSubmit { m.submitDraft() }
                .onExitCommand { m.escape() }
                .padding(.leading, Pet.textInset)
                .padding(.trailing, 16)
                .padding(.top, 14)
            Spacer(minLength: 0)
            HStack(spacing: 10) {
                Button { m.newChat() } label: {
                    Image(systemName: "plus")
                        .font(.system(size: 15, weight: .medium))
                        .foregroundColor(.white.opacity(0.85))
                        .frame(width: 28, height: 28)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("New chat")

                Button { m.speakReplies.toggle() } label: {
                    HStack(spacing: 5) {
                        Image(systemName: m.speakReplies ? "speaker.wave.2" : "speaker.slash")
                        Text(m.speakReplies ? "Speak replies" : "Silent")
                    }
                    .font(.system(size: 12.5, design: .monospaced))
                    .foregroundColor(m.speakReplies ? .white.opacity(0.85) : .dim)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)

                Spacer(minLength: 4)

                Button { popModelMenu() } label: {
                    HStack(spacing: 4) {
                        Text(m.model.label)
                            .font(.system(size: 13, design: .monospaced))
                            .foregroundColor(.white)
                        Image(systemName: "chevron.down")
                            .font(.system(size: 9, weight: .semibold))
                            .foregroundColor(.dim)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)

                Button { m.toggleDictation() } label: {
                    Image(systemName: m.dictating ? "mic.fill" : "mic")
                        .font(.system(size: 15, weight: .medium))
                        .foregroundColor(m.dictating ? .clawd : .white.opacity(0.85))
                        .scaleEffect(m.dictating ? 1 + m.level * 0.25 : 1)
                        .frame(width: 28, height: 28)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("Dictate")

                Button { m.toggleVoice() } label: { VoiceOrb(active: m.voiceMode, level: m.level) }
                    .buttonStyle(.plain)
                    .help("Talk with Claude")
            }
            .padding(.leading, 12)
            .padding(.trailing, 10)
            .padding(.bottom, 10)
        }
        .frame(width: Pet.composerSize.width, height: Pet.composerSize.height)
        .background(
            RoundedRectangle(cornerRadius: 26, style: .continuous)
                .fill(Color.panelFill)
                .overlay(RoundedRectangle(cornerRadius: 26, style: .continuous).strokeBorder(Color.panelStroke, lineWidth: 1))
                .shadow(color: .black.opacity(0.38), radius: 16, y: 6)
        )
        .overlay(alignment: .topLeading) {
            // The little grab pill under the pet, like the Codex composer has.
            Capsule()
                .fill(Color.white.opacity(0.22))
                .frame(width: 22, height: 4)
                .offset(x: Pet.petCenterX - 11, y: 36)
        }
        .onChange(of: m.voiceMode) { _, on in if on { focused = false } }
        .onChange(of: m.focusTick) { _, _ in focused = true }
    }

    private func popModelMenu() {
        let menu = NSMenu()
        for mm in ClaudeModel.allCases {
            menu.addItem(ClosureMenuItem("\(mm.label)   \(mm.blurb)", checked: m.model == mm) { [m] in
                MainActor.assumeIsolated { m.model = mm }
            })
        }
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
    }
}

struct VoiceOrb: View {
    let active: Bool
    let level: Double

    var body: some View {
        ZStack {
            if active {
                Circle()
                    .stroke(Color.clawd.opacity(0.55), lineWidth: 2)
                    .scaleEffect(1 + level * 0.55)
                    .animation(.easeOut(duration: 0.12), value: level)
            }
            Circle().fill(active ? Color.clawd : Color.white)
            if active {
                HStack(spacing: 2.5) {
                    ForEach(0 ..< 4, id: \.self) { i in
                        let wobble = [0.55, 1.0, 0.8, 0.45][i]
                        Capsule()
                            .fill(Color.white)
                            .frame(width: 2.6, height: 5 + 11 * max(0.15, level) * wobble)
                    }
                }
                .animation(.easeOut(duration: 0.1), value: level)
            } else {
                Image(systemName: "waveform")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(.black)
            }
        }
        .frame(width: 34, height: 34)
        .contentShape(Circle())
    }
}

struct BubbleShape: Shape {
    var tailX: CGFloat
    var tail: CGFloat = 8

    func path(in rect: CGRect) -> Path {
        var p = Path()
        let body = CGRect(x: rect.minX, y: rect.minY, width: rect.width, height: rect.height - tail)
        p.addRoundedRect(in: body, cornerSize: CGSize(width: 16, height: 16), style: .continuous)
        p.move(to: CGPoint(x: tailX - 8, y: body.maxY - 1))
        p.addLine(to: CGPoint(x: tailX, y: rect.maxY))
        p.addLine(to: CGPoint(x: tailX + 8, y: body.maxY - 1))
        p.closeSubpath()
        return p
    }
}

struct BubbleView: View {
    @ObservedObject var m: PetModel
    let b: Bubble

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Text("✳")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundColor(b.kind == .error ? .alertYellow : .clawd)
                Text(b.status)
                    .font(.system(size: 11.5, weight: .semibold, design: .monospaced))
                    .foregroundColor(.white.opacity(0.85))
                Spacer(minLength: 4)
                iconButton("arrow.up.right", help: "Open Claude") { m.openClaude() }
                if !(b.body.isEmpty && b.detail.isEmpty) {
                    iconButton("doc.on.doc", help: "Copy") { m.copyBubble() }
                }
                iconButton("xmark", help: "Dismiss") { m.dismissBubble() }
            }
            if !b.prompt.isEmpty {
                Text(b.kind == .code ? "“\(b.prompt)”" : b.prompt)
                    .font(.system(size: 11.5, design: .monospaced))
                    .foregroundColor(.dim)
                    .lineLimit(2)
            }
            if !b.detail.isEmpty {
                HStack(spacing: 6) {
                    Circle().fill(Color.clawd).frame(width: 6, height: 6)
                    Text(b.detail)
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundColor(.white.opacity(0.8))
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }
            if !b.body.isEmpty {
                if b.body.count > 360 {
                    ScrollViewReader { proxy in
                        ScrollView(showsIndicators: false) {
                            bodyText.id("end")
                        }
                        .frame(height: 170)
                        .onChange(of: b.body) { _, _ in proxy.scrollTo("end", anchor: .bottom) }
                    }
                } else {
                    bodyText
                }
            }
        }
        .padding(.horizontal, 13)
        .padding(.top, 10)
        .padding(.bottom, 10 + 8)
        .frame(width: 400, alignment: .leading)
        .background(
            BubbleShape(tailX: Pet.petCenterX)
                .fill(Color.panelFill.opacity(0.98))
                .overlay(BubbleShape(tailX: Pet.petCenterX).stroke(Color.panelStroke, lineWidth: 1))
                .shadow(color: .black.opacity(0.35), radius: 12, y: 4)
        )
    }

    private var bodyText: some View {
        Text(Self.markdown(b.body))
            .font(.system(size: 13.5, design: .serif))
            .foregroundColor(.white.opacity(0.93))
            .lineSpacing(2)
            .textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
            .fixedSize(horizontal: false, vertical: true)
    }

    /// Bold, italics, `code` and links render; anything half-streamed stays literal.
    static func markdown(_ s: String) -> AttributedString {
        let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        return (try? AttributedString(markdown: s, options: options)) ?? AttributedString(s)
    }

    private func iconButton(_ name: String, help: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: name)
                .font(.system(size: 10.5, weight: .semibold))
                .foregroundColor(.dim)
                .frame(width: 20, height: 20)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

// MARK: - Sparring with the Codex pet

enum FightOutcome { case clawd, codex, draw }

struct FightScript {
    static let duration = 6.6
    var start: Date
    var home: CGPoint // Clawd's canvas top-left, in arena (screen, top-left) coordinates
    var spot: CGPoint // where he stops beside Codex
    var cloud: CGPoint // center of the brawl
    var dir: CGFloat // -1 runs left, +1 runs right
    var outcome: FightOutcome
    var taunt: String
    var result: String
    var seed: Int
}

enum FightKit {
    // Codex draws its pet inside a floating window that also holds the pet's
    // tray, quick chat and captions, so where the pet sits inside that window
    // changes with its layout. Clawd reads where Codex itself says the pet is:
    // Codex saves the pet's top-left corner and the screen it's on (in points)
    // to ~/.codex/.codex-global-state.json whenever the pet is dropped
    // ("electron-avatar-overlay-bounds"), along with its size setting
    // ("avatar-overlay-mascot-width-px", 80 to 224, 112 by default; frames are
    // 192 x 208). A pet that was never moved sits at Codex's default spot, the
    // bottom right of the visible screen, 24 points in. The spot only counts
    // while one of Codex's floating windows covers it.

    /// A saved spot: the pet's center from its screen's top-left, and that screen's size, in points.
    struct Spot: Equatable { var center: CGPoint; var screen: CGSize }

    struct SavedPet {
        var spots: [Spot] = []
        var size = CGSize(width: 112, height: 122)
        var note = "no saved spot (the pet was never moved)"
    }

    static var codexStateURL: URL {
        let env = ProcessInfo.processInfo.environment["CODEX_HOME"].flatMap { $0.isEmpty ? nil : $0 }
        let home = env.map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        return home.appendingPathComponent(".codex-global-state.json")
    }

    nonisolated(unsafe) private static var cached: (stamp: Date, pet: SavedPet)?

    /// Codex's saved pet position and size, read again only when the file changes.
    static func savedPet() -> SavedPet {
        let url = codexStateURL
        let stamp = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
        if let c = cached, c.stamp == stamp { return c.pet }
        var pet = SavedPet()
        func num(_ v: Any?) -> CGFloat? { (v as? NSNumber).map { CGFloat($0.doubleValue) } }
        if let data = try? Data(contentsOf: url),
           let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] {
            var width: CGFloat = 112
            if let atoms = root["electron-persisted-atom-state"] as? [String: Any],
               let w = num(atoms["avatar-overlay-mascot-width-px"]) {
                width = min(224, max(80, w.rounded()))
            }
            pet.size = CGSize(width: width, height: ceil(width * 208 / 192))
            if let bounds = root["electron-avatar-overlay-bounds"] as? [String: Any] {
                var records = [bounds] // the latest one first
                for key in ["byDisplayId", "byResolution"] {
                    if let map = bounds[key] as? [String: Any] { records += map.values.compactMap { $0 as? [String: Any] } }
                }
                for r in records {
                    guard let x = num(r["x"]), let y = num(r["y"]), let d = r["displayBounds"] as? [String: Any],
                          let dx = num(d["x"]), let dy = num(d["y"]), let dw = num(d["width"]), let dh = num(d["height"]) else { continue }
                    let spot = Spot(center: CGPoint(x: x - dx + pet.size.width / 2, y: y - dy + pet.size.height / 2),
                                    screen: CGSize(width: dw, height: dh))
                    if !pet.spots.contains(spot) { pet.spots.append(spot) }
                }
            }
            if !pet.spots.isEmpty { pet.note = "\(pet.spots.count) saved spot(s)" }
        } else {
            pet.note = "couldn't read Codex's settings"
        }
        cached = (stamp, pet)
        return pet
    }

    /// Codex's floating windows (the pet's among them), in top-left screen coordinates.
    static func floatingCodexWindows() -> [CGRect] {
        let opts: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(opts, kCGNullWindowID) as? [[String: Any]] else { return [] }
        return list.compactMap { w -> CGRect? in
            let owner = w[kCGWindowOwnerName as String] as? String ?? ""
            guard owner == "ChatGPT" || owner == "Codex", // not "Codex Computer Use"
                  (w[kCGWindowLayer as String] as? Int ?? 0) > 0,
                  let d = w[kCGWindowBounds as String] as? NSDictionary,
                  let r = CGRect(dictionaryRepresentation: d as CFDictionary),
                  r.width >= 40, r.height >= 40 else { return nil }
            return r
        }
    }

    /// The Codex pet's center in Cocoa screen coordinates, or nil when it isn't out.
    static func codexPetCenter() -> CGPoint? { locate().center }

    static func locate() -> (center: CGPoint?, how: String) {
        let windows = floatingCodexWindows()
        guard !windows.isEmpty else { return (nil, "no floating Codex window") }
        let pet = savedPet()
        let primaryTop = NSScreen.screens.first?.frame.maxY ?? 0
        func topLeft(_ r: CGRect) -> CGRect { CGRect(x: r.minX, y: primaryTop - r.maxY, width: r.width, height: r.height) }
        let screens = NSScreen.screens.map { (frame: topLeft($0.frame), visible: topLeft($0.visibleFrame)) }
        func found(_ p: CGPoint) -> Bool { windows.contains { $0.contains(p) } }

        // Where Codex saved the pet, on a screen of the size it was saved on.
        for spot in pet.spots {
            for s in screens where abs(s.frame.width - spot.screen.width) < 2 && abs(s.frame.height - spot.screen.height) < 2 {
                let p = CGPoint(x: s.frame.minX + spot.center.x, y: s.frame.minY + spot.center.y)
                if found(p) { return (CGPoint(x: p.x, y: primaryTop - p.y), "where Codex saved it") }
            }
        }
        // A pet that was never moved: Codex's default spot.
        for s in screens {
            let p = CGPoint(x: s.visible.maxX - 24 - pet.size.width / 2, y: s.visible.maxY - 24 - pet.size.height / 2)
            if found(p) { return (CGPoint(x: p.x, y: primaryTop - p.y), "Codex's default spot") }
        }
        return (nil, "Codex windows are up, but none covers where the pet should be")
    }

    /// `ClawdPet --codex-debug`: everything Clawd used to find the pet.
    static func describe() -> String {
        let pet = savedPet()
        var out = ["Codex settings: \(codexStateURL.path)",
                   "  \(pet.note); pet size \(Int(pet.size.width))x\(Int(pet.size.height)) points"]
        for s in pet.spots {
            out.append("  saved: center \(Int(s.center.x)),\(Int(s.center.y)) on a \(Int(s.screen.width))x\(Int(s.screen.height)) screen")
        }
        for s in NSScreen.screens { out.append("Screen \(s.frame) visible \(s.visibleFrame)") }
        for r in floatingCodexWindows() { out.append("Floating Codex window \(r)") }
        let (center, how) = locate()
        out.append(center.map { "Result: out, pet at \(Int($0.x)),\(Int($0.y)) (Cocoa points), \(how)" } ?? "Result: missing (\(how))")
        return out.joined(separator: "\n")
    }
}

struct FightView: View {
    let s: FightScript
    private let petW = CGFloat(Pet.gridCols) * Pet.px
    private let petH = CGFloat(Pet.gridRows) * Pet.px

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30.0)) { timeline in
            let t = timeline.date.timeIntervalSince(s.start)
            let now = timeline.date.timeIntervalSinceReferenceDate
            ZStack(alignment: .topLeading) {
                Color.clear
                if t < 1.5 || t >= 4.25 {
                    let p = position(t)
                    let look = look(t)
                    ZStack(alignment: .topLeading) {
                        Canvas { g, _ in ClawdPainter.paint(g, px: Pet.px, look: look, t: now) }
                        PetGlyph(mood: look.mood, t: now)
                    }
                    .frame(width: petW, height: petH)
                    .offset(x: p.x, y: p.y)
                    if let line = line(t) {
                        SpeechTag(text: line).position(x: p.x + petW / 2, y: p.y - 6)
                    }
                }
                if t >= 1.4, t < 4.9 {
                    BrawlCloud(t: t - 1.4, seed: s.seed).position(s.cloud)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }

    private func ease(_ x: Double) -> Double {
        let c = min(1, max(0, x))
        return c * c * (3 - 2 * c)
    }

    private func lerp(_ a: CGPoint, _ b: CGPoint, _ k: Double) -> CGPoint {
        CGPoint(x: a.x + (b.x - a.x) * k, y: a.y + (b.y - a.y) * k)
    }

    private func position(_ t: Double) -> CGPoint {
        if t < 0.6 { return s.home }
        if t < 1.5 { return lerp(s.home, s.spot, ease((t - 0.6) / 0.9)) }
        if t < 5.3 { return s.spot }
        if t < 6.4 { return lerp(s.spot, s.home, ease((t - 5.3) / 1.1)) }
        return s.home
    }

    private func look(_ t: Double) -> ClawdLook {
        if t < 0.6 { return ClawdLook(mood: .happy) }
        if t < 1.5 { return ClawdLook(mood: .idle, walk: true, lookDX: Int(s.dir)) }
        if t < 5.3 { return ClawdLook(mood: s.outcome == .codex ? .dizzy : .happy) }
        return ClawdLook(mood: .idle, walk: t < 6.4, lookDX: -Int(s.dir))
    }

    private func line(_ t: Double) -> String? {
        if t < 1.3 { return s.taunt }
        if t >= 4.4, t < 5.9 { return s.result }
        return nil
    }
}

/// The classic cartoon scuffle: a jittering dust cloud with limbs flailing out
/// of it and POW!/BONK! popping around the edges.
struct BrawlCloud: View {
    let t: Double
    let seed: Int

    static let words = ["POW!", "BONK!", "ZAP!", "WHAM!", "✳", "</>", "BAM!", "KAPOW!", "{ }", "★"]

    private func rnd(_ a: Int, _ b: Int) -> Double {
        var x = UInt64(truncatingIfNeeded: (a &* 73_856_093) ^ (b &* 19_349_663) ^ (seed &* 83_492_791))
        x ^= x >> 13
        x = x &* 0x5BD1_E995
        x ^= x >> 15
        return Double(x % 10000) / 10000
    }

    var body: some View {
        let f = Int(t * 12)
        let poof = max(0, (t - 2.85) / 0.6)
        let grow = min(1, t / 0.18)
        ZStack {
            ForEach(0 ..< 6, id: \.self) { i in
                let a = (Double(i) / 6 + rnd(f / 2, i) * 0.14) * .pi * 2
                Limb(robot: i % 2 == 1)
                    .rotationEffect(.radians(a - .pi / 2))
                    .offset(x: cos(a) * 72, y: sin(a) * 50)
            }
            ForEach(0 ..< 11, id: \.self) { i in
                let a = Double(i) / 11 * .pi * 2 + t * 0.9
                let r = 38 + 10 * sin(t * 11 + Double(i) * 1.7)
                let d = 58 + CGFloat(i % 4) * 9 + 6 * sin(t * 13 + Double(i))
                Circle()
                    .fill(i % 3 == 0 ? Color(white: 0.80) : Color(white: 0.96))
                    .overlay(Circle().stroke(Color.black.opacity(0.12), lineWidth: 1.5))
                    .frame(width: d, height: d)
                    .offset(x: cos(a) * r * 1.35, y: sin(a) * r * 0.8)
            }
            Ellipse().fill(Color(white: 0.97)).frame(width: 120, height: 82)
            ForEach(0 ..< 3, id: \.self) { k in pop(k) }
        }
        .offset(x: sin(t * 31) * 5, y: cos(t * 23) * 3)
        .scaleEffect((0.4 + 0.6 * grow) * (1 + poof * 0.5))
        .opacity(1 - min(1, poof))
    }

    private func pop(_ k: Int) -> some View {
        let period = 0.42
        let phase = t + Double(k) * 0.14
        let cycle = Int(phase / period)
        let local = phase.truncatingRemainder(dividingBy: period) / period
        let word = Self.words[Int(rnd(cycle, k + 7) * Double(Self.words.count)) % Self.words.count]
        let a = rnd(cycle, k + 31) * .pi * 2
        let scale = local < 0.25 ? local / 0.25 * 1.25 : 1.25 - (local - 0.25) * 0.35
        return Text(word)
            .font(.system(size: 19, weight: .black, design: .rounded))
            .foregroundColor(k == 1 ? .clawd : .alertYellow)
            .shadow(color: .black.opacity(0.9), radius: 0, x: 1.5, y: 1.5)
            .rotationEffect(.degrees((rnd(cycle, k) - 0.5) * 40))
            .scaleEffect(scale)
            .opacity(local > 0.8 ? (1 - local) / 0.2 : 1)
            .offset(x: cos(a) * 84, y: sin(a) * 54 - 10)
    }
}

struct Limb: View {
    let robot: Bool

    var body: some View {
        VStack(spacing: 0) {
            Rectangle().fill(robot ? Color(white: 0.86) : Color.clawd).frame(width: 12, height: 22)
            Rectangle()
                .fill(robot ? Color(red: 0.25, green: 0.55, blue: 0.95) : Color.clawdDark)
                .frame(width: 14, height: 8)
        }
    }
}

struct SpeechTag: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 12, weight: .semibold, design: .monospaced))
            .foregroundColor(.white)
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(Capsule().fill(Color.panelFill).overlay(Capsule().stroke(Color.panelStroke, lineWidth: 1)))
            .shadow(color: .black.opacity(0.35), radius: 8, y: 3)
            .fixedSize()
    }
}

// MARK: - Window

/// Clawd's buttons work on the first click even while you're in another app,
/// instead of that click only activating his window.
final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for _: NSEvent?) -> Bool { true }
}

final class PetPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    /// Set for Clawd's own window (not the sparring arena).
    weak var model: PetModel?
    private var dragStart: (mouse: CGPoint, origin: CGPoint)?
    private var dragMoved = false

    /// A left click on Clawd opens Claude and a drag moves him; any other click
    /// (the composer, the bubble) takes the keyboard, so typing lands here and
    /// never in the app underneath.
    override func sendEvent(_ event: NSEvent) {
        guard let m = model else { return super.sendEvent(event) }
        let inWindow = event.locationInWindow
        let topLeft = CGPoint(x: inWindow.x, y: frame.height - inWindow.y)
        let onScreen = convertPoint(toScreen: inWindow)

        switch event.type {
        case .leftMouseDown where m.petRect.contains(topLeft):
            dragStart = (onScreen, frame.origin)
            dragMoved = false
            m.dragging = true
            return
        case .leftMouseDragged where dragStart != nil:
            guard let s = dragStart else { return }
            let dx = onScreen.x - s.mouse.x, dy = onScreen.y - s.mouse.y
            if !dragMoved, hypot(dx, dy) > 3 { dragMoved = true }
            if dragMoved { setFrameOrigin(CGPoint(x: s.origin.x + dx, y: s.origin.y + dy)) }
            return
        case .leftMouseUp where dragStart != nil:
            dragStart = nil
            m.dragging = false
            if dragMoved { m.savePosition() } else { m.petClicked() }
            return
        case .leftMouseDown:
            if !isKeyWindow { makeKey() }
        default:
            break
        }
        super.sendEvent(event)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var panel: PetPanel!
    private let model = PetModel()
    private var tracker: Timer?
    private var hotKey: HotKey?

    func applicationDidFinishLaunching(_: Notification) {
        NSApp.setActivationPolicy(.accessory)

        panel = PetPanel(
            contentRect: CGRect(origin: model.initialOrigin(), size: Pet.windowSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isMovable = false

        let host = FirstClickHostingView(rootView: RootView(m: model))
        host.sizingOptions = []
        host.frame = CGRect(origin: .zero, size: Pet.windowSize)
        panel.contentView = host
        model.window = panel
        panel.model = model
        panel.orderFrontRegardless()

        tracker = Timer.scheduledTimer(withTimeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.trackMouse() }
        }
        model.start()
        hotKey = HotKey(keyCode: kVK_ANSI_C, modifiers: controlKey | optionKey) { [weak self] in
            MainActor.assumeIsolated { self?.model.summon() }
        }
    }

    func application(_: NSApplication, open urls: [URL]) {
        for url in urls { model.handle(url) }
    }

    /// Only Clawd, his composer and his bubble take clicks; the rest of the
    /// window lets them fall through to whatever is underneath.
    private func trackMouse() {
        guard let host = panel.contentView else { return }
        if model.dragging {
            if panel.ignoresMouseEvents { panel.ignoresMouseEvents = false }
            return
        }
        let p = panel.convertPoint(fromScreen: NSEvent.mouseLocation)
        let point = CGPoint(x: p.x, y: host.bounds.height - p.y)
        let inside = model.hitRects.contains { $0.insetBy(dx: -3, dy: -3).contains(point) }
        if panel.ignoresMouseEvents == inside { panel.ignoresMouseEvents = !inside }
    }
}

// MARK: - Icon rendering (used by build.sh)

@MainActor
func renderIcon(to path: String) {
    let size: CGFloat = 1024
    let px: CGFloat = 52
    let view = ZStack {
        RoundedRectangle(cornerRadius: 230, style: .continuous)
            .fill(LinearGradient(
                colors: [Color(red: 0.16, green: 0.15, blue: 0.15), Color(red: 0.08, green: 0.075, blue: 0.075)],
                startPoint: .top, endPoint: .bottom
            ))
            .frame(width: 824, height: 824)
        Canvas { g, _ in ClawdPainter.paint(g, px: px, look: ClawdLook(mood: .happy), t: 0.2) }
            .frame(width: CGFloat(Pet.gridCols) * px, height: CGFloat(Pet.gridRows) * px)
            .offset(y: -20)
    }
    .frame(width: size, height: size)

    let renderer = ImageRenderer(content: view)
    renderer.scale = 1
    guard let cg = renderer.cgImage else { return }
    let rep = NSBitmapImageRep(cgImage: cg)
    if let data = rep.representation(using: .png, properties: [:]) {
        try? data.write(to: URL(fileURLWithPath: path))
    }
}

@main
enum ClawdPetMain {
    static func main() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--render-icon"), i + 1 < args.count {
            MainActor.assumeIsolated { renderIcon(to: args[i + 1]) }
            return
        }
        if args.contains("--codex-check") { // used by /pet fight
            print(FightKit.codexPetCenter() != nil ? "out" : "missing")
            return
        }
        if args.contains("--codex-debug") { // what Clawd sees when he looks for the Codex pet
            print(FightKit.describe())
            return
        }
        let app = NSApplication.shared
        let delegate = MainActor.assumeIsolated { AppDelegate() }
        app.delegate = delegate
        app.run()
    }
}
