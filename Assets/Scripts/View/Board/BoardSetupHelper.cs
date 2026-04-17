using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared board setup logic used by both GameController and ReplayViewController.
/// Extracts board/view creation, camera setup, and snapshot restoration.
/// </summary>
public static class BoardSetupHelper
{
    /// <summary>
    /// Creates a Board and BoardView pair. The BoardView is initialized without spawning arrows
    /// (caller adds them incrementally).
    /// </summary>
    public static (Board board, BoardView boardView) CreateBoardAndView(
        int width,
        int height,
        VisualSettings visualSettings
    )
    {
        var board = new Board(width, height);
        var boardGo = new GameObject("BoardView");
        var boardView = boardGo.AddComponent<BoardView>();
        boardView.Init(board, visualSettings, spawnArrows: false);
        return (board, boardView);
    }

    /// <summary>
    /// Sets up an orthographic camera controller fitted to the board.
    /// </summary>
    public static CameraController SetupCamera(Camera camera, Board board, float? zoomSpeed = null)
    {
        var camCtrl = camera.gameObject.GetComponent<CameraController>();
        if (camCtrl == null)
            camCtrl = camera.gameObject.AddComponent<CameraController>();
        camCtrl.Init(board);
        if (zoomSpeed.HasValue)
            camCtrl.ZoomSpeed = zoomSpeed.Value;
        return camCtrl;
    }

    /// <summary>
    /// Restores arrows from a board snapshot onto the board and view.
    /// Yields progress values for loading UI. Returns the total arrow count.
    /// </summary>
    public static IEnumerator<int> RestoreBoardFromSnapshot(
        Board board,
        BoardView boardView,
        List<List<Cell>> snapshot,
        float frameBudgetMs = 12f
    )
    {
        var snapshotArrows = new List<Arrow>(snapshot.Count);
        foreach (List<Cell> arrowCells in snapshot)
            snapshotArrows.Add(new Arrow(arrowCells));

        int totalArrows = snapshotArrows.Count;
        Debug.Log(
            $"[BoardSetupHelper] RestoreBoardFromSnapshot: {totalArrows} arrows, board={board.Width}x{board.Height}"
        );
        int totalSteps = totalArrows * 2;
        var restorer = board.RestoreArrowsIncremental(snapshotArrows);

        int viewedCount = 0;
        while (true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool done = false;
            while (sw.ElapsedMilliseconds < frameBudgetMs)
            {
                if (!restorer.MoveNext())
                {
                    done = true;
                    break;
                }
                if (viewedCount < totalArrows)
                    boardView.AddArrowView(snapshotArrows[viewedCount++]);
            }

            yield return restorer.Current;

            if (done)
                break;
        }
        Debug.Log(
            $"[BoardSetupHelper] RestoreBoardFromSnapshot complete: {board.Arrows.Count} arrows placed"
        );
    }

    /// <summary>
    /// Restores arrows from a pre-built arrow list (e.g. from BinarySnapshot.DecodeFull)
    /// onto the board and view. Same incremental yielding pattern as the cell-list overload.
    /// </summary>
    public static IEnumerator<int> RestoreBoardFromSnapshotArrows(
        Board board,
        BoardView boardView,
        IReadOnlyList<Arrow> arrows,
        float frameBudgetMs = 12f
    )
    {
        int totalArrows = arrows.Count;
        Debug.Log(
            $"[BoardSetupHelper] RestoreBoardFromSnapshotArrows: {totalArrows} arrows, board={board.Width}x{board.Height}"
        );
        var restorer = board.RestoreArrowsIncremental(arrows);

        int viewedCount = 0;
        while (true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool done = false;
            while (sw.ElapsedMilliseconds < frameBudgetMs)
            {
                if (!restorer.MoveNext())
                {
                    done = true;
                    break;
                }
                if (viewedCount < totalArrows)
                    boardView.AddArrowView(arrows[viewedCount++]);
            }

            yield return restorer.Current;

            if (done)
                break;
        }
        Debug.Log(
            $"[BoardSetupHelper] RestoreBoardFromSnapshotArrows complete: {board.Arrows.Count} arrows placed"
        );
    }
}
