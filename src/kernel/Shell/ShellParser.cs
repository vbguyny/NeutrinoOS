// NeutrinoOS kernel - Phase 5 shell: parser
//
// Builds the AST (ShellScript) from the token stream:
//
//   line     := pipeline ((';' | '&&' | '||') pipeline)*
//   pipeline := command ('|' command)* ('&')?
//   command  := word (word | redirection)*
//   redirection := ('<' | '>' | '>>' | '2>' | '2>>') word
//
// Errors are returned as (message, column) pairs (no exceptions).

using System;

namespace ProtonOS.Shell;

/// <summary>Parses shell lines into <see cref="ShellScript"/> trees (see file header).</summary>
public static class ShellParser
{
    /// <summary>Maximum commands in one pipeline (the shell supports 8 stages).</summary>
    public const int MaxPipelineCommands = 8;

    /// <summary>Maximum arguments (including the program name) per command.</summary>
    public const int MaxCommandArgs = 64;

    /// <summary>Maximum redirections per command.</summary>
    public const int MaxCommandRedirs = 8;

    /// <summary>Maximum pipelines in one line (separated by ; && ||).</summary>
    public const int MaxSequenceEntries = 16;

    /// <summary>
    /// Parses the token stream produced by <see cref="ShellLexer.Tokenize"/>.
    /// Returns true and a fully populated script, or false with
    /// <paramref name="error"/> / <paramref name="errorCol"/> set.
    /// </summary>
    public static bool TryParse(ShellToken[] tokens, int tokenCount, out ShellScript script,
        out string? error, out int errorCol)
    {
        script = default;
        error = null;
        errorCol = 0;

        var entries = new ShellPipelineEntry[MaxSequenceEntries];
        int entryCount = 0;
        int pos = 0;
        ShellSeqOp pendingOp = ShellSeqOp.Always;

        while (true)
        {
            // Skip separators between pipelines.
            while (tokens[pos].Kind == ShellTokenKind.Semi)
            {
                pendingOp = ShellSeqOp.Always;
                pos++;
            }

            if (tokens[pos].Kind == ShellTokenKind.Eof)
                break;

            if (entryCount >= MaxSequenceEntries)
            {
                error = "too many commands in one line";
                errorCol = tokens[pos].Col;
                return false;
            }

            if (!TryParsePipeline(tokens, tokenCount, ref pos, out ShellPipeline pipeline,
                    out error, out errorCol))
                return false;

            // Field-wise store (see JobManager.cs note: whole-struct array
            // stores of structs with GC references need RhpByRefAssignRef).
            entries[entryCount].Op = pendingOp;
            entries[entryCount].Pipeline.Background = pipeline.Background;
            entries[entryCount].Pipeline.CommandCount = pipeline.CommandCount;
            entries[entryCount].Pipeline.Commands = pipeline.Commands;
            entryCount++;

            switch (tokens[pos].Kind)
            {
                case ShellTokenKind.Semi:
                    pendingOp = ShellSeqOp.Always;
                    pos++;
                    continue;
                case ShellTokenKind.AndIf:
                    pendingOp = ShellSeqOp.AndIf;
                    pos++;
                    continue;
                case ShellTokenKind.OrIf:
                    pendingOp = ShellSeqOp.OrIf;
                    pos++;
                    continue;
                case ShellTokenKind.Eof:
                    break;
                default:
                    error = "unexpected '" + Describe(tokens[pos]) + "'";
                    errorCol = tokens[pos].Col;
                    return false;
            }

            if (tokens[pos].Kind == ShellTokenKind.Eof)
                break;
        }

        script.Entries = entries;
        script.EntryCount = entryCount;
        return true;
    }

    private static bool TryParsePipeline(ShellToken[] tokens, int tokenCount, ref int pos,
        out ShellPipeline pipeline, out string? error, out int errorCol)
    {
        pipeline = default;
        error = null;
        errorCol = 0;

        var commands = new ShellCommand[MaxPipelineCommands];
        int commandCount = 0;

        while (true)
        {
            if (commandCount >= MaxPipelineCommands)
            {
                error = "too many commands in the pipeline (max 8)";
                errorCol = tokens[pos].Col;
                return false;
            }

            if (!TryParseCommand(tokens, tokenCount, ref pos, out ShellCommand command,
                    out error, out errorCol))
                return false;

            // Field-wise store (RhpByRefAssignRef note).
            commands[commandCount].Args = command.Args;
            commands[commandCount].ArgCount = command.ArgCount;
            commands[commandCount].Redirs = command.Redirs;
            commands[commandCount].RedirCount = command.RedirCount;
            commandCount++;

            if (tokens[pos].Kind == ShellTokenKind.Pipe)
            {
                pos++;
                if (tokens[pos].Kind == ShellTokenKind.Eof)
                {
                    error = "expected a command after '|'";
                    errorCol = tokens[pos].Col;
                    return false;
                }
                continue;
            }

            break;
        }

        bool background = false;
        if (tokens[pos].Kind == ShellTokenKind.Amp)
        {
            background = true;
            pos++;
        }

        pipeline.Commands = commands;
        pipeline.CommandCount = commandCount;
        pipeline.Background = background;
        return true;
    }

    private static bool TryParseCommand(ShellToken[] tokens, int tokenCount, ref int pos,
        out ShellCommand command, out string? error, out int errorCol)
    {
        command = default;
        error = null;
        errorCol = 0;

        var args = new string[MaxCommandArgs];
        int argCount = 0;
        var redirs = new ShellRedir[MaxCommandRedirs];
        int redirCount = 0;

        while (true)
        {
            ShellToken t = tokens[pos];
            switch (t.Kind)
            {
                case ShellTokenKind.Word:
                    if (argCount >= MaxCommandArgs)
                    {
                        error = "too many arguments (max 64)";
                        errorCol = t.Col;
                        return false;
                    }
                    args[argCount++] = t.Text!;
                    pos++;
                    continue;

                case ShellTokenKind.RedirIn:
                case ShellTokenKind.RedirOut:
                case ShellTokenKind.RedirAppend:
                case ShellTokenKind.RedirErrOut:
                case ShellTokenKind.RedirErrAppend:
                {
                    ShellTokenKind kind = t.Kind;
                    int col = t.Col;
                    pos++;
                    if (tokens[pos].Kind != ShellTokenKind.Word)
                    {
                        error = "missing redirection target after '" + Describe(t) + "'";
                        errorCol = tokens[pos].Kind == ShellTokenKind.Eof ? col : tokens[pos].Col;
                        return false;
                    }
                    if (redirCount >= MaxCommandRedirs)
                    {
                        error = "too many redirections (max 8)";
                        errorCol = col;
                        return false;
                    }

                    // Field-wise store (RhpByRefAssignRef note).
                    redirs[redirCount].Kind = kind switch
                    {
                        ShellTokenKind.RedirIn => ShellRedirKind.In,
                        ShellTokenKind.RedirOut => ShellRedirKind.Out,
                        ShellTokenKind.RedirAppend => ShellRedirKind.OutAppend,
                        ShellTokenKind.RedirErrOut => ShellRedirKind.ErrOut,
                        _ => ShellRedirKind.ErrOutAppend
                    };
                    redirs[redirCount].Target = tokens[pos].Text!;
                    redirCount++;
                    pos++;
                    continue;
                }

                default:
                    if (argCount == 0)
                    {
                        error = "expected a command, found '" + Describe(t) + "'";
                        errorCol = t.Col;
                        return false;
                    }
                    break;
            }

            break;
        }

        command.Args = args;
        command.ArgCount = argCount;
        command.Redirs = redirs;
        command.RedirCount = redirCount;
        return true;
    }

    private static string Describe(ShellToken t)
    {
        switch (t.Kind)
        {
            case ShellTokenKind.Pipe: return "|";
            case ShellTokenKind.Semi: return ";";
            case ShellTokenKind.Amp: return "&";
            case ShellTokenKind.AndIf: return "&&";
            case ShellTokenKind.OrIf: return "||";
            case ShellTokenKind.RedirIn: return "<";
            case ShellTokenKind.RedirOut: return ">";
            case ShellTokenKind.RedirAppend: return ">>";
            case ShellTokenKind.RedirErrOut: return "2>";
            case ShellTokenKind.RedirErrAppend: return "2>>";
            case ShellTokenKind.Eof: return "end of line";
            default: return t.Text ?? "?";
        }
    }
}
