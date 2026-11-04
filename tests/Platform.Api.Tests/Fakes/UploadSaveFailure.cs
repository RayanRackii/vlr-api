using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Platform.Api.Tests.Fakes;

/// <summary>One-shot save failure, before persistence or after a successful save acknowledgement.</summary>
public sealed class UploadSaveFailure : SaveChangesInterceptor
{
    private int remaining;
    private bool ambiguous;

    public void Arm(int save, bool afterSave = false) { remaining = save; ambiguous = afterSave; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (remaining > 0 && --remaining == 0 && !ambiguous)
            throw new InvalidOperationException("Injected upload save failure.");
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (remaining == 0 && ambiguous)
        {
            ambiguous = false;
            throw new InvalidOperationException("Save acknowledgement lost.");
        }
        return ValueTask.FromResult(result);
    }
}
