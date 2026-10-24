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

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../agenda")]
    [InlineData("a.b")]
    [InlineData("-estudio")]
    [InlineData("estudio-")]
    [InlineData("a")]
    [InlineData("estu dio")]
    [InlineData("estudio/admin")]
    public void Portal_link_rejects_dot_segments_and_invalid_labels(string subdomain)
    {
        var id = Guid.Parse("33333333-3333-4333-8333-333333333333");

        Assert.Null(CustomerPortalLinks.ReservationAgenda(
            "https://app.example.test",
            subdomain,
            id));
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

        var unicode = EmailFromHeader.Format(
            "Norte\u2028\u2029\u0085\r\n",
            "noreply@example.test",
            "Rolvix");
        Assert.Equal("Norte <noreply@example.test>", unicode);
        Assert.DoesNotContain("\u2028", unicode, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", unicode, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0085", unicode, StringComparison.Ordinal);
    }

    [Fact]
    public void Reservation_subject_strips_line_breaks_and_unicode_separators()
    {
        var subject = ReservationCustomerEmail.SubjectFor(
            RentalEventTypes.ReservationConfirmed,
            "Sala 2\r\nBcc: evil@x.com\0\u2028\u2029\u0085");

        Assert.Equal("Reserva Sala 2Bcc: evil@x.com confirmada", subject);
        Assert.DoesNotContain("\r", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0085", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_and_customer_names_are_html_encoded()
    {
        var payload = Payload(total: ReservationCustomerEmail.FormatMoney(80m), deposit: null, url: null);
        payload[ReservationCustomerEmail.TenantNameKey] = "Clube <script> & \"Norte\"";
        payload[ReservationCustomerEmail.CustomerNameKey] = "Ana <b>";

        var rendered = ReservationCustomerEmail.Render(
            RentalEventTypes.ReservationConfirmed,
            payload);

        Assert.Contains("Olá Ana &lt;b&gt;", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(
            "Estabelecimento: <strong>Clube &lt;script&gt; &amp; &quot;Norte&quot;</strong>",
            rendered.HtmlBody,
            StringComparison.Ordinal);
        Assert.Contains("Clube &lt;script&gt; &amp; &quot;Norte&quot;", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", rendered.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", rendered.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Clube <script> & \"Norte\"", rendered.FromDisplayName);
    }

    [Fact]
    public async Task Publisher_ignores_foreign_pricing_row_and_foreign_asset_name()
    {
        await using var seeded = await PublisherSeed.CreateAsync(localWindowCovers: true);
        var publisher = new RentalsNotificationPublisher(
            seeded.Db,
            seeded.TenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);

        await publisher.PublishReservationEventAsync(
            seeded.Reservation,
            RentalEventTypes.ReservationPendingDeposit);
        await seeded.Db.SaveChangesAsync();

        var notification = Assert.Single(seeded.Db.Notifications);
        Assert.Equal("Sala 2", notification.Payload[ReservationCustomerEmail.ReferenceKey]);
        Assert.DoesNotContain(
            "Quadra Alheia",
            notification.Payload[ReservationCustomerEmail.ReferenceKey],
            StringComparison.Ordinal);
        Assert.Equal(
            ReservationCustomerEmail.FormatMoney(50m),
            notification.Payload[ReservationCustomerEmail.DepositKey]);

        // Proves the other tenant's price row is stored. The deposit above came from the ambient 50% window.
        var foreignPricing = await seeded.Db.RentalPricings
            .IgnoreQueryFilters()
            .SingleAsync(pricing => pricing.TenantId == seeded.OtherTenantId);
        Assert.Equal(10m, foreignPricing.DepositPercentage);
        Assert.Equal(seeded.Reservation.Items.Single(item => item.TenantId == seeded.TenantId).RentalAssetId, foreignPricing.RentalAssetId);
    }

    [Fact]
    public async Task Null_pricing_window_omits_the_deposit_even_when_a_foreign_window_covers_it()
    {
        await using var seeded = await PublisherSeed.CreateAsync(localWindowCovers: false);
        var publisher = new RentalsNotificationPublisher(
            seeded.Db,
            seeded.TenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);

        await publisher.PublishReservationEventAsync(
            seeded.Reservation,
            RentalEventTypes.ReservationPendingDeposit);
        await seeded.Db.SaveChangesAsync();

        var notification = Assert.Single(seeded.Db.Notifications);
        Assert.Equal("Sala 2", notification.Payload[ReservationCustomerEmail.ReferenceKey]);
        Assert.Equal(
            ReservationCustomerEmail.FormatMoney(100m),
            notification.Payload[ReservationCustomerEmail.TotalKey]);
        Assert.Null(notification.Payload[ReservationCustomerEmail.DepositKey]);
        Assert.DoesNotContain(
            "Quadra Alheia",
            notification.Payload[ReservationCustomerEmail.ReferenceKey],
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hangfire_single_delivery_without_tenant_is_sent(bool failClosed)
    {
        // INotificationOutboxScheduler enqueues ProcessDeliveryAsync alone: no tenant, no job scope.
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider, failClosed: failClosed);
        var tenant = new Tenant("Estúdio Norte", "70707070000191", subdomain: "estudio-hangfire");
        db.Tenants.Add(tenant);
        var deliveryId = QueueReservationEmail(db, tenant.Id, "ana@club.test", "Sala 1");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new ThrowingEmail("nobody@club.test");
        var processor = new NotificationOutboxProcessor(
            db,
            email,
            new DevWhatsAppRecorder(),
            NullLogger<NotificationOutboxProcessor>.Instance,
            TimeProvider.System);

        await processor.ProcessDeliveryAsync(deliveryId, CancellationToken.None);

        var delivery = await db.NotificationDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(["ana@club.test"], email.Recipients);
        Assert.False(PlatformScope.IsActive);
    }

    [Fact]
    public async Task Poison_reservation_email_does_not_block_the_outbox_batch()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenant = new Tenant("Estúdio Norte", "70707070000191", subdomain: "estudio-poison");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        tenantProvider.TenantId = null;

        var poison = QueueReservationEmail(db, tenant.Id, "poison@club.test", "Sala veneno");
        var healthy = QueueReservationEmail(db, tenant.Id, "healthy@club.test", "Sala 2");
        await db.SaveChangesAsync();

        var email = new ThrowingEmail("poison@club.test");
        var processor = new NotificationOutboxProcessor(
            db,
            email,
            new DevWhatsAppRecorder(),
            NullLogger<NotificationOutboxProcessor>.Instance,
            TimeProvider.System);

        await processor.ProcessDueAsync(CancellationToken.None);

        var poisonDelivery = await db.NotificationDeliveries.SingleAsync(delivery => delivery.Id == poison);
        var healthyDelivery = await db.NotificationDeliveries.SingleAsync(delivery => delivery.Id == healthy);
        Assert.Equal(NotificationDeliveryStatus.Failed, poisonDelivery.Status);
        Assert.Contains("poison reservation email", poisonDelivery.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, poisonDelivery.AttemptCount);
        Assert.Equal(NotificationDeliveryStatus.Sent, healthyDelivery.Status);
        Assert.Equal(["healthy@club.test"], email.Recipients);
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

    private static Guid QueueReservationEmail(
        AppDbContext db,
        Guid tenantId,
        string recipient,
        string reference)
    {
        var notification = new Notification
        {
            TenantId = tenantId,
            EventType = RentalEventTypes.ReservationConfirmed,
            AggregateType = "Reservation",
            AggregateId = Guid.NewGuid(),
            Payload = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ReservationCustomerEmail.ReferenceKey] = reference,
                [ReservationCustomerEmail.CustomerNameKey] = "Ana",
                [ReservationCustomerEmail.TenantNameKey] = "Estúdio Norte",
            },
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenantId,
            NotificationId = notification.Id,
            Channel = NotificationChannel.Email,
            RecipientKind = NotificationRecipientKind.Customer,
            RecipientEmail = recipient,
            Status = NotificationDeliveryStatus.Queued,
            NextAttemptAt = DateTimeOffset.UtcNow,
        };
        notification.AddDelivery(delivery);
        db.Notifications.Add(notification);
        return delivery.Id;
    }

    private sealed class ThrowingEmail : IEmailProvider
    {
        private readonly string _poisonRecipient;

        public ThrowingEmail(string poisonRecipient)
        {
            _poisonRecipient = poisonRecipient;
        }

        public List<string> Recipients { get; } = [];

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
            if (string.Equals(recipient, _poisonRecipient, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("poison reservation email");
            }

            Recipients.Add(recipient);
            return Task.CompletedTask;
        }
    }

    private sealed class PublisherSeed : IAsyncDisposable
    {
        private PublisherSeed(
            AppDbContext db,
            FakeTenantProvider tenantProvider,
            Reservation reservation,
            Guid tenantId,
            Guid otherTenantId)
        {
            Db = db;
            TenantProvider = tenantProvider;
            Reservation = reservation;
            TenantId = tenantId;
            OtherTenantId = otherTenantId;
        }

        public AppDbContext Db { get; }

        public FakeTenantProvider TenantProvider { get; }

        public Reservation Reservation { get; }

        public Guid TenantId { get; }

        public Guid OtherTenantId { get; }

        public static async Task<PublisherSeed> CreateAsync(bool localWindowCovers)
        {
            var tenantProvider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(tenantProvider);
            var tenant = new Tenant("Estúdio Norte", "30303030000191", subdomain: "estudio");
            var other = new Tenant("Outro Lugar", "40404040000191", subdomain: "outro");
            var unit = new Unit(tenant.Id, "Matriz");
            var otherUnit = new Unit(other.Id, "Outra");
            var category = new AssetCategory { TenantId = tenant.Id, Name = "Salas" };
            var otherCategory = new AssetCategory { TenantId = other.Id, Name = "Outras" };
            var family = new AssetFamily
            {
                Key = $"spaces-{Guid.NewGuid():N}"[..32],
                Label = "Spaces",
                FieldSchemaJson = "{}",
            };
            var asset = new Asset
            {
                TenantId = tenant.Id,
                UnitId = unit.Id,
                CategoryId = category.Id,
                FamilyId = family.Id,
                Name = "Sala 2",
                Tag = "S2",
                Status = AssetStatus.Active,
                IsRentable = true,
            };
            var otherAsset = new Asset
            {
                TenantId = other.Id,
                UnitId = otherUnit.Id,
                CategoryId = otherCategory.Id,
                FamilyId = family.Id,
                Name = "Quadra Alheia",
                Tag = "QA",
                Status = AssetStatus.Active,
                IsRentable = true,
            };
            var rental = new RentalAsset
            {
                TenantId = tenant.Id,
                AssetId = asset.Id,
                Asset = asset,
                Type = RentalAssetType.Location,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = true,
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(22, 0),
            };
            var otherRental = new RentalAsset
            {
                TenantId = other.Id,
                AssetId = otherAsset.Id,
                Asset = otherAsset,
                Type = RentalAssetType.Location,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = true,
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(22, 0),
            };
            var customer = new Customer
            {
                TenantId = tenant.Id,
                Name = "Ana",
                Email = "ana@estudio.test",
            };
            var date = new DateOnly(2026, 9, 1);
            tenantProvider.TenantId = tenant.Id;
            db.Tenants.AddRange(tenant, other);
            db.Units.AddRange(unit, otherUnit);
            db.AssetCategories.AddRange(category, otherCategory);
            db.AssetFamilies.Add(family);
            db.Assets.AddRange(asset, otherAsset);
            db.RentalAssets.AddRange(rental, otherRental);
            db.Customers.Add(customer);
            db.RentalPricings.Add(new RentalPricing
            {
                TenantId = tenant.Id,
                RentalAssetId = rental.Id,
                DayOfWeek = date.DayOfWeek,
                StartTime = localWindowCovers ? new TimeOnly(8, 0) : new TimeOnly(18, 0),
                EndTime = new TimeOnly(22, 0),
                PricePerHour = 100m,
                RequiresDeposit = true,
                DepositPercentage = 50m,
            });
            db.RentalPricings.Add(new RentalPricing
            {
                TenantId = other.Id,
                RentalAssetId = rental.Id,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(22, 0),
                PricePerHour = 1m,
                RequiresDeposit = true,
                DepositPercentage = 10m,
            });

            var reservation = new Reservation
            {
                TenantId = tenant.Id,
                UnitId = unit.Id,
                CustomerId = customer.Id,
                CustomerName = "Ana",
                CustomerWhatsApp = "11988887777",
                StartDateTime = BrazilTimeZone.AtLocal(date, new TimeOnly(10, 0)),
                EndDateTime = BrazilTimeZone.AtLocal(date, new TimeOnly(11, 0)),
                Status = ReservationStatus.PendingDeposit,
                TotalAmount = 100m,
                DepositPaid = 0m,
                Customer = customer,
            };
            reservation.AddItem(new ReservationItem
            {
                TenantId = tenant.Id,
                ReservationId = reservation.Id,
                RentalAssetId = rental.Id,
                RentalAsset = rental,
                Quantity = 1,
                UnitPrice = 100m,
                SubTotal = 100m,
            });
            reservation.AddItem(new ReservationItem
            {
                TenantId = other.Id,
                ReservationId = reservation.Id,
                RentalAssetId = otherRental.Id,
                RentalAsset = otherRental,
                Quantity = 1,
                UnitPrice = 80m,
                SubTotal = 80m,
            });
            db.Reservations.Add(reservation);
            db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
            {
                TenantId = tenant.Id,
                EventType = RentalEventTypes.ReservationPendingDeposit,
                Channel = NotificationChannel.Email,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            return new PublisherSeed(db, tenantProvider, reservation, tenant.Id, other.Id);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
