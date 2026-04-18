using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// EditMode tests for <see cref="CoopPlayerTimer"/>. Drives it with a stub
/// clock + capture-all emit to verify pause/resume + cadence without Unity
/// runtime.
/// </summary>
public class CoopPlayerTimerTests
{
    private List<long> _emitted;
    private float _nowSec;
    private bool _focused;
    private CoopPlayerTimer _timer;

    [SetUp]
    public void SetUp()
    {
        _emitted = new List<long>();
        _nowSec = 0f;
        _focused = true;
        _timer = new CoopPlayerTimer(
            emit: millis => _emitted.Add(millis),
            isFocusedProvider: () => _focused,
            timeProvider: () => _nowSec
        );
    }

    private void Advance(float seconds)
    {
        _nowSec += seconds;
        _timer.Tick(seconds);
    }

    [Test]
    public void Tick_DoesNothing_UntilStarted()
    {
        Advance(10f);
        Assert.AreEqual(0, _timer.AccumulatedMillis);
        Assert.IsEmpty(_emitted);
    }

    [Test]
    public void Tick_AccumulatesAfterStart()
    {
        _timer.Start();
        Advance(2.5f);
        Assert.AreEqual(2500, _timer.AccumulatedMillis);
    }

    [Test]
    public void Tick_DoesNotAdvanceWhenUnfocused()
    {
        _timer.Start();
        Advance(1f);
        _focused = false;
        Advance(3f);
        Assert.AreEqual(1000, _timer.AccumulatedMillis);
    }

    [Test]
    public void Tick_ResumesWhenRefocused()
    {
        _timer.Start();
        Advance(1f);
        _focused = false;
        Advance(3f);
        _focused = true;
        Advance(2f);
        Assert.AreEqual(3000, _timer.AccumulatedMillis);
    }

    [Test]
    public void Tick_EmitsOnceEveryFiveSeconds()
    {
        _timer.Start();
        // First 4 seconds, no emit yet.
        Advance(1f);
        Advance(1f);
        Advance(1f);
        Advance(1f);
        Assert.IsEmpty(_emitted);

        // At t=5 (cumulative tick sum), first emit fires.
        Advance(1f);
        Assert.AreEqual(1, _emitted.Count);
        Assert.AreEqual(5000, _emitted[0]);

        // Next emit at ~10 s.
        Advance(5f);
        Assert.AreEqual(2, _emitted.Count);
        Assert.AreEqual(10000, _emitted[1]);
    }

    [Test]
    public void NoteAcceptedClear_TriggersPromptEmit()
    {
        _timer.Start();
        Advance(1f);
        _timer.NoteAcceptedClear();
        // Post-clear emit delay is 100 ms. Before the delay, no emit yet.
        Advance(0.05f);
        Assert.IsEmpty(_emitted);
        // After the delay, emit fires once.
        Advance(0.10f);
        Assert.AreEqual(1, _emitted.Count);
        Assert.AreEqual(1150, _emitted[0]);
    }

    [Test]
    public void NoteAcceptedClear_ImplicitlyStartsTimer()
    {
        _timer.NoteAcceptedClear();
        Assert.IsTrue(_timer.IsRunning);
        Advance(0.5f);
        Assert.AreEqual(500, _timer.AccumulatedMillis);
    }

    [Test]
    public void Dispose_StopsEmitting()
    {
        _timer.Start();
        Advance(5f); // emits once
        _timer.Dispose();
        Advance(10f); // should NOT emit
        Assert.AreEqual(1, _emitted.Count);
    }
}
