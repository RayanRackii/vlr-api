namespace Platform.Core.Infrastructure.MigrationOps;

public sealed class DatabaseTargetException : InvalidOperationException
{
    public DatabaseTargetException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
