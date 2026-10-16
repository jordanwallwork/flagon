using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

public class GettingStartedTests
{
    [Fact]
    public async Task AddFlagon_without_storage_uses_the_in_memory_store()
    {
        await using var host = await FlagonTestHost.StartAsync();

        Assert.IsType<InMemoryFlagStore>(host.Store);
    }
}
