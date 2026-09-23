// NeutrinoOS kernel - Phase 5 shell: token and AST types
//
// The shell parses a command line into a sequence of pipelines; each
// pipeline is one or more commands joined by '|', with optional
// redirections and an optional background marker. The AST uses fixed
// arrays + counts (kernel AOT code has no generic collections) and the
// parser reports syntax errors as (message, column) pairs instead of
// throwing, matching the kernel's no-exception style.

namespace ProtonOS.Shell;

/// <summary>Token kinds produced by <see cref="ShellLexer"/>.</summary>
public enum ShellTokenKind : byte
{
    /// <summary>A word (command name, argument, redirection target); Text is the expanded value.</summary>
    Word = 0,
    /// <summary>Pipeline separator: <c>|</c>.</summary>
    Pipe,
    /// <summary>Sequence separator: <c>;</c>.</summary>
    Semi,
    /// <summary>Background marker: <c>&amp;</c>.</summary>
    Amp,
    /// <summary>Conditional AND: <c>&amp;&amp;</c>.</summary>
    AndIf,
    /// <summary>Conditional OR: <c>||</c>.</summary>
    OrIf,
    /// <summary>Input redirection: <c>&lt;</c>.</summary>
    RedirIn,
    /// <summary>Output truncate: <c>&gt;</c>.</summary>
    RedirOut,
    /// <summary>Output append: <c>&gt;&gt;</c>.</summary>
    RedirAppend,
    /// <summary>Error output truncate: <c>2&gt;</c>.</summary>
    RedirErrOut,
    /// <summary>Error output append: <c>2&gt;&gt;</c>.</summary>
    RedirErrAppend,
    /// <summary>End of input.</summary>
    Eof
}

/// <summary>One lexical token. <see cref="Col"/> is 1-based for error messages.</summary>
public struct ShellToken
{
    /// <summary>Token kind.</summary>
    public ShellTokenKind Kind;

    /// <summary>Expanded text (words only; null for operators).</summary>
    public string? Text;

    /// <summary>1-based column of the token start in the source line.</summary>
    public int Col;
}

/// <summary>Redirection kinds attached to a command.</summary>
public enum ShellRedirKind : byte
{
    /// <summary><c>&lt; path</c>: standard input from a file.</summary>
    In,
    /// <summary><c>&gt; path</c>: standard output to a file (truncate).</summary>
    Out,
    /// <summary><c>&gt;&gt; path</c>: standard output to a file (append).</summary>
    OutAppend,
    /// <summary><c>2&gt; path</c>: standard error to a file (truncate).</summary>
    ErrOut,
    /// <summary><c>2&gt;&gt; path</c>: standard error to a file (append).</summary>
    ErrOutAppend
}

/// <summary>A redirection: kind plus resolved target path.</summary>
public struct ShellRedir
{
    /// <summary>Redirection kind.</summary>
    public ShellRedirKind Kind;

    /// <summary>Target path (already path-resolved by the executor).</summary>
    public string Target;
}

/// <summary>One command: program name + arguments + redirections.</summary>
public struct ShellCommand
{
    /// <summary>Arguments; Args[0] is the program/utility name.</summary>
    public string[] Args;

    /// <summary>Number of valid entries in <see cref="Args"/>.</summary>
    public int ArgCount;

    /// <summary>Redirections attached to this command.</summary>
    public ShellRedir[] Redirs;

    /// <summary>Number of valid entries in <see cref="Redirs"/>.</summary>
    public int RedirCount;
}

/// <summary>One pipeline: commands joined by '|', possibly backgrounded.</summary>
public struct ShellPipeline
{
    /// <summary>Commands in the pipeline (left to right).</summary>
    public ShellCommand[] Commands;

    /// <summary>Number of valid entries in <see cref="Commands"/>.</summary>
    public int CommandCount;

    /// <summary>True when the pipeline ends with '&amp;'.</summary>
    public bool Background;
}

/// <summary>How a pipeline connects to the previous one in a sequence.</summary>
public enum ShellSeqOp : byte
{
    /// <summary><c>;</c> (or start): always run.</summary>
    Always,
    /// <summary><c>&amp;&amp;</c>: run only when the previous exit code was 0.</summary>
    AndIf,
    /// <summary><c>||</c>: run only when the previous exit code was non-zero.</summary>
    OrIf
}

/// <summary>A pipeline plus its connection operator.</summary>
public struct ShellPipelineEntry
{
    /// <summary>Connection to the previous pipeline.</summary>
    public ShellSeqOp Op;

    /// <summary>The pipeline.</summary>
    public ShellPipeline Pipeline;
}

/// <summary>A parsed command line: one or more pipelines.</summary>
public struct ShellScript
{
    /// <summary>Pipelines in execution order.</summary>
    public ShellPipelineEntry[] Entries;

    /// <summary>Number of valid entries in <see cref="Entries"/>.</summary>
    public int EntryCount;
}
