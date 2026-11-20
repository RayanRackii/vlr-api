using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Common;
using Platform.Api.Jobs;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Idempotency;

public sealed class IdempotencyServiceTests
{
    private const string Scope = "test.create";

    private static readonly IdempotencyOptions FastOptions = new()
    {
        PendingWait = TimeSpan.FromMilliseconds(300),
        PollInterval = TimeSpan.FromMilliseconds(10),
        PendingStaleAfter = TimeSpan.FromMinutes(2),
    };

    [Theory]
    [InlineData("a", true)]
    [InlineData("0b6f1c2e-6c3a-4a53-8d1a-5f0f0f0f0f0f", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("with\ncontrol", false)]
    public void Header_validation_accepts_one_to_one_hundred_printable_characters(string key, bool valid) =>
        Assert.Equal(valid, IdempotencyHeader.IsValid(key));

    [Fact]
    public void Header_validation_enforces_the_length_limit()
    {
        Assert.True(IdempotencyHeader.IsValid(new string('k', 100)));
        Assert.False(IdempotencyHeader.IsValid(new string('k', 101)));
    }

    [Fact]
    public async Task First_use_claims_the_key_and_a_repeat_replays_the_stored_resource()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        var resource = Guid.NewGuid();

        var first = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);
        Assert.Equal(IdempotencyOutcome.Proceed, first.Outcome);
        await h.Service().CompleteAsync(first.ClaimId, resource, 201, CancellationToken.None);

        var repeat = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.Replay, repeat.Outcome);
        Assert.Equal(resource, repeat.ResourceId);
        Assert.Equal(201, repeat.ResponseStatus);
        Assert.Equal(1, await h.Db().IdempotencyKeys.CountAsync());
    }

    [Fact]
    public async Task The_same_key_with_a_different_payload_is_reused()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        var first = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);
        await h.Service().CompleteAsync(first.ClaimId, Guid.NewGuid(), 200, CancellationToken.None);

        var other = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("b"), CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.KeyReused, other.Outcome);
    }

    [Fact]
    public async Task A_different_payload_is_reused_even_while_the_first_request_is_running()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        var other = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("b"), CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.KeyReused, other.Outcome);
    }

    [Fact]
    public async Task The_key_is_isolated_by_customer_scope_and_tenant()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        var payload = new Payload("a");
        var first = await h.Service().BeginAsync(Scope, customer, "key-1", payload, CancellationToken.None);
        await h.Service().CompleteAsync(first.ClaimId, Guid.NewGuid(), 200, CancellationToken.None);

        var otherCustomer = await h.Service().BeginAsync(Scope, Guid.NewGuid(), "key-1", payload, CancellationToken.None);
        var otherScope = await h.Service().BeginAsync("test.other", customer, "key-1", payload, CancellationToken.None);
        h.Tenants.TenantId = Guid.NewGuid();
        var otherTenant = await h.Service().BeginAsync(Scope, customer, "key-1", payload, CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.Proceed, otherCustomer.Outcome);
        Assert.Equal(IdempotencyOutcome.Proceed, otherScope.Outcome);
        Assert.Equal(IdempotencyOutcome.Proceed, otherTenant.Outcome);
        Assert.Equal(4, await h.Db().IdempotencyKeys.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task An_expired_key_is_reclaimed_by_the_next_request()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        var old = Guid.NewGuid();
        var expiredRow = new IdempotencyKey(
            h.Tenants.TenantId!.Value, Scope, customer, "key-1", "hash", DateTimeOffset.UtcNow.AddHours(-25));
        expiredRow.Complete(old, 200);
        h.Seed(expiredRow);

        var begin = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("anything"), CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.Proceed, begin.Outcome);
        var rows = await h.Db().IdempotencyKeys.ToListAsync();
        var row = Assert.Single(rows);
        Assert.NotEqual(old, row.Id);
        Assert.Null(row.ResourceId);
    }

    [Fact]
    public async Task An_abandoned_claim_lets_the_retry_run_the_operation_again()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        var first = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        await h.Service().AbandonAsync(first.ClaimId, CancellationToken.None);
        var retry = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.Proceed, retry.Outcome);
        Assert.Equal(1, await h.Db().IdempotencyKeys.CountAsync());
    }

    [Fact]
    public async Task A_claim_still_running_after_the_wait_answers_in_progress()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        var second = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.InProgress, second.Outcome);
    }

    [Fact]
    public async Task A_waiting_request_replays_the_resource_the_first_request_completes()
    {
        var h = Harness.Create(new IdempotencyOptions
        {
            PendingWait = TimeSpan.FromSeconds(10),
            PollInterval = TimeSpan.FromMilliseconds(10),
        });
        var customer = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var first = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        var waiting = h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await h.Service().CompleteAsync(first.ClaimId, resource, 200, CancellationToken.None);

        var replay = await waiting;
        Assert.Equal(IdempotencyOutcome.Replay, replay.Outcome);
        Assert.Equal(resource, replay.ResourceId);
    }

    [Fact]
    public async Task A_claim_that_never_completed_is_taken_over_after_the_stale_window()
    {
        var h = Harness.Create();
        var customer = Guid.NewGuid();
        var crashed = new IdempotencyKey(
            h.Tenants.TenantId!.Value, Scope, customer, "key-1", HashOf(new Payload("a")), DateTimeOffset.UtcNow.AddMinutes(-5));
        h.Seed(crashed);

        var begin = await h.Service().BeginAsync(Scope, customer, "key-1", new Payload("a"), CancellationToken.None);

        Assert.Equal(IdempotencyOutcome.Proceed, begin.Outcome);
        Assert.NotEqual(crashed.Id, begin.ClaimId);
    }

    [Fact]
    public async Task Completing_a_claim_that_was_taken_over_is_a_no_op()
    {
        var h = Harness.Create();

        await h.Service().CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), 200, CancellationToken.None);

        Assert.Equal(0, await h.Db().IdempotencyKeys.CountAsync());
    }

    [Fact]
    public async Task Retention_job_deletes_only_expired_keys_across_tenants()
    {
        var databaseName = $"idem-retention-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using (var seed = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            seed.IdempotencyKeys.AddRange(
                new IdempotencyKey(tenantA, Scope, Guid.NewGuid(), "expired-a", "h", now.AddHours(-30)),
                new IdempotencyKey(tenantB, Scope, Guid.NewGuid(), "expired-b", "h", now.AddHours(-25)),
                new IdempotencyKey(tenantA, Scope, Guid.NewGuid(), "live-a", "h", now.AddHours(-1)),
                new IdempotencyKey(tenantB, Scope, Guid.NewGuid(), "live-b", "h", now));
            await seed.SaveChangesAsync();
        }

        await using (var jobDb = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName, failClosed: true))
        {
            await new DataRetentionJob(jobDb, NullLogger<DataRetentionJob>.Instance)
                .ExecuteAsync(CancellationToken.None);
        }

        await using var verify = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var left = await verify.IdempotencyKeys.Select(k => k.Key).OrderBy(k => k).ToListAsync();
        Assert.Equal(["live-a", "live-b"], left);
    }

    private static string HashOf(object payload)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            payload,
            payload.GetType(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(json));
    }

    private sealed record Payload(string Value);

    private sealed class Harness
    {
        private readonly string databaseName = $"idem-{Guid.NewGuid():N}";
        private readonly IdempotencyOptions options;

        private Harness(IdempotencyOptions options)
        {
            this.options = options;
            Tenants = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        }

        public FakeTenantProvider Tenants { get; }

        public static Harness Create(IdempotencyOptions? options = null) => new(options ?? FastOptions);

        /// <summary>A fresh context on the shared store, like a new request.</summary>
        public AppDbContext Db() => InMemoryAppDb.Create(Tenants, databaseName);

        public IdempotencyService Service() => new(Db(), Tenants, TimeProvider.System, options);

        public void Seed(IdempotencyKey row)
        {
            using var db = Db();
            db.IdempotencyKeys.Add(row);
            db.SaveChanges();
        }
    }
}
