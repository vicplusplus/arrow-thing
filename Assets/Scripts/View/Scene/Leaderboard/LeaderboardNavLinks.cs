using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Wires the leaderboard's <see cref="FocusNavigator"/> graph after the
/// items have been added. Pulled out of <c>RebuildEntryNavigator</c> so
/// the controller's "build items" phase and "wire links" phase live in
/// separate, individually-readable units.
///
/// <para>Stateless — every input the wiring needs is on the
/// <see cref="Sections"/> bag. The controller builds items, captures
/// each section's start/end indices into a Sections instance, calls
/// <c>Navigator.SetItems(items, initialFocus)</c>, then invokes
/// <see cref="Apply"/>. Focus restoration after rebuild stays in the
/// controller because it inspects post-rebuild controller state.</para>
/// </summary>
public static class LeaderboardNavLinks
{
    /// <summary>
    /// Index bag: where each named region lives in the items list. Slots
    /// that are absent in this render pass (e.g. mode tabs not built,
    /// refresh button hidden, player panel not visible) carry -1.
    /// </summary>
    public sealed class Sections
    {
        // Header row.
        public int BackIdx;
        public int LocalIdx;
        public int GlobalIdx;

        // Mode tabs (-1 if the corresponding tab button wasn't present).
        public int ModeClassicIdx = -1;
        public int ModeEndlessIdx = -1;

        // Size tabs row.
        public int TabsStart;
        public int TabsEnd;
        public int SizeTabCount;

        // Refresh button (global view only; -1 if absent).
        public int RefreshIdx = -1;

        // Sort row (classic local only; SortCount == 0 if absent).
        public int SortStart;
        public int SortCount;

        // Entries (entry row + its N inline buttons, repeating).
        public int EntriesStart;
        public int EntryCount;

        // Player panel play button (global view, after entries; -1 if absent).
        public int PlayerPlayIdx = -1;

        // Mode + active tab — drive the size-tab Up target and a few
        // policy branches that depend on which tab is "current".
        public bool IsEndlessMode;
        public int ActiveTabIndex;
    }

    /// <summary>
    /// Apply the entire leaderboard nav graph in one pass. Caller must
    /// have populated <paramref name="items"/> in the order the
    /// <paramref name="sections"/> indices reference, and called
    /// <c>nav.SetItems(items, ...)</c> already.
    /// </summary>
    public static void Apply(
        FocusNavigator nav,
        IReadOnlyList<FocusNavigator.FocusItem> items,
        Sections sections
    )
    {
        // Top row is a single horizontal chain:
        //   Back ↔ Classic ↔ Endless ↔ Local ↔ Global
        // Each pair is bidirectional. The chain is built defensively — if a
        // slot isn't present (e.g. mode tabs missing on some legacy state),
        // adjacent links collapse around it.
        var topChain = new List<int> { sections.BackIdx };
        if (sections.ModeClassicIdx >= 0)
            topChain.Add(sections.ModeClassicIdx);
        if (sections.ModeEndlessIdx >= 0)
            topChain.Add(sections.ModeEndlessIdx);
        topChain.Add(sections.LocalIdx);
        topChain.Add(sections.GlobalIdx);
        for (int i = 0; i < topChain.Count - 1; i++)
            nav.LinkBidi(topChain[i], FocusNavigator.NavDir.Right, topChain[i + 1]);

        // Tab row: horizontal chain + refresh button at the end in global view.
        if (sections.SizeTabCount > 1)
            nav.LinkRow(sections.TabsStart, sections.SizeTabCount);
        if (sections.RefreshIdx >= 0 && sections.SizeTabCount > 0)
            nav.LinkBidi(sections.TabsEnd, FocusNavigator.NavDir.Right, sections.RefreshIdx);

        // Classic's last size tab (the "All" tab in classic mode) → Right → Local.
        // Lets the player chain rightward off the size tabs into the toggle.
        // No symmetric link from local → Left → All — local already chains
        // back through Endless/Classic into the size tabs via Down.
        if (!sections.IsEndlessMode && sections.SizeTabCount > 0 && sections.RefreshIdx < 0)
            nav.Link(sections.TabsEnd, FocusNavigator.NavDir.Right, sections.LocalIdx);

        // Top-row → Down → size tabs:
        //   Back / Classic → first size tab.
        //   Endless / Local / Global → last size tab.
        // Falls back to the available tabs row if mode tabs aren't present.
        if (sections.SizeTabCount > 0)
        {
            nav.Link(sections.BackIdx, FocusNavigator.NavDir.Down, sections.TabsStart);
            if (sections.ModeClassicIdx >= 0)
                nav.Link(sections.ModeClassicIdx, FocusNavigator.NavDir.Down, sections.TabsStart);
            if (sections.ModeEndlessIdx >= 0)
                nav.Link(sections.ModeEndlessIdx, FocusNavigator.NavDir.Down, sections.TabsEnd);
            nav.Link(sections.LocalIdx, FocusNavigator.NavDir.Down, sections.TabsEnd);
            nav.Link(sections.GlobalIdx, FocusNavigator.NavDir.Down, sections.TabsEnd);
        }

        // Size tabs → Up → currently active mode tab (the one that brought
        // these size tabs into existence). Falls back to back/local if mode
        // tabs aren't present.
        int sizeTabsUpTarget = sections.IsEndlessMode
            ? (sections.ModeEndlessIdx >= 0 ? sections.ModeEndlessIdx : sections.LocalIdx)
            : (sections.ModeClassicIdx >= 0 ? sections.ModeClassicIdx : sections.BackIdx);
        for (int i = 0; i < sections.SizeTabCount; i++)
            nav.Link(sections.TabsStart + i, FocusNavigator.NavDir.Up, sizeTabsUpTarget);

        // Tabs → Down → sort (or entries if no sort).
        int belowTabs = sections.SortCount > 0 ? sections.SortStart : sections.EntriesStart;
        for (int i = 0; i < sections.SizeTabCount; i++)
            nav.Link(sections.TabsStart + i, FocusNavigator.NavDir.Down, belowTabs);

        // Refresh button: Up → active mode tab (matches size-tab Up target),
        // Down → same as last tab.
        if (sections.RefreshIdx >= 0)
        {
            nav.Link(sections.RefreshIdx, FocusNavigator.NavDir.Up, sizeTabsUpTarget);
            nav.Link(sections.RefreshIdx, FocusNavigator.NavDir.Down, belowTabs);
        }

        // Sort row.
        if (sections.SortCount > 0)
        {
            if (sections.SortCount > 1)
                nav.LinkRow(sections.SortStart, sections.SortCount);

            // Sort → Up → active tab.
            for (int i = 0; i < sections.SortCount; i++)
                nav.Link(
                    sections.SortStart + i,
                    FocusNavigator.NavDir.Up,
                    sections.TabsStart + sections.ActiveTabIndex
                );

            // Sort → Down → first entry.
            if (sections.EntryCount > 0)
                for (int i = 0; i < sections.SortCount; i++)
                    nav.Link(
                        sections.SortStart + i,
                        FocusNavigator.NavDir.Down,
                        sections.EntriesStart
                    );
        }

        // Entry rows: row↔row vertical chain, row↔button row-grid.
        // Each row is [row, btn0, btn1, btn2, ...]. Buttons form columns
        // across rows.
        int prevRowIdx = -1;
        int prevBtnCount = 0;
        int curIdx = sections.EntriesStart;
        for (int e = 0; e < sections.EntryCount; e++)
        {
            int rowIdx = curIdx;
            var row = items[rowIdx].Element;
            int btnCount = row.Query<Button>(className: "lb-row-btn").ToList().Count;
            int firstBtnIdx = rowIdx + 1;
            int lastBtnIdx = rowIdx + btnCount;

            // Row → Right → first inline button, first button → Left → row.
            if (btnCount > 0)
            {
                nav.Link(rowIdx, FocusNavigator.NavDir.Right, firstBtnIdx);
                nav.Link(firstBtnIdx, FocusNavigator.NavDir.Left, rowIdx);
            }

            // Inline buttons: horizontal chain.
            for (int i = firstBtnIdx; i < lastBtnIdx; i++)
                nav.LinkBidi(i, FocusNavigator.NavDir.Right, i + 1);

            // Vertical: row↔row.
            if (prevRowIdx >= 0)
                nav.LinkBidi(prevRowIdx, FocusNavigator.NavDir.Down, rowIdx);

            // Vertical: button column↔button column (grid navigation).
            // Both rows always have the same buttons (fav, play, ctx) — the set
            // is determined by compact mode which applies uniformly to all rows.
            if (prevRowIdx >= 0)
            {
                int cols = Mathf.Min(btnCount, prevBtnCount);
                for (int c = 0; c < cols; c++)
                {
                    int prevBtn = prevRowIdx + 1 + c;
                    int curBtn = rowIdx + 1 + c;
                    nav.LinkBidi(prevBtn, FocusNavigator.NavDir.Down, curBtn);
                }
            }

            prevRowIdx = rowIdx;
            prevBtnCount = btnCount;
            curIdx += 1 + btnCount;
        }

        // First entry row + all its inline buttons → Up → sort (or tabs).
        // Uses LinkBreak so DAS stops at #1 and requires a fresh press to exit.
        if (sections.EntryCount > 0)
        {
            int aboveEntries =
                sections.SortCount > 0
                    ? sections.SortStart
                    : sections.TabsStart + sections.ActiveTabIndex;
            var firstRow = items[sections.EntriesStart].Element;
            int firstRowBtnCount = firstRow.Query<Button>(className: "lb-row-btn").ToList().Count;

            nav.LinkBreak(sections.EntriesStart, FocusNavigator.NavDir.Up, aboveEntries);
            for (int c = 1; c <= firstRowBtnCount; c++)
                nav.LinkBreak(sections.EntriesStart + c, FocusNavigator.NavDir.Up, aboveEntries);
        }

        // Player panel play button below the last entry (with DAS break).
        if (sections.PlayerPlayIdx >= 0 && prevRowIdx >= 0)
        {
            nav.LinkBreak(prevRowIdx, FocusNavigator.NavDir.Down, sections.PlayerPlayIdx);
            nav.Link(sections.PlayerPlayIdx, FocusNavigator.NavDir.Up, prevRowIdx);
            // Also link last row's inline buttons down to player panel.
            if (prevBtnCount > 0)
            {
                for (int bi = 1; bi <= prevBtnCount; bi++)
                    nav.LinkBreak(
                        prevRowIdx + bi,
                        FocusNavigator.NavDir.Down,
                        sections.PlayerPlayIdx
                    );
            }
        }
        else if (sections.PlayerPlayIdx >= 0)
        {
            // No entries — link player panel below tabs/sort.
            int above =
                sections.SortCount > 0
                    ? sections.SortStart
                    : sections.TabsStart + sections.ActiveTabIndex;
            nav.Link(above, FocusNavigator.NavDir.Down, sections.PlayerPlayIdx);
            nav.Link(sections.PlayerPlayIdx, FocusNavigator.NavDir.Up, above);
        }
    }
}
