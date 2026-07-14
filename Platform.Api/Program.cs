using Microsoft.AspNetCore.Diagnostics;
using Platform.Api.Authentication;
using Platform.Api.Features.CreateTenant;
using Platform.Api.Jobs;
using Platform.Api.Modules.Assets;
using Platform.Api.Modules.Dashboard;
using Platform.Api.Modules.Pmoc;
using Platform.Api.Modules.WorkOrders;
using Platform.Core.Infrastructure;
using Platform.Core.Infrastructure.Persistence;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Platform.Api host");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName()
        .Enrich.WithProperty("Application", "Platform.Api")
        .WriteTo.Console());

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
    builder.Services.AddCorePersistence(connectionString);
    builder.Services.AddSupabaseAdminClient(builder.Configuration);
    builder.Services.AddSupabaseAuthentication(builder.Configuration);
    builder.Services.AddAssetsModule();
    builder.Services.AddPmocModule();
    builder.Services.AddWorkOrdersModule();
    builder.Services.AddDashboardModule();
    builder.Services.AddScoped<ICreateTenantHandler, CreateTenantHandler>();
    builder.Services.AddPlatformHangfire(connectionString);

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var allowedOrigins = ResolveCorsAllowedOrigins(builder.Configuration);

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
                return;
            }

            if (builder.Environment.IsDevelopment())
            {
                policy.WithOrigins(
                        "http://localhost:5173",
                        "http://127.0.0.1:5173")
                    .AllowAnyHeader()
                    .AllowAnyMethod();
                return;
            }

            throw new InvalidOperationException(
                "Cors:AllowedOrigins must be configured outside Development (env Cors__AllowedOrigins).");
        });
    });

    var app = builder.Build();

    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

            if (exception is TenantResolutionException tenantResolutionException)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = tenantResolutionException.Message });
                return;
            }

            if (exception is UnauthorizedAccessException unauthorizedAccessException)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = unauthorizedAccessException.Message });
                return;
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
        });
    });

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Platform API v1");
            options.RoutePrefix = "swagger";
        });

        app.UseHttpsRedirection();
    }

    app.UseCors();

    app.UseSerilogRequestLogging();

    app.UseAuthentication();
    app.UseAuthorization();

    app.UsePlatformHangfireDashboard();

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
        .AllowAnonymous();

    app.MapCreateTenantEndpoint();
    app.MapControllers();

    app.MapPlatformRecurringJobs();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Platform.Api host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static string[] ResolveCorsAllowedOrigins(IConfiguration configuration)
{
    var fromChildren = configuration
        .GetSection("Cors:AllowedOrigins")
        .GetChildren()
        .Select(child => child.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (fromChildren.Length > 0)
    {
        return fromChildren;
    }

    var csv = configuration["Cors:AllowedOrigins"];
    if (string.IsNullOrWhiteSpace(csv))
    {
        return [];
    }

    return csv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
