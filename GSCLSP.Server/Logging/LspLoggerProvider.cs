using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;

namespace GSCLSP.Server.Logging;

public class CustomLspLoggerProvider : ILoggerProvider
{
    private readonly IServiceProvider _serviceProvider;

    public CustomLspLoggerProvider(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    public ILogger CreateLogger(string categoryName) => new CustomLspLogger(_serviceProvider, categoryName);

    public void Dispose() { }
}

internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new NullScope();
    private NullScope() { }
    public void Dispose() { }
}

public class CustomLspLogger(IServiceProvider serviceProvider, string categoryName) : ILogger
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly string _categoryName = categoryName;
    private ILanguageServer? _server;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        string message = formatter(state, exception);
        if (exception != null)
        {
            message += $"\n{exception}";
        }

        var messageType = logLevel switch
        {
            LogLevel.Critical => MessageType.Error,
            LogLevel.Error => MessageType.Error,
            LogLevel.Warning => MessageType.Warning,
            LogLevel.Information => MessageType.Info,
            LogLevel.Debug or LogLevel.Trace => MessageType.Log,
            _ => MessageType.Log
        };

        try
        {
            _server ??= _serviceProvider.GetService<ILanguageServer>();

            if (_server != null)
            {
                _server.Window.LogMessage(new LogMessageParams
                {
                    Type = messageType,
                    Message = $"[{_categoryName}] {message}"
                });
                return;
            }
        }
        catch
        {
            // Fallback during extreme early startup before the container is ready
        }

        Console.Error.WriteLine($"[{_categoryName}] {message}");
    }
}
public static class LoggingExtensions
{
    public static ILoggingBuilder AddCustomLanguageProtocolLogging(this ILoggingBuilder builder, ILanguageServer server)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider>(new CustomLspLoggerProvider(server))
        );
        return builder;
    }
}