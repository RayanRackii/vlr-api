using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.Api.Authentication;

namespace Platform.Api.Tests.Http;

public sealed class PlatformExceptionHandlerTests
{
    private const string Canary = "CANARY-internal-detail";

    [Fact]
    public async Task KeyNotFound_maps_to_404_with_a_generic_message()
    {
        var (status, body) = await Send(new KeyNotFoundException(Canary));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("resource.not_found", body.GetProperty("code").GetString());
        Assert.DoesNotContain(Canary, body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Argument_and_format_exceptions_map_to_400()
    {
        var (argStatus, argBody) = await Send(new ArgumentOutOfRangeException("page", Canary));
        var (fmtStatus, fmtBody) = await Send(new FormatException(Canary));

        Assert.Equal(HttpStatusCode.BadRequest, argStatus);
        Assert.Equal(HttpStatusCode.BadRequest, fmtStatus);
        Assert.Equal("request.invalid", argBody.GetProperty("code").GetString());
        Assert.Equal("request.invalid", fmtBody.GetProperty("code").GetString());
        Assert.DoesNotContain(Canary, argBody.ToString() + fmtBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrency_failures_map_to_409_concurrency_conflict()
    {
        var (efStatus, efBody) = await Send(new DbUpdateConcurrencyException(Canary));
        var (pgStatus, pgBody) = await Send(new PostgresException(Canary, "ERROR", "ERROR", "40P01"));
        var (wrappedStatus, _) = await Send(new DbUpdateException(
            Canary,
            new PostgresException(Canary, "ERROR", "ERROR", "40001")));

        Assert.Equal(HttpStatusCode.Conflict, efStatus);
        Assert.Equal(HttpStatusCode.Conflict, pgStatus);
        Assert.Equal(HttpStatusCode.Conflict, wrappedStatus);
        Assert.Equal("concurrency.conflict", efBody.GetProperty("code").GetString());
        Assert.Equal("concurrency.conflict", pgBody.GetProperty("code").GetString());
        Assert.DoesNotContain(Canary, efBody.ToString() + pgBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_exceptions_stay_500_and_never_leak_the_message()
    {
        var (status, body) = await Send(new InvalidOperationException(Canary));
        var (uniqueStatus, _) = await Send(new DbUpdateException(
            Canary,
            new PostgresException(Canary, "ERROR", "ERROR", "23505")));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(HttpStatusCode.InternalServerError, uniqueStatus);
        Assert.Equal("An unexpected error occurred.", body.GetProperty("error").GetString());
        Assert.DoesNotContain(Canary, body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedAccess_stays_401_as_before()
    {
        var (status, body) = await Send(new UnauthorizedAccessException("Tenant context is required."));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("Tenant context is required.", body.GetProperty("error").GetString());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(Exception exception)
    {
        using var host = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.Configure(app =>
            {
                app.UsePlatformExceptionHandler();
                app.Run(_ => throw exception);
            });
        }).Start();

        var response = await host.GetTestClient().GetAsync("/boom");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.Clone());
    }
}
