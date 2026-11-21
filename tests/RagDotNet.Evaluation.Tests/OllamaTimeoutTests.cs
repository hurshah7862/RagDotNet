using System.Net;
using System.Net.Sockets;
using RagDotNet.Evaluation.Providers;

namespace RagDotNet.Evaluation.Tests;

public class OllamaTimeoutTests
{
    [Fact]
    public async Task An_HttpClient_timeout_counts_as_a_provider_timeout_but_a_cancelled_run_does_not()
    {
        // A server that accepts the connection and never answers, like a model stuck generating.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };

        var timedOut = await Assert.ThrowsAnyAsync<TaskCanceledException>(() => http.GetAsync(new Uri($"http://127.0.0.1:{port}/"), TestContext.Current.CancellationToken));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await Assert.ThrowsAnyAsync<TaskCanceledException>(() => http.GetAsync(new Uri($"http://127.0.0.1:{port}/"), cts.Token));

        Assert.True(OllamaProviders.IsTimeout(timedOut));
        Assert.False(OllamaProviders.IsTimeout(cancelled));
    }
}
