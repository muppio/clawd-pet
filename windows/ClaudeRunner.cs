using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace ClawdPet;

// MARK: - Talking to Claude (headless Claude Code)

abstract record RunnerEvent;
sealed record RunStatus(string Text) : RunnerEvent;
sealed record RunDelta(string Text) : RunnerEvent;
sealed record RunDone(string? Result, string? Session) : RunnerEvent;
sealed record RunFailed(string Message) : RunnerEvent;

/// How to start `claude`: claude.exe directly, or node + cli.js for an npm
/// install (a .cmd shim can't take arguments safely), plus a PATH that has it.
sealed record ClaudeCommand(string FileName, string[] Prefix, string PathEnv);

sealed class ClaudeRunner
{
    public const string ChatPrompt =
        "You are Claude, chatting through Clawd: a small pixel-art buddy that floats in the corner of the " +
        "user's Windows screen. Keep replies short and friendly, usually one to four sentences. Plain text only: " +
        "no headings, tables or code fences unless the user asks for code.";

    public const string VoicePrompt =
        "You are Claude, talking out loud through Clawd, a small desktop buddy on the user's Windows PC. Your reply " +
        "is read aloud by text-to-speech, so answer in one to three natural spoken sentences: no markdown, " +
        "lists, URLs, code or emoji.";

    static readonly UTF8Encoding Utf8 = new(false);
    static readonly object ResolveLock = new();
    static ClaudeCommand? resolved;

    Process? process;
    int generation; // read and written on the UI thread only

    /// Finds `claude` once it's installed (a miss is retried next time).
    public static ClaudeCommand? Resolve()
    {
        lock (ResolveLock)
        {
            resolved ??= Find();
            return resolved;
        }
    }

    static ClaudeCommand? Find()
    {
        string home = Pet.Home;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string>();

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            var d = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
            if (d.Length > 0 && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
        }

        void AddPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (var d in path.Split(';', StringSplitOptions.RemoveEmptyEntries)) Add(d);
        }

        // This process's PATH, plus the saved ones (an install since login isn't in ours yet).
        AddPath(Environment.GetEnvironmentVariable("PATH"));
        AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        Add(Path.Combine(home, ".local", "bin")); // the native installer
        Add(Path.Combine(home, ".claude", "local"));
        Add(Path.Combine(appData, "npm")); // npm install -g
        Add(Path.Combine(local, "Programs", "claude"));
        string pathEnv = string.Join(';', dirs);

        foreach (var d in dirs)
        {
            var exe = Path.Combine(d, "claude.exe");
            if (File.Exists(exe)) return new ClaudeCommand(exe, Array.Empty<string>(), pathEnv);
        }
        foreach (var d in dirs)
        {
            var cmd = Path.Combine(d, "claude.cmd");
            if (!File.Exists(cmd)) continue;
            var cli = Path.Combine(d, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (File.Exists(cli))
            {
                var node = Path.Combine(d, "node.exe");
                if (!File.Exists(node)) node = dirs.Select(x => Path.Combine(x, "node.exe")).FirstOrDefault(File.Exists) ?? "";
                if (node.Length > 0) return new ClaudeCommand(node, new[] { cli }, pathEnv);
            }
            // Last resort: let cmd.exe run the shim.
            return new ClaudeCommand(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", cmd }, pathEnv);
        }
        return null;
    }

    public void Cancel()
    {
        generation++;
        var p = process;
        process = null;
        if (p != null) Task.Run(() => Kill(p));
    }

    static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }

    public void Ask(string prompt, string model, string? resume, bool voice, Action<RunnerEvent> on)
    {
        Cancel();
        int gen = generation;
        var dispatcher = Application.Current.Dispatcher;

        void Emit(RunnerEvent ev) => dispatcher.InvokeAsync(() =>
        {
            if (generation == gen) on(ev);
        });

        Task.Run(() =>
        {
            var cmd = Resolve();
            if (cmd == null)
            {
                Emit(new RunFailed("I couldn't find the claude CLI. Install Claude Code, then try again."));
                return;
            }
            try
            {
                Directory.CreateDirectory(Pet.ChatDir);
            }
            catch
            {
                // The run below says what went wrong.
            }

            var psi = new ProcessStartInfo(cmd.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
                WorkingDirectory = Pet.ChatDir,
            };
            foreach (var a in cmd.Prefix) psi.ArgumentList.Add(a);
            string[] args =
            {
                "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--model", model, "--strict-mcp-config", "--setting-sources", "",
                "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch",
                "--append-system-prompt", voice ? VoicePrompt : ChatPrompt,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (resume != null)
            {
                psi.ArgumentList.Add("--resume");
                psi.ArgumentList.Add(resume);
            }
            psi.Environment["PATH"] = cmd.PathEnv;

            var p = new Process { StartInfo = psi };
            var errText = new StringBuilder();
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (errText) errText.AppendLine(e.Data);
            };
            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                Emit(new RunFailed($"Couldn't start Claude: {ex.Message}"));
                return;
            }
            dispatcher.InvokeAsync(() =>
            {
                if (generation == gen) process = p;
                else Task.Run(() => Kill(p));
            });
            p.BeginErrorReadLine();
            try
            {
                p.StandardInput.Write(prompt);
                p.StandardInput.Close();
            }
            catch
            {
                // It exited early; stderr says why.
            }

            bool sawText = false, finished = false;
            string? session = null;

            void Handle(string line)
            {
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    return;
                }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;
                    string? type = Str(root, "type");
                    if (session == null && Str(root, "session_id") is string sid) session = sid;

                    if (type == "stream_event" && root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
                    {
                        string? evType = Str(ev, "type");
                        if (evType == "content_block_start" && ev.TryGetProperty("content_block", out var block) &&
                            block.ValueKind == JsonValueKind.Object)
                        {
                            string? bt = Str(block, "type");
                            if (bt == "text" && sawText) Emit(new RunDelta("\n\n"));
                            if (bt == "server_tool_use" || bt == "tool_use")
                            {
                                string name = Str(block, "name") ?? "";
                                Emit(new RunStatus(name.Contains("fetch", StringComparison.OrdinalIgnoreCase)
                                    ? "Reading a page…"
                                    : "Searching the web…"));
                            }
                        }
                        else if (evType == "content_block_delta" && ev.TryGetProperty("delta", out var delta) &&
                                 delta.ValueKind == JsonValueKind.Object && Str(delta, "type") == "text_delta" &&
                                 Str(delta, "text") is string text)
                        {
                            sawText = true;
                            Emit(new RunDelta(text));
                        }
                    }
                    else if (type == "result")
                    {
                        finished = true;
                        bool isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                        string? result = Str(root, "result");
                        if (isError) Emit(new RunFailed(string.IsNullOrEmpty(result) ? "Claude couldn't answer that one." : result));
                        else Emit(new RunDone(result, session));
                    }
                }
            }

            try
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length > 0) Handle(line);
                }
                p.WaitForExit();
            }
            catch
            {
                // Killed by Cancel: its events are dropped anyway.
            }

            if (!finished)
            {
                string tail;
                lock (errText) tail = errText.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                int code = -1;
                try
                {
                    code = p.ExitCode;
                }
                catch
                {
                    // Not available.
                }
                Emit(new RunFailed(tail.Length == 0 ? $"Claude stopped unexpectedly (exit {code})." : tail));
            }
            p.Dispose();
        });
    }

    static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// Cuts a streaming reply into speakable sentences.
sealed class SentenceChunker
{
    static readonly Regex Boundary = new(@"[.!?…]+[""'”’)\]]*\s+|[:;]\s+|\n+", RegexOptions.Compiled);
    static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled);
    static readonly Regex Marks = new(@"[*_#`>|~]+", RegexOptions.Compiled);
    static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    string buffer = "";

    public List<string> Feed(string text)
    {
        buffer += text;
        var output = new List<string>();
        int searchStart = 0;
        while (searchStart <= buffer.Length)
        {
            var m = Boundary.Match(buffer, searchStart);
            if (!m.Success) break;
            int end = m.Index + m.Length;
            string candidate = buffer[..end];
            bool isNewline = m.Value.Contains('\n');
            if (candidate.Length >= 28 || isNewline)
            {
                buffer = buffer[end..];
                var c = Clean(candidate);
                if (c.Length > 0) output.Add(c);
                searchStart = 0;
            }
            else
            {
                searchStart = end;
            }
        }
        return output;
    }

    public List<string> Flush()
    {
        var c = Clean(buffer);
        buffer = "";
        return c.Length == 0 ? new List<string>() : new List<string> { c };
    }

    public static string Clean(string s)
    {
        var t = Link.Replace(s, "$1");
        t = Url.Replace(t, "");
        t = Marks.Replace(t, "");
        t = Space.Replace(t, " ");
        return t.Trim();
    }
}
