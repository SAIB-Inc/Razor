using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Razor.Configuration;

public static class LoggingConfiguration
{
    public static IServiceCollection ConfigureLogging(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new ColorConsoleLoggerProvider());
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        return services;
    }
}

public class ColorConsoleLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, ColorConsoleLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new ColorConsoleLogger(name));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

public class ColorConsoleLogger : ILogger
{
    private readonly string _categoryName;
    private static readonly ConcurrentDictionary<string, DateTime> _lastLogEntries = new();

    private static readonly Dictionary<string, (string Name, string Component)> _categoryMappings = new()
    {
        { "Razor.Commands.QueryCommand", ("Razor", "cardano.node.Query") },
        { "Razor.Commands.TransactionCommand", ("Razor", "cardano.node.Transaction") },
        { "Razor.Services.NodeService.ChainSync", ("Razor", "cardano.node.ChainSync") },
        // Default fallback
        { "Razor", ("Razor", "cardano.cli") }
    };

    public ColorConsoleLogger(string categoryName)
    {
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);

        var logKey = $"{_categoryName}:{logLevel}:{message}";

        if (_lastLogEntries.TryGetValue(logKey, out var lastLogTime))
        {
            var elapsed = DateTime.UtcNow - lastLogTime;
            if (elapsed.TotalMilliseconds < 1000)
            {
                return;
            }
        }

        _lastLogEntries[logKey] = DateTime.UtcNow;

        if (_lastLogEntries.Count > 1000)
        {
            CleanupOldEntries();
        }

        var originalColor = Console.ForegroundColor;

        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ff UTC");
            var shortCategory = GetShortCategory(_categoryName, logLevel);

            Console.ForegroundColor = GetCategoryColor(shortCategory);
            Console.Write($"[{shortCategory}] ");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"[{timestamp}] ");

            Console.ForegroundColor = GetLogLevelColor(logLevel);
            Console.WriteLine(message);

            if (exception != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(exception.ToString());
            }
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }

    private void CleanupOldEntries()
    {
        var cutoffTime = DateTime.UtcNow.AddSeconds(-30);
        foreach (var key in _lastLogEntries.Keys)
        {
            if (_lastLogEntries.TryGetValue(key, out var time) && time < cutoffTime)
            {
                _lastLogEntries.TryRemove(key, out _);
            }
        }
    }

    private ConsoleColor GetCategoryColor(string category)
    {
        if (category.Contains("ChainSync"))
            return ConsoleColor.Cyan;
        if (category.Contains("Query"))
            return ConsoleColor.DarkCyan;
        if (category.Contains("Transaction"))
            return ConsoleColor.DarkCyan;

        return ConsoleColor.White;
    }

    private ConsoleColor GetLogLevelColor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => ConsoleColor.Gray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Information => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.DarkRed,
            _ => ConsoleColor.White,
        };
    }

    private string GetShortCategory(string categoryName, LogLevel logLevel)
    {
        string logLevelStr = logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Info",
            LogLevel.Warning => "Warn",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Crit",
            _ => "Info"
        };

        (string prefix, string component) = ("Razor", "cardano.cli");

        if (_categoryMappings.TryGetValue(categoryName, out var exactMatch))
        {
            (prefix, component) = exactMatch;
        }
        else
        {
            foreach (var mapping in _categoryMappings)
            {
                if (categoryName.StartsWith(mapping.Key) && mapping.Key.Length > prefix.Length)
                {
                    (prefix, component) = mapping.Value;
                }
            }
        }

        return $"{prefix}:{component}:{logLevelStr}";
    }

}