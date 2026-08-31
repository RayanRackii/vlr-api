using Microsoft.AspNetCore.Diagnostics;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Features.CreateTenant;
using Platform.Api.Jobs;
using Platform.Api.Modules.Admin;
using Platform.Api.Modules.Assets;
using Platform.Api.Modules.Auth;
using Platform.Api.Modules.Catalog;
using Platform.Api.Modules.CustomerAuth;
using Platform.Api.Modules.Dashboard;
using Platform.Api.Modules.ModuleMenuItems;
using Platform.Api.Modules.Pmoc;
using Platform.Api.Modules.RegistrationFields;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Roles;
using Platform.Api.Modules.Users;
using Platform.Api.Modules.Webhooks;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Storage;
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

    // Console sink comes only from Serilog:WriteTo in appsettings — do not add
    // WriteTo.Console() here or every log line is duplicated in Railway/Docker.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName());

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
    builder.Services.AddScoped<ITrialGuard, TrialGuard>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddCorePersistence(connectionString);
    builder.Services.AddSupabaseAdminClient(builder.Configuration);
    builder.Services.AddSupabaseAuthentication(builder.Configuration);
    builder.Services.AddRbacAuthorization();
    builder.Services.AddAssetsModule();
    builder.Services.AddPmocModule();
    builder.Services.AddWorkOrdersModule();
    builder.Services.AddDashboardModule();
    builder.Services.AddRentalsModule();
    builder.Services.AddCatalogModule();
    builder.Services.AddUsersModule();
    builder.Services.AddRolesModule();
    builder.Services.AddAuthModule();
    builder.Services.AddCustomerAuthModule(builder.Configuration);
    builder.Services.AddRegistrationFieldsModule();
    builder.Services.AddModuleMenuItemsModule();
    builder.Services.AddAdminModule();
    builder.Services.AddScoped<ICreateTenantHandler, CreateTenantHandler>();
    builder.Services.AddMediatR(configuration =>
        configuration.RegisterServicesFromAssembly(typeof(Program).Assembly));
    builder.Services.AddWebhooksModule();
    builder.Services.AddStorage(builder.Configuration);
    builder.Services.AddNotificationInfrastructure(builder.Configuration, builder.Environment);
    builder.Services.AddPlatformHangfire(connectionString);

    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddHostedService<ClientRoleDiagnosticHostedService>();
    }

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
            policy.WithOrigins(allowedOrigins)
                .SetIsOriginAllowedToAllowWildcardSubdomains()
                .AllowAnyHeader()
                .AllowAnyMethod();
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
    string[] defaultOrigins =
    [
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "https://rolvix.com.br",
        "https://www.rolvix.com.br",
        "https://*.rolvix.com.br",
    ];

    var fromConfig = configuration
        .GetSection("Cors:AllowedOrigins")
        .GetChildren()
        .Select(child => child.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .ToList();

    if (fromConfig.Count == 0)
    {
        var csv = configuration["Cors:AllowedOrigins"];
        if (!string.IsNullOrWhiteSpace(csv))
        {
            fromConfig.AddRange(
                csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    return defaultOrigins
        .Concat(fromConfig)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
