using Microsoft.Extensions.Hosting;

namespace RagDotNet.Hosting;

/// <summary>Starts and stops the registered <see cref="IIngestionJobQueue"/> with the host, when it is a hosted service; otherwise does nothing.</summary>
internal sealed class QueueHostedService(IIngestionJobQueue queue) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        queue is IHostedService hosted ? hosted.StartAsync(cancellationToken) : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        queue is IHostedService hosted ? hosted.StopAsync(cancellationToken) : Task.CompletedTask;
}
