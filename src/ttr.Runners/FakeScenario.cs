using Ttr.Core;

namespace Ttr.Runners;

/// <summary>One planned fake test: its identity, predetermined outcome, simulated duration,
/// and whether it is announced during discovery or only appears via run events (theory rows).</summary>
public sealed record FakePlan(
    TestIdentity Identity,
    TestOutcome Outcome,
    TimeSpan Duration,
    bool DiscoverUpfront);

/// <summary>Pacing knobs so each scenario streams at a realistic (and scenario-appropriate) rate.</summary>
public sealed record FakePacing(
    int DiscoveryBatchSize,
    int DiscoveryBatchDelayMs,
    int MaxConcurrency,
    int StartIntervalMs);

/// <summary>The fully-built scenario: what to emit and how fast.</summary>
public sealed record FakeScenario(string Name, IReadOnlyList<FakePlan> Plans, FakePacing Pacing);
