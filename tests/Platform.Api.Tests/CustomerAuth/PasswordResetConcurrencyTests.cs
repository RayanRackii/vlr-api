using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class PasswordResetConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private static int SeedSequence;

    private readonly PostgresContainerFixture _postgres;

    public PasswordResetConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Twenty_parallel_wrong_guesses_evaluate_at_most_five()
    {
        var factory = RequireFactory();
        var seeded = await SeedResetAsync(factory, "guess@club.test");
        var probe = new OtpHmacEvaluationCounter();
        var wrong = seeded.Code == "000000" ? "000001" : "000000";

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = factory.Create(seeded.TenantProvider);
            var auth = CreateAuth(db, seeded.TenantProvider, hmacEvaluations: probe);
            await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
                () => auth.ConfirmPasswordResetAsync(
                    Confirm(seeded.Email, wrong, "new-secret"),
                    CancellationToken.None));
        }));

        await using var verify = factory.Create(seeded.TenantProvider);
        var otp = await verify.OtpCodes.SingleAsync(row => row.Purpose == OtpPurposes.PasswordReset);
        var customer = await verify.Customers.SingleAsync(row => row.Email == seeded.Email);
        Assert.Equal(5, probe.Count);
        Assert.Equal(5, otp.Attempts);
        Assert.False(otp.IsUsed);
        Assert.Equal(seeded.PasswordHash, customer.PasswordHash);
        Assert.Null(customer.PasswordChangedAt);
    }

    [DockerFact]
    public async Task Two_parallel_correct_confirms_succeed_once()
    {
        var factory = RequireFactory();
        var seeded = await SeedResetAsync(factory, "race@club.test", verifyEmail: false);
        var wins = 0;

        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = factory.Create(seeded.TenantProvider);
            var auth = CreateAuth(db, seeded.TenantProvider);
            try
            {
                await auth.ConfirmPasswordResetAsync(
                    Confirm(seeded.Email, seeded.Code, "new-secret"),
                    CancellationToken.None);
                Interlocked.Increment(ref wins);
            }
            catch (PhoneVerificationInvalidException)
            {
            }
        }));

        await using var verify = factory.Create(seeded.TenantProvider);
        var otp = await verify.OtpCodes.SingleAsync(row => row.Purpose == OtpPurposes.PasswordReset);
        var customer = await verify.Customers.SingleAsync(row => row.Email == seeded.Email);
        Assert.Equal(1, wins);
        Assert.True(otp.IsUsed);
        Assert.NotEqual(seeded.PasswordHash, customer.PasswordHash);
        Assert.NotNull(customer.PasswordChangedAt);
        Assert.Equal(customer.PasswordChangedAt, customer.EmailVerifiedAt);
    }

    [DockerFact]
    public async Task Confirm_ip_limit_returns_429()
    {
        var factory = RequireFactory();
        var gate = new PhoneVerificationSendGate(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        var seeded = await SeedResetAsync(factory, "limited@club.test", gate);
        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            var decision = gate.Decide(
                seeded.TenantProvider.TenantId!.Value,
                $"prefill{i}@club.test",
                clientIp: null,
                IPhoneVerificationSendGate.PasswordResetConfirmScope);
            Assert.Equal(PhoneVerificationSendDecision.Send, decision);
        }

        await using var db = factory.Create(seeded.TenantProvider);
        var auth = CreateAuth(db, seeded.TenantProvider, gate);
        var controller = CustomerAuthHarness.CreateController(auth);
        var result = await controller.ConfirmPasswordReset(
            Confirm(seeded.Email, seeded.Code, "new-secret"),
            "authclub",
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        await using var verify = factory.Create(seeded.TenantProvider);
        var customer = await verify.Customers.SingleAsync(row => row.Email == seeded.Email);
        Assert.Equal(seeded.PasswordHash, customer.PasswordHash);
        Assert.False(await verify.OtpCodes.AnyAsync(row =>
            row.Purpose == OtpPurposes.PasswordReset && row.IsUsed));
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static async Task<SeededReset> SeedResetAsync(
        PostgresAppDbFactory factory,
        string email,
        IPhoneVerificationSendGate? gate = null,
        bool verifyEmail = true)
    {
        var seed = Interlocked.Increment(ref SeedSequence);
        var tenant = new Tenant("Auth Club", $"99{seed:D12}", subdomain: $"auth{seed}");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = factory.Create(tenantProvider);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var emailProvider = new FakeEmailProvider();
        var auth = CreateAuth(db, tenantProvider, gate, emailProvider);
        await auth.RegisterAsync(CustomerAuthHarness.NewRegister(email: email), CancellationToken.None);
        if (verifyEmail)
        {
            var verificationCode = FakeEmailProvider.ExtractSixDigitCode(emailProvider.Sent[^1].Body);
            await auth.VerifyEmailAsync(
                new VerifyEmailRequestDto { Email = email, Code = verificationCode },
                CancellationToken.None);
        }

        emailProvider.Sent.Clear();
        await auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = email },
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(emailProvider.Sent[^1].Body);
        var customer = await db.Customers.SingleAsync(row => row.Email == email);
        return new SeededReset(tenantProvider, email, code, customer.PasswordHash);
    }

    private static CustomerAuthService CreateAuth(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        IPhoneVerificationSendGate? gate = null,
        FakeEmailProvider? email = null,
        OtpHmacEvaluationCounter? hmacEvaluations = null) =>
        CustomerAuthHarness.CreateService(
            db,
            tenantProvider,
            new FakePhoneVerificationClient(),
            email ?? new FakeEmailProvider(),
            gate ?? new AllowAllPhoneVerificationSendGate(),
            TimeProvider.System,
            hmacEvaluations: hmacEvaluations);

    private static PasswordResetConfirmRequestDto Confirm(string email, string code, string password) =>
        new()
        {
            Email = email,
            Code = code,
            NewPassword = password,
        };

    private sealed record SeededReset(
        FakeTenantProvider TenantProvider,
        string Email,
        string Code,
        string? PasswordHash);
}
