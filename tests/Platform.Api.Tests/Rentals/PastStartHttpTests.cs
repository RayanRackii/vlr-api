using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class PastStartHttpTests
{
    [Fact]
    public async Task Post_lesson_in_the_past_returns_400_with_stable_code()
    {
        using var host = StartHost(typeof(TeacherLessonsController));
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StaffAuthHandler.HeaderName, "teacher@club.test");

        var response = await client.PostAsJsonAsync(
            "/api/schedule/lessons",
            new
            {
                rentalAssetId = Guid.NewGuid(),
                date = "2026-08-25",
                startTime = "10:00:00",
                endTime = "11:00:00",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        Assert.Equal(StartInThePastException.Code, body.Code);
        Assert.Equal("Aulas só podem alterar horários futuros.", body.Error);
    }

    [Fact]
    public async Task Post_reservation_in_the_past_returns_400_with_stable_code()
    {
        using var host = StartHost(typeof(ReservationsController), customer: true);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(CustomerAuthHandler.HeaderName, "member@club.test");

        var response = await client.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                unitId = Guid.NewGuid(),
                date = "2026-08-25",
                startTime = "10:00:00",
                endTime = "11:00:00",
                items = new[] { new { assetId = Guid.NewGuid(), quantity = 1 } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        Assert.Equal(StartInThePastException.Code, body.Code);
        Assert.Equal("O início do horário solicitado já passou.", body.Error);
    }

    [Fact]
    public async Task Post_book_slot_in_the_past_returns_400_with_stable_code()
    {
        using var host = StartHost(typeof(ScheduleController), customer: true);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(CustomerAuthHandler.HeaderName, "member@club.test");

        var response = await client.PostAsJsonAsync(
            "/api/schedule/slots/book",
            new { slotId = Guid.NewGuid(), unitId = Guid.NewGuid(), quantity = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        Assert.Equal(StartInThePastException.Code, body.Code);
        Assert.Equal("O início do horário solicitado já passou.", body.Error);
    }

    private static IHost StartHost(Type controller, bool customer = false) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<ITeacherLessonService, ThrowingLessonService>();
                    services.AddSingleton<IReservationService, ThrowingReservationService>();
                    services.AddSingleton<IScheduleService, ThrowingScheduleService>();
                    services.AddSingleton<IRentalAssetService, UnusedRentalAssetService>();
                    services.AddSingleton<IPublicTenantBinder, UnusedTenantBinder>();
                    services.AddSingleton<ITenantProvider, NullTenantProvider>();
                    if (customer)
                    {
                        services.AddAuthentication(CustomerJwtBearerDefaults.AuthenticationScheme)
                            .AddScheme<AuthenticationSchemeOptions, CustomerAuthHandler>(
                                CustomerJwtBearerDefaults.AuthenticationScheme,
                                _ => { });
                    }
                    else
                    {
                        services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                            .AddScheme<AuthenticationSchemeOptions, StaffAuthHandler>(
                                SupabaseJwtBearerDefaults.AuthenticationScheme,
                                _ => { });
                    }

                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationHandler, AllowPermissionHandler>();
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(controller.Assembly));
                            manager.FeatureProviders.Add(new SingleControllerFeatureProvider(controller));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

    private sealed record ErrorBody(string? Error, string? Code);

    private sealed class AllowPermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class NullTenantProvider : ITenantProvider
    {
        public Guid? TenantId => null;
    }

    private sealed class UnusedTenantBinder : IPublicTenantBinder
    {
        public Task BindFromSubdomainAsync(string? subdomain, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingLessonService : ITeacherLessonService
    {
        public Task<SlotResponseDto> CreateAsync(
            CreateTeacherLessonRequestDto request,
            CancellationToken cancellationToken) =>
            throw new StartInThePastException("Aulas só podem alterar horários futuros.");

        public Task<SlotResponseDto> RemoveAsync(
            RemoveTeacherLessonRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class ThrowingReservationService : IReservationService
    {
        public Task<ReservationResponseDto> CreateReservationAsync(
            Guid customerId,
            CreateReservationRequestDto request,
            CancellationToken cancellationToken) =>
            throw new StartInThePastException("O início do horário solicitado já passou.");

        public Task<CheckAvailabilityResponseDto> CheckAvailabilityAsync(
            CheckAvailabilityRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ReservationResponseDto>> ListMineAsync(
            Guid customerId,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ReservationResponseDto>> ListAdminAsync(
            DateOnly? from,
            DateOnly? to,
            ReservationStatus? status,
            Guid? assetId,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ReservationResponseDto> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ReservationResponseDto> CompleteAsync(Guid reservationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ReservationResponseDto> CancelAsync(Guid reservationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ReservationResponseDto> CancelByCustomerAsync(
            Guid customerId,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class ThrowingScheduleService : IScheduleService
    {
        public Task<ReservationResponseDto> BookSlotAsync(
            Guid customerId,
            BookSlotRequestDto request,
            CancellationToken cancellationToken) =>
            throw new StartInThePastException("O início do horário solicitado já passou.");

        public Task<IReadOnlyList<ScheduleTemplateResponseDto>> ListTemplatesAsync(
            Guid? rentalAssetId,
            CancellationToken cancellationToken,
            IReadOnlyCollection<Guid>? rentalAssetIds = null,
            DayOfWeek? dayOfWeek = null) =>
            throw new NotImplementedException();

        public Task<ScheduleTemplateResponseDto> CreateTemplateAsync(
            UpsertScheduleTemplateRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ScheduleTemplateResponseDto> UpdateTemplateAsync(
            Guid id,
            UpsertScheduleTemplateRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task DeleteTemplateAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<SeedDefaultTemplatesResponseDto> SeedDefaultTemplatesAsync(
            SeedDefaultTemplatesRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<DayScheduleResponseDto> GetDayAsync(
            DateOnly date,
            Guid? rentalAssetId,
            bool customerFacing,
            CancellationToken cancellationToken,
            IReadOnlyCollection<Guid>? rentalAssetIds = null) =>
            throw new NotImplementedException();

        public Task<int> PublishDayAsync(PublishDayRequestDto request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<SlotResponseDto> UpsertSlotAsync(
            UpsertSlotRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task CancelSlotAsync(Guid slotId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<SlotResponseDto> ApplyDailyOccurrenceAsync(
            ApplyDailyOccurrenceRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ApplyWeeklyRuleResponseDto> ApplyWeeklyRuleAsync(
            ApplyWeeklyRuleRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class UnusedRentalAssetService : IRentalAssetService
    {
        public Task<IReadOnlyList<RentalAssetResponse>> ListRentableAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<RentalAssetResponse?> GetByAssetIdAsync(Guid assetId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<RentalAssetResponse> UpdateSchedulePolicyAsync(
            Guid rentalAssetId,
            UpdateRentalSchedulePolicyRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<BulkUpdateRentalSchedulePolicyResponseDto> UpdateSchedulePolicyBulkAsync(
            BulkUpdateRentalSchedulePolicyRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<RentalAssetResponse> SetMaxAdvanceBookingDaysAsync(
            Guid rentalAssetId,
            int? maxAdvanceBookingDays,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class SingleControllerFeatureProvider(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == controllerType;
    }

    private sealed class StaffAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string HeaderName = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values)
                || string.IsNullOrWhiteSpace(values.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = values.ToString();
            Claim[] claims =
            [
                new("email", email),
                new(ClaimTypes.Email, email),
                new("sub", Guid.NewGuid().ToString()),
            ];
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class CustomerAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string HeaderName = "X-Test-Customer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values)
                || string.IsNullOrWhiteSpace(values.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var customerId = Guid.NewGuid();
            Claim[] claims =
            [
                new(CustomerClaimTypes.CustomerId, customerId.ToString()),
                new(CustomerClaimTypes.TenantId, Guid.NewGuid().ToString()),
                new(CustomerClaimTypes.Role, AuthRoles.Customer),
            ];
            var identity = new ClaimsIdentity(
                claims,
                Scheme.Name,
                CustomerClaimTypes.Role,
                CustomerClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
