using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Notifications;

public sealed class ReservationCustomerEmailTests
{
    [Fact]
    public void Confirmed_email_includes_schedule_price_tenant_and_link_without_deposit()
    {
        var reservationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var url = CustomerPortalLinks.ReservationAgenda(
            "https://app.example.test",
            "estudio",
            reservationId);
        var rendered = ReservationCustomerEmail.Render(
            RentalEventTypes.ReservationConfirmed,
            Payload(
                total: ReservationCustomerEmail.FormatMoney(80m),
                deposit: null,
                url: url));

        Assert.Equal("Reserva Sala 2 confirmada", rendered.Subject);
        Assert.Equal("Estúdio Norte", rendered.FromDisplayName);
        Assert.Contains("foi confirmada", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Estúdio Norte", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("01/09/2026", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("10:00–11:00", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Total:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(ReservationCustomerEmail.FormatMoney(80m)!, rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Ver reserva", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"/t/estudio/agenda?reservation={reservationId:D}", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Depósito:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Rolvix", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Quadra", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FICC", rendered.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_deposit_email_includes_the_deposit_amount()
    {
        var rendered = ReservationCustomerEmail.Render(
            RentalEventTypes.ReservationPendingDeposit,
            Payload(
                total: ReservationCustomerEmail.FormatMoney(100m),
                deposit: ReservationCustomerEmail.FormatMoney(50m),
                url: "https://app.example.test/t/estudio/agenda?reservation=abc"));

        Assert.Equal("Reserva Sala 2 aguardando depósito", rendered.Subject);
        Assert.Contains("está aguardando depósito", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Depósito:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(ReservationCustomerEmail.FormatMoney(50m)!, rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(ReservationCustomerEmail.FormatMoney(100m)!, rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("Estúdio Norte", rendered.FromDisplayName);
    }

    [Fact]
    public void Missing_price_omits_total_and_deposit_lines()
    {
        var rendered = ReservationCustomerEmail.Render(
            RentalEventTypes.ReservationPendingDeposit,
            Payload(total: null, deposit: null, url: null));

        Assert.Contains("está aguardando depósito", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Data:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Horário:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Total:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Depósito:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Ver reserva", rendered.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_tenant_name_omits_the_establishment_line_and_keeps_provider_sender()
    {
        var payload = Payload(total: ReservationCustomerEmail.FormatMoney(10m), deposit: null, url: null);
        payload[ReservationCustomerEmail.TenantNameKey] = "  ";

        var rendered = ReservationCustomerEmail.Render(
            RentalEventTypes.ReservationConfirmed,
            payload);

        Assert.Null(rendered.FromDisplayName);
        Assert.DoesNotContain("Estabelecimento:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Rolvix.", rendered.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_name_is_encoded_and_unsafe_links_are_dropped()
    {
        var payload = Payload(total: null, deposit: null, url: "javascript:alert(1)");
        payload[ReservationCustomerEmail.ReferenceKey] = "Sala <script>";

        var rendered = ReservationCustomerEmail.Render(
            RentalEventTypes.ReservationConfirmed,
            payload);

        Assert.Contains("Sala &lt;script&gt;", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", rendered.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", rendered.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ver reserva", rendered.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Schedule_uses_the_tenant_time_zone()
    {
        var zone = BrazilTimeZone.Resolve();
        var start = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);

        var schedule = ReservationCustomerEmail.FormatSchedule(start, end, zone);

        Assert.Equal("20/10/2026", schedule.Date);
        Assert.Equal("12:00", schedule.Start);
        Assert.Equal("13:00", schedule.End);
    }

    [Fact]
    public void Portal_link_uses_the_existing_web_origin_and_subdomain()
    {
        var id = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var url = CustomerPortalLinks.ReservationAgenda(
            "https://app.example.test/",
            "Estudio",
            id);

        Assert.Equal(
            $"https://app.example.test/t/estudio/agenda?reservation={id:D}",
            url);
        Assert.Null(CustomerPortalLinks.ReservationAgenda("https://app.example.test", "  ", id));
        Assert.Null(CustomerPortalLinks.ReservationAgenda(null, "estudio", id));
    }

    [Fact]
    public void From_header_changes_only_the_display_name()
    {
        Assert.Equal(
            "\"Estúdio Norte\" <noreply@example.test>",
            EmailFromHeader.Format("Estúdio Norte", "noreply@example.test", "Rolvix"));
        Assert.Equal(
            "Rolvix <noreply@example.test>",
            EmailFromHeader.Format(null, "noreply@example.test", "Rolvix"));
        Assert.Equal(
            "Rolvix <noreply@example.test>",
            EmailFromHeader.Format(" \r\n ", "noreply@example.test", "Rolvix"));

        var hostile = EmailFromHeader.Format(
            "Evil <bcc@x>\r\nBcc: x",
            "noreply@example.test",
            "Rolvix");
        Assert.EndsWith("<noreply@example.test>", hostile, StringComparison.Ordinal);
        Assert.DoesNotContain("bcc@x", hostile, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", hostile, StringComparison.Ordinal);
        var displayName = hostile[..hostile.LastIndexOf('<')];
        Assert.DoesNotContain("<", displayName, StringComparison.Ordinal);
        Assert.DoesNotContain(">", displayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_reservation_email_is_sent_with_the_tenant_display_name()
    {
        var sent = await SendBookingEmailAsync(requiresDeposit: true);

        Assert.Equal("Estúdio Norte", sent.FromDisplayName);
        Assert.Equal("Reserva Sala 2 aguardando depósito", sent.Subject);
        Assert.Contains("está aguardando depósito", sent.Body, StringComparison.Ordinal);
        Assert.Contains("01/09/2026", sent.Body, StringComparison.Ordinal);
        Assert.Contains("10:00–11:00", sent.Body, StringComparison.Ordinal);
        Assert.Contains(ReservationCustomerEmail.FormatMoney(100m)!, sent.Body, StringComparison.Ordinal);
        Assert.Contains("Depósito:", sent.Body, StringComparison.Ordinal);
        Assert.Contains(ReservationCustomerEmail.FormatMoney(50m)!, sent.Body, StringComparison.Ordinal);
        Assert.Contains("Estúdio Norte", sent.Body, StringComparison.Ordinal);
        Assert.Contains("/t/estudio/agenda?reservation=", sent.Body, StringComparison.Ordinal);
        Assert.Contains(sent.ReservationId.ToString("D"), sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Rolvix", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Quadra", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("FICC", sent.Body, StringComparison.Ordinal);
        Assert.Equal("cliente@club.test", sent.Recipient);
    }

    [Fact]
    public async Task Confirmed_reservation_email_omits_the_deposit_line()
    {
        var sent = await SendBookingEmailAsync(requiresDeposit: false);

        Assert.Equal("Reserva Sala 2 confirmada", sent.Subject);
        Assert.Equal("Estúdio Norte", sent.FromDisplayName);
        Assert.Contains("foi confirmada", sent.Body, StringComparison.Ordinal);
        Assert.Contains(ReservationCustomerEmail.FormatMoney(100m)!, sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Depósito:", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(ReservationCustomerEmail.FormatMoney(50m)!, sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_total_payload_omits_price_and_deposit_when_rendered()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenant = new Tenant("Estúdio Norte", "30303030000191", subdomain: "estudio");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        tenantProvider.TenantId = tenant.Id;

        var reservation = new Reservation
        {
            TenantId = tenant.Id,
            UnitId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            CustomerName = "Ana",
            CustomerWhatsApp = "11988887777",
            StartDateTime = BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(10, 0)),
            EndDateTime = BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(11, 0)),
            Status = ReservationStatus.PendingDeposit,
            TotalAmount = 0m,
            DepositPaid = 0m,
            Customer = new Customer
            {
                TenantId = tenant.Id,
                Name = "Ana",
                Email = "ana@estudio.test",
            },
        };
        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenant.Id,
            EventType = RentalEventTypes.ReservationPendingDeposit,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);
        await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationPendingDeposit);
        await db.SaveChangesAsync();

        var notification = Assert.Single(db.Notifications);
        Assert.Null(notification.Payload[ReservationCustomerEmail.TotalKey]);
        Assert.Null(notification.Payload[ReservationCustomerEmail.DepositKey]);

        var rendered = ReservationCustomerEmail.Render(
            RentalEventTypes.ReservationPendingDeposit,
            notification.Payload);
        Assert.DoesNotContain("Total:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Depósito:", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("10:00–11:00", rendered.HtmlBody, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> Payload(string? total, string? deposit, string? url) =>
        new(StringComparer.Ordinal)
        {
            [ReservationCustomerEmail.TenantNameKey] = "Estúdio Norte",
            [ReservationCustomerEmail.CustomerNameKey] = "Ana",
            [ReservationCustomerEmail.ReferenceKey] = "Sala 2",
            [ReservationCustomerEmail.StatusKey] = "Confirmada",
            [ReservationCustomerEmail.DateKey] = "01/09/2026",
            [ReservationCustomerEmail.StartKey] = "10:00",
            [ReservationCustomerEmail.EndKey] = "11:00",
            [ReservationCustomerEmail.TotalKey] = total,
            [ReservationCustomerEmail.DepositKey] = deposit,
            [ReservationCustomerEmail.UrlKey] = url,
        };

    private static async Task<Sent> SendBookingEmailAsync(bool requiresDeposit)
    {
        var harness = await LocationBookingHarness.CreateAsync();
        var tenant = await harness.Db.Tenants.SingleAsync();
        tenant.UpdateProfile(tenant.LegalName, tenant.TaxId, "Estúdio Norte", "estudio");
        var asset = await harness.Db.Assets.SingleAsync(item => item.Id == harness.AssetId);
        asset.Name = "Sala 2";
        var rental = await harness.Db.RentalAssets.SingleAsync(item => item.Id == harness.RentalAssetId);
        rental.RequiresDeposit = requiresDeposit;
        harness.Db.RentalPricings.Add(new RentalPricing
        {
            TenantId = harness.TenantId,
            RentalAssetId = harness.RentalAssetId,
            DayOfWeek = LocationBookingHarness.Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(22, 0),
            PricePerHour = 100m,
            RequiresDeposit = requiresDeposit,
            DepositPercentage = requiresDeposit ? 50m : 0m,
        });
        var eventType = requiresDeposit
            ? RentalEventTypes.ReservationPendingDeposit
            : RentalEventTypes.ReservationConfirmed;
        harness.Db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = harness.TenantId,
            EventType = eventType,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendBaseUrl"] = "https://app.example.test",
            })
            .Build();
        var email = new CapturingEmail();
        var publisher = new RentalsNotificationPublisher(
            harness.Db,
            harness.TenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance,
            configuration,
            new FakeHostEnvironment { EnvironmentName = "Production" });
        var scheduler = new RecordingOutboxScheduler();
        var processor = new NotificationOutboxProcessor(
            harness.Db,
            email,
            new DevWhatsAppRecorder(),
            NullLogger<NotificationOutboxProcessor>.Instance,
            TimeProvider.System);
        var reservations = new ReservationService(
            harness.Db,
            harness.TenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(harness.Db, harness.TenantProvider),
            publisher,
            scheduler,
            LocationBookingHarness.BookingClock);

        var created = await reservations.CreateReservationAsync(
            harness.CustomerId,
            new CreateReservationRequestDto
            {
                UnitId = harness.UnitId,
                Date = LocationBookingHarness.Date,
                StartTime = LocationBookingHarness.Start,
                EndTime = LocationBookingHarness.End,
                Items = [new CreateReservationItemRequestDto { AssetId = harness.AssetId, Quantity = 1 }],
            },
            CancellationToken.None);

        foreach (var id in scheduler.Scheduled)
        {
            await processor.ProcessDeliveryAsync(id, CancellationToken.None);
        }

        await harness.DisposeAsync();
        return sent(email, created.Id);

        static Sent sent(CapturingEmail captured, Guid reservationId) =>
            new(captured.Recipient, captured.Subject, captured.Body, captured.FromDisplayName, reservationId);
    }

    private sealed record Sent(
        string? Recipient,
        string? Subject,
        string? Body,
        string? FromDisplayName,
        Guid ReservationId);

    private sealed class CapturingEmail : IEmailProvider
    {
        public string? Recipient { get; private set; }

        public string? Subject { get; private set; }

        public string? Body { get; private set; }

        public string? FromDisplayName { get; private set; }

        public Task SendAsync(
            string recipient,
            string subject,
            string body,
            CancellationToken cancellationToken = default) =>
            SendAsync(recipient, subject, body, fromDisplayName: null, cancellationToken);

        public Task SendAsync(
            string recipient,
            string subject,
            string body,
            string? fromDisplayName,
            CancellationToken cancellationToken)
        {
            Recipient = recipient;
            Subject = subject;
            Body = body;
            FromDisplayName = fromDisplayName;
            return Task.CompletedTask;
        }
    }
}
