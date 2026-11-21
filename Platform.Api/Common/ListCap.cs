using System.Globalization;

namespace Platform.Api.Common;

/// <summary>
/// Hard server-side cap for list endpoints that still return a plain array (no paging contract yet).
/// The newest <see cref="MaxRows"/> rows are returned and the real row count goes in <see cref="TotalCountHeader"/>.
/// </summary>
public static class ListCap
{
    public const int MaxRows = 500;
    public const string TotalCountHeader = "X-Total-Count";

    /// <summary>
    /// Publishes the total row count. When fewer than <see cref="MaxRows"/> rows came back the list is
    /// complete, so the count query is skipped.
    /// </summary>
    public static async Task PublishTotalAsync(
        IHttpContextAccessor? httpContextAccessor,
        int returned,
        Func<Task<int>> countAll)
    {
        var context = httpContextAccessor?.HttpContext;
        if (context is null || context.Response.HasStarted)
        {
            return;
        }

        var total = returned < MaxRows ? returned : await countAll();
        context.Response.Headers[TotalCountHeader] = total.ToString(CultureInfo.InvariantCulture);
    }
}
