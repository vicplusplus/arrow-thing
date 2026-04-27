using NUnit.Framework;

/// <summary>
/// Mono-side parity check: the canonical <see cref="EndlessRunParityScenario"/>
/// hash must match the .NET-bootstrapped <see cref="EndlessRunParityScenario.ExpectedHashV1"/>.
/// A failure here means <see cref="EndlessRun"/> drifted between Unity's Mono
/// runtime and the server's .NET runtime — almost certainly in
/// <see cref="System.Math.Cos"/> / <see cref="System.Math.Pow"/> /
/// <see cref="System.Math.Round"/> or accumulated <c>float</c> arithmetic —
/// which would cause the server replay verifier to reject legitimate runs.
///
/// <para>If this fires after a tuning change: bump
/// <see cref="EndlessTuning.CurrentVersion"/>, recompute
/// <see cref="EndlessRunParityScenario.ExpectedHashV1"/> from the .NET test,
/// and confirm Mono still agrees.</para>
/// </summary>
[TestFixture]
public class EndlessRunParityTests
{
    [Test]
    public void EndlessRun_ParityScenario_MatchesExpectedHash()
    {
        ulong actual = EndlessRunParityScenario.RunAndHash();
        Assert.That(
            actual,
            Is.EqualTo(EndlessRunParityScenario.ExpectedHashV1),
            "Mono drift: EndlessRun produced a different state hash than the .NET reference. "
                + "If this fired after a tuning change, update ExpectedHashV1 and bump EndlessTuning. "
                + $"Mono hash = 0x{actual:X16} ({actual}UL)"
        );
    }

    [Test]
    public void EndlessRun_ParityScenario_IsRepeatable()
    {
        // Sanity: two back-to-back runs must produce the same hash.
        // Catches accidental statics / global RNG / time-source leaks
        // before they can mask cross-runtime drift.
        ulong a = EndlessRunParityScenario.RunAndHash();
        ulong b = EndlessRunParityScenario.RunAndHash();
        Assert.That(b, Is.EqualTo(a));
    }
}
