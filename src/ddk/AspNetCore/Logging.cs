// NeutrinoOS - ASP.NET Core namespace compatibility layer (M7):
// Microsoft.Extensions.Logging.
//
// The logging surface minimal-API apps use:
//
//   logger.LogInformation("...");           // extension shapes
//   builder.Services.AddSingleton<ILogger>(...);
//
// Documented limits in docs/KESTREL-PORT.md M7: this is the simplified
// level + message shape. Real ASP.NET Core's ILogger.Log is generic
// over state/args and LogInformation takes format args; code using
// string interpolation ("$\"...\"") or single-string calls compiles
// against both. Structured logging/EventId/source generation are later
// milestones.

namespace Microsoft.Extensions.Logging;

/// <summary>Log severity (matches the ASP.NET Core values).</summary>
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

/// <summary>The simplified logger shape (level + message; see file header).</summary>
public interface ILogger
{
    /// <summary>True when a message at this level would be written.</summary>
    bool IsEnabled(LogLevel level);

    /// <summary>Write a message at the given level.</summary>
    void Log(LogLevel level, string message);
}

/// <summary>The logger factory shape.</summary>
public interface ILoggerFactory
{
    /// <summary>Create (or fetch) the logger for a category.</summary>
    ILogger CreateLogger(string categoryName);
}

/// <summary>LogInformation/LogWarning/... extension shapes.</summary>
public static class LoggerExtensions
{
    public static void LogTrace(this ILogger logger, string message) => logger.Log(LogLevel.Trace, message);

    public static void LogDebug(this ILogger logger, string message) => logger.Log(LogLevel.Debug, message);

    public static void LogInformation(this ILogger logger, string message) => logger.Log(LogLevel.Information, message);

    public static void LogWarning(this ILogger logger, string message) => logger.Log(LogLevel.Warning, message);

    public static void LogError(this ILogger logger, string message) => logger.Log(LogLevel.Error, message);

    public static void LogCritical(this ILogger logger, string message) => logger.Log(LogLevel.Critical, message);
}
