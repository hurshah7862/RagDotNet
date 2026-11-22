using RagDotNet.StoreTests.Infrastructure;

namespace RagDotNet.StoreTests;

public class StoreGateTests
{
    [Fact]
    public void Available_store_runs() => Assert.Null(StoreGate.Decide("X", null, required: true));

    [Fact]
    public void Unavailable_required_store_fails_naming_store_and_reason()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StoreGate.Decide("Qdrant", "Docker is not running", required: true));
        Assert.Contains("Qdrant", ex.Message);
        Assert.Contains("Docker is not running", ex.Message);
        Assert.Contains("RAGDOTNET_STORES_REQUIRED", ex.Message);
    }

    [Fact]
    public void Unavailable_optional_store_skips_with_the_reason()
    {
        var reason = StoreGate.Decide("Qdrant", "Docker is not running", required: false);
        Assert.Equal("Qdrant: Docker is not running", reason);
    }
}
