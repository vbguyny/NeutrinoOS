// NeutrinoOS kernel - Phase 5 shell: tokenizer
//
// Splits a command line into tokens with POSIX-style quoting:
//   - whitespace separates words (spaces and tabs),
//   - '...' is literal (no escapes, no expansion),
//   - "..." supports \" \\ \$ \` escapes and variable expansion,
//   - \x outside quotes escapes the next character,
//   - $NAME, ${NAME}, $? and $$ expand (the latter two to the last exit
//     code and the shell PID).
// Operators (|, ;, &, &&, ||, <, >, >>, 2>, 2>>) are recognized with or
// without surrounding whitespace. Expansion happens during tokenization
// (the token carries the final text), matching how the executor consumes
// words directly.

using System;

namespace ProtonOS.Shell;

/// <summary>Result of one token; internal lexer iteration state.</summary>
internal struct ShellLexerState
{
    /// <summary>Current scan position.</summary>
    public int Pos;

    /// <summary>True once the line has been fully scanned.</summary>
    public bool Done;
}

/// <summary>
/// Tokenizer for the Phase 5 shell (see file header). Produces at most
/// <see cref="MaxTokens"/> tokens; longer lines are a syntax error.
/// </summary>
public static class ShellLexer
{
    /// <summary>Maximum number of tokens in one line.</summary>
    public const int MaxTokens = 128;

    /// <summary>Maximum number of characters in one expanded word.</summary>
    public const int MaxWordChars = 1024;

    /// <summary>
    /// Tokenizes <paramref name="line"/> into <paramref name="tokens"/>.
    /// Returns the token count (including the terminating Eof token), or
    /// -1 with <paramref name="error"/>/<paramref name="errorCol"/> set
    /// on a syntax error.
    /// </summary>
    public static int Tokenize(string line, ShellToken[] tokens, out string? error, out int errorCol)
    {
        error = null;
        errorCol = 0;

        int pos = 0;
        int count = 0;
        int n = line.Length;

        while (true)
        {
            // Skip whitespace
            while (pos < n && (line[pos] == ' ' || line[pos] == '\t'))
                pos++;

            if (pos >= n)
                break;

            if (count >= MaxTokens - 1)
            {
                error = "too many tokens";
                errorCol = pos + 1;
                return -1;
            }

            char c = line[pos];
            int col = pos + 1;

            // Operators
            switch (c)
            {
                case '|':
                    if (pos + 1 < n && line[pos + 1] == '|')
                    {
                        EmitOp(tokens, count, ShellTokenKind.OrIf, col);
                        count++;
                        pos += 2;
                    }
                    else
                    {
                        EmitOp(tokens, count, ShellTokenKind.Pipe, col);
                        count++;
                        pos++;
                    }
                    continue;
                case ';':
                    EmitOp(tokens, count, ShellTokenKind.Semi, col);
                    count++;
                    pos++;
                    continue;
                case '&':
                    if (pos + 1 < n && line[pos + 1] == '&')
                    {
                        EmitOp(tokens, count, ShellTokenKind.AndIf, col);
                        count++;
                        pos += 2;
                    }
                    else
                    {
                        EmitOp(tokens, count, ShellTokenKind.Amp, col);
                        count++;
                        pos++;
                    }
                    continue;
                case '<':
                    EmitOp(tokens, count, ShellTokenKind.RedirIn, col);
                    count++;
                    pos++;
                    continue;
                case '>':
                    if (pos + 1 < n && line[pos + 1] == '>')
                    {
                        EmitOp(tokens, count, ShellTokenKind.RedirAppend, col);
                        count++;
                        pos += 2;
                    }
                    else
                    {
                        EmitOp(tokens, count, ShellTokenKind.RedirOut, col);
                        count++;
                        pos++;
                    }
                    continue;
            }

            // "2>" / "2>>" error redirection (only when the token starts here)
            if (c == '2' && pos + 1 < n && line[pos + 1] == '>')
            {
                if (pos + 2 < n && line[pos + 2] == '>')
                {
                    EmitOp(tokens, count, ShellTokenKind.RedirErrAppend, col);
                    count++;
                    pos += 3;
                }
                else
                {
                    EmitOp(tokens, count, ShellTokenKind.RedirErrOut, col);
                    count++;
                    pos += 2;
                }
                continue;
            }

            // A word: assemble with quoting, escapes and variable expansion.
            var sb = new System.Text.StringBuilder();
            bool wordDone = false;
            while (pos < n && !wordDone)
            {
                char w = line[pos];
                if (w == ' ' || w == '\t')
                    break;

                switch (w)
                {
                    case '|':
                    case ';':
                    case '&':
                    case '<':
                    case '>':
                        wordDone = true;
                        continue;
                }

                if (w == '\'')
                {
                    // Single quotes: literal until the closing quote.
                    int close = line.IndexOf('\'', pos + 1);
                    if (close < 0)
                    {
                        error = "unclosed single quote";
                        errorCol = pos + 1;
                        return -1;
                    }
                    if (sb.Length + (close - pos - 1) > MaxWordChars)
                    {
                        error = "word too long";
                        errorCol = col;
                        return -1;
                    }
                    sb.Append(line, pos + 1, close - pos - 1);
                    pos = close + 1;
                    continue;
                }

                if (w == '"')
                {
                    // Double quotes: escapes for \" \\ \$ \` and $ expansion.
                    pos++;
                    bool closed = false;
                    while (pos < n)
                    {
                        char d = line[pos];
                        if (d == '"')
                        {
                            pos++;
                            closed = true;
                            break;
                        }
                        if (d == '\\')
                        {
                            if (pos + 1 < n)
                            {
                                char e = line[pos + 1];
                                if (e == '"' || e == '\\' || e == '$' || e == '`')
                                {
                                    sb.Append(e);
                                    pos += 2;
                                    continue;
                                }
                            }
                            sb.Append('\\');
                            pos++;
                            continue;
                        }
                        if (d == '$')
                        {
                            if (!ExpandVariable(line, ref pos, sb, out string? verr, out int vcol))
                            {
                                error = verr;
                                errorCol = vcol;
                                return -1;
                            }
                            continue;
                        }
                        sb.Append(d);
                        pos++;
                    }
                    if (!closed)
                    {
                        error = "unclosed double quote";
                        errorCol = col;
                        return -1;
                    }
                    continue;
                }

                if (w == '\\')
                {
                    // Backslash escape outside quotes: the next character is literal.
                    if (pos + 1 >= n)
                    {
                        error = "trailing backslash";
                        errorCol = pos + 1;
                        return -1;
                    }
                    sb.Append(line[pos + 1]);
                    pos += 2;
                    continue;
                }

                if (w == '$')
                {
                    if (!ExpandVariable(line, ref pos, sb, out string? verr, out int vcol))
                    {
                        error = verr;
                        errorCol = vcol;
                        return -1;
                    }
                    continue;
                }

                sb.Append(w);
                pos++;
            }

            if (sb.Length > MaxWordChars)
            {
                error = "word too long";
                errorCol = col;
                return -1;
            }

            tokens[count].Kind = ShellTokenKind.Word;
            tokens[count].Text = sb.ToString();
            tokens[count].Col = col;
            count++;
        }

        EmitOp(tokens, count, ShellTokenKind.Eof, n + 1);
        return count + 1;
    }

    /// <summary>
    /// Writes an operator token in place (a whole-struct array store of a
    /// struct containing a GC reference needs the RhpByRefAssignRef helper,
    /// which the kernel runtime does not link - see JobManager.cs).
    /// </summary>
    private static void EmitOp(ShellToken[] tokens, int index, ShellTokenKind kind, int col)
    {
        tokens[index].Kind = kind;
        tokens[index].Text = null;
        tokens[index].Col = col;
    }

    /// <summary>
    /// Expands the variable starting at <paramref name="pos"/> (which must
    /// point at '$') and appends the value to <paramref name="sb"/>.
    /// Supports $NAME, ${NAME}, $? and $$. Unknown variables expand to the
    /// empty string. Returns false on a malformed ${...} reference.
    /// </summary>
    private static bool ExpandVariable(string line, ref int pos, System.Text.StringBuilder sb,
        out string? error, out int errorCol)
    {
        error = null;
        errorCol = 0;

        int n = line.Length;
        // pos points at '$'
        if (pos + 1 >= n)
        {
            // A lone '$' is literal.
            sb.Append('$');
            pos++;
            return true;
        }

        char next = line[pos + 1];

        if (next == '$')
        {
            AppendInt(sb, ShellState.ShellPid);
            pos += 2;
            return true;
        }

        if (next == '?')
        {
            AppendInt(sb, ShellState.LastExit);
            pos += 2;
            return true;
        }

        if (next == '{')
        {
            int close = line.IndexOf('}', pos + 2);
            if (close < 0)
            {
                error = "unclosed ${...}";
                errorCol = pos + 1;
                return false;
            }
            string name = line.Substring(pos + 2, close - pos - 2);
            if (name.Length == 0)
            {
                error = "empty variable name";
                errorCol = pos + 1;
                return false;
            }
            sb.Append(ShellState.GetVar(name));
            pos = close + 1;
            return true;
        }

        if (IsNameStart(next))
        {
            int start = pos + 1;
            int end = start;
            while (end < n && IsNameChar(line[end]))
                end++;
            string name = line.Substring(start, end - start);
            sb.Append(ShellState.GetVar(name));
            pos = end;
            return true;
        }

        // '$' followed by anything else: literal '$'.
        sb.Append('$');
        pos++;
        return true;
    }

    private static void AppendInt(System.Text.StringBuilder sb, int value)
    {
        sb.Append(value);
    }

    /// <summary>True for [A-Za-z_]: the first character of a variable name.</summary>
    public static bool IsNameStart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

    /// <summary>True for [A-Za-z0-9_]: a subsequent variable name character.</summary>
    public static bool IsNameChar(char c)
        => IsNameStart(c) || (c >= '0' && c <= '9');
}
