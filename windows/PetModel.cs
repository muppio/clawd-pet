using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ClawdPet;

// MARK: - The pet's brain (PetModel in app/ClawdPet.swift)

sealed class PetModel
{
    /// Something the window shows changed; it redraws what differs.
    public event Action? Changed;
    public event Action? Squished;
    public event Action? FocusComposer;

    public PetWindow? Window;
    public bool Dragging;

    readonly Settings settings = Settings.Load();
    readonly ClaudeRunner runner = new();
    readonly Listener listener = new();
    readonly Speaker speaker = new();
    readonly List<DispatcherTimer> timers = new();
    SentenceChunker chunker = new();
    string? sessionID;
    bool chatBusy;
    bool streamingText;
    bool streamDone = true;
    bool awaitingFinish;
    bool replySpoken;
    Mood? flashMood;
    DateTime flashUntil = DateTime.MinValue;
    DateTime lastActivity = DateTime.UtcNow;
    CodeStatus? code;
    DateTime codeUpdated = DateTime.MinValue;
    DateTime? lastStatusMtime;
    FightWindow? arena;
    DateTime nextRandomSpar = DateTime.UtcNow.AddSeconds(Random.Shared.Next(1200, 2401));

    Mood mood = Mood.Idle;
    string draft = "";
    Bubble? bubble;
    bool dictating, voiceMode, mouthOpen, clawdAway, reduceMotion, codexPetOut;
    double level;

    public Mood Mood { get => mood; private set => Set(ref mood, value); }
    public string Draft { get => draft; set => Set(ref draft, value ?? ""); }
    public Bubble? Bubble { get => bubble; private set => Set(ref bubble, value); }
    public bool Dictating { get => dictating; private set => Set(ref dictating, value); }
    public bool VoiceMode { get => voiceMode; private set => Set(ref voiceMode, value); }
    public double Level { get => level; private set => Set(ref level, value); }
    public bool MouthOpen { get => mouthOpen; private set => Set(ref mouthOpen, value); }
    public bool ClawdAway { get => clawdAway; private set => Set(ref clawdAway, value); } // off sparring with Codex
    public bool ReduceMotion { get => reduceMotion; private set => Set(ref reduceMotion, value); }

    /// Whether the Codex pet is on screen right now (the menu reads this).
    public bool CodexPetOut { get => codexPetOut; private set => Set(ref codexPetOut, value); }

    public bool SparRandomly
    {
        get => settings.SparRandomly;
        set { settings.SparRandomly = value; settings.Save(); Notify(); }
    }

    public bool ComposerOpen
    {
        get => settings.ComposerOpen;
        set { settings.ComposerOpen = value; settings.Save(); Notify(); }
    }

    public bool SpeakReplies
    {
        get => settings.SpeakReplies;
        set { settings.SpeakReplies = value; settings.Save(); Notify(); }
    }

    public ClaudeModel Model
    {
        get => ClaudeModels.Parse(settings.Model);
        set { settings.Model = value.Id(); settings.Save(); Notify(); }
    }

    public string Placeholder =>
        VoiceMode ? (listener.Active ? "Listening…" : "Voice mode · tap the orb to stop")
        : Dictating ? "Listening… tap the mic to stop"
        : "Work with Claude";

    void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke();
    }

    void Notify() => Changed?.Invoke();

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Pet.SupportDir);
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
        sessionID = settings.SessionID;
        reduceMotion = !SystemParameters.ClientAreaAnimation;
        speaker.OnIdle = SpeakerIdle;
        speaker.OnWord = () =>
        {
            MouthOpen = true;
            Later.After(0.11, () => MouthOpen = false);
        };
        Task.Run(() => ClaudeRunner.Resolve());

        timers.Add(Later.Every(0.5, PollStatus));
        timers.Add(Later.Every(1.0, () =>
        {
            RefreshMood();
            MaybeSparOnItsOwn();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 3 == 0) CheckCodexPet();
            ReduceMotion = !SystemParameters.ClientAreaAnimation;
        }));
        PollStatus();
        Flash(Mood.Happy, 1.6);
        if (!settings.Welcomed)
        {
            settings.Welcomed = true;
            settings.Save();
            var hello = new Bubble(
                BubbleKind.Code,
                "Hi, I'm Clawd!",
                Body: "**Click me** to open Claude · **Ctrl+Alt+C** to type to me · tap the **orb** to talk · **drag** me anywhere · **right-click** for more (try *Spar with Codex* 🥊)");
            Bubble = hello;
            Later.After(25, () =>
            {
                if (Bubble?.Id == hello.Id) Bubble = null;
            });
        }
    }

    public void Shutdown()
    {
        foreach (var t in timers) t.Stop();
        runner.Cancel();
        listener.Stop();
        speaker.Stop();
        arena?.Close();
    }

    // MARK: Mood

    public void RefreshMood()
    {
        var now = DateTime.UtcNow;
        bool codeFresh = (now - codeUpdated).TotalSeconds < 30 * 60;
        Mood next;
        if (listener.Active) next = Mood.Listening;
        else if (speaker.IsSpeaking) next = Mood.Speaking;
        else if (chatBusy && streamingText) next = Mood.Speaking;
        else if (chatBusy) next = Mood.Thinking;
        else if (flashMood is Mood f && flashUntil > now) next = f;
        else if (codeFresh && code?.State == "working") next = Mood.Working;
        else if (codeFresh && code?.State == "needs") next = Mood.Alert;
        else if ((now - lastActivity).TotalSeconds > 300) next = Mood.Sleepy;
        else next = Mood.Idle;
        Mood = next;
    }

    public void Flash(Mood m, double seconds)
    {
        flashMood = m;
        flashUntil = DateTime.UtcNow.AddSeconds(seconds);
        RefreshMood();
        Later.After(seconds + 0.05, RefreshMood);
    }

    public void Poke()
    {
        lastActivity = DateTime.UtcNow;
        if (Mood == Mood.Sleepy) RefreshMood();
    }

    /// The global shortcut: open the composer and put the cursor in it.
    public void Summon()
    {
        Poke();
        if (!ComposerOpen) ComposerOpen = true;
        Window?.BringToFront();
        Later.After(0.05, () => FocusComposer?.Invoke());
    }

    void FlapMouth()
    {
        MouthOpen = true;
        Later.After(0.09, () => MouthOpen = false);
    }

    // MARK: Clicks

    public void PetClicked()
    {
        Poke();
        Squished?.Invoke();
        OpenClaude();
    }

    public async void OpenClaude()
    {
        var (ok, appId) = await ClaudeApp.OpenAsync(settings.ClaudeAppId);
        if (appId != settings.ClaudeAppId)
        {
            settings.ClaudeAppId = appId;
            settings.Save();
        }
        if (!ok) ShowError("I couldn't find the Claude app on this PC. Get it from claude.ai/download, then click me again.");
    }

    public void NewChat()
    {
        runner.Cancel();
        speaker.Stop();
        chatBusy = false;
        sessionID = null;
        settings.SessionID = null;
        settings.Save();
        Bubble = null;
        Flash(Mood.Happy, 1.2);
    }

    public void DismissBubble() => Bubble = null;

    public void CopyBubble()
    {
        if (Bubble is not { } b) return;
        var text = b.Body.Length == 0 ? b.Detail : b.Body;
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Pet.Log(ex); // the clipboard was busy
        }
    }

    public void Escape()
    {
        if (VoiceMode)
        {
            StopVoice();
            return;
        }
        if (Dictating)
        {
            StopDictation();
            return;
        }
        if (chatBusy)
        {
            runner.Cancel();
            speaker.Stop();
            chatBusy = false;
            Bubble = null;
            RefreshMood();
            return;
        }
        if (Draft.Length > 0)
        {
            Draft = "";
            return;
        }
        Bubble = null;
    }

    // MARK: Chat

    public void SubmitDraft()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        if (Dictating) StopDictation();
        Draft = "";
        Ask(text, spoken: false);
    }

    public void Ask(string text, bool spoken)
    {
        lastActivity = DateTime.UtcNow;
        speaker.Stop();
        chunker = new SentenceChunker();
        chatBusy = true;
        streamingText = false;
        streamDone = false;
        awaitingFinish = true;
        replySpoken = spoken || SpeakReplies;
        Bubble = new Bubble(BubbleKind.Chat, "Thinking…", text);
        RefreshMood();
        runner.Ask(text, Model.Id(), sessionID, spoken, Handle);
    }

    void Handle(RunnerEvent ev)
    {
        switch (ev)
        {
            case RunStatus s:
                if (Bubble is { Kind: BubbleKind.Chat } sb) Bubble = sb with { Status = s.Text };
                break;

            case RunDelta d:
                if (Bubble is { Kind: BubbleKind.Chat } db) Bubble = db with { Body = db.Body + d.Text, Status = "Claude" };
                if (replySpoken)
                {
                    foreach (var sentence in chunker.Feed(d.Text)) speaker.Say(sentence);
                }
                else
                {
                    // Silent replies: he "talks" as the words stream in.
                    streamingText = true;
                    FlapMouth();
                }
                break;

            case RunDone done:
                if (done.Session != null)
                {
                    sessionID = done.Session;
                    settings.SessionID = done.Session;
                    settings.Save();
                }
                if (Bubble is { Kind: BubbleKind.Chat } b)
                {
                    if (b.Body.Length == 0 && done.Result != null)
                    {
                        b = b with { Body = done.Result };
                        if (replySpoken)
                        {
                            chunker = new SentenceChunker();
                            foreach (var sentence in chunker.Feed(done.Result)) speaker.Say(sentence);
                        }
                    }
                    Bubble = b with { Status = "Claude" };
                }
                if (replySpoken)
                {
                    foreach (var sentence in chunker.Flush()) speaker.Say(sentence);
                }
                chatBusy = false;
                streamingText = false;
                streamDone = true;
                if (!speaker.IsSpeaking) ReplyFinished();
                break;

            case RunFailed f:
                chatBusy = false;
                streamingText = false;
                streamDone = true;
                awaitingFinish = false;
                if (f.Message.Contains("session", StringComparison.OrdinalIgnoreCase))
                {
                    sessionID = null;
                    settings.SessionID = null;
                    settings.Save();
                }
                ShowError(f.Message);
                if (VoiceMode) StopVoice();
                break;
        }
        RefreshMood();
    }

    void SpeakerIdle()
    {
        RefreshMood();
        if (streamDone) ReplyFinished();
    }

    void ReplyFinished()
    {
        if (!awaitingFinish) return;
        awaitingFinish = false;
        lastActivity = DateTime.UtcNow;
        Flash(Mood.Happy, 2.2);
        if (VoiceMode)
        {
            Later.After(0.35, Listen);
        }
        else if (Bubble?.Id is Guid id)
        {
            Later.After(75, () =>
            {
                if (Bubble?.Id == id && !chatBusy) Bubble = null;
            });
        }
    }

    public void ShowError(string message)
    {
        Bubble = new Bubble(BubbleKind.Error, "Hmm…", Body: message);
        Flash(Mood.Alert, 2.5);
    }

    static string MicError(Exception ex) =>
        $"Couldn't start the microphone: {ex.Message} Check Settings › Privacy & security › Microphone, and let desktop apps use it.";

    // MARK: Voice

    public void ToggleVoice()
    {
        if (VoiceMode)
        {
            StopVoice();
            return;
        }
        if (Listener.Problem() is string problem)
        {
            ShowError(problem);
            return;
        }
        if (Dictating) StopDictation();
        VoiceMode = true;
        Listen();
    }

    async void Listen()
    {
        if (!VoiceMode) return;
        speaker.Stop();
        Draft = "";
        try
        {
            var starting = listener.StartAsync(
                silence: 1.3,
                noSpeechTimeout: 14,
                partial: text => Draft = text,
                level: l => Level = l,
                final: Heard);
            RefreshMood();
            Notify();
            await starting;
        }
        catch (Exception ex)
        {
            VoiceMode = false;
            ShowError(MicError(ex));
        }
        RefreshMood();
        Notify();
    }

    void Heard(string? text)
    {
        Level = 0;
        if (!VoiceMode) return;
        if (string.IsNullOrEmpty(text))
        {
            StopVoice();
            return;
        }
        Draft = "";
        Ask(text, spoken: true);
    }

    public void StopVoice()
    {
        VoiceMode = false;
        listener.Stop();
        speaker.Stop();
        Level = 0;
        if (Draft.Length > 0 && !chatBusy) Draft = "";
        RefreshMood();
        Notify();
    }

    // MARK: Dictation

    public async void ToggleDictation()
    {
        if (Dictating)
        {
            StopDictation();
            return;
        }
        if (Listener.Problem() is string problem)
        {
            ShowError(problem);
            return;
        }
        if (VoiceMode) StopVoice();
        string prefix = Draft.Length == 0 ? "" : Draft + " ";
        try
        {
            var starting = listener.StartAsync(
                silence: 2.6,
                noSpeechTimeout: 10,
                partial: text => Draft = prefix + text,
                level: l => Level = l,
                final: _ =>
                {
                    Dictating = false;
                    Level = 0;
                    RefreshMood();
                });
            Dictating = true;
            RefreshMood();
            await starting;
        }
        catch (Exception ex)
        {
            Dictating = false;
            ShowError(MicError(ex));
        }
        RefreshMood();
    }

    public void StopDictation()
    {
        listener.Finish();
        Dictating = false;
        Level = 0;
        RefreshMood();
    }

    // MARK: Claude Code status (written by the clawd-pet mod)

    void PollStatus()
    {
        DateTime mtime;
        try
        {
            if (!File.Exists(Pet.StatusFile)) return;
            mtime = File.GetLastWriteTimeUtc(Pet.StatusFile);
        }
        catch
        {
            return;
        }
        if (mtime == lastStatusMtime) return;
        bool first = lastStatusMtime == null;

        CodeStatus? status;
        try
        {
            // The mod may be mid-write: share the file, and retry next tick on a bad read.
            using var stream = new FileStream(Pet.StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            status = JsonSerializer.Deserialize<CodeStatus>(stream, CodeStatus.Json);
        }
        catch
        {
            return;
        }
        lastStatusMtime = mtime;
        if (status == null || status.State.Length == 0) return;
        if (first && (DateTime.UtcNow - mtime).TotalSeconds > 120) return;
        ApplyCode(status);
    }

    void ApplyCode(CodeStatus s)
    {
        code = s;
        codeUpdated = DateTime.UtcNow;
        lastActivity = DateTime.UtcNow;
        bool chatHolds = Bubble?.Kind is BubbleKind.Chat or BubbleKind.Error || VoiceMode || Dictating;

        void Show(string status)
        {
            if (chatHolds) return;
            if (Bubble is { Kind: BubbleKind.Code } b)
                Bubble = b with { Status = status, Prompt = s.Title ?? "", Detail = s.Detail ?? "" };
            else
                Bubble = new Bubble(BubbleKind.Code, status, s.Title ?? "", Detail: s.Detail ?? "");
        }

        string place = string.IsNullOrEmpty(s.Project) ? "" : $" · {s.Project}";
        switch (s.State)
        {
            case "working":
                Show($"Claude is working{place}");
                break;
            case "needs":
                Show($"Claude needs you{place}");
                Flash(Mood.Alert, 4);
                break;
            case "done":
                Show($"Done ✓{place}");
                Flash(Mood.Happy, 3);
                if (Bubble is { Kind: BubbleKind.Code } done)
                {
                    var id = done.Id;
                    Later.After(12, () =>
                    {
                        if (Bubble?.Id == id && code?.State != "working") Bubble = null;
                    });
                }
                break;
            case "error":
                Show("Claude stopped");
                break;
            default:
                if (Bubble?.Kind == BubbleKind.Code) Bubble = null;
                break;
        }
        RefreshMood();
    }

    // MARK: Sparring with Codex

    void MaybeSparOnItsOwn()
    {
        if (!SparRandomly || DateTime.UtcNow < nextRandomSpar) return;
        nextRandomSpar = DateTime.UtcNow.AddSeconds(Random.Shared.Next(1200, 2401));
        bool quiet = !chatBusy && !VoiceMode && !Dictating && Mood != Mood.Working && Mood != Mood.Alert;
        if (quiet) StartFight(onlyIfCodexIsOut: true);
    }

    public void CheckCodexPet()
    {
        try
        {
            CodexPetOut = CodexPet.FindCenter() != null;
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
            CodexPetOut = false;
        }
    }

    static string Pick(params string[] lines) => lines[Random.Shared.Next(lines.Length)];

    static Rect Around(Point p) => new(p.X - 160, p.Y - 50, Pet.PetWidth + 320, Pet.PetHeight + 70);

    /// Clawd runs over to the Codex pet, they vanish into a cartoon brawl
    /// cloud, and he walks home with the result. Purely for fun, and only when
    /// the Codex pet is actually out: otherwise he says how to summon it.
    public void StartFight(bool onlyIfCodexIsOut = false)
    {
        if (arena != null || VoiceMode || Window?.FightFrame() is not { } frame) return;
        CheckCodexPet();
        var codexCenter = CodexPetOut ? CodexPet.FindCenter() : null;
        if (codexCenter == null)
        {
            if (!onlyIfCodexIsOut)
            {
                var note = new Bubble(
                    BubbleKind.Code,
                    "No one to spar with",
                    Body: "The Codex pet isn't on screen. Open the **Codex app** and type **/pet** to bring it out, then try again. 🥊");
                Bubble = note;
                Flash(Mood.Alert, 2);
                Later.After(12, () =>
                {
                    if (Bubble?.Id == note.Id) Bubble = null;
                });
            }
            return;
        }
        Poke();

        // Work in the DIPs of the monitor Clawd is on, with its top-left at 0,0.
        var (petTopLeft, monitor, scale) = frame;
        Point ToArena(Point p) => new((p.X - monitor.Left) / scale, (p.Y - monitor.Top) / scale);
        var screen = new Size(monitor.Width / scale, monitor.Height / scale);

        var home = ToArena(petTopLeft);
        var clawdCenter = new Point(home.X + Pet.PetWidth / 2, home.Y + Pet.PetHeight / 2);
        var target = ToArena(codexCenter.Value);
        var inner = new Rect(20, 20, Math.Max(0, screen.Width - 40), Math.Max(0, screen.Height - 40));
        if (!inner.Contains(target))
        {
            var far = new Bubble(
                BubbleKind.Code,
                "Too far to spar",
                Body: "The Codex pet is on another screen. Drag Clawd over to it, then try again. 🥊");
            Bubble = far;
            Flash(Mood.Alert, 2);
            Later.After(10, () =>
            {
                if (Bubble?.Id == far.Id) Bubble = null;
            });
            return;
        }

        double dir = target.X < clawdCenter.X ? -1 : 1;
        double spotY = Math.Abs(target.Y - clawdCenter.Y) > 120 ? target.Y - Pet.PetHeight / 2 : home.Y;
        var spot = new Point(target.X - dir * 74 - Pet.PetWidth / 2, spotY);
        var cloud = new Point((spot.X + Pet.PetWidth / 2 + target.X) / 2, (spot.Y + Pet.PetHeight / 2 + target.Y) / 2 - 6);

        double roll = Random.Shared.NextDouble();
        var outcome = roll < 0.45 ? FightOutcome.Clawd : roll < 0.85 ? FightOutcome.Codex : FightOutcome.Draw;
        string taunt = Pick("Hey Codex! Spar? ✳", "En garde, robot!", "Tabs vs spaces. Settle it!", "Last to ship buys tokens!", "Code review… in the ring!");
        string result = outcome switch
        {
            FightOutcome.Clawd => Pick("Clawd wins! ✳", "Flawless refactor!", "GG, Codex 🤝", "Ship it! 🏆"),
            FightOutcome.Codex => Pick("Oof. Codex got me.", "Rematch after this build…", "Codex wins this round!"),
            _ => Pick("Draw! Pair-programming it is.", "Tie! Merge conflict resolved.", "Even match. Friends again!"),
        };
        var script = new FightScript(DateTime.UtcNow, home, spot, cloud, dir, outcome, taunt, result, Random.Shared.Next(0, 10000));

        // The arena only covers where the action is, not the whole screen.
        var bounds = Rect.Union(Rect.Union(Around(home), Around(spot)), new Rect(cloud.X - 230, cloud.Y - 160, 460, 320));
        bounds.Intersect(new Rect(screen));
        if (bounds.IsEmpty) return;
        var physical = new Native.RECT
        {
            Left = monitor.Left + (int)Math.Floor(bounds.X * scale),
            Top = monitor.Top + (int)Math.Floor(bounds.Y * scale),
            Right = monitor.Left + (int)Math.Ceiling(bounds.Right * scale),
            Bottom = monitor.Top + (int)Math.Ceiling(bounds.Bottom * scale),
        };
        var origin = new Vector((physical.Left - monitor.Left) / scale, (physical.Top - monitor.Top) / scale);

        arena = new FightWindow(script, origin, physical, scale);
        arena.Show();
        ClawdAway = true;
        if (Bubble?.Kind != BubbleKind.Chat) Bubble = null;

        Later.After(FightScript.Duration, () =>
        {
            arena?.Close();
            arena = null;
            ClawdAway = false;
            switch (outcome)
            {
                case FightOutcome.Clawd:
                    settings.ScoreClawd++;
                    break;
                case FightOutcome.Codex:
                    settings.ScoreCodex++;
                    break;
                default:
                    settings.ScoreDraw++;
                    break;
            }
            settings.Save();
            var b = new Bubble(
                BubbleKind.Code,
                "Clawd vs Codex",
                Body: result,
                Detail: $"Clawd {settings.ScoreClawd} · Codex {settings.ScoreCodex} · Draws {settings.ScoreDraw}");
            Bubble = b;
            Flash(outcome == FightOutcome.Codex ? Mood.Dizzy : Mood.Happy, 2.4);
            Later.After(9, () =>
            {
                if (Bubble?.Id == b.Id) Bubble = null;
            });
        });
    }

    // MARK: Links (clawdpet://ask?q=…, clawdpet://voice, clawdpet://new, clawdpet://fight)

    /// A message from a second ClawdPet.exe: a clawdpet:// link, or nothing
    /// (someone just started it again), which brings Clawd to the front.
    public void HandleMessage(string message)
    {
        if (message.StartsWith("clawdpet:", StringComparison.OrdinalIgnoreCase)) HandleUrl(message);
        else Window?.BringToFront();
    }

    public void HandleUrl(string raw)
    {
        var s = raw.Trim();
        int colon = s.IndexOf(':');
        if (colon < 0 || !s[..colon].Equals("clawdpet", StringComparison.OrdinalIgnoreCase)) return;
        var rest = s[(colon + 1)..].TrimStart('/');
        int qi = rest.IndexOf('?');
        string host = (qi >= 0 ? rest[..qi] : rest).TrimEnd('/').ToLowerInvariant();
        string? q = null;
        if (qi >= 0)
        {
            foreach (var part in rest[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv[0] != "q") continue;
                q = Uri.UnescapeDataString(kv.Length > 1 ? kv[1] : "").Trim();
                break;
            }
        }
        switch (host)
        {
            case "ask":
                if (!string.IsNullOrEmpty(q)) Ask(q, spoken: false);
                break;
            case "voice":
                ToggleVoice();
                break;
            case "new":
                NewChat();
                break;
            case "fight":
            case "spar":
                StartFight();
                break;
        }
    }

    // MARK: Window position

    public Point InitialOrigin()
    {
        if (settings.OriginX is double x && settings.OriginY is double y)
        {
            var probe = new Rect(x + 120, y + 120, Pet.WindowWidth - 240, Pet.WindowHeight - 240);
            var desktop = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            if (desktop.IntersectsWith(probe)) return new Point(x, y);
        }
        return HomeOrigin();
    }

    /// Bottom-right corner of the main screen, just above the taskbar.
    public static Point HomeOrigin()
    {
        var work = SystemParameters.WorkArea;
        return new Point(work.Right - Pet.WindowWidth - 20, work.Bottom - Pet.WindowHeight - 10);
    }

    public void SnapHome()
    {
        settings.OriginX = null;
        settings.OriginY = null;
        settings.Save();
        Window?.MoveTo(HomeOrigin());
    }

    public void SavePosition(double left, double top)
    {
        settings.OriginX = left;
        settings.OriginY = top;
        settings.Save();
    }

    // MARK: Open at login (HKCU Run key)

    public bool LoginEnabled => Integration.LoginEnabled;

    public void SetLogin(bool on)
    {
        try
        {
            Integration.SetLogin(on);
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't change Open at login: {ex.Message}");
        }
        Notify();
    }
}
