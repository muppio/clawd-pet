using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClawdPet;

// MARK: - Clawd, in pixels (a straight port of ClawdPainter in app/ClawdPet.swift)

readonly record struct ClawdLook(
    Mood Mood = Mood.Idle,
    bool Hover = false,
    bool MouthOpen = false,
    bool Calm = false, // "Show animations in Windows" is off: no bobbing or hopping
    bool Walk = false, // step in place (used while he runs across the screen)
    int? LookDX = null // force his gaze left (-1) or right (+1)
);

enum Ink { Body, Light, Dark, Eye, Blush }

readonly record struct Pixel(int X, int Y, int W, int H, Ink Ink);

/// One frame of Clawd: his grid pixels plus the width of his ground shadow.
/// Two equal poses draw identically, so the view redraws only when this changes.
sealed class ClawdPose : IEquatable<ClawdPose>
{
    public readonly List<Pixel> Pixels = new();
    public int ShadowCols = 11;

    public bool Equals(ClawdPose? other) =>
        other is not null && ShadowCols == other.ShadowCols && Pixels.SequenceEqual(other.Pixels);

    public override bool Equals(object? obj) => Equals(obj as ClawdPose);
    public override int GetHashCode() => HashCode.Combine(ShadowCols, Pixels.Count);
}

enum GlyphTone { Clawd, Yellow, White }
enum GlyphWeight { Bold, Heavy, Black }

/// A glyph over his head: Claude's spinner, "!", z's, a sparkle or dizzy stars.
readonly record struct GlyphMark(
    string Text,
    double X,
    double Y,
    double Size,
    GlyphTone Tone,
    GlyphWeight Weight = GlyphWeight.Bold,
    bool Rounded = false,
    double Opacity = 1
);

static class ClawdPainter
{
    static readonly string[] Spinner = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };

    static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// Clawd on a 16x13 grid of pixels. Body is 10x7 with 2x2 arms either side
    /// and four little legs; every pose is a few pixel offsets.
    public static ClawdPose Pose(ClawdLook look, double t)
    {
        var pose = new ClawdPose();
        long frame = (long)(t * 8);
        const int ox = 1, oy = 3;
        bool walking = look.Walk || look.Mood is Mood.Thinking or Mood.Working;

        int bob = look.Mood switch
        {
            Mood.Happy => -Round(Math.Abs(Math.Sin(t * 7.5)) * 2.4),
            Mood.Thinking or Mood.Working or Mood.Sleepy or Mood.Dizzy => 0,
            Mood.Listening or Mood.Alert => Math.Sin(t * 5) > 0 ? -1 : 0,
            _ => Math.Sin(t * 2.2) > 0.55 ? -1 : 0,
        };
        if (look.Calm) bob = 0;

        void Fill(int c, int r, int w, int h, Ink ink) => pose.Pixels.Add(new Pixel(ox + c, oy + bob + r, w, h, ink));

        // The shadow stays on the ground and shrinks while he hops.
        pose.ShadowCols = 11 + bob;

        // Legs: alternate steps while working.
        int[] legs = { 3, 5, 8, 10 };
        for (int i = 0; i < legs.Length; i++)
        {
            bool lifted = walking && ((i % 2 == 0) == (frame % 2 == 0));
            Fill(legs[i], 7, 1, 1, Ink.Body);
            if (!lifted) Fill(legs[i], 8, 1, 1, Ink.Dark);
        }

        // Arms.
        int leftRow = 3, rightRow = 3;
        switch (look.Mood)
        {
            case Mood.Listening:
            case Mood.Alert:
                leftRow = 2;
                rightRow = 2;
                break;
            case Mood.Happy:
                leftRow = frame % 2 == 0 ? 2 : 3;
                rightRow = frame % 2 == 0 ? 3 : 2;
                break;
            case Mood.Speaking:
                rightRow = (frame / 2) % 2 == 0 ? 3 : 2;
                break;
            case Mood.Sleepy:
            case Mood.Dizzy:
                leftRow = 4;
                rightRow = 4;
                break;
            default:
                if (look.Hover) rightRow = (frame / 2) % 2 == 0 ? 2 : 3;
                break;
        }
        Fill(0, leftRow, 2, 1, Ink.Body);
        Fill(0, leftRow + 1, 2, 1, Ink.Dark);
        Fill(12, rightRow, 2, 1, Ink.Body);
        Fill(12, rightRow + 1, 2, 1, Ink.Dark);

        // Body with a little light and shade.
        Fill(2, 0, 10, 7, Ink.Body);
        Fill(3, 0, 8, 1, Ink.Light);
        Fill(2, 1, 1, 3, Ink.Light);
        Fill(2, 6, 10, 1, Ink.Dark);

        // Eyes.
        bool blink = t % 3.9 < 0.22;
        const int lx = 4, rx = 9;
        switch (look.Mood)
        {
            case Mood.Happy:
                foreach (int x in new[] { lx, rx })
                {
                    Fill(x - 1, 3, 1, 1, Ink.Eye);
                    Fill(x, 2, 1, 1, Ink.Eye);
                    Fill(x + 1, 3, 1, 1, Ink.Eye);
                }
                break;
            case Mood.Sleepy:
                Fill(lx - 1, 3, 2, 1, Ink.Eye);
                Fill(rx, 3, 2, 1, Ink.Eye);
                break;
            case Mood.Dizzy:
                foreach (int x in new[] { lx, rx })
                {
                    Fill(x - 1, 1, 1, 1, Ink.Eye);
                    Fill(x + 1, 1, 1, 1, Ink.Eye);
                    Fill(x, 2, 1, 1, Ink.Eye);
                    Fill(x - 1, 3, 1, 1, Ink.Eye);
                    Fill(x + 1, 3, 1, 1, Ink.Eye);
                }
                break;
            case Mood.Listening:
            case Mood.Alert:
                if (blink)
                {
                    Fill(lx, 3, 1, 1, Ink.Eye);
                    Fill(rx, 3, 1, 1, Ink.Eye);
                }
                else
                {
                    Fill(lx, 1, 1, 3, Ink.Eye);
                    Fill(rx, 1, 1, 3, Ink.Eye);
                }
                break;
            case Mood.Thinking:
            case Mood.Working:
                if (blink)
                {
                    Fill(lx + 1, 2, 1, 1, Ink.Eye);
                    Fill(rx + 1, 2, 1, 1, Ink.Eye);
                }
                else
                {
                    Fill(lx + 1, 1, 1, 2, Ink.Eye);
                    Fill(rx + 1, 1, 1, 2, Ink.Eye);
                }
                break;
            default:
                double phase = t % 9;
                int dx = look.LookDX ?? (look.Hover ? 0 : (phase < 1.4 ? -1 : (phase > 5 && phase < 6.3 ? 1 : 0)));
                if (blink)
                {
                    Fill(lx + dx, 3, 1, 1, Ink.Eye);
                    Fill(rx + dx, 3, 1, 1, Ink.Eye);
                }
                else
                {
                    Fill(lx + dx, 2, 1, 2, Ink.Eye);
                    Fill(rx + dx, 2, 1, 2, Ink.Eye);
                }
                break;
        }

        if (look.Mood == Mood.Speaking && look.MouthOpen) Fill(6, 5, 2, 1, Ink.Eye);
        if (look.Mood == Mood.Happy || look.Hover)
        {
            Fill(3, 4, 1, 1, Ink.Blush);
            Fill(10, 4, 1, 1, Ink.Blush);
        }
        return pose;
    }

    public static Rect PixelRect(Pixel q, double px, double dx = 0, double dy = 0) =>
        new(q.X * px + dx, q.Y * px + dy, q.W * px, q.H * px);

    public static Rect ShadowRect(ClawdPose p, double px, double dx = 0, double dy = 0)
    {
        double w = p.ShadowCols * px;
        return new Rect(8 * px - w / 2 + dx, 12 * px - px * 0.35 + dy, w, px * 1.1);
    }

    public static Brush InkBrush(Ink ink) => ink switch
    {
        Ink.Light => Palette.ClawdLightBrush,
        Ink.Dark => Palette.ClawdDarkBrush,
        Ink.Eye => Palette.EyeBrush,
        Ink.Blush => Palette.BlushBrush,
        _ => Palette.ClawdBrush,
    };

    public static GlyphMark[] Glyphs(Mood mood, double t)
    {
        long f = (long)(t * 9);
        switch (mood)
        {
            case Mood.Thinking:
            case Mood.Working:
                return new[] { new GlyphMark(Spinner[f % Spinner.Length], 78, -4, 18, GlyphTone.Clawd) };
            case Mood.Alert:
                return new[] { new GlyphMark("!", 82, Math.Sin(t * 8) > 0 ? -6 : -3, 20, GlyphTone.Yellow, GlyphWeight.Black, true) };
            case Mood.Sleepy:
            {
                double d = Math.Round((t * 5) % 14, MidpointRounding.AwayFromZero);
                var z = new GlyphMark("z", 80 + d * 0.4, 10 - d, 10, GlyphTone.White, GlyphWeight.Heavy, true, 0.55);
                if (d > 4)
                    return new[] { z, new GlyphMark("Z", 86 + d * 0.3, 2 - d * 0.6, 13, GlyphTone.White, GlyphWeight.Heavy, true, 0.55) };
                return new[] { z };
            }
            case Mood.Happy:
                return new[] { new GlyphMark("✦", 82, 0, 13, GlyphTone.Yellow, Opacity: f % 4 < 2 ? 1 : 0.35) };
            case Mood.Dizzy:
                return Enumerable.Range(0, 3).Select(i =>
                {
                    double a = t * 5 + i * 2.1;
                    return new GlyphMark(
                        "★",
                        Math.Round(43 + Math.Cos(a) * 28, MidpointRounding.AwayFromZero),
                        Math.Round(6 + Math.Sin(a) * 5, MidpointRounding.AwayFromZero),
                        10,
                        GlyphTone.Yellow);
                }).ToArray();
            default:
                return Array.Empty<GlyphMark>();
        }
    }

    public static FormattedText GlyphText(GlyphMark g, double pixelsPerDip)
    {
        var weight = g.Weight switch
        {
            GlyphWeight.Heavy => FontWeights.ExtraBold,
            GlyphWeight.Black => FontWeights.Black,
            _ => FontWeights.Bold,
        };
        Color color = g.Tone switch
        {
            GlyphTone.Yellow => Palette.AlertYellow,
            GlyphTone.White => Colors.White,
            _ => Palette.Clawd,
        };
        var typeface = new Typeface(Palette.Symbols, FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(g.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, g.Size,
            Palette.Brush(color, g.Opacity), pixelsPerDip);
    }

    public static void DrawShadow(DrawingContext dc, ClawdPose p, double px, double dx, double dy)
    {
        var r = ShadowRect(p, px, dx, dy);
        dc.DrawEllipse(Palette.Shadow, null, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
    }

    public static void DrawPixels(DrawingContext dc, ClawdPose p, double px, double dx, double dy)
    {
        foreach (var q in p.Pixels) dc.DrawRectangle(InkBrush(q.Ink), null, PixelRect(q, px, dx, dy));
    }

    public static void DrawGlyphs(DrawingContext dc, IEnumerable<GlyphMark> marks, double dx, double dy, double pixelsPerDip)
    {
        foreach (var g in marks) dc.DrawText(GlyphText(g, pixelsPerDip), new Point(g.X + dx, g.Y + dy));
    }
}

/// Clawd on screen: a timer checks his pose a few times a second and only
/// redraws when a pixel actually changes, so an always-on pet costs next to
/// nothing while he's just standing there. Three layers: the soft shadow, the
/// pixels (aliased, so they stay crisp at 125% or 150% scaling) and the glyphs.
sealed class SpriteView : FrameworkElement
{
    public const double Headroom = 18; // room above his head for glyphs
    public const double Sideroom = 16; // and to his right

    readonly DrawingVisual shadowLayer = new();
    readonly DrawingVisual pixelLayer = new();
    readonly DrawingVisual glyphLayer = new();
    readonly VisualCollection layers;
    DispatcherTimer? timer;
    ClawdLook look;
    ClawdPose pose = new();
    GlyphMark[] marks = Array.Empty<GlyphMark>();

    public SpriteView()
    {
        layers = new VisualCollection(this);
        layers.Add(shadowLayer);
        layers.Add(pixelLayer);
        layers.Add(glyphLayer);
        RenderOptions.SetEdgeMode(pixelLayer, EdgeMode.Aliased);
        Width = Pet.PetWidth + Sideroom;
        Height = Pet.PetHeight + Headroom;
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            Reschedule();
            Tick(true);
        };
        Unloaded += (_, _) => StopTimer();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Reschedule();
                Tick(true);
            }
            else
            {
                StopTimer();
            }
        };
    }

    protected override int VisualChildrenCount => layers.Count;
    protected override Visual GetVisualChild(int index) => layers[index];

    public ClawdLook Look
    {
        get => look;
        set
        {
            if (value == look) return;
            var old = look;
            look = value;
            Tick(true);
            if (old.Mood != value.Mood || old.Hover != value.Hover) Reschedule();
        }
    }

    double Fps => look.Hover ? 12 : look.Mood switch
    {
        Mood.Idle => 5,
        Mood.Sleepy => 4,
        _ => 12,
    };

    void StopTimer()
    {
        timer?.Stop();
        timer = null;
    }

    void Reschedule()
    {
        StopTimer();
        if (!IsLoaded || !IsVisible) return;
        timer = Later.Every(1 / Fps, () => Tick(false));
    }

    void Tick(bool force)
    {
        double t = Pet.Now;
        var p = ClawdPainter.Pose(look, t);
        var g = ClawdPainter.Glyphs(look.Mood, t);
        bool poseChanged = force || !p.Equals(pose);
        bool marksChanged = force || !g.SequenceEqual(marks);
        if (!poseChanged && !marksChanged) return;

        if (poseChanged)
        {
            if (force || p.ShadowCols != pose.ShadowCols)
            {
                using var dc = shadowLayer.RenderOpen();
                ClawdPainter.DrawShadow(dc, p, Pet.Px, 0, Headroom);
            }
            using (var dc = pixelLayer.RenderOpen()) ClawdPainter.DrawPixels(dc, p, Pet.Px, 0, Headroom);
            pose = p;
        }
        if (marksChanged)
        {
            using var dc = glyphLayer.RenderOpen();
            ClawdPainter.DrawGlyphs(dc, g, 0, Headroom, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            marks = g;
        }
    }
}
