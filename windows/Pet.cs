using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClawdPet;

// MARK: - Constants (the same numbers as app/ClawdPet.swift)

static class Pet
{
    public const double WindowWidth = 450, WindowHeight = 470;
    public const double ComposerWidth = 420, ComposerHeight = 96;
    public const double Px = 6;
    public const int GridCols = 16, GridRows = 13;
    public const double PetInset = 14; // pet canvas's left edge over the composer
    public const double ComposerTop = 46; // composer starts this far below the pet canvas top
    public const double TextInset = 112; // composer text starts right of the pet
    public const double PetWidth = GridCols * Px, PetHeight = GridRows * Px;
    public const double PetCenterX = PetInset + PetWidth / 2;

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string SupportDir => Path.Combine(Home, ".clawd-pet");
    public static string StatusFile => Path.Combine(SupportDir, "status.json");
    public static string ChatDir => Path.Combine(SupportDir, "chat");
    public static string SettingsFile => Path.Combine(SupportDir, "settings.json");
    public static string ErrorLog => Path.Combine(SupportDir, "error.log");

    static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// Seconds on the same clock the Mac app animates with.
    public static double Now => (DateTime.UtcNow - Epoch).TotalSeconds;

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SupportDir);
            File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n");
        }
        catch
        {
            // Logging must never take Clawd down.
        }
    }
}

static class Palette
{
    public static readonly Color Clawd = Color.FromRgb(0xD7, 0x77, 0x57); // #D77757
    public static readonly Color ClawdLight = Color.FromRgb(0xEC, 0x99, 0x78);
    public static readonly Color ClawdDark = Color.FromRgb(0xB0, 0x5B, 0x3F);
    public static readonly Color Eye = Color.FromRgb(0x17, 0x13, 0x12);
    public static readonly Color Blush = Color.FromRgb(0xFF, 0x99, 0xA3);
    public static readonly Color PanelFill = Color.FromRgb(0x31, 0x31, 0x34);
    public static readonly Color AlertYellow = Color.FromRgb(0xFF, 0xCC, 0x52);

    public static SolidColorBrush Brush(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush ClawdBrush = Brush(Clawd);
    public static readonly SolidColorBrush ClawdLightBrush = Brush(ClawdLight);
    public static readonly SolidColorBrush ClawdDarkBrush = Brush(ClawdDark);
    public static readonly SolidColorBrush EyeBrush = Brush(Eye);
    public static readonly SolidColorBrush BlushBrush = Brush(Blush);
    public static readonly SolidColorBrush YellowBrush = Brush(AlertYellow);
    public static readonly SolidColorBrush PanelBrush = Brush(PanelFill);
    public static readonly SolidColorBrush PanelStroke = Brush(Colors.White, 0.10);
    public static readonly SolidColorBrush Dim = Brush(Colors.White, 0.42);
    public static readonly SolidColorBrush White85 = Brush(Colors.White, 0.85);
    public static readonly SolidColorBrush White80 = Brush(Colors.White, 0.80);
    public static readonly SolidColorBrush White93 = Brush(Colors.White, 0.93);
    public static readonly SolidColorBrush Shadow = Brush(Colors.Black, 0.32);

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Serif = new("Georgia, Cambria, Times New Roman");
    public static readonly FontFamily Symbols = new("Segoe UI, Segoe UI Symbol");
}

enum Mood { Idle, Thinking, Working, Listening, Speaking, Happy, Alert, Sleepy, Dizzy }

enum ClaudeModel { Opus, Sonnet, Haiku }

static class ClaudeModels
{
    public static string Id(this ClaudeModel m) => m switch
    {
        ClaudeModel.Opus => "opus",
        ClaudeModel.Haiku => "haiku",
        _ => "sonnet",
    };

    public static string Label(this ClaudeModel m) => m switch
    {
        ClaudeModel.Opus => "Opus 5.5",
        ClaudeModel.Haiku => "Haiku 5.5",
        _ => "Sonnet 5.5",
    };

    public static string Blurb(this ClaudeModel m) => m switch
    {
        ClaudeModel.Opus => "Smartest",
        ClaudeModel.Haiku => "Fastest",
        _ => "Fast + smart",
    };

    public static ClaudeModel Parse(string? id) => id switch
    {
        "opus" => ClaudeModel.Opus,
        "haiku" => ClaudeModel.Haiku,
        _ => ClaudeModel.Sonnet,
    };
}

enum BubbleKind { Chat, Code, Error }

/// What his speech bubble shows. A new Id is a new bubble (it pops in);
/// `with` keeps the Id, so streamed text updates the same bubble.
sealed record Bubble(BubbleKind Kind, string Status, string Prompt = "", string Body = "", string Detail = "")
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// ~/.clawd-pet/status.json, written by the clawd-pet mod on every turn.
sealed class CodeStatus
{
    public string State { get; set; } = "";
    public string? Title { get; set; }
    public string? Detail { get; set; }
    public string? Project { get; set; }

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}

/// The Mac app keeps these in UserDefaults; here they're a small JSON file
/// next to the status file, so uninstalling is deleting one folder.
sealed class Settings
{
    public double? OriginX { get; set; }
    public double? OriginY { get; set; }
    public bool ComposerOpen { get; set; } = true;
    public bool SpeakReplies { get; set; }
    public bool SparRandomly { get; set; }
    public string Model { get; set; } = "sonnet";
    public string? SessionID { get; set; }
    public bool Welcomed { get; set; }
    public int ScoreClawd { get; set; }
    public int ScoreCodex { get; set; }
    public int ScoreDraw { get; set; }
    public string? ClaudeAppId { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Pet.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Pet.SettingsFile)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Pet.SupportDir);
            var tmp = Pet.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Pet.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
    }
}

/// One-shot and repeating timers on the UI thread.
static class Later
{
    public static DispatcherTimer After(double seconds, Action work)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0.001, seconds)) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            work();
        };
        t.Start();
        return t;
    }

    public static DispatcherTimer Every(double seconds, Action work)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => work();
        t.Start();
        return t;
    }
}
