using System;
using System.Collections.Generic;

/// <summary>
/// Accumulates <see cref="ReplayEvent"/>s during a game session. Auto-increments seq.
/// Can be initialized from prior events to continue a resumed save.
/// Pure C# — no Unity dependency.
/// </summary>
public sealed class ReplayRecorder
{
    private int _nextSeq;
    private readonly List<ReplayEvent> _events;

    /// <summary>Start a fresh recorder for a new game.</summary>
    public ReplayRecorder()
    {
        _events = new List<ReplayEvent>();
    }

    /// <summary>
    /// Resume from a prior save's event list. New events will be appended with seq
    /// continuing from <paramref name="nextSeq"/>.
    /// </summary>
    public ReplayRecorder(List<ReplayEvent> priorEvents, int nextSeq)
    {
        _events = new List<ReplayEvent>(priorEvents);
        _nextSeq = nextSeq;
    }

    public IReadOnlyList<ReplayEvent> Events => _events;

    public void RecordSessionStart()
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.SessionStart,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordSessionLeave()
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.SessionLeave,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordSessionRejoin()
    {
        // If the previous session didn't end with a session_leave (e.g. force-quit),
        // inject one using the timestamp of the last recorded event to avoid
        // orphan rejoins that break time computation.
        if (_events.Count > 0 && _events[_events.Count - 1].type != ReplayEventType.SessionLeave)
        {
            _events.Add(
                new ReplayEvent
                {
                    seq = _nextSeq++,
                    type = ReplayEventType.SessionLeave,
                    timestamp = _events[_events.Count - 1].timestamp,
                }
            );
        }

        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.SessionRejoin,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordStartSolve()
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.StartSolve,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordClear(float posX, float posY)
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.Clear,
                posX = posX,
                posY = posY,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    /// <summary>Records the end of the solve (board fully cleared).</summary>
    public void RecordEndSolve()
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.EndSolve,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordReject(float posX, float posY)
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.Reject,
                posX = posX,
                posY = posY,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordMiss(float posX, float posY)
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.Miss,
                posX = posX,
                posY = posY,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    // ---- Cell + sim-time variants (endless / future deterministic modes) ----
    //
    // These mirror RecordClear/Reject/Miss/Topout but record cell coords
    // (instead of world pos) and a sim-clock seconds value (instead of
    // wall-clock ISO). Verifier reads simTime + cellX/cellY directly so the
    // entire replay reproduces deterministically without world↔cell
    // conversion or wall-clock involvement. Wall-clock timestamp is still
    // populated as a debug aid.

    public void RecordClearAtCell(float simTime, int cellX, int cellY)
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.Clear,
                simTime = simTime,
                cellX = cellX,
                cellY = cellY,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordRejectAtCell(float simTime, int cellX, int cellY)
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.Reject,
                simTime = simTime,
                cellX = cellX,
                cellY = cellY,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordMissAtCell(float simTime, int cellX, int cellY)
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.Miss,
                simTime = simTime,
                cellX = cellX,
                cellY = cellY,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    public void RecordTopout(float simTime)
    {
        _events.Add(
            new ReplayEvent
            {
                seq = _nextSeq++,
                type = ReplayEventType.Topout,
                simTime = simTime,
                timestamp = DateTime.UtcNow.ToString("O"),
            }
        );
    }

    /// <summary>
    /// Produces a <see cref="ReplayData"/> snapshot of all accumulated events.
    /// </summary>
    /// <param name="boardSnapshot">
    /// Initial arrow configuration (all arrows before any clears). Each inner list is
    /// one arrow's cells (head to tail). Required for all new saves.
    /// </param>
    /// <param name="finalTime">Pass the solve elapsed at completion, or -1 for in-progress.</param>
    /// <param name="gameVersion">Application version string, passed from the view layer.</param>
    public ReplayData ToReplayData(
        string gameId,
        int seed,
        int boardWidth,
        int boardHeight,
        int maxArrowLength,
        float inspectionDuration,
        IReadOnlyList<IReadOnlyList<Cell>> boardSnapshot = null,
        double finalTime = -1.0,
        string gameVersion = null
    )
    {
        var data = new ReplayData
        {
            version = 5,
            gameId = gameId,
            seed = seed,
            boardWidth = boardWidth,
            boardHeight = boardHeight,
            maxArrowLength = maxArrowLength,
            inspectionDuration = inspectionDuration,
            gameVersion = gameVersion,
            events = new List<ReplayEvent>(_events),
            finalTime = finalTime,
        };
        if (boardSnapshot != null)
            data.SetSnapshotArrows(boardSnapshot);
        return data;
    }
}
