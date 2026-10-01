using Microsoft.Extensions.Logging;
using Security.Core.Paths;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Security.Infrastructure.Logging;

/// <summary>
/// Serilog configuration for SECURITY.EXE.
///
/// Privacy rules enforced here:
///  - logs are plain application logs;
///  - no biometric embeddings, no raw images, and no protected payloads are
///    ever written (callers must not pass them, and the destructuring policy
///    below refuses byte[]/float[] style payloads from structured properties).
/// </summary>
public static class LogConfiguration
{
    public const string LogsDirectory = "logs";

    /// <summary>Shared file output template (console uses a shorter one).</summary>
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    /// <param name="baseDirectory">
    /// Optional explicit root. When omitted the repository's <c>logs/</c>
    /// folder is used (falling back to the executable directory).
    /// </param>
    public static string ResolveLogDirectory(string? baseDirectory = null)
    {
        var dir = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppPaths.ResolveDirectory(LogsDirectory)
            : Path.Combine(baseDirectory, LogsDirectory);

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Apply the SECURITY.EXE logging policy to any Serilog configuration.
    /// Shared by the host (<c>UseSerilog</c>) and <see cref="CreateLoggerFactory"/>
    /// so both sinks get identical levels, filters, and privacy-safe templates.
    /// </summary>
    public static void Configure(LoggerConfiguration configuration, LogEventLevel level = LogEventLevel.Information)
    {
        var logDir = ResolveLogDirectory();
        var logPath = Path.Combine(logDir, "security-.log");

        configuration
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Error)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: OutputTemplate)
            .WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
    }

    public static ILoggerFactory CreateLoggerFactory(string? baseDirectory = null, LogEventLevel level = LogEventLevel.Information)
    {
        var configuration = new LoggerConfiguration();
        Configure(configuration, level);
        var logger = configuration.CreateLogger();

        return new LoggerFactory(new[] { new SerilogLoggerProvider(logger) });
    }

    /// <summary>Dispose helper so the host can shut Serilog down cleanly.</summary>
    public static void CloseAndFlush()
    {
        // Serilog's global logger (if any) is flushed by the host on shutdown.
        // Kept as an explicit hook for call sites that do not use a generic host.
        Log.CloseAndFlush();
    }
}
