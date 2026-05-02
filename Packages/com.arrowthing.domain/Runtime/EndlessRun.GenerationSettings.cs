using System;

public sealed partial class EndlessRun
{
    /// <summary>
    /// All endless-mode garbage-generation constants in one struct so the
    /// client (<see cref="EndlessModeController"/>) and the server-side replay
    /// simulator can't drift. <see cref="ReplayData.tuningsVersion"/> selects
    /// which version's values apply when verifying a recorded run.
    ///
    /// Version policy: bump the version whenever a change to these values
    /// would alter replay outcome (almost any change here qualifies). Old
    /// replays continue verifying against the matching historical version
    /// via <see cref="ForVersion"/>; once a version stops being shipped by
    /// any live client, it can be removed.
    /// </summary>
    public readonly struct GenerationSettings
    {
        /// <summary>Preview duration as a fraction of the current push interval.</summary>
        public readonly float PreviewDurationFractionOfPush;

        /// <summary>Max arrow length the spawn generator may produce.</summary>
        public readonly int EndlessMaxArrowLength;

        /// <summary>Garbage cost charged for any arrow regardless of complexity.</summary>
        public readonly int WeightPerArrow;

        /// <summary>Garbage cost added per perpendicular forward dep at placement time.</summary>
        public readonly int WeightPerPerpDep;

        /// <summary>Candidate placements sampled per arrow spawn (highest score wins).</summary>
        public readonly int SpawnCandidates;

        /// <summary>Sweet-spot arrow length the scorer prefers.</summary>
        public readonly int ScoreIdealLength;

        /// <summary>Per-cell penalty for arrows shorter or longer than the ideal.</summary>
        public readonly int ScoreLengthPenalty;

        /// <summary>Per-perp-dep bonus in the score function.</summary>
        public readonly int ScorePerpDepBonus;

        /// <summary>Push interval at clear=0 (run start).</summary>
        public readonly float PushIntervalAtStartSeconds;

        /// <summary>Hard floor on push interval; difficulty caps here.</summary>
        public readonly float PushIntervalMinSeconds;

        /// <summary>Per-tier reduction in push interval (seconds).</summary>
        public readonly float PushIntervalStepDownPerTier;

        /// <summary>Baseline combo size as a fraction of total board cells.</summary>
        public readonly float ComboBasePctOfBoard;

        /// <summary>Clear count for the first difficulty tier on a 100-cell (10×10) board.</summary>
        public readonly int ClearsForFirstTierAt100Cells;

        /// <summary>Power-law exponent for the difficulty-tier ramp.</summary>
        public readonly float DifficultyTierPower;

        /// <summary>Board occupancy at which the catch-up bonus reaches zero.</summary>
        public readonly float TargetOccupancy;

        /// <summary>Peak magnitude of the occupancy catch-up bonus, fraction of board cells.</summary>
        public readonly float OccupancyAdaptivePctOfBoard;

        /// <summary>Hard cap on combo size as a fraction of board cells.</summary>
        public readonly float MaxComboPctOfBoard;

        /// <summary>Sim-time gap between clears that resets the player's combo streak.</summary>
        public readonly float ComboTimerSeconds;

        public GenerationSettings(
            float previewDurationFractionOfPush,
            int endlessMaxArrowLength,
            int weightPerArrow,
            int weightPerPerpDep,
            int spawnCandidates,
            int scoreIdealLength,
            int scoreLengthPenalty,
            int scorePerpDepBonus,
            float pushIntervalAtStartSeconds,
            float pushIntervalMinSeconds,
            float pushIntervalStepDownPerTier,
            float comboBasePctOfBoard,
            int clearsForFirstTierAt100Cells,
            float difficultyTierPower,
            float targetOccupancy,
            float occupancyAdaptivePctOfBoard,
            float maxComboPctOfBoard,
            float comboTimerSeconds
        )
        {
            PreviewDurationFractionOfPush = previewDurationFractionOfPush;
            EndlessMaxArrowLength = endlessMaxArrowLength;
            WeightPerArrow = weightPerArrow;
            WeightPerPerpDep = weightPerPerpDep;
            SpawnCandidates = spawnCandidates;
            ScoreIdealLength = scoreIdealLength;
            ScoreLengthPenalty = scoreLengthPenalty;
            ScorePerpDepBonus = scorePerpDepBonus;
            PushIntervalAtStartSeconds = pushIntervalAtStartSeconds;
            PushIntervalMinSeconds = pushIntervalMinSeconds;
            PushIntervalStepDownPerTier = pushIntervalStepDownPerTier;
            ComboBasePctOfBoard = comboBasePctOfBoard;
            ClearsForFirstTierAt100Cells = clearsForFirstTierAt100Cells;
            DifficultyTierPower = difficultyTierPower;
            TargetOccupancy = targetOccupancy;
            OccupancyAdaptivePctOfBoard = occupancyAdaptivePctOfBoard;
            MaxComboPctOfBoard = maxComboPctOfBoard;
            ComboTimerSeconds = comboTimerSeconds;
        }

        /// <summary>
        /// Initial endless tuning shipped with the verification rollout. Bump
        /// the version when any value here changes in a replay-affecting way.
        /// </summary>
        public static readonly GenerationSettings V1 = new GenerationSettings(
            previewDurationFractionOfPush: 0.6f,
            endlessMaxArrowLength: 6,
            weightPerArrow: 1,
            weightPerPerpDep: 2,
            spawnCandidates: 6,
            scoreIdealLength: 4,
            scoreLengthPenalty: 2,
            scorePerpDepBonus: 5,
            pushIntervalAtStartSeconds: 2.0f,
            pushIntervalMinSeconds: 0.7f,
            pushIntervalStepDownPerTier: 0.1f,
            comboBasePctOfBoard: 0.09f,
            clearsForFirstTierAt100Cells: 10,
            difficultyTierPower: 0.78f,
            targetOccupancy: 0.5f,
            occupancyAdaptivePctOfBoard: 0.22f,
            maxComboPctOfBoard: 0.40f,
            comboTimerSeconds: 1f
        );

        /// <summary>
        /// Picks the historical settings matching <paramref name="version"/>.
        /// Throws on unknown versions — submission pre-verify gates this so
        /// the worker doesn't see unsupported versions.
        /// </summary>
        public static GenerationSettings ForVersion(int version)
        {
            return version switch
            {
                1 => V1,
                _ => throw new ArgumentException(
                    $"Unknown endless generation-settings version: {version}",
                    nameof(version)
                ),
            };
        }

        /// <summary>Highest settings version currently produced by clients.</summary>
        public const int CurrentVersion = 1;
    }
}
