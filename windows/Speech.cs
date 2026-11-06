using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ClawdPet;

// MARK: - Voice in, voice out
//
// Both run on Windows' own speech engine (SAPI through System.Speech), which
// works offline: nothing you say leaves the PC until Clawd sends the words to
// Claude. Natural/Neural voices are picked first when Windows exposes them.

sealed class Speaker
{
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    readonly HashSet<Prompt> pending = new();
    SpeechSynthesizer? synth;
    bool broken;

    public Action? OnIdle;
    public Action? OnWord;
    public bool IsSpeaking => pending.Count > 0;

    SpeechSynthesizer? Synth()
    {
        if (synth != null || broken) return synth;
        try
        {
            var s = new SpeechSynthesizer();
            s.SetOutputToDefaultAudioDevice();
            if (PickVoice(s) is string name) s.SelectVoice(name);
            s.Rate = 1; // a touch quicker than Windows' default, like the Mac app
            s.SpeakCompleted += (_, e) => ui.InvokeAsync(() =>
            {
                if (pending.Remove(e.Prompt) && pending.Count == 0) OnIdle?.Invoke();
            });
            s.SpeakProgress += (_, _) => ui.InvokeAsync(() => OnWord?.Invoke());
            synth = s;
        }
        catch (Exception ex)
        {
            broken = true;
            Pet.Log(ex);
        }
        return synth;
    }

    static string? PickVoice(SpeechSynthesizer s)
    {
        string[] preferred = { "Aria", "Jenny", "Ava", "Zira", "Mark", "David" };
        var voices = s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();

        int Rank(VoiceInfo v)
        {
            int r = v.Culture?.Name == "en-US" ? 100 : (v.Culture?.TwoLetterISOLanguageName == "en" ? 50 : 0);
            if (v.Name.Contains("Natural") || v.Name.Contains("Neural") || v.Name.Contains("Online")) r += 30;
            int i = Array.FindIndex(preferred, p => v.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) r += 10 - i;
            return r;
        }

        var best = voices.OrderByDescending(Rank).FirstOrDefault();
        return best != null && Rank(best) >= 50 ? best.Name : null; // never read English in another language's voice
    }

    public void Say(string text)
    {
        var s = Synth();
        if (s == null) return;
        try
        {
            pending.Add(s.SpeakAsync(text));
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
    }

    public void Stop()
    {
        pending.Clear();
        try
        {
            synth?.SpeakAsyncCancelAll();
        }
        catch
        {
            // Nothing was speaking.
        }
    }
}

sealed class Listener
{
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    SpeechRecognitionEngine? engine;
    DispatcherTimer? silenceTimer;
    DispatcherTimer? timeoutTimer;
    string committed = "";
    string latest = "";
    Action<string?>? finalHandler;
    int generation;

    public bool Active { get; private set; }

    /// Why listening can't work on this PC, or null when it can.
    public static string? Problem()
    {
        try
        {
            return SpeechRecognitionEngine.InstalledRecognizers().Count == 0
                ? "Windows has no speech recognizer installed. Add English under Settings › Time & language › Speech, then try again."
                : null;
        }
        catch (Exception ex)
        {
            return $"Speech recognition isn't available on this PC: {ex.Message}";
        }
    }

    static RecognizerInfo? Pick()
    {
        var all = SpeechRecognitionEngine.InstalledRecognizers();
        return all.FirstOrDefault(r => r.Culture.Name == "en-US")
               ?? all.FirstOrDefault(r => r.Culture.Name == CultureInfo.CurrentUICulture.Name)
               ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
               ?? all.FirstOrDefault();
    }

    /// Starts listening; `Active` is true at once, and the engine warms up in
    /// the background. Throws when there's no recognizer or no microphone.
    public async Task StartAsync(
        double silence,
        double noSpeechTimeout,
        Action<string> partial,
        Action<double> level,
        Action<string?> final)
    {
        Stop();
        int gen = ++generation;
        latest = "";
        committed = "";
        finalHandler = final;
        Active = true;

        SpeechRecognitionEngine e;
        try
        {
            e = await Task.Run(() =>
            {
                var info = Pick() ?? throw new InvalidOperationException(
                    "Windows has no speech recognizer installed. Add English under Settings › Time & language › Speech.");
                var x = new SpeechRecognitionEngine(info);
                try
                {
                    x.LoadGrammar(new DictationGrammar());
                    x.SetInputToDefaultAudioDevice();
                }
                catch
                {
                    x.Dispose();
                    throw;
                }
                return x;
            });
        }
        catch
        {
            if (gen == generation)
            {
                Active = false;
                finalHandler = null;
            }
            throw;
        }

        if (gen != generation) // stopped while warming up
        {
            _ = Task.Run(() => e.Dispose());
            return;
        }

        e.SpeechHypothesized += (_, a) =>
        {
            string text = a.Result?.Text ?? "";
            ui.InvokeAsync(() => Heard(gen, (committed + text).Trim(), silence, partial));
        };
        e.SpeechRecognized += (_, a) =>
        {
            string text = a.Result?.Text ?? "";
            ui.InvokeAsync(() =>
            {
                if (gen != generation || !Active) return;
                if (text.Trim().Length > 0) committed += text.Trim() + " ";
                Heard(gen, committed.Trim(), silence, partial);
            });
        };
        e.AudioLevelUpdated += (_, a) =>
        {
            double l = Math.Min(1.0, a.AudioLevel / 60.0);
            ui.InvokeAsync(() =>
            {
                if (gen == generation && Active) level(l);
            });
        };
        e.RecognizeCompleted += (_, _) => ui.InvokeAsync(() =>
        {
            if (gen == generation && Active) Finish();
        });

        engine = e;
        try
        {
            e.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch
        {
            Stop();
            throw;
        }
        timeoutTimer = Later.After(noSpeechTimeout, () =>
        {
            if (gen == generation && latest.Length == 0) Finish();
        });
    }

    void Heard(int gen, string text, double silence, Action<string> partial)
    {
        if (gen != generation || !Active || text.Length == 0) return;
        latest = text;
        partial(text);
        ArmSilence(silence);
    }

    void ArmSilence(double seconds)
    {
        silenceTimer?.Stop();
        int gen = generation;
        silenceTimer = Later.After(seconds, () =>
        {
            if (gen == generation) Finish();
        });
    }

    /// Stops listening and hands over what was heard.
    public void Finish()
    {
        if (!Active) return;
        string text = latest.Trim();
        var handler = finalHandler;
        Stop();
        handler?.Invoke(text.Length == 0 ? null : text);
    }

    /// Stops listening and drops what was heard.
    public void Stop()
    {
        silenceTimer?.Stop();
        silenceTimer = null;
        timeoutTimer?.Stop();
        timeoutTimer = null;
        var e = engine;
        engine = null;
        if (e != null)
        {
            Task.Run(() =>
            {
                try
                {
                    e.RecognizeAsyncCancel();
                }
                catch
                {
                    // Already stopped.
                }
                try
                {
                    e.Dispose();
                }
                catch
                {
                    // Already disposed.
                }
            });
        }
        finalHandler = null;
        Active = false;
        generation++;
    }
}
