using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Infrastructure;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Tenancy;

/// <summary>
/// Passo 5 (1/2): ITenantContext runs in parallel to ITenantProvider.
/// The provider (and so the EF filter) never changes; only the context reports Platform.
/// </summary>
public sealed class TenantContextTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public void Tenant_mode_when_provider_resolves_a_tenant()
    {
        var tenantId = Guid.NewGuid();
        var context = new TenantContext(new FakeTenantProvider { TenantId = tenantId });

        Assert.Equal(TenantContextMode.Tenant, context.Mode);
        Assert.Equal(tenantId, context.TenantId);
    }

    [Fact]
    public void None_mode_when_provider_has_no_tenant_and_no_platform_scope()
    {
        var context = new TenantContext(new FakeTenantProvider());

        Assert.Equal(TenantContextMode.None, context.Mode);
        Assert.Null(context.TenantId);
    }

    [Fact]
    public void Platform_mode_inside_scope_without_changing_the_provider()
    {
        var tenantId = Guid.NewGuid();
        var provider = new FakeTenantProvider { TenantId = tenantId };
        var context = new TenantContext(provider);

        using (PlatformScope.Begin(Logger, "test"))
        {
            Assert.Equal(TenantContextMode.Platform, context.Mode);
            Assert.Null(context.TenantId);
            Assert.Equal(tenantId, provider.TenantId);
        }

        Assert.Equal(TenantContextMode.Tenant, context.Mode);
        Assert.Equal(tenantId, context.TenantId);
    }

    [Fact]
    public void Nested_scopes_restore_outer_scope_then_previous_mode()
    {
        var context = new TenantContext(new FakeTenantProvider());

        var outer = PlatformScope.Begin(Logger, "outer");
        var inner = PlatformScope.Begin(Logger, "inner");
        Assert.Equal("inner", PlatformScope.CurrentReason);

        inner.Dispose();
        Assert.Equal(TenantContextMode.Platform, context.Mode);
        Assert.Equal("outer", PlatformScope.CurrentReason);

        outer.Dispose();
        Assert.Equal(TenantContextMode.None, context.Mode);
        Assert.False(PlatformScope.IsActive);
        Assert.Null(PlatformScope.CurrentReason);
    }

    [Fact]
    public void Double_dispose_is_a_no_op()
    {
        var outer = PlatformScope.Begin(Logger, "outer");
        var inner = PlatformScope.Begin(Logger, "inner");

        inner.Dispose();
        inner.Dispose();

        Assert.Equal("outer", PlatformScope.CurrentReason);
        outer.Dispose();
        Assert.False(PlatformScope.IsActive);
    }

    [Fact]
    public async Task Dispose_in_a_child_flow_does_not_consume_the_owner_flow_dispose()
    {
        var scope = PlatformScope.Begin(Logger, "owner");

        await Task.Run(scope.Dispose);

        Assert.True(PlatformScope.IsActive);
        Assert.Equal("owner", PlatformScope.CurrentReason);

        scope.Dispose();

        Assert.False(PlatformScope.IsActive);
    }

    [Fact]
    public async Task Dispose_in_an_unrelated_flow_is_a_no_op()
    {
        var scope = PlatformScope.Begin(Logger, "owner");

        Task disposeTask;
        using (ExecutionContext.SuppressFlow())
        {
            disposeTask = Task.Run(scope.Dispose);
        }

        await disposeTask;

        Assert.True(PlatformScope.IsActive);
        Assert.Equal("owner", PlatformScope.CurrentReason);

        scope.Dispose();
        Assert.False(PlatformScope.IsActive);
    }

    [Fact]
    public void Repeated_begin_and_dispose_leave_no_ambient_scope()
    {
        var context = new TenantContext(new FakeTenantProvider());

        for (var i = 0; i < 128; i++)
        {
            using var scope = PlatformScope.Begin(Logger, $"scope-{i}");
        }

        Assert.False(PlatformScope.IsActive);
        Assert.Null(PlatformScope.CurrentReason);
        Assert.Equal(TenantContextMode.None, context.Mode);
    }

    [Fact]
    public void Disposing_outer_before_inner_throws_and_keeps_inner_active()
    {
        var outer = PlatformScope.Begin(Logger, "outer");
        var inner = PlatformScope.Begin(Logger, "inner");

        Assert.Throws<InvalidOperationException>(outer.Dispose);
        Assert.Equal("inner", PlatformScope.CurrentReason);

        inner.Dispose();
        outer.Dispose();
        Assert.False(PlatformScope.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Begin_requires_a_reason(string reason)
    {
        Assert.Throws<ArgumentException>(() => PlatformScope.Begin(Logger, reason));
        Assert.False(PlatformScope.IsActive);
    }

    [Fact]
    public void Begin_logs_the_reason()
    {
        using var loggers = new CapturingLoggerProvider();
        var logger = loggers.CreateLogger("test");

        using (PlatformScope.Begin(logger, "pmoc-engine"))
        {
        }

        var entry = Assert.Single(loggers.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("pmoc-engine", entry.Message);
    }

    [Fact]
    public void Begin_does_not_activate_the_scope_when_logging_throws()
    {
        Assert.Throws<InvalidOperationException>(() => PlatformScope.Begin(new ThrowingLogger(), "job"));

        Assert.False(PlatformScope.IsActive);
        Assert.Null(PlatformScope.CurrentReason);
    }

    [Fact]
    public async Task Scope_does_not_leak_to_parallel_tasks()
    {
        var context = new TenantContext(new FakeTenantProvider());
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var platformTask = Task.Run(async () =>
        {
            using var scope = PlatformScope.Begin(Logger, "job");
            entered.SetResult();
            await release.Task;
            return context.Mode;
        });

        await entered.Task;
        var otherMode = await Task.Run(() => context.Mode);
        release.SetResult();

        Assert.Equal(TenantContextMode.None, otherMode);
        Assert.Equal(TenantContextMode.None, context.Mode);
        Assert.Equal(TenantContextMode.Platform, await platformTask);
    }

    [Fact]
    public async Task Scope_opened_in_awaited_method_does_not_flow_back_to_caller()
    {
        var context = new TenantContext(new FakeTenantProvider());

        await EnterWithoutDisposeAsync();

        Assert.Equal(TenantContextMode.None, context.Mode);

        static async Task EnterWithoutDisposeAsync()
        {
            await Task.Yield();
            PlatformScope.Begin(Logger, "leaky");
        }
    }

    [Fact]
    public async Task Scope_flows_into_child_tasks()
    {
        var context = new TenantContext(new FakeTenantProvider());

        using (PlatformScope.Begin(Logger, "fan-out"))
        {
            var modes = await Task.WhenAll(
                Task.Run(() => context.Mode),
                Task.Run(async () => { await Task.Yield(); return context.Mode; }));

            Assert.All(modes, mode => Assert.Equal(TenantContextMode.Platform, mode));
        }
    }

    [Fact]
    public void Core_persistence_registers_tenant_context_over_the_provider()
    {
        var tenantId = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddCorePersistence("Host=localhost;Database=unused");
        services.AddScoped<ITenantProvider>(_ => new FakeTenantProvider { TenantId = tenantId });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        Assert.Equal(TenantContextMode.Tenant, context.Mode);
        Assert.Equal(tenantId, context.TenantId);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("Logging failed.");
    }
}
