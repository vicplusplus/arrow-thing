using System;
using UnityEngine;

/// <summary>
/// Per-player timer for co-op mode. Counts the local player's active
/// solve time: starts on the first accepted clear-attempt, pauses when
/// the Unity player loses focus, and periodically reports the accumulated
/// time to the server via <c>timer_update</c>.
///
/// The timer is purely client-side authoritative. The server stores the
/// last reported value and echoes it in <c>roster_patch</c>, so other
/// players see our elapsed time.
///
/// <para>
/// Callers drive the timer with <see cref="Tick"/> every frame (usually
/// from a MonoBehaviour's Update). The timer quietly stops ticking
/// between focus-loss and focus-regain.
/// </para>
/// </summary>
public sealed class CoopPlayerTimer
{
    /// <summary>Emit a <c>timer_update</c> at most this often.</summary>
    private const float EmitIntervalSec = 5f;

    /// <summary>Also emit right after a clear, throttled to this many ms.</summary>
    private const float PostClearEmitDelaySec = 0.1f;

    private readonly CoopClient _client;
    private long _accumulatedMillis;
    private bool _started;
    private bool _disposed;
    private float _emitAccum;
    private bool _scheduledPostClearEmit;
    private float _postClearEmitAt;
    private Func<bool> _isFocusedProvider;

    public long AccumulatedMillis => _accumulatedMillis;
    public bool IsRunning => _started && !_disposed;

    public CoopPlayerTimer(CoopClient client, Func<bool> isFocusedProvider = null)
    {
        _client = client;
        _isFocusedProvider = isFocusedProvider ?? (() => Application.isFocused);
    }

    /// <summary>Mark the timer as started. Idempotent.</summary>
    public void Start()
    {
        _started = true;
    }

    /// <summary>
    /// Advance the timer by <paramref name="deltaSeconds"/>. No-op when not
    /// started, disposed, or while the focus provider returns false.
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        if (_disposed || !_started)
            return;
        if (!_isFocusedProvider())
            return;

        _accumulatedMillis += (long)(deltaSeconds * 1000.0);
        _emitAccum += deltaSeconds;

        if (_scheduledPostClearEmit && Time.unscaledTime >= _postClearEmitAt)
        {
            _scheduledPostClearEmit = false;
            EmitNow();
            return;
        }

        if (_emitAccum >= EmitIntervalSec)
            EmitNow();
    }

    /// <summary>
    /// Call right after the local player's tap is accepted by the server
    /// (via <see cref="CoopSession.ClearedEvent.IsLocal"/>). Schedules a
    /// short-delay emit so the sidebar updates quickly without waiting for
    /// the full 5 s tick.
    /// </summary>
    public void NoteAcceptedClear()
    {
        if (!_started)
            Start();
        _scheduledPostClearEmit = true;
        _postClearEmitAt = Time.unscaledTime + PostClearEmitDelaySec;
    }

    /// <summary>
    /// Force-report the current accumulated time. Safe to call at any point;
    /// resets the emit cadence so the next auto-emit is 5 s away.
    /// </summary>
    public void EmitNow()
    {
        _emitAccum = 0f;
        if (_disposed)
            return;
        try
        {
            _ = _client.SendAsync(CoopMessage.TimerUpdate(_accumulatedMillis));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoopPlayerTimer] Failed to send timer_update: {e.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
