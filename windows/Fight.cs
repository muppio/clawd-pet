using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClawdPet;

// MARK: - Sparring with the Codex pet

enum FightOutcome { Clawd, Codex, Draw }

/// Positions are in the DIPs of Clawd's monitor, its top-left at 0,0.
sealed record FightScript(
    DateTime Start,
    Point Home, // Clawd's canvas top-left
    Point Spot, // where he stops beside Codex
    Point Cloud, // center of the brawl
    double Dir, // -1 runs left, +1 runs right
    FightOutcome Outcome,
    string Taunt,
    string Result,
    int Seed)
{
    public const double Duration = 6.6;
}

// =============================================================================
//  Finding the Codex pet
// =============================================================================
//
// Codex draws its pet inside an always-on-top window that also holds the pet's
// tray, quick chat and captions, so where the pet sits inside that window
// changes with its layout. Guessing from the window sent Clawd to the wrong
// spot, so Clawd reads where Codex itself says the pet is instead:
//
//   * Codex saves the pet's top-left corner and the screen it's on, in DIPs,
//     to %USERPROFILE%\.codex\.codex-global-state.json whenever the pet is
//     dropped ("electron-avatar-overlay-bounds"), and keeps the pet's size
//     setting there too ("avatar-overlay-mascot-width-px", 80 to 224, 112 by
//     default; its frames are 192 x 208).
//   * A pet that was never moved sits at Codex's default spot: the bottom
//     right of the work area, 24 DIPs in.
//
// That spot only counts while one of Codex's always-on-top windows covers it,
// so a pet that's put away (or an old save) never sends Clawd off to an empty
// spot, and sparring stays greyed out instead.
//
// `ClawdPet.exe --codex-windows | Out-String` prints what Clawd found.
static class CodexPet
{
    /// Process names (without .exe) that can own the pet. Not "Codex Computer Use".
    static readonly string[] Owners = { "Codex", "ChatGPT" };

    const double DefaultWidth = 112, FrameAspect = 192.0 / 208.0, DefaultInset = 24;

    /// A saved spot: the pet's center from its screen's top-left corner, and that screen's size, all in DIPs.
    public sealed record Spot(double X, double Y, double ScreenWidth, double ScreenHeight);

    public sealed record Saved(IReadOnlyList<Spot> Spots, double Width, double Height, string Note);

    public sealed record WindowInfo(
        IntPtr Handle,
        string Process,
        string Title,
        string Class,
        Native.RECT Rect,
        long ExStyle,
        long Style,
        bool Cloaked);

    static (DateTime Stamp, Saved Value)? cache;

    public static string StatePath
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrEmpty(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(home, ".codex-global-state.json");
        }
    }

    /// The Codex pet's center on screen in physical pixels, or null when it isn't out.
    public static Point? FindCenter() => Locate(out _);

    static Point? Locate(out string how)
    {
        var floating = Windows().Where(Floating).ToList();
        if (floating.Count == 0)
        {
            how = "no always-on-top Codex window";
            return null;
        }
        var saved = ReadSaved();
        var monitors = Native.Monitors();

        // Where Codex saved the pet, on a screen of the size it was saved on.
        foreach (var spot in saved.Spots)
        {
            foreach (var m in monitors)
            {
                if (Math.Abs(m.Bounds.Width / m.Scale - spot.ScreenWidth) > 2 || Math.Abs(m.Bounds.Height / m.Scale - spot.ScreenHeight) > 2) continue;
                var p = new Point(m.Bounds.Left + spot.X * m.Scale, m.Bounds.Top + spot.Y * m.Scale);
                if (floating.Any(w => Covers(w.Rect, p)))
                {
                    how = "where Codex saved it";
                    return p;
                }
            }
        }

        // A pet that was never moved: Codex's default spot.
        foreach (var m in monitors)
        {
            var p = new Point(
                m.Work.Right - (DefaultInset + saved.Width / 2) * m.Scale,
                m.Work.Bottom - (DefaultInset + saved.Height / 2) * m.Scale);
            if (floating.Any(w => Covers(w.Rect, p)))
            {
                how = "Codex's default spot";
                return p;
            }
        }
        how = "Codex windows are up, but none covers where the pet should be";
        return null;
    }

    static bool Floating(WindowInfo w) =>
        !w.Cloaked && (w.ExStyle & Native.WS_EX_TOPMOST) != 0 && w.Rect.Width >= 40 && w.Rect.Height >= 40;

    static bool Covers(Native.RECT r, Point p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// Codex's saved pet position and size, read again only when the file changes.
    public static Saved ReadSaved()
    {
        var path = StatePath;
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stamp = DateTime.MinValue;
        }
        if (cache is { } c && c.Stamp == stamp) return c.Value;
        var value = Parse(path);
        cache = (stamp, value);
        return value;
    }

    static Saved Parse(string path)
    {
        double width = DefaultWidth;
        var spots = new List<Spot>();
        string note;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("electron-persisted-atom-state", out var atoms) && atoms.ValueKind == JsonValueKind.Object &&
                atoms.TryGetProperty("avatar-overlay-mascot-width-px", out var size) && size.ValueKind == JsonValueKind.Number)
                width = Math.Round(Math.Clamp(size.GetDouble(), 80, 224));
            double height = Math.Ceiling(width / FrameAspect);

            void Add(JsonElement r)
            {
                if (r.ValueKind != JsonValueKind.Object || Num(r, "x") is not double x || Num(r, "y") is not double y) return;
                if (!r.TryGetProperty("displayBounds", out var d) || Num(d, "x") is not double dx || Num(d, "y") is not double dy ||
                    Num(d, "width") is not double dw || Num(d, "height") is not double dh) return;
                var spot = new Spot(x - dx + width / 2, y - dy + height / 2, dw, dh);
                if (!spots.Contains(spot)) spots.Add(spot);
            }

            if (root.TryGetProperty("electron-avatar-overlay-bounds", out var bounds) && bounds.ValueKind == JsonValueKind.Object)
            {
                Add(bounds); // the latest one first
                foreach (var key in new[] { "byDisplayId", "byResolution" })
                {
                    if (bounds.TryGetProperty(key, out var map) && map.ValueKind == JsonValueKind.Object)
                        foreach (var entry in map.EnumerateObject()) Add(entry.Value);
                }
            }
            note = spots.Count > 0 ? $"{spots.Count} saved spot(s)" : "no saved spot (the pet was never moved)";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            note = $"couldn't read Codex's settings ({e.GetType().Name})";
        }
        return new Saved(spots, width, Math.Ceiling(width / FrameAspect), note);
    }

    static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static List<WindowInfo> Windows()
    {
        var pids = new Dictionary<uint, string>();
        foreach (var name in Owners)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                pids[(uint)p.Id] = p.ProcessName;
                p.Dispose();
            }
        }
        var list = new List<WindowInfo>();
        if (pids.Count == 0) return list;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!pids.TryGetValue(pid, out var owner)) return true;
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            if (!Native.GetWindowRect(h, out var rect)) return true;
            list.Add(new WindowInfo(h, owner, Native.Title(h), Native.ClassName(h), rect,
                Native.GetExStyle(h), Native.GetStyle(h), Native.IsCloaked(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// `ClawdPet.exe --codex-windows`: everything Clawd used to find the pet.
    public static void Describe(TextWriter o)
    {
        var saved = ReadSaved();
        o.WriteLine($"Codex settings: {StatePath}");
        o.WriteLine($"  {saved.Note}; pet size {saved.Width:0}x{saved.Height:0} DIPs");
        foreach (var s in saved.Spots)
            o.WriteLine($"  saved: center {s.X:0},{s.Y:0} on a {s.ScreenWidth:0}x{s.ScreenHeight:0} screen (DIPs)");
        foreach (var m in Native.Monitors())
            o.WriteLine($"Monitor {m.Bounds} work {m.Work} scale {m.Scale:0.##} = {m.Bounds.Width / m.Scale:0}x{m.Bounds.Height / m.Scale:0} DIPs");
        var all = Windows();
        o.WriteLine($"Visible windows owned by {string.Join(" or ", Owners)}: {all.Count}");
        foreach (var w in all)
        {
            string flags = string.Join(",", new[]
            {
                (w.ExStyle & Native.WS_EX_TOPMOST) != 0 ? "topmost" : null,
                (w.ExStyle & Native.WS_EX_LAYERED) != 0 ? "layered" : null,
                (w.ExStyle & Native.WS_EX_TRANSPARENT) != 0 ? "transparent" : null,
                (w.ExStyle & Native.WS_EX_NOREDIRECTIONBITMAP) != 0 ? "noredirect" : null,
                (w.ExStyle & Native.WS_EX_TOOLWINDOW) != 0 ? "toolwindow" : null,
                (w.ExStyle & Native.WS_EX_NOACTIVATE) != 0 ? "noactivate" : null,
                (w.Style & Native.WS_CAPTION) == Native.WS_CAPTION ? "caption" : null,
                w.Cloaked ? "cloaked" : null,
            }.Where(f => f != null));
            o.WriteLine(
                $"  {w.Process} hwnd=0x{w.Handle.ToInt64():X} rect={w.Rect} exstyle=0x{w.ExStyle:X} [{flags}] " +
                $"class=\"{w.Class}\" title=\"{w.Title}\"{(Floating(w) ? " (can hold the pet)" : "")}");
        }
        o.WriteLine(Locate(out var how) is Point p
            ? $"Result: out, pet at {p.X:0},{p.Y:0} (physical pixels), {how}"
            : $"Result: missing ({how})");
    }
}

/// A click-through, always-on-top window over just the part of the screen
/// where the fight happens. It closes itself when the model says so.
sealed class FightWindow : Window
{
    public FightWindow(FightScript script, Vector origin, Native.RECT physical, double scale)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = physical.Left / scale;
        Top = physical.Top / scale;
        Width = physical.Width / scale;
        Height = physical.Height / scale;
        Content = new FightView(script, origin);
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(h, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            // Exact physical placement, whatever DPI the other monitors run at.
            Native.SetWindowPos(h, Native.HWND_TOPMOST, physical.Left, physical.Top, physical.Width, physical.Height, Native.SWP_NOACTIVATE);
        };
    }
}

/// The fight, 30 frames a second for 6.6 seconds: Clawd runs over, the brawl
/// cloud takes over, and he walks home with the result.
sealed class FightView : FrameworkElement
{
    readonly FightScript s;
    readonly Vector origin;
    readonly DrawingVisual shadowLayer = new();
    readonly DrawingVisual pixelLayer = new();
    readonly DrawingVisual frontLayer = new();
    readonly DrawingVisual cloudLayer = new();
    readonly VisualCollection layers;
    DispatcherTimer? timer;

    public FightView(FightScript script, Vector arenaOrigin)
    {
        s = script;
        origin = arenaOrigin;
        layers = new VisualCollection(this);
        layers.Add(shadowLayer);
        layers.Add(pixelLayer);
        layers.Add(frontLayer);
        layers.Add(cloudLayer); // the cloud swallows Clawd as he arrives
        RenderOptions.SetEdgeMode(pixelLayer, EdgeMode.Aliased);
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            Draw();
            timer = Later.Every(1.0 / 30.0, Draw);
        };
        Unloaded += (_, _) => timer?.Stop();
    }

    protected override int VisualChildrenCount => layers.Count;
    protected override Visual GetVisualChild(int index) => layers[index];

    void Draw()
    {
        double t = (DateTime.UtcNow - s.Start).TotalSeconds;
        double now = Pet.Now;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        using (var shadow = shadowLayer.RenderOpen())
        using (var pixels = pixelLayer.RenderOpen())
        using (var front = frontLayer.RenderOpen())
        {
            if (t < 1.5 || t >= 4.25)
            {
                var p = Position(t) - origin;
                var look = Look(t);
                var pose = ClawdPainter.Pose(look, now);
                ClawdPainter.DrawShadow(shadow, pose, Pet.Px, p.X, p.Y);
                ClawdPainter.DrawPixels(pixels, pose, Pet.Px, p.X, p.Y);
                ClawdPainter.DrawGlyphs(front, ClawdPainter.Glyphs(look.Mood, now), p.X, p.Y, ppd);
                if (Line(t) is string line) SpeechTag.Draw(front, line, new Point(p.X + Pet.PetWidth / 2, p.Y - 6), ppd);
            }
        }
        using (var cloud = cloudLayer.RenderOpen())
        {
            if (t >= 1.4 && t < 4.9) BrawlCloud.Draw(cloud, t - 1.4, s.Seed, s.Cloud - origin, ppd);
        }
    }

    static double Ease(double x)
    {
        double c = Math.Clamp(x, 0, 1);
        return c * c * (3 - 2 * c);
    }

    static Point Lerp(Point a, Point b, double k) => new(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);

    Point Position(double t)
    {
        if (t < 0.6) return s.Home;
        if (t < 1.5) return Lerp(s.Home, s.Spot, Ease((t - 0.6) / 0.9));
        if (t < 5.3) return s.Spot;
        if (t < 6.4) return Lerp(s.Spot, s.Home, Ease((t - 5.3) / 1.1));
        return s.Home;
    }

    ClawdLook Look(double t)
    {
        if (t < 0.6) return new ClawdLook(Mood.Happy);
        if (t < 1.5) return new ClawdLook(Mood.Idle, Walk: true, LookDX: (int)s.Dir);
        if (t < 5.3) return new ClawdLook(s.Outcome == FightOutcome.Codex ? Mood.Dizzy : Mood.Happy);
        return new ClawdLook(Mood.Idle, Walk: t < 6.4, LookDX: -(int)s.Dir);
    }

    string? Line(double t)
    {
        if (t < 1.3) return s.Taunt;
        if (t >= 4.4 && t < 5.9) return s.Result;
        return null;
    }
}

/// The classic cartoon scuffle: a jittering dust cloud with limbs flailing out
/// of it and POW!/BONK! popping around the edges.
static class BrawlCloud
{
    static readonly string[] Words = { "POW!", "BONK!", "ZAP!", "WHAM!", "✳", "</>", "BAM!", "KAPOW!", "{ }", "★" };
    static readonly Brush Gray80 = Palette.Brush(Color.FromRgb(204, 204, 204));
    static readonly Brush Gray86 = Palette.Brush(Color.FromRgb(219, 219, 219));
    static readonly Brush Gray96 = Palette.Brush(Color.FromRgb(245, 245, 245));
    static readonly Brush Gray97 = Palette.Brush(Color.FromRgb(247, 247, 247));
    static readonly Brush RobotBlue = Palette.Brush(Color.FromRgb(64, 140, 242));
    static readonly Brush TextShadow = Palette.Brush(Colors.Black, 0.9);
    static readonly Pen Puff = MakePuff();
    static readonly Typeface Heavy = new(Palette.Symbols, FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    static Pen MakePuff()
    {
        var p = new Pen(Palette.Brush(Colors.Black, 0.12), 1.5);
        p.Freeze();
        return p;
    }

    static double Rnd(int a, int b, int seed)
    {
        unchecked
        {
            ulong x = (ulong)(((long)a * 73_856_093L) ^ ((long)b * 19_349_663L) ^ ((long)seed * 83_492_791L));
            x ^= x >> 13;
            x *= 0x5BD1_E995UL;
            x ^= x >> 15;
            return (x % 10000) / 10000.0;
        }
    }

    public static void Draw(DrawingContext dc, double t, int seed, Point center, double ppd)
    {
        int f = (int)(t * 12);
        double poof = Math.Max(0, (t - 2.85) / 0.6);
        double grow = Math.Min(1, t / 0.18);
        double scale = (0.4 + 0.6 * grow) * (1 + poof * 0.5);
        double opacity = 1 - Math.Min(1, poof);
        if (opacity <= 0) return;

        dc.PushTransform(new TranslateTransform(center.X + Math.Sin(t * 31) * 5, center.Y + Math.Cos(t * 23) * 3));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushOpacity(opacity);

        for (int i = 0; i < 6; i++)
        {
            double a = (i / 6.0 + Rnd(f / 2, i, seed) * 0.14) * Math.PI * 2;
            bool robot = i % 2 == 1;
            dc.PushTransform(new TranslateTransform(Math.Cos(a) * 72, Math.Sin(a) * 50));
            dc.PushTransform(new RotateTransform((a - Math.PI / 2) * 180 / Math.PI));
            dc.DrawRectangle(robot ? Gray86 : Palette.ClawdBrush, null, new Rect(-6, -15, 12, 22));
            dc.DrawRectangle(robot ? RobotBlue : Palette.ClawdDarkBrush, null, new Rect(-7, 7, 14, 8));
            dc.Pop();
            dc.Pop();
        }
        for (int i = 0; i < 11; i++)
        {
            double a = i / 11.0 * Math.PI * 2 + t * 0.9;
            double r = 38 + 10 * Math.Sin(t * 11 + i * 1.7);
            double d = 58 + (i % 4) * 9 + 6 * Math.Sin(t * 13 + i);
            dc.DrawEllipse(i % 3 == 0 ? Gray80 : Gray96, Puff, new Point(Math.Cos(a) * r * 1.35, Math.Sin(a) * r * 0.8), d / 2, d / 2);
        }
        dc.DrawEllipse(Gray97, null, new Point(0, 0), 60, 41);
        for (int k = 0; k < 3; k++) Pop(dc, t, k, seed, ppd);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    static void Pop(DrawingContext dc, double t, int k, int seed, double ppd)
    {
        const double period = 0.42;
        double phase = t + k * 0.14;
        int cycle = (int)(phase / period);
        double local = (phase % period) / period;
        string word = Words[(int)(Rnd(cycle, k + 7, seed) * Words.Length) % Words.Length];
        double a = Rnd(cycle, k + 31, seed) * Math.PI * 2;
        double scale = local < 0.25 ? local / 0.25 * 1.25 : 1.25 - (local - 0.25) * 0.35;
        double opacity = local > 0.8 ? (1 - local) / 0.2 : 1;

        var text = new FormattedText(word, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Heavy, 19,
            k == 1 ? Palette.ClawdBrush : Palette.YellowBrush, ppd);
        var shade = new FormattedText(word, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Heavy, 19, TextShadow, ppd);
        var at = new Point(-text.Width / 2, -text.Height / 2);

        dc.PushTransform(new TranslateTransform(Math.Cos(a) * 84, Math.Sin(a) * 54 - 10));
        dc.PushOpacity(opacity);
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushTransform(new RotateTransform((Rnd(cycle, k, seed) - 0.5) * 40));
        dc.DrawText(shade, at + new Vector(1.5, 1.5));
        dc.DrawText(text, at);
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }
}

/// The little capsule Clawd talks in while he's away from his bubble.
static class SpeechTag
{
    static readonly Typeface Font = new(Palette.Mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Brush Shade = Palette.Brush(Colors.Black, 0.25);
    static readonly Pen Edge = MakeEdge();

    static Pen MakeEdge()
    {
        var p = new Pen(Palette.PanelStroke, 1);
        p.Freeze();
        return p;
    }

    public static void Draw(DrawingContext dc, string text, Point center, double ppd)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 12, Brushes.White, ppd);
        double w = ft.WidthIncludingTrailingWhitespace + 20, h = ft.Height + 10;
        var r = new Rect(center.X - w / 2, center.Y - h / 2, w, h);
        dc.DrawRoundedRectangle(Shade, null, new Rect(r.X, r.Y + 3, w, h), h / 2, h / 2);
        dc.DrawRoundedRectangle(Palette.PanelBrush, Edge, r, h / 2, h / 2);
        dc.DrawText(ft, new Point(r.X + 10, r.Y + 5));
    }
}
