using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Platform.Api.Authorization;
using Platform.Api.Features.CreateTenant;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Admin;

public sealed class TrialOnboardingInviteTests
{
    [Fact]
    public void Trial_json_without_admin_password_binds_as_omitted()
    {
        var json = """
            {
              "legalName": "Club",
              "taxId": "1",
              "headquartersUnitName": "Matriz",
              "adminFullName": "Ada Admin",
              "adminEmail": "ada@club.test",
              "adminPhone": "11999998888",
              "isTrial": true
            }
            """;

        var request = JsonSerializer.Deserialize<CreateTenantRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Null(request.AdminPassword);
        Assert.True(request.IsTrial);
        Assert.Equal("11999998888", request.AdminPhone);
    }

    [Fact]
    public async Task Trial_without_password_creates_pending_invite_and_skips_auth_user()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Handler.HandleAsync(
            TrialRequest("Ada@Club.test", "11999998888"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        var body = Body<CreateTenantResponse>(result);
        Assert.Null(body.AdminUserId);
        Assert.Null(body.SupabaseAuthId);
        Assert.NotNull(body.InviteId);
        Assert.NotEqual(Guid.Empty, body.SuperAdminRoleId);

        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.MetadataUpdates);
        Assert.Empty(await harness.Db.Users.ToListAsync());

        var invite = await harness.Db.UserInvites
            .Include(item => item.InviteRoles)
            .SingleAsync();
        Assert.Equal(body.InviteId, invite.Id);
        Assert.Equal(body.TenantId, invite.TenantId);
        Assert.Equal("ada@club.test", invite.Email);
        Assert.Equal(SystemRoles.SuperAdmin, invite.RoleName);
        Assert.True(invite.IsPending);
        Assert.Contains(invite.InviteRoles, item => item.RoleId == body.SuperAdminRoleId);

        var role = await harness.Db.Roles.SingleAsync(item => item.Id == body.SuperAdminRoleId);
        Assert.Equal(SystemRoles.SuperAdmin, role.Name);
        Assert.True(role.IsSystemRole);

        Assert.True(harness.Queue.TryRead(out var message));
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Equal("Email", message.Type);
        Assert.Equal("ada@club.test", message.Recipient);
        Assert.StartsWith("Convite Rolvix", message.Subject, StringComparison.Ordinal);
        Assert.Contains($"/invite?token={invite.Token}", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(invite.Token, string.Join('\n', harness.Logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trial_with_blank_password_is_treated_as_omitted()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Handler.HandleAsync(
            TrialRequest("blank@club.test", "11999997777", password: "   "),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        Assert.Empty(harness.Auth.Created);
        Assert.NotNull(Body<CreateTenantResponse>(result).InviteId);
    }

    [Fact]
    public async Task Trial_with_admin_password_is_rejected()
    {
        await using var harness = await Harness.CreateAsync();
        const string suppliedPassword = "SuppliedSecret99";

        var result = await harness.Handler.HandleAsync(
            TrialRequest("secret@club.test", "11999996666", password: suppliedPassword),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("AdminPassword is not accepted.", Error(result));
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(await harness.Db.Tenants.ToListAsync());
        Assert.Empty(await harness.Db.TrialSignupClaims.ToListAsync());
        Assert.Empty(await harness.Db.UserInvites.ToListAsync());
        Assert.False(harness.Queue.TryRead(out _));
        Assert.DoesNotContain(
            suppliedPassword,
            string.Join('\n', harness.Logger.Messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_trial_email_returns_409()
    {
        await using var harness = await Harness.CreateAsync();

        var first = await harness.Handler.HandleAsync(
            TrialRequest("Same@Club.test", "11911112222", legalName: "First Club"),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Status(first));

        var second = await harness.Handler.HandleAsync(
            TrialRequest("same@club.test", "11933334444", legalName: "Second Club"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, Status(second));
        Assert.Equal("Trial already used for this email or phone.", Error(second));
        Assert.Equal(1, await harness.Db.Tenants.CountAsync());
        Assert.Equal(1, await harness.Db.UserInvites.CountAsync());
        Assert.Empty(harness.Auth.Created);
    }

    [Fact]
    public async Task Duplicate_trial_phone_returns_409()
    {
        await using var harness = await Harness.CreateAsync();

        var first = await harness.Handler.HandleAsync(
            TrialRequest("phone-a@club.test", "11988887777", legalName: "Phone Club A"),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Status(first));

        var second = await harness.Handler.HandleAsync(
            TrialRequest("phone-b@club.test", "(11) 98888-7777", legalName: "Phone Club B"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, Status(second));
        Assert.Equal("Trial already used for this email or phone.", Error(second));
        Assert.Equal(1, await harness.Db.Tenants.CountAsync());
        Assert.Empty(harness.Auth.Created);
    }

    [Fact]
    public async Task Accept_invite_sets_the_password_chosen_by_the_invitee()
    {
        await using var harness = await Harness.CreateAsync();
        const string chosenPassword = "invitee-owns-1";

        var created = await harness.Handler.HandleAsync(
            TrialRequest("join@club.test", "11922223333", legalName: "Join Club"),
            CancellationToken.None);
        var body = Body<CreateTenantResponse>(created);
        var invite = await harness.Db.UserInvites.SingleAsync();
        Assert.Empty(harness.Auth.Created);

        var accepted = await harness.Invites.AcceptInviteAsync(
            new AcceptInviteRequestDto
            {
                Token = invite.Token,
                Password = chosenPassword,
            },
            CancellationToken.None);

        var createdAuth = Assert.Single(harness.Auth.Created);
        Assert.Equal("join@club.test", createdAuth.Email);
        Assert.Equal(chosenPassword, createdAuth.Password);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Contains(harness.Auth.MetadataUpdates, item => item.TenantId == body.TenantId);

        var user = await harness.Db.Users.SingleAsync();
        Assert.Equal(accepted.UserId, user.Id);
        Assert.Equal(createdAuth.UserId, user.SupabaseAuthId);
        Assert.Equal("join@club.test", user.Email);

        var assignment = await harness.Db.UserRoles.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(body.SuperAdminRoleId, assignment.RoleId);

        var storedInvite = await harness.Db.UserInvites.SingleAsync();
        Assert.NotNull(storedInvite.AcceptedAt);
        Assert.Equal(user.Id, storedInvite.CreatedUserId);

        Assert.DoesNotContain(
            chosenPassword,
            string.Join('\n', harness.Logger.Messages),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            invite.Token,
            string.Join('\n', harness.Logger.Messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wizard_invite_still_sends_the_same_email_without_a_password()
    {
        await using var harness = await Harness.CreateAsync();
        var tenant = new Tenant("Wizard Club", "99888777000191");
        harness.Db.Tenants.Add(tenant);
        await harness.Db.SaveChangesAsync();

        var invite = await harness.Invites.InviteAsync(
            tenant.Id,
            new InviteTenantUserRequestDto
            {
                FullName = "Wizard Admin",
                Email = "Wizard@Club.test",
                RoleName = SystemRoles.Admin,
            },
            CancellationToken.None);

        Assert.Equal(SystemRoles.Admin, invite.RoleName);
        Assert.Empty(harness.Auth.Created);

        var stored = await harness.Db.UserInvites.SingleAsync();
        Assert.True(harness.Queue.TryRead(out var message));
        Assert.Equal("wizard@club.test", message.Recipient);
        Assert.Contains($"/invite?token={stored.Token}", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Token, string.Join('\n', harness.Logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_trial_public_create_still_requires_and_sets_a_password()
    {
        await using var harness = await Harness.CreateAsync();

        var missing = await harness.Handler.HandleAsync(
            new CreateTenantRequest(
                "Legacy Club",
                "11222333000181",
                null,
                "Matriz",
                "HQ",
                "Legacy Admin",
                "legacy@club.test",
                IsTrial: false),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(missing));
        Assert.Equal("AdminPassword is required.", Error(missing));
        Assert.Empty(harness.Auth.Created);

        var created = await harness.Handler.HandleAsync(
            new CreateTenantRequest(
                "Legacy Club",
                "11222333000181",
                null,
                "Matriz",
                "HQ",
                "Legacy Admin",
                "legacy@club.test",
                "password1",
                IsTrial: false),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(created));
        var body = Body<CreateTenantResponse>(created);
        Assert.NotNull(body.AdminUserId);
        Assert.Equal(harness.Auth.NextUserId, body.SupabaseAuthId);
        Assert.Null(body.InviteId);
        var authUser = Assert.Single(harness.Auth.Created);
        Assert.Equal("legacy@club.test", authUser.Email);
        Assert.Equal("password1", authUser.Password);
        Assert.Empty(await harness.Db.UserInvites.ToListAsync());
        Assert.False(harness.Queue.TryRead(out _));
    }

    private static CreateTenantRequest TrialRequest(
        string email,
        string phone,
        string? password = null,
        string? legalName = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new CreateTenantRequest(
            legalName ?? $"Club {suffix}",
            $"99{suffix}000191",
            null,
            "Matriz",
            "HQ",
            "Ada Admin",
            email,
            password,
            phone,
            IsTrial: true);
    }

    private static int Status(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode
        ?? throw new InvalidOperationException("Result has no status code.");

    private static T Body<T>(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return Assert.IsType<T>(value);
    }

    private static string Error(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value
            ?? throw new InvalidOperationException("Result has no body.");
        var error = value.GetType().GetProperty("error")?.GetValue(value);
        return Assert.IsType<string>(error);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            AppDbContext db,
            CreateTenantHandler handler,
            TenantUserAdminService invites,
            RecordingAuth auth,
            NotificationQueue queue,
            ListLogger<TenantUserAdminService> logger)
        {
            Db = db;
            Handler = handler;
            Invites = invites;
            Auth = auth;
            Queue = queue;
            Logger = logger;
        }

        public AppDbContext Db { get; }

        public CreateTenantHandler Handler { get; }

        public TenantUserAdminService Invites { get; }

        public RecordingAuth Auth { get; }

        public NotificationQueue Queue { get; }

        public ListLogger<TenantUserAdminService> Logger { get; }

        public static async Task<Harness> CreateAsync()
        {
            var tenantProvider = new FakeTenantProvider { TenantId = null };
            var db = InMemoryAppDb.Create(tenantProvider, ignoreInMemoryTransactions: true);
            db.AssetFamilies.AddRange(
                Family(AssetFamilyKeys.Spaces, "Spaces", 1),
                Family(AssetFamilyKeys.Electrical, "Electrical", 2),
                Family(AssetFamilyKeys.Goods, "Goods", 3),
                Family(AssetFamilyKeys.Generic, "Generic", 4));
            await db.SaveChangesAsync();

            var auth = new RecordingAuth();
            var queue = new NotificationQueue();
            var logger = new ListLogger<TenantUserAdminService>();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:FrontendBaseUrl"] = "https://app.example.test",
                })
                .Build();
            var platformAdmin = new FakePlatformAdminChecker();
            var invites = new TenantUserAdminService(
                db,
                auth,
                queue,
                configuration,
                new FakeHostEnvironment(),
                new TrialGuard(db, tenantProvider, platformAdmin),
                platformAdmin,
                logger);
            var handler = new CreateTenantHandler(
                db,
                auth,
                new TenantAccessBootstrapper(db),
                invites);

            return new Harness(db, handler, invites, auth, queue, logger);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static AssetFamily Family(string key, string label, int sortOrder) =>
            new()
            {
                Key = key,
                Label = label,
                FieldSchemaJson = """{"fields":[]}""",
                SortOrder = sortOrder,
                IsActive = true,
            };
    }

    private sealed class RecordingAuth : ISupabaseAuthAdminClient
    {
        public List<CreatedAuthUser> Created { get; } = [];

        public List<(string UserId, Guid TenantId)> MetadataUpdates { get; } = [];

        public List<(string UserId, string Password)> PasswordsSet { get; } = [];

        public string NextUserId { get; set; } = "auth-user-1";

        public Task<string> CreateUserAsync(
            string email,
            string password,
            CancellationToken cancellationToken = default)
        {
            Created.Add(new CreatedAuthUser(email, password, NextUserId));
            return Task.FromResult(NextUserId);
        }

        public Task<string?> FindUserIdByEmailAsync(
            string email,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> UserExistsAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task UpdateUserAppMetadataAsync(
            string supabaseUserId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            MetadataUpdates.Add((supabaseUserId, tenantId));
            return Task.CompletedTask;
        }

        public Task ClearUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetUserPasswordAsync(
            string supabaseUserId,
            string password,
            CancellationToken cancellationToken = default)
        {
            PasswordsSet.Add((supabaseUserId, password));
            return Task.CompletedTask;
        }

        public Task DeleteUserAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
            string email,
            string redirectTo,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseRecoveryLink("token", null));
    }

    private sealed record CreatedAuthUser(string Email, string Password, string UserId);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
