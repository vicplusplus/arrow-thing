using System;

/// <summary>
/// Two-phase timer: inspection countdown followed by solve timer.
/// Pure C# — no Unity dependency. All timestamps use the same clock
/// (caller's responsibility).
/// </summary>
public sealed class GameTimer
{
    public enum Phase
    {
        Inspection,
        Solving,
        Finished,
    }

    private readonly double _inspectionDuration;
    private double _inspectionStart;
    private double _solveStart;

    public Phase CurrentPhase { get; private set; }
    public bool IsSolving => CurrentPhase == Phase.Solving;
    public double InspectionRemaining { get; private set; }
    public double SolveElapsed { get; private set; }

    public event Action<Phase> PhaseChanged;

    public GameTimer(double inspectionDuration = 15.0)
    {
        _inspectionDuration = inspectionDuration;
        InspectionRemaining = inspectionDuration;
        CurrentPhase = Phase.Inspection;
    }

    /// <summary>
    /// Begin inspection countdown. Call once at game start.
    /// </summary>
    public void Start(double current)
    {
        _inspectionStart = current;
        InspectionRemaining = _inspectionDuration;
        CurrentPhase = Phase.Inspection;
    }

    /// <summary>
    /// Call every frame with the current time to update display values.
    /// </summary>
    public void Tick(double current)
    {
        switch (CurrentPhase)
        {
            case Phase.Inspection:
                InspectionRemaining = Math.Max(
                    0.0,
                    _inspectionDuration - (current - _inspectionStart)
                );
                if (InspectionRemaining <= 0.0)
                {
                    StartSolve(_inspectionStart + _inspectionDuration);
                    SolveElapsed = current - _solveStart;
                }
                break;

            case Phase.Solving:
                SolveElapsed = current - _solveStart;
                break;
        }
    }

    /// <summary>
    /// Transition to solving. Uses the same clock as Tick.
    /// </summary>
    public void StartSolve(double current)
    {
        if (CurrentPhase != Phase.Inspection)
            return;

        _solveStart = current;
        SolveElapsed = 0.0;
        InspectionRemaining = 0.0;
        CurrentPhase = Phase.Solving;
        PhaseChanged?.Invoke(Phase.Solving);
    }

    /// <summary>
    /// Resume solving from a previously saved elapsed time.
    /// Skips the inspection phase and restores the solve timer offset.
    /// </summary>
    public void Resume(double current, double priorElapsed)
    {
        _solveStart = current - priorElapsed;
        SolveElapsed = priorElapsed;
        InspectionRemaining = 0.0;
        CurrentPhase = Phase.Solving;
        PhaseChanged?.Invoke(Phase.Solving);
    }

    /// <summary>
    /// End the solve. Uses the same clock as Tick.
    /// </summary>
    public void Finish(double current)
    {
        if (CurrentPhase != Phase.Solving)
            return;

        SolveElapsed = current - _solveStart;
        CurrentPhase = Phase.Finished;
        PhaseChanged?.Invoke(Phase.Finished);
    }
}
