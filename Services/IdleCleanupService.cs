namespace FlameStreamBackend.Services;

public class IdleCleanupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout   = TimeSpan.FromSeconds(90);

    private readonly HlsService _hls;

    public IdleCleanupService(HlsService hls) => _hls = hls;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            _hls.CleanupIdleJobs(IdleTimeout);
    }
}
