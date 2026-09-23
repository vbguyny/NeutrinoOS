// NeutrinoOS kernel - Phase 5 shell: initialization and environment
//
// Sets the default environment (Task 5), loads the persistent history
// file, runs /etc/profile and ~/.profile when present, and builds the
// prompt (default "neutrinoos> "; PS1 with \u \h \w \$ expansions).

using System;
using System.IO;
using ProtonOS.Platform;

namespace ProtonOS.Shell;

/// <summary>Shell initialization: defaults, profiles, prompt (see file header).</summary>
public static class ShellInit
{
    /// <summary>Path of the system profile script.</summary>
    public const string SystemProfile = "/etc/profile";

    /// <summary>
    /// Path of the user profile script. NeutrinoOS's boot volume is FAT32
    /// with 8.3 short names (a leading-dot name like /.profile is not a
    /// valid short name), so the user profile is /profile (HOME is "/").
    /// </summary>
    public const string UserProfile = "/profile";

    /// <summary>
    /// Path of the persistent history file (FAT-friendly name; the spec's
    /// ~/.history maps to /history.txt on NeutrinoOS - see
    /// docs/PHASE5-SHELL.md).
    /// </summary>
    public const string HistoryFile = "/history.txt";

    /// <summary>
    /// Applies the default environment (only for variables that are not
    /// set yet), initializes the shell PID, installs the tab completer,
    /// loads history and executes the profile scripts.
    /// </summary>
    public static unsafe void Initialize()
    {
        SetDefault("PATH", "/bin:/apps");
        SetDefault("HOME", "/");
        SetDefault("SHELL", "/bin/shell.dll");
        SetDefault("TERM", "vt100");
        SetDefault("USER", "root");
        ShellState.SetVar("PWD", Directory.GetCurrentDirectory());

        ShellState.ShellPid = (int)ProtonOS.Threading.Scheduler.GetCurrentThreadId();

        // Phase 5: command/file tab completion (deferred; see
        // LineDiscipline and ShellCompletion).
        LineDiscipline.TabCompleter = &ShellCompletion.TryComplete;

        LoadHistory();
        RunProfile(SystemProfile);
        RunProfile(UserProfile);
        ShellState.SetVar("PWD", Directory.GetCurrentDirectory());
    }

    private static void SetDefault(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) == null)
            ShellState.SetVar(name, value);
    }

    /// <summary>
    /// Executes a profile script (source semantics, silent when missing,
    /// limited to the last 32 history entries and no argument support).
    /// </summary>
    private static void RunProfile(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                ShellExecutor.ExecuteLine(line);
            }
        }
        catch (Exception)
        {
            Console.Error.WriteLine("neutrinoos: warning: could not read " + path);
        }
    }

    // ==================== History persistence ====================

    /// <summary>
    /// Loads up to the last 32 lines of <.history> into the line
    /// discipline's in-memory history (arrow-key browsing).
    /// </summary>
    public static void LoadHistory()
    {
        if (!File.Exists(HistoryFile))
            return;

        try
        {
            string[] lines = File.ReadAllLines(HistoryFile);
            int start = lines.Length - LineDiscipline.HistoryCapacity;
            if (start < 0)
                start = 0;
            for (int i = start; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                    LineDiscipline.AddHistoryEntry(lines[i]);
            }
        }
        catch (Exception)
        {
            // History is best-effort.
        }
    }

    /// <summary>
    /// Phase 5: history is kept in the line discipline's in-memory store
    /// during the session (the spec's "load on startup, append on exit"
    /// model) and persisted by <see cref="SaveHistoryOnExit"/> when the
    /// shell session ends. Per-command file appends are intentionally
    /// avoided: they would add FAT writes to every keystroke path and
    /// expose shell commands to file-system failures.
    /// </summary>
    public static void RecordHistory(string line)
    {
        // In-memory history is recorded by LineDiscipline.PushHistory
        // when the line completes; nothing to do here.
    }

    /// <summary>
    /// Persists the last <see cref="LineDiscipline"/> history entries to
    /// /.history (best-effort; failures are silent). Called once when the
    /// shell session ends, per the Phase 5 history model.
    /// </summary>
    public static void SaveHistoryOnExit()
    {
        int count = LineDiscipline.GetHistoryCount();
        if (count == 0)
            return;

        try
        {
            var buffer = new char[LineDiscipline.LineCapacity];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                int len = LineDiscipline.GetHistoryEntry(i, buffer);
                sb.Append(buffer, 0, len);
                sb.Append('\n');
            }
            File.WriteAllText(HistoryFile, sb.ToString());
        }
        catch (Exception)
        {
            // Ignore: read-only volume or full disk.
        }
    }

    // ==================== Prompt ====================

    /// <summary>
    /// Builds the prompt: "neutrinoos> " by default; when PS1 is set, the
    /// escapes \u (user), \h (host), \w (working directory) and \$
    /// (root indicator) are expanded.
    /// </summary>
    public static string BuildPrompt()
    {
        string ps1 = ShellState.GetVar("PS1");
        if (ps1.Length == 0)
            return "neutrinoos> ";

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < ps1.Length; i++)
        {
            char c = ps1[i];
            if (c != '\\' || i + 1 >= ps1.Length)
            {
                sb.Append(c);
                continue;
            }

            i++;
            switch (ps1[i])
            {
                case 'u':
                    sb.Append(ShellState.GetVar("USER").Length > 0 ? ShellState.GetVar("USER") : "root");
                    break;
                case 'h':
                    sb.Append("neutrinoos");
                    break;
                case 'w':
                    sb.Append(Directory.GetCurrentDirectory());
                    break;
                case '$':
                    sb.Append(ShellState.GetVar("USER") == "root" ? "#" : "$");
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case '\\':
                    sb.Append('\\');
                    break;
                default:
                    sb.Append('\\');
                    sb.Append(ps1[i]);
                    break;
            }
        }
        return sb.ToString();
    }
}
