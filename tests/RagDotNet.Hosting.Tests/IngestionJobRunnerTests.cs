using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Embedding;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Hosting.Tests;

public sealed class IngestionJobRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ReadOnlyScope : IAccessScope
    {
        public ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken) =>
            new(TestScopes.Tenant("a", permissions: RagPermissions.Read));
    }

    private static RagPipelineBuilder Partitioned(VectorStore store) =>
        new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "test", 64)
            .UseAccessScope(new ReadOnlyScope())
            .Configure(o =>
            {
                o.Metadata.Add<string>("tenant");
                o.PartitionKeys.Add("tenant");
            });

    private static IngestionJobRunner Runner(IRagAdmin admin, Action<IngestionQueueOptions>? configure = null)
    {
        var options = new IngestionQueueOptions();
        configure?.Invoke(options);
        return new IngestionJobRunner(admin, Options.Create(options));
    }

    [Fact]
    public async Task Job_without_scope_ingests_with_full_access()
    {
        var admin = Partitioned(new InMemoryVectorStore()).BuildAdmin();

        var result = await Runner(admin).RunAsync(new IngestionJob("j1", Md("d", "alpha beta gamma"), null), Ct);

        Assert.Equal(IngestStatus.Created, result.Status);
        var hits = await admin.ForScope(null).RetrieveAsync("alpha", cancellationToken: Ct);
        Assert.Equal("d", Assert.Single(hits).DocumentId);
    }

    [Fact]
    public async Task Job_with_scope_ingests_into_that_scope()
    {
        var admin = Partitioned(new InMemoryVectorStore()).BuildAdmin();

        await Runner(admin).RunAsync(new IngestionJob("j1", Md("d", "alpha beta gamma"), TestScopes.Tenant("a")), Ct);

        var stats = await admin.GetStatsAsync(Ct);
        Assert.Equal("a", Assert.Single(stats.Partitions).Partition["tenant"]);
        Assert.Empty(await admin.ForScope(TestScopes.Tenant("b")).RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Single(await admin.ForScope(TestScopes.Tenant("a")).RetrieveAsync("alpha", cancellationToken: Ct));
    }

    public static TheoryData<string> Permanent => new(
        nameof(UnsupportedDocumentException), nameof(DocumentReadException), nameof(EmbeddingModelMismatchException),
        nameof(RagAccessDeniedException), nameof(ArgumentException), nameof(InvalidOperationException), nameof(FileNotFoundException));

    public static TheoryData<string> Transient => new(
        nameof(HttpRequestException), nameof(TimeoutException), nameof(IOException), nameof(VectorStoreException), nameof(Exception));

    private static Exception Make(string name) => name switch
    {
        nameof(UnsupportedDocumentException) => new UnsupportedDocumentException("a.xyz", null),
        nameof(DocumentReadException) => new DocumentReadException("a.pdf", "broken"),
        nameof(EmbeddingModelMismatchException) => new EmbeddingModelMismatchException("old", "new"),
        nameof(RagAccessDeniedException) => new RagAccessDeniedException("ingest", RagPermissions.Write, "denied"),
        nameof(ArgumentException) => new ArgumentException("bad"),
        nameof(InvalidOperationException) => new InvalidOperationException("settings"),
        nameof(FileNotFoundException) => new FileNotFoundException("missing"),
        nameof(HttpRequestException) => new HttpRequestException("429"),
        nameof(TimeoutException) => new TimeoutException(),
        nameof(IOException) => new IOException("disk"),
        nameof(VectorStoreException) => new VectorStoreException("down"),
        _ => new Exception("unknown"),
    };

    private static IngestionJobRunner DefaultRunner() => Runner(Partitioned(new InMemoryVectorStore()).BuildAdmin());

    [Theory]
    [MemberData(nameof(Permanent))]
    public void Default_classification_permanent(string exception) => Assert.False(DefaultRunner().IsTransient(Make(exception)));

    [Theory]
    [MemberData(nameof(Transient))]
    public void Default_classification_transient(string exception) => Assert.True(DefaultRunner().IsTransient(Make(exception)));

    [Fact]
    public void Option_overrides_classification()
    {
        var runner = Runner(Partitioned(new InMemoryVectorStore()).BuildAdmin(), o => o.IsTransient = _ => false);
        Assert.False(runner.IsTransient(new HttpRequestException("429")));
    }
}
