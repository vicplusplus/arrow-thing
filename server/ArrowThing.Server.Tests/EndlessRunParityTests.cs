using Xunit;
using Xunit.Abstractions;

namespace ArrowThing.Server.Tests;

/// <summary>
/// .NET-side parity check: drives <see cref="EndlessRunParityScenario.RunAndHash"/>
/// and asserts the result matches the frozen
/// <see cref="EndlessRunParityScenario.ExpectedHashV1"/>. The matching
/// EditMode test under Mono catches any cross-runtime drift in
/// <see cref="System.Math"/> primitives or accumulated float arithmetic.
///
/// <para>The .NET hash is the canonical reference: bumping endless tuning
/// requires recomputing the hash here, then verifying Mono agrees by
/// running the EditMode test in the Unity editor.</para>
/// </summary>
public class EndlessRunParityTests
{
    private readonly ITestOutputHelper _output;

    public EndlessRunParityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EndlessRun_ParityScenario_MatchesExpectedHash()
    {
        ulong actual = EndlessRunParityScenario.RunAndHash();
        _output.WriteLine($".NET RunAndHash() = 0x{actual:X16} ({actual}UL)");
        Assert.Equal(EndlessRunParityScenario.ExpectedHashV1, actual);
    }

    [Fact]
    public void EndlessRun_ParityScenario_IsRepeatable()
    {
        // Sanity: same scenario, different invocation, identical hash.
        // Catches accidental statics / global RNG / DateTime.Now leaks
        // before they can mask cross-runtime drift.
        ulong a = EndlessRunParityScenario.RunAndHash();
        ulong b = EndlessRunParityScenario.RunAndHash();
        Assert.Equal(a, b);
    }
}
