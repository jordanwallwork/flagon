using System.Collections.Concurrent;

namespace Flagon.Testing.Tests;

public class SynchronizationContextTests
{
    [Fact]
    public void Create_and_SetFlag_do_not_deadlock_inside_a_single_threaded_synchronization_context()
    {
        var served = SingleThreadedContext.Run(() =>
        {
            using var flags = TestFlagon.Create(f => f.Define("seeded", true));
            flags.Flags.SetFlag("new-checkout", true);
            return flags.IsEnabled("new-checkout") && flags.IsEnabled("seeded");
        });

        Assert.True(served);
    }

    [Fact]
    public void Every_TestFlags_method_runs_inside_a_single_threaded_synchronization_context()
    {
        var served = SingleThreadedContext.Run(() =>
        {
            using var flags = TestFlagon.Create(f => f.Define("seeded", true));
            flags.Flags
                .SetFlag("text", "a", """{"if":[true,"default",null]}""")
                .SetFlag("count", 1)
                .SetFlag("ratio", 0.5)
                .SetObjectFlag("layout", """{"Columns":1}""")
                .SetFlag("theme", new { Name = "dark" })
                .SetFlag("theme", new { Name = "light" }, options: null, targeting: null)
                .SetFlag("size", new Dictionary<string, string> { ["s"] = "small", ["l"] = "large" }, "s")
                .SetTargeting("size", null)
                .EnableFor("beta", "user-1")
                .Disable("count")
                .Enable("count")
                .Archive("ratio")
                .Remove("ratio")
                .Reset();
            return flags.IsEnabled("seeded") && flags.GetBooleanDetails("beta").ErrorCode == ErrorCode.FlagNotFound;
        });

        Assert.True(served);
    }

    /// <summary>
    /// Runs a delegate on one thread whose <see cref="SynchronizationContext"/> only runs posted work on that same
    /// thread, as UI frameworks and some test runners do: a continuation captured by blocking code never runs.
    /// </summary>
    private sealed class SingleThreadedContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public static T Run<T>(Func<T> body)
        {
            var context = new SingleThreadedContext();
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(context);
                context.Post(_ =>
                {
                    try
                    {
                        result = body();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        context._queue.CompleteAdding();
                    }
                }, null);

                foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            });
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Deadlocked inside the synchronization context.");
            if (error is not null)
            {
                throw new InvalidOperationException("The body threw.", error);
            }

            return result;
        }
    }
}
