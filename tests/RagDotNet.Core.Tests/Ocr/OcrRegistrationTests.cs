using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Ocr;
using RagDotNet.Pipeline;

namespace RagDotNet.Core.Tests.Ocr;

public class OcrRegistrationTests
{
    [Fact]
    public void Ocr_options_have_the_spec_defaults()
    {
        var o = new RagOptions().Ocr;

        Assert.True(o.OcrEmbeddedImages);
        Assert.Equal(64, o.MinImageSize);
        Assert.Equal(0.6, o.MinConfidence);
        Assert.Equal(300, o.PageDpi);
        Assert.Equal(DocumentOcrMode.FillMissingPages, o.DocumentProviderMode);
    }

    [Theory]
    [InlineData(-1, 0.6, 300)]
    [InlineData(64, 1.5, 300)]
    [InlineData(64, -0.1, 300)]
    [InlineData(64, 0.6, 0)]
    [InlineData(64, 0.6, 1201)]
    public void Invalid_ocr_options_fail_validation(int minSize, double minConfidence, int dpi)
    {
        var options = new RagOptions { EmbeddingDimensions = 8 };
        options.Ocr.MinImageSize = minSize;
        options.Ocr.MinConfidence = minConfidence;
        options.Ocr.PageDpi = dpi;

        Assert.True(RagOptionsValidator.Validate(options, hasContentStore: false, appChunkers: []).Failed);
    }

    [Fact]
    public void Di_registers_the_last_ocr_provider_and_every_renderer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddRag(o => o.EmbeddingDimensions = 64)
            .AddOcrProvider<FakeOcrA>()
            .AddOcrProvider<FakeOcrB>()
            .AddDocumentOcrProvider<FakeDocumentOcr>()
            .AddPageRenderer<FakeRenderer1>()
            .AddPageRenderer<FakeRenderer2>();
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<RagEngine>();

        Assert.IsType<FakeOcrB>(engine.OcrProvider);
        Assert.IsType<FakeDocumentOcr>(engine.DocumentOcrProvider);
        Assert.Equal([typeof(FakeRenderer1), typeof(FakeRenderer2)], engine.PageRenderers.Select(r => r.GetType()));
    }

    [Fact]
    public void Builder_passes_ocr_registrations_to_the_engine()
    {
        var (pipeline, _, _, _, _) = TestRag.Create(builder: b => b
            .UseOcrProvider(new FakeOcrA())
            .UseDocumentOcrProvider(new FakeDocumentOcr())
            .AddPageRenderer(new FakeRenderer1()));

        var engine = ((RagPipeline)pipeline).Engine;

        Assert.IsType<FakeOcrA>(engine.OcrProvider);
        Assert.IsType<FakeDocumentOcr>(engine.DocumentOcrProvider);
        Assert.IsType<FakeRenderer1>(Assert.Single(engine.PageRenderers));
    }

    [Fact]
    public void Builder_ocr_methods_throw_after_Build()
    {
        var (_, builder, _, _, _) = TestRag.Create();

        Assert.Throws<InvalidOperationException>(() => builder.UseOcrProvider(new FakeOcrA()));
        Assert.Throws<InvalidOperationException>(() => builder.UseDocumentOcrProvider(new FakeDocumentOcr()));
        Assert.Throws<InvalidOperationException>(() => builder.AddPageRenderer(new FakeRenderer1()));
    }

    [Fact]
    public void Ocr_warning_codes_exist()
    {
        Assert.Equal("OcrFailed", IngestWarningCodes.OcrFailed);
        Assert.Equal("OcrLowConfidence", IngestWarningCodes.OcrLowConfidence);
    }

    private sealed class FakeOcrA : IOcrProvider
    {
        public string Id => "a";

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken) => Task.FromResult(new OcrResult("a", 1));
    }

    private sealed class FakeOcrB : IOcrProvider
    {
        public string Id => "b";

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken) => Task.FromResult(new OcrResult("b", 1));
    }

    private sealed class FakeDocumentOcr : IDocumentOcrProvider
    {
        public string Id => "doc";

        public bool CanProcess(string? contentType, string fileName) => true;

        public Task<DocumentOcrResult> ProcessAsync(ReadOnlyMemory<byte> document, DocumentReadContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new DocumentOcrResult([]));
    }

    private sealed class FakeRenderer1 : IPageRenderer
    {
        public bool CanRender(string? contentType, string fileName) => true;

        public Task<ImageInput> RenderPageAsync(ReadOnlyMemory<byte> document, int pageNumber, int dpi, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageInput(Array.Empty<byte>(), "image/png"));
    }

    private sealed class FakeRenderer2 : IPageRenderer
    {
        public bool CanRender(string? contentType, string fileName) => true;

        public Task<ImageInput> RenderPageAsync(ReadOnlyMemory<byte> document, int pageNumber, int dpi, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageInput(Array.Empty<byte>(), "image/png"));
    }
}
