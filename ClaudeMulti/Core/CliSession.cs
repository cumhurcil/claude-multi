using System.ComponentModel;
using System.Diagnostics;
using ClaudeMulti.Core.Transfer;

namespace ClaudeMulti.Core;

/// <summary>
/// What runs inside the terminal window of an API instance on macOS and Linux (<c>ClaudeMulti --run-cli &lt;id&gt;</c>):
/// loads the instance's saved gateway settings, starts Claude Code with them in its environment and waits for it.
/// </summary>
public static class CliSession
{
    public static int Run(ClaudeEnvironment env, string instanceId, IKeyProtector protector)
    {
        try
        {
            var instance = InstanceCatalog.List(env).FirstOrDefault(i => i.Id == instanceId)
                ?? throw new InvalidOperationException($"Instance '{instanceId}' not found.");
            var settings = ApiProfile.Load(instance, protector)
                ?? throw new InvalidOperationException($"'{instanceId}' has no API settings.");
            if (settings.ApiKey.Length == 0)
                throw new InvalidOperationException("Could not read the API key (it may have been saved by another user or computer). Enter the key again in Claude Multi.");
            var cli = CliLauncher.FindCli(env)
                ?? throw new InvalidOperationException("Claude Code (CLI) not found. To install it, run: curl -fsSL https://claude.ai/install.sh | bash");

            // Ctrl+C is for Claude Code; this wrapper just waits for it to finish.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            using var process = Process.Start(CliLauncher.BuildCliInfo(cli, instance, settings, env.Home))
                ?? throw new InvalidOperationException("Could not start Claude Code.");
            process.WaitForExit();
            if (process.ExitCode != 0) Pause($"Claude Code exited with code {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Pause(e.Message);
            return 1;
        }
    }

    /// <summary>Keeps the window open so the message can be read; a terminal closes with its program otherwise.</summary>
    private static void Pause(string message)
    {
        Console.Error.WriteLine(message);
        if (Console.IsInputRedirected) return;
        Console.Error.WriteLine("Press Enter to close this window.");
        Console.ReadLine();
    }
}
