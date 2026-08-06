namespace Tessera.Web.Services;

// The proactive counterpart to MessageProcessor's reactive queue consumer
// (docs/01-architettura.md): due reminders, the daily digest, recurring-expense
// generation. Single BackgroundService, single timer, single app instance — no
// distributed lease, which is exactly why Fase 1 stays single-instance.
public sealed class SchedulerWorker(
    IEnumerable<IScheduledJob> jobs, ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastRun = jobs.ToDictionary(j => j.Name, _ => DateTimeOffset.MinValue);
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var job in jobs)
            {
                if (now - lastRun[job.Name] < job.Interval)
                {
                    continue;
                }

                lastRun[job.Name] = now;
                try
                {
                    await job.RunAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Scheduled job {JobName} failed", job.Name);
                }
            }
        }
    }
}
