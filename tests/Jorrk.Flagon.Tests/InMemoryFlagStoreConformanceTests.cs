using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Storage.Conformance;
using Xunit;

namespace Jorrk.Flagon.Tests;

public class InMemoryFlagStoreConformanceTests : FlagStoreConformanceTests
{
    protected override Task<IFlagStore> CreateStoreAsync() => Task.FromResult<IFlagStore>(new InMemoryFlagStore());

    [Fact(Skip = "The in-memory store has no storage that a second instance could share.")]
    public override Task A_write_through_a_second_instance_signals_the_first() => Task.CompletedTask;
}
