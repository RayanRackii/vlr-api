using System.Threading.Channels;
using Platform.Api.Notifications;

namespace Platform.Api.Modules.CustomerAuth.Services;

public sealed record PasswordResetEmailWork(string Recipient, string RecipientName, string Code);

public interface IPasswordResetEmailQueue
{
    void Enqueue(PasswordResetEmailWork work);
}

public sealed class PasswordResetEmailQueue(ILogger<PasswordResetEmailQueue> logger) : IPasswordResetEmailQueue
{
    public const int Capacity = 1000;

    private readonly Channel<PasswordResetEmailWork> _channel =
        Channel.CreateBounded<PasswordResetEmailWork>(
            new BoundedChannelOptions(Capacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });

    public void Enqueue(PasswordResetEmailWork work)
    {
        if (_channel.Writer.TryWrite(work))
        {
            return;
        }

        logger.LogWarning("Password reset email was dropped because the queue is full.");
    }

    public IAsyncEnumerable<PasswordResetEmailWork> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class PasswordResetEmailDispatcher(
    PasswordResetEmailQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<PasswordResetEmailDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailure(logger, ex);
            }
        }
    }

    private async Task SendAsync(PasswordResetEmailWork work, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var emailProvider = scope.ServiceProvider.GetRequiredService<IEmailProvider>();
        var html = RolvixEmailLayout.Wrap(
            work.RecipientName,
            RolvixEmailLayout.PasswordResetBody(work.Code));
        await emailProvider.SendAsync(
            work.Recipient,
            RolvixEmailLayout.PasswordResetSubject,
            html,
            cancellationToken);
    }

    internal static void LogFailure(ILogger logger, Exception ex) =>
        logger.LogError(
            "Password reset email failed ({ExceptionType}).",
            ex.GetType().Name);
}
