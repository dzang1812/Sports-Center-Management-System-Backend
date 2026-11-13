using SportsCenter.Services.Membership;

namespace SportsCenter.API;

// PayOS never tells us about cancelled or expired links, so pending bank-transfer payments are re-checked periodically.
public sealed class PayOsSyncWorker(IServiceScopeFactory scopes, ILogger<PayOsSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().SyncAllPendingPayOsAsync(stoppingToken);
                if (result.MarkedFailed + result.CancelledOnPayOs + result.Errors > 0)
                    logger.LogInformation("PayOS sync: {Result}", result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "PayOS pending payment sync failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
