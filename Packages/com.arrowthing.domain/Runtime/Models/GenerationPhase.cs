/// <summary>
/// Phases of incremental board generation. Yielded by
/// <see cref="BoardGeneration.FillBoardIncremental"/> as sentinel values
/// between phases, and used by the view layer to track progress.
/// </summary>
public enum GenerationPhase
{
    Generating,
    Compacting,
    Finalizing,
}
