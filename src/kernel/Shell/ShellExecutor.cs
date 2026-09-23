// NeutrinoOS kernel - Phase 5 shell: executor
//
// Executes a parsed ShellScript:
//   - sequences with ';' / '&&' / '||' (conditional on the previous exit code),
//   - pipelines with '|': each stage runs in process; the left stage's
//     stdout is captured with a StringWriter and fed to the right stage
//     through a StringReader (Console.SetOut/SetIn - the same korlib
//     Console the JIT-compiled utilities resolve their calls to, so
//     external utilities are redirected transparently). The pipe is
//     single-shot: the left command runs to completion first. This is
//     documented in docs/PHASE5-DESIGN.md ("in-memory pipe").
//   - redirections: > >> (stdout), 2> 2>> (stderr), < (stdin). Output
//     redirections capture the command's output and commit the file
//     when the stage completes (whole-file commit; see the design doc).
//   - dispatch: built-ins first (ShellBuiltins), then $PATH lookup for
//     <dir>/<name>.dll, executed via the Tier-0 JIT (AssemblyRunner).
//     A name with '/' or a .dll suffix is an explicit path.

using System;
using System.IO;
using ProtonOS.Platform;

namespace ProtonOS.Shell;

/// <summary>Parses and executes shell command lines (see file header).</summary>
public static class ShellExecutor
{
    /// <summary>Exit code used when a command is not found (bash convention).</summary>
    public const int ExitCommandNotFound = 127;

    /// <summary>Exit code used when a command exists but cannot be executed.</summary>
    public const int ExitNotExecutable = 126;

    /// <summary>Exit code used for syntax errors.</summary>
    public const int ExitSyntaxError = 2;

    // Scratch buffers reused across lines (single-threaded shell).
    private static readonly ShellToken[] _tokens = new ShellToken[ShellLexer.MaxTokens];

    /// <summary>
    /// True while a foreground command is executing. The background job
    /// pump checks this so a job never runs re-entrantly inside a
    /// foreground command's JIT frame (see docs/PHASE5-DESIGN.md).
    /// </summary>
    public static bool InForegroundCommand;

    /// <summary>
    /// Parses and executes one command line; returns the exit code of the
    /// last executed pipeline and updates ShellState.LastExit ($?).
    /// </summary>
    public static int ExecuteLine(string line)
    {
        int tokenCount = ShellLexer.Tokenize(line, _tokens, out string? error, out int errorCol);
        if (tokenCount < 0)
        {
            Console.Error.WriteLine("neutrinoos: syntax error: " + error + " (column " + errorCol + ")");
            ShellState.LastExit = ExitSyntaxError;
            return ExitSyntaxError;
        }

        if (!ShellParser.TryParse(_tokens, tokenCount, out ShellScript script, out error, out errorCol))
        {
            Console.Error.WriteLine("neutrinoos: syntax error: " + error + " (column " + errorCol + ")");
            ShellState.LastExit = ExitSyntaxError;
            return ExitSyntaxError;
        }

        int rc = ExecuteScript(script);
        ShellState.LastExit = rc;
        return rc;
    }

    /// <summary>Executes all pipelines of a script (handles ;, &amp;&amp; and ||).</summary>
    public static int ExecuteScript(ShellScript script)
    {
        int last = 0;
        for (int i = 0; i < script.EntryCount; i++)
        {
            ShellPipelineEntry entry = script.Entries[i];

            bool run;
            switch (entry.Op)
            {
                case ShellSeqOp.AndIf: run = last == 0; break;
                case ShellSeqOp.OrIf: run = last != 0; break;
                default: run = true; break;
            }
            if (!run)
                continue;

            last = ExecutePipeline(entry.Pipeline);
        }
        return last;
    }

    /// <summary>Executes one pipeline (possibly backgrounded) and returns its exit code.</summary>
    public static int ExecutePipeline(ShellPipeline pipeline)
    {
        if (pipeline.Background)
            return JobManager.StartBackground(pipeline);

        int last = 0;
        string? pipeText = null;

        for (int i = 0; i < pipeline.CommandCount; i++)
        {
            ShellCommand cmd = pipeline.Commands[i];
            bool isLast = i == pipeline.CommandCount - 1;

            // -------- Redirection targets --------
            string? inputFile = null;
            string? outFile = null;
            bool outAppend = false;
            string? errFile = null;
            bool errAppend = false;

            for (int r = 0; r < cmd.RedirCount; r++)
            {
                ShellRedir redir = cmd.Redirs[r];
                switch (redir.Kind)
                {
                    case ShellRedirKind.In:
                        inputFile = Path.GetFullPath(redir.Target);
                        break;
                    case ShellRedirKind.Out:
                        outFile = Path.GetFullPath(redir.Target);
                        outAppend = false;
                        break;
                    case ShellRedirKind.OutAppend:
                        outFile = Path.GetFullPath(redir.Target);
                        outAppend = true;
                        break;
                    case ShellRedirKind.ErrOut:
                        errFile = Path.GetFullPath(redir.Target);
                        errAppend = false;
                        break;
                    case ShellRedirKind.ErrOutAppend:
                        errFile = Path.GetFullPath(redir.Target);
                        errAppend = true;
                        break;
                }
            }

            // -------- Standard input --------
            TextReader? stdin = null;
            if (inputFile != null)
            {
                if (!File.Exists(inputFile) || Directory.Exists(inputFile))
                {
                    Console.Error.WriteLine("neutrinoos: " + inputFile + ": no such file");
                    return 1;
                }
                stdin = new StringReader(File.ReadAllText(inputFile));
            }
            else if (pipeText != null)
            {
                stdin = new StringReader(pipeText);
            }

            // -------- Standard output capture --------
            // Capture when: a later stage consumes the pipe, or stdout is
            // redirected to a file.
            StringWriter? stdoutCapture = null;
            if (outFile != null || !isLast)
                stdoutCapture = new StringWriter();

            StringWriter? stderrCapture = null;
            if (errFile != null)
                stderrCapture = new StringWriter();

            if (stdin != null)
                Console.SetIn(stdin);
            if (stdoutCapture != null)
                Console.SetOut(stdoutCapture);
            if (stderrCapture != null)
                Console.SetError(stderrCapture);

            int rc;
            try
            {
                rc = RunCommand(cmd);
            }
            finally
            {
                Console.SetIn(null);
                Console.SetOut(null);
                Console.SetError(null);
                Console.Flush();
            }

            // -------- Commit redirected output --------
            if (outFile != null && stdoutCapture != null)
                CommitFile(outFile, stdoutCapture.ToString(), outAppend);
            if (errFile != null && stderrCapture != null)
                CommitFile(errFile, stderrCapture.ToString(), errAppend);

            // -------- Feed the next stage --------
            if (stdoutCapture != null)
                pipeText = outFile == null ? stdoutCapture.ToString() : "";
            else
                pipeText = null;

            last = rc;
        }

        return last;
    }

    private static void CommitFile(string path, string text, bool append)
    {
        try
        {
            if (append)
                File.AppendAllText(path, text);
            else
                File.WriteAllText(path, text);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("neutrinoos: " + path + ": write failed");
        }
    }

    /// <summary>
    /// Runs one command (built-in or external) with the console already
    /// redirected by the caller.
    /// </summary>
    private static int RunCommand(ShellCommand cmd)
    {
        string[] args = new string[cmd.ArgCount];
        for (int i = 0; i < cmd.ArgCount; i++)
            args[i] = cmd.Args[i];

        string name = args[0];

        // Single-level alias expansion: `alias ll='ls -l'` then `ll /` runs `ls -l /`.
        string? alias = ShellState.GetAlias(name);
        if (alias != null)
        {
            args = ExpandAlias(alias, args);
            name = args[0];
        }

        int builtinRc;
        if (ShellBuiltins.TryRun(args, out builtinRc))
            return builtinRc;

        string? exe = ShellState.FindExecutable(name);
        if (exe == null)
        {
            Console.Error.WriteLine("neutrinoos: " + name + ": command not found");
            return ExitCommandNotFound;
        }

        string[] progArgs = new string[args.Length - 1];
        for (int i = 1; i < args.Length; i++)
            progArgs[i - 1] = args[i];

        InForegroundCommand = true;
        int rc;
        try
        {
            rc = AssemblyRunner.Run(exe, progArgs);
        }
        finally
        {
            InForegroundCommand = false;
        }
        if (rc < 0)
            return ExitNotExecutable;
        return rc;
    }

    /// <summary>
    /// Builds the argument array for an alias body: the body's words
    /// followed by the original command's arguments (args[1..]).
    /// </summary>
    private static string[] ExpandAlias(string aliasBody, string[] originalArgs)
    {
        var bodyTokens = new ShellToken[ShellLexer.MaxTokens];
        int n = ShellLexer.Tokenize(aliasBody, bodyTokens, out _, out _);
        if (n < 0)
            return originalArgs;

        int wordCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (bodyTokens[i].Kind == ShellTokenKind.Word)
                wordCount++;
        }
        if (wordCount == 0)
            return originalArgs;

        int extra = originalArgs.Length - 1;
        string[] result = new string[wordCount + extra];
        int w = 0;
        for (int i = 0; i < n; i++)
        {
            if (bodyTokens[i].Kind == ShellTokenKind.Word)
                result[w++] = bodyTokens[i].Text!;
        }
        for (int i = 0; i < extra; i++)
            result[w++] = originalArgs[i + 1];
        return result;
    }
}
