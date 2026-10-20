using Platform.Api.Modules.Taxonomy.Services;

namespace Platform.Api.Modules.Taxonomy;

public static class TaxonomyModuleExtensions
{
    public static IServiceCollection AddTaxonomyModule(this IServiceCollection services)
    {
        services.AddScoped<TaxonomyService>();
        return services;
    }
}
