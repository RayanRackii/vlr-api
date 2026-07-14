namespace Platform.Core.Infrastructure.Supabase;

public sealed class SupabaseOptions
{
    public const string SectionName = "Supabase";

    public string Url { get; set; } = null!;

    public string JwtSecret { get; set; } = null!;

    public string ServiceRoleKey { get; set; } = null!;
}
