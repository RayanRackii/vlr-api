using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Payments.Gateways.Asaas;

namespace Platform.Api.Tests.Billing;

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (exception is not null)
        {
            Messages.Add(exception.ToString());
        }
    }
}

internal sealed class StaticAsaasOptions(AsaasOptions value) : IOptionsMonitor<AsaasOptions>
{
    public AsaasOptions CurrentValue => value;

    public AsaasOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<AsaasOptions, string?> listener) => null;
}
