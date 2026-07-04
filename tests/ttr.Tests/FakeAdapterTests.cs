using System.Threading.Channels;
using Ttr.Core;
using Ttr.Runners;
using Xunit;

namespace Ttr.Tests;

public class FakeAdapterTests
{
    [Fact]
    public void Same_seed_produces_identical_plans()
    {
        var a = ScenarioBuilder.Build("default", 42);
        var b = ScenarioBuilder.Build("default", 42);
        Assert.True(a.Plans.SequenceEqual(b.Plans));
    }

    [Fact]
    public void Different_seeds_diverge()
    {
        var a = ScenarioBuilder.Build("default", 1);
        var b = ScenarioBuilder.Build("default", 2);
        Assert.False(a.Plans.SequenceEqual(b.Plans));
    }

    [Fact]
    public void Default_scenario_has_realistic_shape()
    {
        var s = ScenarioBuilder.Build("default", 7);
        Assert.True(s.Plans.Count >= 300);
        Assert.Contains(s.Plans, p => p.Identity.Method.Length > 120);              // long name
        Assert.Contains(s.Plans, p => p.Identity.Method.Any(c => c > 0x2000));       // CJK/emoji
        Assert.Contains(s.Plans, p => !p.DiscoverUpfront && p.Identity.IsCase);      // mid-run theory
    }

    [Fact]
    public void Big_scenario_is_ten_thousand()
        => Assert.Equal(10_000, ScenarioBuilder.Build("big", 0).Plans.Count);

    [Theory]
    [InlineData("default")]
    [InlineData("big")]
    [InlineData("flaky")]
    [InlineData("slow")]
    public void Known_scenarios_are_known(string name)
        => Assert.True(ScenarioBuilder.IsKnown(name));

    [Fact]
    public void Unknown_scenario_is_not_known()
        => Assert.False(ScenarioBuilder.IsKnown("nope"));

    [Fact]
    public async Task Discovery_then_run_streams_expected_event_types()
    {
        var channel = Channel.CreateUnbounded<AppEvent>();
        var adapter = new FakeAdapter("default", 3);
        var target = new TestTarget("default");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await adapter.DiscoverAsync(target, channel.Writer, cts.Token);
        await adapter.RunAsync(target, [], channel.Writer, cts.Token);
        channel.Writer.Complete();

        int discovered = 0, started = 0, finished = 0, completed = 0;
        await foreach (var e in channel.Reader.ReadAllAsync(cts.Token))
        {
            switch (e)
            {
                case AppEvent.TestsDiscovered d: discovered += d.Tests.Count; break;
                case AppEvent.TestStarted: started++; break;
                case AppEvent.TestFinished: finished++; break;
                case AppEvent.RunCompleted: completed++; break;
            }
        }

        Assert.True(discovered > 0);
        Assert.Equal(started, finished);
        Assert.True(finished >= discovered);   // run also emits the mid-run theory rows
        Assert.Equal(1, completed);
    }

    [Fact]
    public void Full_run_through_reducer_leaves_no_running_tests()
    {
        // Feed every scenario event through the reducer and confirm a consistent terminal state.
        var scenario = ScenarioBuilder.Build("default", 5);
        var state = AppState.Initial("default");

        foreach (var plan in scenario.Plans.Where(p => p.DiscoverUpfront))
            state = Reducer.Reduce(state, new AppEvent.TestsDiscovered([plan.Identity]));
        foreach (var plan in scenario.Plans)
        {
            state = Reducer.Reduce(state, new AppEvent.TestStarted(plan.Identity));
            state = Reducer.Reduce(state, new AppEvent.TestFinished(plan.Identity, plan.Outcome, plan.Duration));
        }
        state = Reducer.Reduce(state, new AppEvent.RunCompleted());

        Assert.Equal(scenario.Plans.Count, state.Root.TotalLeaves);
        Assert.Equal(0, state.Root.Running);
        Assert.Equal(scenario.Plans.Count, state.Passed + state.Failed + state.Skipped);
    }
}
