namespace Tessera.Web.Services;

public interface IScheduledJob
{
    string Name { get; }
    TimeSpan Interval { get; }
    Task RunAsync(CancellationToken ct);
}
