using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace PrologMobile.Api.Logging;

public sealed class InventoryConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "inventory";

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var severity = logEntry.LogLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRITICAL",
            _ => throw new ArgumentOutOfRangeException(nameof(logEntry), logEntry.LogLevel, "Unsupported log level.")
        };

        textWriter.Write(DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        textWriter.Write(' ');
        textWriter.Write(severity.PadRight(5));
        textWriter.Write(' ');
        textWriter.WriteLine(message);

        if (logEntry.Exception is not null)
        {
            textWriter.WriteLine(logEntry.Exception);
        }
    }
}
