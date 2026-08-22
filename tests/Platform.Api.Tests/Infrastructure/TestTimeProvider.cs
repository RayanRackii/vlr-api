namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Controllable <see cref="TimeProvider"/> so queue tests freeze and advance time
/// without sleeping 90s. (Microsoft.Extensions.Time.Testing is not in this restore source.)
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
