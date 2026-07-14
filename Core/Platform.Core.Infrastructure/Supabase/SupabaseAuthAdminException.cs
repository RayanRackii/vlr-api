namespace Platform.Core.Infrastructure.Supabase;

public sealed class SupabaseAuthAdminException : Exception
{
    public int StatusCode { get; }

    public SupabaseAuthAdminException(string message, int statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
