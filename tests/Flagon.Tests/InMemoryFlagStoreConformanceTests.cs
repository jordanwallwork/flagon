using Flagon.Storage;
using Flagon.Storage.Conformance;

namespace Flagon.Tests;

public class InMemoryFlagStoreConformanceTests : FlagStoreConformanceTests
{
    protected override Task<IFlagStore> CreateStoreAsync() => Task.FromResult<IFlagStore>(new InMemoryFlagStore());
}
