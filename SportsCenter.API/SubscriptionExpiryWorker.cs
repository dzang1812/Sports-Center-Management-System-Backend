using SportsCenter.Services.Operations;

namespace SportsCenter.API;

public sealed class SubscriptionExpiryWorker(IServiceScopeFactory scopes, ILogger<SubscriptionExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<SubscriptionExpiryNotifier>().RunAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Sent {Count} subscription expiry notifications", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Subscription expiry notification run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
