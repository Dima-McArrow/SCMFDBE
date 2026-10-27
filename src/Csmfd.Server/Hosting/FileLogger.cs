using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Csmfd.Server.Hosting;

/// <summary>
/// Bounded local diagnostics (spec 41.16): at most 3 files of 1 MiB, warnings/lifecycle only.
/// Callers never log tokens, pairing codes, keys, Game.log content or full paths.
/// The minimum level can change at runtime (temporary debug logging); the logger factory must let
/// Debug through so this provider is the one that decides.
/// </summary>
internal sealed class FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxFiles = 3;

    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private volatile int _minimumLevel = (int)minimumLevel;

    /// <summary>Read on every IsEnabled call, so a change applies to existing loggers at once.</summary>
    public LogLevel MinimumLevel
    {
        get => (LogLevel)_minimumLevel;
        set => _minimumLevel = (int)value;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level,-11} {category}: {message}{(exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message)}");

        lock (_gate)
        {
            try
            {
                var writer = _writer ??= Open();
                if (writer.BaseStream.Length > MaxFileBytes)
                {
                    writer.Dispose();
                    Rotate();
                    writer = _writer = Open();
                }

                writer.WriteLine(line);
                writer.Flush();
            }
            catch (IOException)
            {
                // Logging must never break input release or networking.
                _writer = null;
            }
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(directory);
        var stream = new FileStream(Path.Combine(directory, "csmfd.log"), FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream);
    }

    private void Rotate()
    {
        for (var index = MaxFiles - 1; index >= 1; index--)
        {
            var source = Path.Combine(directory, index == 1 ? "csmfd.log" : $"csmfd.{index - 1}.log");
            var target = Path.Combine(directory, $"csmfd.{index}.log");
            if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }
        }
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
