using Ttr.Core;

namespace Ttr.Runners;

/// <summary>One planned fake test: its identity, predetermined outcome, simulated duration, whether
/// it is announced during discovery or only appears via run events (theory rows), and — for failures
/// — the rich <see cref="TestResultDetail"/> the adapter streams so the detail pane and 'o' modal
/// have real content (brief M1).</summary>
public sealed record FakePlan(
    TestIdentity Identity,
    TestOutcome Outcome,
    TimeSpan Duration,
    bool DiscoverUpfront,
    TestResultDetail? Detail = null);

/// <summary>Pacing knobs so each scenario streams at a realistic (and scenario-appropriate) rate.</summary>
public sealed record FakePacing(
    int DiscoveryBatchSize,
    int DiscoveryBatchDelayMs,
    int MaxConcurrency,
    int StartIntervalMs);

/// <summary>
/// The fully-built scenario: what to emit and how fast. <see cref="RerunPassProbability"/> drives the
/// vanish-on-pass loop (brief M1/M4): on a rerun, a currently-failed test flips to Passed with this
/// probability (0 = reruns are deterministic and keep their outcome; the <c>flaky</c> scenario sets ~0.6).
/// </summary>
/// <param name="Prelude">Backend events (project registration, build lifecycle, phantom/unparseable
/// notices) emitted at the start of discovery — the Phase 3 <c>backend</c> scenario uses these to
/// exercise the new UI states fake-first (brief M1). Null for the legacy scenarios.</param>
/// <param name="Postlude">Backend events emitted after test discovery (e.g. smoke-validation
/// <see cref="AppEvent.DiscoveryFailed"/> for a zero-test / no-handshake project).</param>
/// <param name="RunsSupported">Whether <c>r</c>/<c>R</c> may launch a run (plan §14). The
/// <c>backend</c> scenario sets this false to simulate a Phase 3 read-only session.</param>
public sealed record FakeScenario(
    string Name,
    IReadOnlyList<FakePlan> Plans,
    FakePacing Pacing,
    double RerunPassProbability = 0.0,
    IReadOnlyList<AppEvent>? Prelude = null,
    IReadOnlyList<AppEvent>? Postlude = null,
    bool RunsSupported = true);
