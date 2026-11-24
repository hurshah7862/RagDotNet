using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Pipeline;

namespace RagDotNet.Hosting.Tests;

/// <summary>The code examples in docs/hosting.md, kept compiling and running.</summary>
public sealed class DocsExamplesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // docs/hosting.md, "Enqueue and check status"
    public sealed class UploadHandler(IIngestionQueue queue)
    {
        public ValueTask<string> UploadAsync(Stream upload, string fileName, string? contentType, CancellationToken ct) =>
            queue.EnqueueAsync(new DocumentSource(fileName, upload, fileName, contentType), ct);

        public ValueTask<IngestionJobStatus?> StatusAsync(string jobId, CancellationToken ct) => queue.GetStatusAsync(jobId, ct);
    }

    private static async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        builder.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());

        // docs/hosting.md, "Setup"
        builder.Services.AddRag(o => { o.EmbeddingDimensions = 64; o.ModelCheckInterval = TimeSpan.Zero; })
            .AddIngestionQueue(o => o.Concurrency = 4);

        builder.Services.AddScoped<UploadHandler>();
        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static async Task<IngestionJobStatus> WaitAsync(Func<ValueTask<IngestionJobStatus?>> status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var s = await status();
            if (s?.State is IngestionJobState.Succeeded or IngestionJobState.Failed or IngestionJobState.Cancelled) return s;
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Setup_and_enqueue_example_runs()
    {
        using var host = await StartAsync();
        await using var request = host.Services.CreateAsyncScope();
        var handler = request.ServiceProvider.GetRequiredService<UploadHandler>();

        string jobId;
        await using (var upload = new MemoryStream("# Handbook\n\nalpha beta gamma"u8.ToArray()))
            jobId = await handler.UploadAsync(upload, "handbook.md", "text/markdown", Ct);

        var status = await WaitAsync(() => handler.StatusAsync(jobId, Ct));
        Assert.Equal(IngestionJobState.Succeeded, status.State);

        // docs/hosting.md, a background service creating a scope
        var path = Path.Combine(Path.GetTempPath(), "ragdotnet-docs-" + Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(path, "# Policy\n\ndelta epsilon", Ct);
        try
        {
            var services = host.Services;
            await using var scope = services.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IIngestionQueue>();
            var fileJob = await queue.EnqueueAsync(DocumentSource.FromFile(path), Ct);
            Assert.Equal(IngestionJobState.Succeeded, (await WaitAsync(() => queue.GetStatusAsync(fileJob, Ct))).State);
        }
        finally
        {
            File.Delete(path);
        }

        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Pause_around_reindex_example_runs()
    {
        using var host = await StartAsync();
        await using var request = host.Services.CreateAsyncScope();
        var queue = request.ServiceProvider.GetRequiredService<IIngestionQueue>();
        var admin = host.Services.GetRequiredService<IRagAdmin>();
        var before = await queue.EnqueueAsync(TestRag.Md("d1", "alpha beta gamma"), Ct);
        await WaitAsync(() => queue.GetStatusAsync(before, Ct));

        // docs/hosting.md, "Pause and resume"
        await queue.PauseAsync(Ct);      // returns once nothing is being ingested
        try
        {
            await admin.ReindexAsync(cancellationToken: Ct);
        }
        finally
        {
            await queue.ResumeAsync(Ct);
        }

        var after = await queue.EnqueueAsync(TestRag.Md("d2", "delta epsilon"), Ct);
        Assert.Equal(IngestionJobState.Succeeded, (await WaitAsync(() => queue.GetStatusAsync(after, Ct))).State);
        await host.StopAsync(Ct);
    }
}
