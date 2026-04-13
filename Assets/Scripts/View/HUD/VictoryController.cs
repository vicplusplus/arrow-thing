using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Handles the board-cleared sequence: zoom-to-fit + arrow pull-out run in parallel,
/// then grid fade-out, then victory popup with a randomized message and Play Again / Menu buttons.
/// Records the result to the leaderboard; gold timer indicates personal best.
/// Score submission is fire-and-forget via <see cref="ScoreSubmitter"/>.
/// </summary>
public sealed class VictoryController : MonoBehaviour
{
    private static readonly string[] Messages =
    {
        "Nice!",
        "Nailed it!",
        "That was smooth.",
        "Arrows fear you.",
        "Flawless.",
        "Too easy.",
        "Clean sweep!",
        "Not a scratch.",
        "Speedrun certified.",
        "Unstoppable.",
        "GOAT",
        "Victory Message.",
        "woa",
        "Where did my arrows go????? :(",
        "mmm im hungy. macdonel..",
        "WHAT???",
        "Congrat.",
        "[object Object]",
        "0110100001101001",
        "Dominated.",
        "Wonderful.",
        "Wodnfeurl.",
        "Checkmate.",
        "why so slow?",
        "ok, now do it again but faster",
        "sorry, time below is wrong. you actually took 2 hours. my bad.",
        "Incredible.",
        "Clearly cheated.",
        "huh",
    };

    private UIDocument _uiDocument;
    private UIDocument _hudDocument;
    private BoardGridRenderer _gridRenderer;
    private CameraController _camCtrl;
    private GameTimer _timer;
    private int _boardWidth;
    private int _boardHeight;
    private VisualElement _overlay;
    private Label _messageLabel;
    private Label _timeLabel;
    private System.Func<ReplayData> _buildReplayData;
    private string _recordedGameId;
    private bool _popupVisible;

    [SerializeField]
    private float zoomOutDuration = 0.6f;

    [SerializeField]
    private float gridFadeDuration = 0.5f;

    /// <summary>Tracks whether the zoom-to-fit has finished.</summary>
    private bool _zoomDone;

    /// <summary>Tracks whether the last arrow's pull-out animation has finished.</summary>
    private bool _pullOutDone;

    public void Init(
        UIDocument uiDocument,
        BoardGridRenderer gridRenderer,
        CameraController camCtrl,
        int boardWidth,
        int boardHeight,
        GameTimer timer = null,
        UIDocument hudDocument = null,
        System.Func<ReplayData> buildReplayData = null
    )
    {
        _uiDocument = uiDocument;
        _hudDocument = hudDocument;
        _gridRenderer = gridRenderer;
        _camCtrl = camCtrl;
        _boardWidth = boardWidth;
        _boardHeight = boardHeight;
        _timer = timer;
        _buildReplayData = buildReplayData;

        var root = _uiDocument.rootVisualElement;
        _overlay = root.Q("victory-overlay");
        _messageLabel = root.Q<Label>("victory-message");
        _timeLabel = root.Q<Label>("victory-time");

        root.Q<Button>("play-again-btn").clicked += OnPlayAgain;
        root.Q<Button>("menu-btn").clicked += OnMenu;

        var viewLbBtn = root.Q<Button>("view-leaderboard-btn");
        if (viewLbBtn != null)
            viewLbBtn.clicked += OnViewLeaderboard;
    }

    /// <summary>
    /// Call immediately when the last arrow starts clearing (before animation).
    /// Starts the camera zoom-to-fit in parallel with the pull-out animation.
    /// Fires score submission in the background (fire-and-forget).
    /// </summary>
    public void OnLastArrowClearing()
    {
        _zoomDone = false;
        _pullOutDone = false;

        // Fire-and-forget score submission via GlobalToast for error reporting.
        if (_buildReplayData != null && _timer != null)
        {
            var replay = _buildReplayData.Invoke();
            if (replay != null)
            {
                replay.finalTime = _timer.SolveElapsed;
                ScoreSubmitter.Submit(replay);
            }
        }

        if (_camCtrl != null)
            _camCtrl.ZoomToFit(
                zoomOutDuration,
                () =>
                {
                    _zoomDone = true;
                    TryStartGridFade();
                }
            );
        else
            _zoomDone = true;
    }

    /// <summary>
    /// Call when the last arrow's pull-out animation finishes.
    /// </summary>
    public void OnBoardCleared()
    {
        _pullOutDone = true;
        TryStartGridFade();
    }

    /// <summary>
    /// Starts the grid fade only once both zoom and pull-out are done.
    /// </summary>
    private void TryStartGridFade()
    {
        if (!_zoomDone || !_pullOutDone)
            return;

        _gridRenderer.FadeOut(gridFadeDuration, ShowPopup);
    }

    private void ShowPopup()
    {
        double elapsed = _timer != null ? _timer.SolveElapsed : -1.0;
        Debug.Log(
            $"[VictoryController] Victory popup: board={_boardWidth}x{_boardHeight}, solveElapsed={elapsed:F3}s"
        );

        string msg = Messages[Random.Range(0, Messages.Length)];
        _messageLabel.text = msg;

        // Scale font down for longer messages so they fit the box
        int len = msg.Length;
        if (len > 40)
            _messageLabel.style.fontSize = 20;
        else if (len > 20)
            _messageLabel.style.fontSize = 28;
        else
            _messageLabel.style.fontSize = 40;

        if (_timeLabel != null && _timer != null)
            _timeLabel.text = $"{_boardWidth}x{_boardHeight} - {FormatTime(_timer.SolveElapsed)}";

        if (_hudDocument != null)
            _hudDocument.rootVisualElement.style.display = DisplayStyle.None;

        RecordToLeaderboard();

        _overlay.RemoveFromClassList("victory--hidden");
        _popupVisible = true;

        // Set up keyboard navigation for victory popup buttons.
        SetupVictoryNavigator();
    }

    private FocusNavigator _victoryNavigator;

    private void SetupVictoryNavigator()
    {
        var root = _uiDocument.rootVisualElement;
        _victoryNavigator = new FocusNavigator(root);

        var items = new System.Collections.Generic.List<FocusNavigator.FocusItem>();

        // Vertical column: View Leaderboard -> Play Again -> Menu.
        int lbIdx = -1;
        var viewLb = root.Q<Button>("view-leaderboard-btn");
        if (viewLb != null)
        {
            lbIdx = items.Count;
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = viewLb,
                    OnActivate = () =>
                    {
                        OnViewLeaderboard();
                        return true;
                    },
                }
            );
        }

        int playIdx = -1;
        var playAgain = root.Q<Button>("play-again-btn");
        if (playAgain != null)
        {
            playIdx = items.Count;
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = playAgain,
                    OnActivate = () =>
                    {
                        OnPlayAgain();
                        return true;
                    },
                }
            );
        }

        int menuIdx = -1;
        var menuBtn = root.Q<Button>("menu-btn");
        if (menuBtn != null)
        {
            menuIdx = items.Count;
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = menuBtn,
                    OnActivate = () =>
                    {
                        OnMenu();
                        return true;
                    },
                }
            );
        }

        // Default focus on Play Again.
        int defaultFocus = playIdx >= 0 ? playIdx : 0;
        _victoryNavigator.SetItems(items, defaultFocus);

        // Vertical chain: Leaderboard <-> Play Again <-> Menu.
        if (lbIdx >= 0 && playIdx >= 0)
            _victoryNavigator.LinkBidi(lbIdx, FocusNavigator.NavDir.Down, playIdx);
        if (playIdx >= 0 && menuIdx >= 0)
            _victoryNavigator.LinkBidi(playIdx, FocusNavigator.NavDir.Down, menuIdx);
    }

    private void Update()
    {
        if (_victoryNavigator != null)
            _victoryNavigator.Update();

        if (!_popupVisible)
            return;

        var km = KeybindManager.Instance;
        if (km == null || km.TextFieldFocused)
            return;

        if (SettingsController.Instance != null && SettingsController.Instance.IsOpen)
            return;

        // R — Play Again (Gameplay map still active)
        if (km.QuickReset != null && km.QuickReset.WasPerformedThisFrame())
            OnPlayAgain();
        // L — View Leaderboard (ModeSelect map is disabled during Gameplay,
        // so read OpenLeaderboard binding directly via keyboard)
        else if (
            UnityEngine.InputSystem.Keyboard.current != null
            && UnityEngine.InputSystem.Keyboard.current.lKey.wasPressedThisFrame
        )
            OnViewLeaderboard();
        else if (NavigableScene.ShouldHandleCancel(_victoryNavigator))
            OnMenu();
    }

    private void RecordToLeaderboard()
    {
        var manager = LeaderboardManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning(
                "[VictoryController] RecordToLeaderboard: LeaderboardManager.Instance is null — result not saved"
            );
            return;
        }
        if (_timer == null)
        {
            Debug.LogWarning(
                "[VictoryController] RecordToLeaderboard: timer is null — result not saved"
            );
            return;
        }

        double solveTime = _timer.SolveElapsed;
        bool isNewBest = manager.IsPersonalBest(_boardWidth, _boardHeight, solveTime);
        Debug.Log(
            $"[VictoryController] RecordToLeaderboard: board={_boardWidth}x{_boardHeight}, solveTime={solveTime:F3}s, isNewBest={isNewBest}"
        );

        // Build and record the completed replay
        var replayData = _buildReplayData != null ? _buildReplayData.Invoke() : null;
        if (replayData != null)
        {
            replayData.finalTime = solveTime;
            var entry = manager.RecordResult(replayData);
            _recordedGameId = entry.gameId;
            Debug.Log($"[VictoryController] Result recorded: gameId={_recordedGameId}");
        }
        else
        {
            Debug.LogWarning(
                "[VictoryController] RecordToLeaderboard: buildReplayData returned null — result not saved"
            );
        }

        if (isNewBest && _timeLabel != null)
            _timeLabel.AddToClassList("victory-time--gold");
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0)
            seconds = 0;
        int totalMillis = (int)(seconds * 1000);
        int hours = totalMillis / 3600000;
        int mins = (totalMillis % 3600000) / 60000;
        int secs = (totalMillis % 60000) / 1000;
        int millis = totalMillis % 1000;

        if (hours > 0)
            return $"{hours}:{mins:D2}:{secs:D2}.{millis:D3}";
        if (mins > 0)
            return $"{mins}:{secs:D2}.{millis:D3}";
        return $"{secs}.{millis:D3}";
    }

    private void OnViewLeaderboard()
    {
        GameSettings.LeaderboardFocusGameId = _recordedGameId;
        // Replace the (now stale) Game scene so popping Leaderboard returns
        // to the scene that launched Game (Size Select or MainMenu), not the
        // dead post-victory Game scene.
        SceneNav.Replace("Leaderboard");
    }

    private void OnPlayAgain()
    {
        // Clear stale resume state so the new game generates a fresh board
        // instead of trying to resume the just-completed save data.
        GameSettings.Apply(_boardWidth, _boardHeight);
        SceneNav.Replace("Game");
    }

    private void OnMenu()
    {
        SceneNav.Pop();
    }
}
