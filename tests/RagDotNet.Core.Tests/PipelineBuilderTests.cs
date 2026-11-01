using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Readers;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class PipelineBuilderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedScope : IAccessScope
    {
        public ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken) => new(new AccessScope(new Dictionary<string, string>()));
    }

    public static TheoryData<string> Mutators() =>
    [
        "UseChatClient", "UseEmbeddingGenerator", "UseVectorStore", "Configure", "AddReader",
        "UseLoggerFactory", "UseAccessScope", "UseContentStore",
    ];

    private static void Mutate(RagPipelineBuilder b, string name)
    {
        _ = name switch
        {
            "UseChatClient" => b.UseChatClient(new FakeChatClient()),
            "UseEmbeddingGenerator" => b.UseEmbeddingGenerator(new FakeEmbeddingGenerator(64)),
            "UseVectorStore" => b.UseVectorStore(new InMemoryVectorStore(), "other", 64),
            "Configure" => b.Configure(o => o.PartitionKeys.Add("tenant")),
            "AddReader" => b.AddReader(new PlainTextReader()),
            "UseLoggerFactory" => b.UseLoggerFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance),
            "UseAccessScope" => b.UseAccessScope(new FixedScope()),
            "UseContentStore" => b.UseContentStore(new InMemoryChunkContentStore()),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    [Theory]
    [MemberData(nameof(Mutators))]
    public void Mutating_after_Build_throws(string mutator)
    {
        var (_, builder, _, _, _) = Create();
        var ex = Assert.Throws<InvalidOperationException>(() => Mutate(builder, mutator));
        Assert.Equal("RagPipelineBuilder cannot be changed after Build(); create a new builder.", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Mutators))]
    public void Mutating_after_BuildAdmin_throws(string mutator)
    {
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "test", 64);
        builder.BuildAdmin();
        Assert.Throws<InvalidOperationException>(() => Mutate(builder, mutator));
    }

    [Fact]
    public void Failed_Build_does_not_freeze_the_builder()
    {
        var builder = new RagPipelineBuilder().UseEmbeddingGenerator(new FakeEmbeddingGenerator(64));
        Assert.Throws<InvalidOperationException>(() => builder.Build()); // no vector store yet
        builder.UseVectorStore(new InMemoryVectorStore(), "test", 64);
        Assert.NotNull(builder.Build());
    }

    [Fact]
    public async Task Repeated_Build_and_BuildAdmin_share_one_engine()
    {
        var (first, builder, _, _, _) = Create();
        var second = builder.Build();
        var admin = builder.BuildAdmin();

        await first.IngestAsync(Md("doc", "alpha beta gamma"), cancellationToken: Ct);

        Assert.NotEmpty(await second.RetrieveAsync("alpha beta gamma", cancellationToken: Ct));
        Assert.Equal(1, (await admin.GetStatsAsync(Ct)).Partitions.Single().DocumentCount);
        Assert.Same(((RagDotNet.Pipeline.RagPipeline)first).Engine, ((RagDotNet.Pipeline.RagPipeline)second).Engine);
    }
}
