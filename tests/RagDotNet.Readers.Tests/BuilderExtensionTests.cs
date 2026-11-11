// Deliberately no `using Microsoft.Extensions.DependencyInjection`: builder users import only RagDotNet.Configuration.
using RagDotNet.Configuration;
using static RagDotNet.Readers.Tests.ReaderTestKit;

namespace RagDotNet.Readers.Tests;

public class BuilderExtensionTests
{
    [Fact] // Final review minor: the builder overloads needed the DI namespace to be visible
    public void Reader_extensions_for_the_builder_need_only_the_configuration_namespace()
    {
        RagPipelineBuilder builder = Builder().AddPdfReader().AddDocxReader().AddHtmlReader();

        Assert.NotNull(builder.Build());
    }
}
