namespace Platform.Api.Common;

public readonly record struct DirectoryPage(string? Term, int Page, int PageSize)
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    public const string LikeEscape = "\\";

    public static bool TryCreate(
        string? q,
        int page,
        int pageSize,
        out DirectoryPage value,
        out string error)
    {
        var term = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (term is { Length: < 2 })
        {
            value = default;
            error = "Search must contain at least 2 characters.";
            return false;
        }

        if (page < 1)
        {
            value = default;
            error = "Page must be at least 1.";
            return false;
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            value = default;
            error = $"Page size must be between 1 and {MaxPageSize}.";
            return false;
        }

        value = new DirectoryPage(term, page, pageSize);
        error = string.Empty;
        return true;
    }

    public static string ToContainsPattern(string term)
    {
        var escaped = term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }
}
