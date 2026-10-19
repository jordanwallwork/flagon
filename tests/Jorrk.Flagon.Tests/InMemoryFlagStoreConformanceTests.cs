using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Storage.Conformance;
using Xunit;

namespace Jorrk.Flagon.Tests;

public class InMemoryFlagStoreConformanceTests : FlagStoreConformanceTests
{
    protected override Task<IFlagStore> CreateStoreAsync() => Task.FromResult<IFlagStore>(new InMemoryFlagStore());
}
