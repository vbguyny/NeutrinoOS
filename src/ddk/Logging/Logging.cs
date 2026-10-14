// NeutrinoOS DDK - Kestrel port milestone M4: the logging shim.
//
// The Microsoft.Extensions.Logging subset the web hosting layer uses:
// ILogger/ILoggerFactory with a console-backed implementation (the CAL
// routes Console output to the serial/VGA consoles) and a null
// implementation. Deliberately small:
//
//   var lf = new ConsoleLoggerFactory("webapp");
//   builder.Services.AddSingleton<ILoggerFactory>(lf);
//   var log = lf.CreateLogger("startup");
//   log.Log(LogLevel.Information, "listening on port 5000");
//
// Rendered line format: [<category>] <Level>: <message>
// (ConsoleLogger keeps the last rendered line in LastMessage so probes
// and status endpoints can expose it without a log file).

using System;

namespace NeutrinoOS.DDK.Logging;

/// <summary>Log severity (matches Microsoft.Extensions.Logging.LogLevel order).</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    None = 6,
}

/// <summary>A minimal logger: one string-based Log entry point.</summary>
public interface ILogger
{
    /// <summary>Minimum level this logger emits.</summary>
    LogLevel MinLevel { get; }

    /// <summary>True when a message at <paramref name="level"/> would be emitted.</summary>
    bool IsEnabled(LogLevel level);

    /// <summary>Emit one message (dropped when below MinLevel).</summary>
    void Log(LogLevel level, string message);
}

/// <summary>Creates loggers by category name.</summary>
public interface ILoggerFactory
{
    /// <summary>Create (or reuse) the logger for a category.</summary>
    ILogger CreateLogger(string categoryName);
}

/// <summary>Console-backed logger (see file header).</summary>
public sealed class ConsoleLogger : ILogger
{
    private readonly string _category;

    /// <summary>The last rendered line, or null when nothing was emitted.</summary>
    public string? LastMessage;

    /// <inheritdoc/>
    public LogLevel MinLevel { get; }

    /// <summary>The category this logger was created for.</summary>
    public string Category => _category;

    /// <summary>Create a logger for a category with a minimum level.</summary>
    public ConsoleLogger(string category, LogLevel minLevel)
    {
        _category = category ?? "";
        MinLevel = minLevel;
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel level) => level >= MinLevel;

    /// <inheritdoc/>
    public void Log(LogLevel level, string message)
    {
        if (level < MinLevel)
            return;
        string line = "[" + _category + "] " + LevelName(level) + ": " + (message ?? "");
        Console.WriteLine(line);
        LastMessage = line;
    }

    /// <summary>Stable level name used in the rendered line.</summary>
    public static string LevelName(LogLevel level)
    {
        if (level == LogLevel.Trace) return "Trace";
        if (level == LogLevel.Debug) return "Debug";
        if (level == LogLevel.Information) return "Information";
        if (level == LogLevel.Warning) return "Warning";
        if (level == LogLevel.Error) return "Error";
        if (level == LogLevel.Critical) return "Critical";
        return "None";
    }
}

/// <summary>Console-backed logger factory; categories are "<prefix>.<name>".</summary>
public sealed class ConsoleLoggerFactory : ILoggerFactory
{
    private const int MaxLoggers = 8;

    private readonly string _prefix;
    private readonly LogLevel _minLevel;
    private readonly ConsoleLogger[] _loggers = new ConsoleLogger[MaxLoggers];
    private int _count;

    /// <summary>Create a factory; default minimum level is Information.</summary>
    public ConsoleLoggerFactory(string prefix)
        : this(prefix, LogLevel.Information)
    {
    }

    /// <summary>Create a factory with an explicit minimum level.</summary>
    public ConsoleLoggerFactory(string prefix, LogLevel minLevel)
    {
        _prefix = prefix ?? "";
        _minLevel = minLevel;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        string category = _prefix.Length == 0
            ? (categoryName ?? "")
            : _prefix + "." + (categoryName ?? "");

        for (int i = 0; i < _count; i++)
        {
            ConsoleLogger existing = _loggers[i];
            if (existing != null && existing.Category == category)
                return existing;
        }

        var logger = new ConsoleLogger(category, _minLevel);
        if (_count < MaxLoggers)
        {
            _loggers[_count] = logger;
            _count++;
        }
        return logger;
    }
}

/// <summary>Discards everything.</summary>
public sealed class NullLogger : ILogger
{
    /// <summary>The shared instance.</summary>
    public static readonly NullLogger Instance = new NullLogger();

    /// <inheritdoc/>
    public LogLevel MinLevel => LogLevel.None;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel level) => false;

    /// <inheritdoc/>
    public void Log(LogLevel level, string message)
    {
    }
}

/// <summary>Creates NullLoggers.</summary>
public sealed class NullLoggerFactory : ILoggerFactory
{
    /// <summary>The shared instance.</summary>
    public static readonly NullLoggerFactory Instance = new NullLoggerFactory();

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
}
