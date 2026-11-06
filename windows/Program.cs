// Clawd Pet for Windows: a little Claude Code buddy that floats in the corner
// of your screen. Click him to jump to Claude; type, dictate or talk
// voice-to-voice with Claude from the composer he sits on. The Mac original is
// app/ClawdPet.swift; this is the same pet, ported to WPF.
//
// Unofficial fan project, not affiliated with Anthropic or OpenAI.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ClawdPet;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Used by /pet fight: is the Codex pet on screen?
        if (args.Contains("--codex-check"))
        {
            Native.ConsoleOut();
            Console.WriteLine(CodexPet.FindCenter() != null ? "out" : "missing");
            return 0;
        }
        // For calibrating CodexPet on a real PC.
        if (args.Contains("--codex-windows"))
        {
            Native.ConsoleOut();
            CodexPet.Describe(Console.Out);
            return 0;
        }
        // Removes the clawdpet:// link handler and the login item.
        if (args.Contains("--uninstall"))
        {
            Integration.Unregister();
            return 0;
        }

        string url = args.FirstOrDefault(a => a.StartsWith("clawdpet:", StringComparison.OrdinalIgnoreCase)) ?? "";

        // One Clawd at a time: a second launch (a clawdpet:// link, /pet) hands its link over and leaves.
        using var mutex = new Mutex(true, SingleInstance.MutexName, out bool first);
        if (!first)
        {
            SingleInstance.Forward(url);
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Pet.Log(e.Exception);
            e.Handled = true; // a glitch shouldn't make Clawd vanish
        };
        var model = new PetModel();
        var window = new PetWindow(model);
        window.Closed += (_, _) => app.Shutdown(); // Alt+F4 on the composer is "Hide Clawd" too
        app.Exit += (_, _) => model.Shutdown();
        try
        {
            Integration.RegisterProtocol();
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
        SingleInstance.Listen(message => app.Dispatcher.InvokeAsync(() => model.HandleMessage(message)));
        window.Show();
        model.Start();
        if (url.Length > 0) model.HandleUrl(url);
        return app.Run();
    }
}

static class SingleInstance
{
    static string Id => Environment.UserName.Replace('\\', '_');
    public static string MutexName => $@"Local\ClawdPet-{Id}";
    static string PipeName => $"ClawdPet-{Id}";

    public static void Forward(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return;
            }
            catch
            {
                Thread.Sleep(200); // the first Clawd may still be starting
            }
        }
    }

    public static void Listen(Action<string> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    onMessage(reader.ReadToEnd().Trim());
                }
                catch (Exception ex)
                {
                    Pet.Log(ex);
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Clawd links",
        };
        thread.Start();
    }
}

/// Per-user registry entries (HKCU, no admin): the clawdpet:// link handler,
/// and "Open at login" in the Run key.
static class Integration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClawdPet";
    const string ProtocolKey = @"Software\Classes\clawdpet";

    static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClawdPet.exe");

    /// Rewritten on every start, so a moved or updated install keeps working.
    public static void RegisterProtocol()
    {
        using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
        key.SetValue("", "URL:Clawd Pet");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Exe}\",0");
        using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{Exe}\" \"%1\"");
    }

    public static bool LoginEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetLogin(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Exe}\"");
        else key.DeleteValue(RunValue, false);
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, false);
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
        try
        {
            SetLogin(false);
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
    }
}

/// Opens the Claude desktop app, wherever this PC has it: the classic
/// installer's folder, a Start menu shortcut, or a Store/MSIX install (found
/// once through Get-StartApps and remembered).
static class ClaudeApp
{
    public static Task<(bool Ok, string? AppId)> OpenAsync(string? cachedAppId) => Task.Run(() =>
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var exe in new[]
                 {
                     Path.Combine(local, "AnthropicClaude", "claude.exe"),
                     Path.Combine(local, "Programs", "Claude", "Claude.exe"),
                 })
        {
            if (File.Exists(exe) && Start(new ProcessStartInfo(exe) { UseShellExecute = true })) return (true, cachedAppId);
        }

        if (cachedAppId != null && StartApp(cachedAppId)) return (true, cachedAppId);

        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var shortcut = FindShortcut(Environment.GetFolderPath(folder));
            if (shortcut != null && Start(new ProcessStartInfo(shortcut) { UseShellExecute = true })) return (true, cachedAppId);
        }

        var appId = LookUpAppId();
        if (appId != null && StartApp(appId)) return (true, appId);
        return (false, cachedAppId);
    });

    static bool StartApp(string appId) =>
        Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });

    static bool Start(ProcessStartInfo psi)
    {
        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
            return false;
        }
    }

    static string? FindShortcut(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            return Directory.EnumerateFiles(folder, "Claude.lnk", options).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    static string? LookUpAppId()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-Command",
                         "Get-StartApps | Where-Object { $_.Name -eq 'Claude' } | Select-Object -First 1 -ExpandProperty AppID",
                     })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(8000))
            {
                try
                {
                    p.Kill();
                }
                catch
                {
                    // Gone.
                }
                return null;
            }
            var id = read.Result.Trim();
            return id.Length > 0 ? id : null;
        }
        catch
        {
            return null;
        }
    }
}
