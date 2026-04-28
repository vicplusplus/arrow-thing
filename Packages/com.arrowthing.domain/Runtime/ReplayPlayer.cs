using System;
using System.Collections.Generic;

/// <summary>
/// Pure C# replay playback engine. Drives event playback with speed control and seeking.
/// The view layer calls <see cref="Advance"/> each frame and acts on returned events.
/// </summary>
public sealed class ReplayPlayer
{
    private static readonly float[] SpeedSteps = { 0.5f, 1f, 2f, 4f, 10f };

    /// <summary>Seconds of lead-in before the first event fires, so early clears are visible.</summary>
    public const double LeadInSeconds = 0.5;

    /// <summary>Seconds of padding after the last event so exit animations can play out.</summary>
    public const double ExitPaddingSeconds = 1.0;

    private readonly ReplayData _data;
    private readonly List<ReplayEvent> _timedEvents; // only clear/reject events (have positions)
    private readonly double[] _eventTimes; // seconds from start for each timed event
    private readonly double _totalDuration;

    private int _currentIndex; // next timed event to fire
    private double _currentTime; // playback clock in seconds
    private int _speedIndex = 1; // index into SpeedSteps (default 1x)

    /// <summary>Arrows cleared so far, in order. Used for backward seeking.</summary>
    private readonly List<int> _clearedEventIndices = new();

    public ReplayPlayer(ReplayData data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));

        // Extract timed events (clear and reject) and compute relative timestamps
        _timedEvents = new List<ReplayEvent>();
        var times = new List<double>();

        DateTime startTime = DateTime.MinValue;
        foreach (var evt in _data.events)
        {
            if (evt.type == ReplayEventType.StartSolve)
            {
                startTime = DateTime.Parse(evt.timestamp).ToUniversalTime();
            }
        }

        if (startTime == DateTime.MinValue && _data.events.Count > 0)
        {
            startTime = DateTime.Parse(_data.events[0].timestamp).ToUniversalTime();
        }

        // We use the solve elapsed computation approach: track active time excluding pauses
        double activeTime = 0;
        DateTime checkpoint = startTime;
        // If there's no StartSolve event, start tracking from the beginning
        bool tracking = (
            startTime != DateTime.MinValue
            && !_data.events.Exists(e => e.type == ReplayEventType.StartSolve)
        );
        bool paused = false; // true between session_leave and session_rejoin
        DateTime lastEventTime = startTime;

        foreach (var evt in _data.events)
        {
            var ts = DateTime.Parse(evt.timestamp).ToUniversalTime();

            switch (evt.type)
            {
                case ReplayEventType.StartSolve:
                    checkpoint = ts;
                    tracking = true;
                    paused = false;
                    break;
                case ReplayEventType.SessionRejoin:
                    if (!paused && tracking)
                    {
                        // Orphan rejoin (no preceding session_leave). The player
                        // force-quit and resumed. Treat the previous event's time
                        // as the implicit leave — accumulate active time up to that
                        // point, excluding the gap between then and this rejoin.
                        activeTime += (lastEventTime - checkpoint).TotalSeconds;
                    }
                    checkpoint = ts;
                    paused = false;
                    break;
                case ReplayEventType.SessionLeave:
                    if (tracking)
                        activeTime += (ts - checkpoint).TotalSeconds;
                    paused = true;
                    break;
                case ReplayEventType.Clear:
                case ReplayEventType.Reject:
                    if (tracking)
                    {
                        double eventTime = activeTime + (ts - checkpoint).TotalSeconds;
                        _timedEvents.Add(evt);
                        times.Add(eventTime);
                    }
                    break;
                case ReplayEventType.EndSolve:
                    if (tracking)
                        activeTime += (ts - checkpoint).TotalSeconds;
                    break;
            }
            lastEventTime = ts;
        }

        _eventTimes = times.ToArray();
        // Shift all event times by lead-in so early clears don't fire at t=0
        for (int i = 0; i < _eventTimes.Length; i++)
            _eventTimes[i] += LeadInSeconds;

        double lastEventRaw =
            _eventTimes.Length > 0 ? _eventTimes[_eventTimes.Length - 1] - LeadInSeconds : 0;
        double rawDuration =
            _data.finalTime >= 0 ? Math.Max(_data.finalTime, lastEventRaw) : lastEventRaw;
        _totalDuration = rawDuration + LeadInSeconds + ExitPaddingSeconds;
    }

    public double CurrentTime => _currentTime;
    public double TotalDuration => _totalDuration;

    /// <summary>Duration excluding exit padding — use for UI display and slider.</summary>
    public double DisplayDuration => _totalDuration - ExitPaddingSeconds;

    public float PlaybackSpeed => SpeedSteps[_speedIndex];
    public bool IsPlaying { get; set; } = true;
    public bool IsFinished => _currentIndex >= _timedEvents.Count;

    /// <summary>Current playback position as 0–1, clamped to display duration.</summary>
    public double NormalizedTime =>
        DisplayDuration > 0 ? Math.Min(1.0, _currentTime / DisplayDuration) : 0;

    /// <summary>Number of timed events (clears + rejects).</summary>
    public int TimedEventCount => _timedEvents.Count;

    /// <summary>Index of next event to fire.</summary>
    public int CurrentEventIndex => _currentIndex;

    /// <summary>Indices of clear events that have been applied, in order.</summary>
    public IReadOnlyList<int> ClearedEventIndices => _clearedEventIndices;

    /// <summary>
    /// Advances the playback clock and returns any events that fire in this interval.
    /// Call once per frame with <c>Time.deltaTime</c>.
    /// </summary>
    public List<ReplayEvent> Advance(double deltaTime)
    {
        var fired = new List<ReplayEvent>();
        if (!IsPlaying || IsFinished)
            return fired;

        _currentTime += deltaTime * SpeedSteps[_speedIndex];
        if (_currentTime > _totalDuration)
            _currentTime = _totalDuration;

        while (_currentIndex < _timedEvents.Count && _eventTimes[_currentIndex] <= _currentTime)
        {
            var evt = _timedEvents[_currentIndex];
            fired.Add(evt);
            if (evt.type == ReplayEventType.Clear)
                _clearedEventIndices.Add(_currentIndex);
            _currentIndex++;
        }

        return fired;
    }

    /// <summary>
    /// Cycles to the next playback speed: 0.5x → 1x → 2x → 4x → 0.5x.
    /// Returns the new speed.
    /// </summary>
    public float CycleSpeed()
    {
        _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
        return SpeedSteps[_speedIndex];
    }

    /// <summary>
    /// Seeks to a normalized time (0–1) mapped to <see cref="DisplayDuration"/>.
    /// Returns a <see cref="SeekResult"/> describing what events need to be applied or undone.
    /// </summary>
    public SeekResult SeekTo(double normalizedTime)
    {
        normalizedTime = Math.Max(0, Math.Min(1, normalizedTime));
        double targetTime = normalizedTime * DisplayDuration;

        // Find the target event index (first event after targetTime)
        int targetIndex = 0;
        while (targetIndex < _timedEvents.Count && _eventTimes[targetIndex] <= targetTime)
            targetIndex++;

        var result = new SeekResult();

        if (targetIndex >= _currentIndex)
        {
            // Forward seek: fire events between current and target
            for (int i = _currentIndex; i < targetIndex; i++)
            {
                result.EventsToApply.Add(_timedEvents[i]);
                if (_timedEvents[i].type == ReplayEventType.Clear)
                    _clearedEventIndices.Add(i);
            }
        }
        else
        {
            // Backward seek: collect cleared events that need to be undone (in reverse)
            for (int i = _clearedEventIndices.Count - 1; i >= 0; i--)
            {
                if (_clearedEventIndices[i] >= targetIndex)
                {
                    result.EventsToUndo.Add(_timedEvents[_clearedEventIndices[i]]);
                    _clearedEventIndices.RemoveAt(i);
                }
                else
                {
                    break;
                }
            }
        }

        _currentIndex = targetIndex;
        _currentTime = targetTime;

        return result;
    }
}

/// <summary>
/// Result of a seek operation, describing what the view layer needs to do.
/// </summary>
public sealed class SeekResult
{
    /// <summary>Clear/reject events to apply (forward seek).</summary>
    public List<ReplayEvent> EventsToApply { get; } = new();

    /// <summary>Clear events to undo — re-add these arrows (backward seek). In reverse clear order.</summary>
    public List<ReplayEvent> EventsToUndo { get; } = new();

    public bool IsForward => EventsToApply.Count > 0;
    public bool IsBackward => EventsToUndo.Count > 0;
}
