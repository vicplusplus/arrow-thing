using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Scene entry point for the Co-op Hub. Default panel is the "My Lobbies" list;
/// Host and Join open as modal overlays. Hosting a new lobby opens a generation-
/// progress modal and transitions to the Coop Game scene when the snapshot arrives.
/// </summary>
public sealed class CoopHubController : NavigableScene
{
    private enum HubState
    {
        List,
        HostModal,
        JoinModal,
        HostingModal,
    }

    private HubState _state = HubState.List;
    private ApiClient _api;

    // UI refs
    private VisualElement _hubRoot;
    private VisualElement _hubList;
    private Label _hubEmpty;
    private VisualElement _hostModal;
    private VisualElement _joinModal;
    private VisualElement _hostingModal;
    private Label _hostError;
    private Label _joinError;
    private Label _hostingLabel;
    private Label _progressPct;
    private VisualElement _progressFill;

    // Form fields
    private LabeledField _hostNameField;
    private LabeledField _joinCodeField;

    // Focus items per lobby row, rebuilt on each RefreshListAsync.
    // Each row is a grid of 4 columns: [visibility, copy, main (play/retry), delete].
    // Columns may be null when the button isn't shown for that row (e.g., no delete
    // for non-owners, no main button for completed lobbies).
    private struct RowFocus
    {
        public FocusNavigator.FocusItem? Visibility;
        public FocusNavigator.FocusItem? Copy;
        public FocusNavigator.FocusItem? Main;
        public FocusNavigator.FocusItem? Delete;
    }

    private readonly List<RowFocus> _rowFocus = new();

    // Host size sliders. Min 20 for easier multiplayer testing; max stays
    // at 400 since large boards are the real target. Snap step 10.
    private const int HostMinDim = 20;
    private const int HostMaxDim = 400;
    private SnapSlider _hostWidthSlider;
    private SnapSlider _hostHeightSlider;

    // Confirm modal
    private ConfirmModal _deleteModal;
    private string _pendingDeleteId;

    // Filter state
    // Default to "active" — completed lobbies would otherwise fill the list
    // with noise. Users opt into the completed view explicitly.
    private string _currentFilter = "active";
    private Button _filterActive;
    private Button _filterCompleted;

    // Hosting WS (for watching gen progress of a just-created lobby)
    private CoopClient _hostingClient;
    private string _hostingCode;

    protected override KeybindManager.Context NavContext => KeybindManager.Context.CoopHub;

    protected override void BuildUI(VisualElement root)
    {
        _api = new ApiClient();

        _hubRoot = root.Q("coop-hub-root");
        _hubList = root.Q("hub-list");
        _hubEmpty = root.Q<Label>("hub-empty");
        _hostModal = root.Q("host-modal");
        _joinModal = root.Q("join-modal");
        _hostingModal = root.Q("hosting-modal");
        _hostError = root.Q<Label>("host-error");
        _joinError = root.Q<Label>("join-error");
        _hostingLabel = root.Q<Label>("hosting-label");
        _progressPct = root.Q<Label>("progress-pct");
        _progressFill = root.Q("hub-progress__fill");

        // Host form
        _hostNameField = new LabeledField("Lobby name", "host-name");
        _hostNameField.OnSubmit = OnHostSubmit;
        root.Q("host-form-slot").Add(_hostNameField.Root);

        // Host size sliders
        _hostWidthSlider = new SnapSlider(
            HostMinDim,
            HostMaxDim,
            200f,
            smallStep: 1f,
            snapStep: 10f,
            format: "0",
            showLock: true
        );
        root.Q("host-width-row").Add(_hostWidthSlider.Root);

        _hostHeightSlider = new SnapSlider(
            HostMinDim,
            HostMaxDim,
            200f,
            smallStep: 1f,
            snapStep: 10f,
            format: "0",
            showLock: true
        );
        root.Q("host-height-row").Add(_hostHeightSlider.Root);

        // Join form
        _joinCodeField = new LabeledField("Code", "join-code");
        _joinCodeField.OnSubmit = OnJoinSubmit;
        root.Q("join-form-slot").Add(_joinCodeField.Root);

        // Buttons
        root.Q<Button>("back-hub-btn").clicked += () => SceneNav.Pop();
        root.Q<Button>("host-btn").clicked += OpenHostModal;
        root.Q<Button>("join-btn").clicked += OpenJoinModal;
        root.Q<Button>("hub-refresh-btn").clicked += () => RefreshListAsync();
        root.Q<Button>("host-submit-btn").clicked += OnHostSubmit;
        root.Q<Button>("host-cancel-btn").clicked += CloseHostModal;
        root.Q<Button>("join-submit-btn").clicked += OnJoinSubmit;
        root.Q<Button>("join-cancel-btn").clicked += CloseJoinModal;
        root.Q<Button>("hosting-cancel-btn").clicked += CloseHostingModal;

        // Backdrop click → close (only if the click hit the overlay itself, not the modal box)
        _hostModal.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _hostModal)
                CloseHostModal();
        });
        _joinModal.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _joinModal)
                CloseJoinModal();
        });

        // Filter buttons
        _filterActive = root.Q<Button>("filter-active");
        _filterCompleted = root.Q<Button>("filter-completed");
        _filterActive.clicked += () => SetFilter("active");
        _filterCompleted.clicked += () => SetFilter("completed");

        // Delete confirm modal
        _deleteModal = new ConfirmModal(
            root.Q("delete-lobby-modal"),
            "Delete this lobby?",
            "Delete",
            "Cancel",
            isDanger: true
        );
        _deleteModal.Confirmed += OnDeleteConfirm;
        _deleteModal.Cancelled += () => _deleteModal.Hide();

        // Narrow layout detection
        _hubRoot.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);

        // Deep-link prefill
        var pending = GameSettings.ConsumePendingLobbyCode();
        if (!string.IsNullOrEmpty(pending))
        {
            OpenJoinModal();
            _joinCodeField.Value = pending;
        }
        else
        {
            RefreshListAsync();
        }
    }

    protected override void BuildNavGraph(FocusNavigator nav)
    {
        var items = new List<FocusNavigator.FocusItem>();

        switch (_state)
        {
            case HubState.List:
                BuildListNavGraph(nav, items);
                break;
            case HubState.HostModal:
                BuildHostModalNavGraph(nav, items);
                break;
            case HubState.JoinModal:
                BuildJoinModalNavGraph(nav, items);
                break;
            case HubState.HostingModal:
                BuildHostingModalNavGraph(nav, items);
                break;
        }
    }

    private void BuildListNavGraph(FocusNavigator nav, List<FocusNavigator.FocusItem> items)
    {
        int backIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("back-hub-btn"),
                OnActivate = () =>
                {
                    SceneNav.Pop();
                    return true;
                },
            }
        );

        int hostIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("host-btn"),
                OnActivate = () =>
                {
                    OpenHostModal();
                    return true;
                },
            }
        );

        int joinIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("join-btn"),
                OnActivate = () =>
                {
                    OpenJoinModal();
                    return true;
                },
            }
        );

        int refreshIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("hub-refresh-btn"),
                OnActivate = () =>
                {
                    RefreshListAsync();
                    return true;
                },
            }
        );

        int fActiveIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _filterActive,
                OnActivate = () =>
                {
                    SetFilter("active");
                    return true;
                },
            }
        );
        int fCompletedIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _filterCompleted,
                OnActivate = () =>
                {
                    SetFilter("completed");
                    return true;
                },
            }
        );

        // Append lobby row focus items. Each row has up to 4 columns:
        // [visibility, copy, main (play/retry), delete]. Missing buttons stay -1.
        // rowIndices[row][col] -> index into `items`, or -1 if absent.
        int rowCount = _rowFocus.Count;
        var rowIndices = new int[rowCount, 4];
        for (int r = 0; r < rowCount; r++)
        {
            var rf = _rowFocus[r];
            rowIndices[r, 0] = AddOpt(items, rf.Visibility);
            rowIndices[r, 1] = AddOpt(items, rf.Copy);
            rowIndices[r, 2] = AddOpt(items, rf.Main);
            rowIndices[r, 3] = AddOpt(items, rf.Delete);
        }

        nav.SetItems(items, hostIdx);

        // Link: back ↔ host ↔ join ↔ refresh
        nav.Link(backIdx, FocusNavigator.NavDir.Down, hostIdx);
        nav.Link(backIdx, FocusNavigator.NavDir.Right, hostIdx);
        nav.LinkBidi(hostIdx, FocusNavigator.NavDir.Right, joinIdx);
        nav.LinkBidi(joinIdx, FocusNavigator.NavDir.Right, refreshIdx);
        nav.Link(hostIdx, FocusNavigator.NavDir.Up, backIdx);
        nav.Link(joinIdx, FocusNavigator.NavDir.Up, backIdx);

        // Link: toolbar ↔ filter row
        nav.Link(hostIdx, FocusNavigator.NavDir.Down, fActiveIdx);
        nav.Link(joinIdx, FocusNavigator.NavDir.Down, fActiveIdx);
        nav.Link(refreshIdx, FocusNavigator.NavDir.Down, fCompletedIdx);
        nav.LinkBidi(fActiveIdx, FocusNavigator.NavDir.Right, fCompletedIdx);
        nav.Link(fActiveIdx, FocusNavigator.NavDir.Up, hostIdx);
        nav.Link(fCompletedIdx, FocusNavigator.NavDir.Up, refreshIdx);

        // Link row grid: filter row → first row's main (falling back to any
        // present column); horizontal between columns within a row;
        // vertical stays in same column across rows.
        if (rowCount > 0)
        {
            int firstMain = FirstNonNeg(rowIndices, 0, preferred: 2);
            if (firstMain >= 0)
            {
                nav.Link(fActiveIdx, FocusNavigator.NavDir.Down, firstMain);
                nav.Link(fCompletedIdx, FocusNavigator.NavDir.Down, firstMain);
            }

            for (int r = 0; r < rowCount; r++)
            {
                // Horizontal chain within row: skip absent columns.
                int prev = -1;
                for (int c = 0; c < 4; c++)
                {
                    int cur = rowIndices[r, c];
                    if (cur < 0)
                        continue;
                    if (prev >= 0)
                        nav.LinkBidi(prev, FocusNavigator.NavDir.Right, cur);
                    prev = cur;
                }

                // Vertical: same column to the next row (or next present column as fallback).
                for (int c = 0; c < 4; c++)
                {
                    int cur = rowIndices[r, c];
                    if (cur < 0)
                        continue;

                    // Up: previous row's same column, or nearest present.
                    if (r == 0)
                    {
                        // Top row goes up to the filter row.
                        nav.Link(cur, FocusNavigator.NavDir.Up, fActiveIdx);
                    }
                    else
                    {
                        int up = NearestColumn(rowIndices, r - 1, c);
                        if (up >= 0)
                            nav.Link(cur, FocusNavigator.NavDir.Up, up);
                    }

                    // Down: next row's same column, or nearest present.
                    if (r < rowCount - 1)
                    {
                        int down = NearestColumn(rowIndices, r + 1, c);
                        if (down >= 0)
                            nav.Link(cur, FocusNavigator.NavDir.Down, down);
                    }
                }
            }
        }
    }

    private static int AddOpt(List<FocusNavigator.FocusItem> items, FocusNavigator.FocusItem? item)
    {
        if (!item.HasValue)
            return -1;
        int idx = items.Count;
        items.Add(item.Value);
        return idx;
    }

    private static int FirstNonNeg(int[,] indices, int row, int preferred)
    {
        // Try preferred column first, then fall back to any present column.
        if (indices[row, preferred] >= 0)
            return indices[row, preferred];
        for (int c = 0; c < 4; c++)
            if (indices[row, c] >= 0)
                return indices[row, c];
        return -1;
    }

    private static int NearestColumn(int[,] indices, int row, int col)
    {
        // Prefer the same column; fall back to nearest present column in the row.
        if (indices[row, col] >= 0)
            return indices[row, col];
        for (int offset = 1; offset < 4; offset++)
        {
            if (col - offset >= 0 && indices[row, col - offset] >= 0)
                return indices[row, col - offset];
            if (col + offset < 4 && indices[row, col + offset] >= 0)
                return indices[row, col + offset];
        }
        return -1;
    }

    private void BuildHostModalNavGraph(FocusNavigator nav, List<FocusNavigator.FocusItem> items)
    {
        items.Add(MakeTextFieldFocusItem(_hostNameField, OnHostSubmit));

        int widthIdx = items.Count;
        items.Add(MakeSliderFocusItem(_hostWidthSlider));
        int heightIdx = items.Count;
        items.Add(MakeSliderFocusItem(_hostHeightSlider));

        int submitIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("host-submit-btn"),
                OnActivate = () =>
                {
                    OnHostSubmit();
                    return true;
                },
            }
        );
        int cancelIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("host-cancel-btn"),
                OnActivate = () =>
                {
                    CloseHostModal();
                    return true;
                },
            }
        );

        nav.SetItems(items, 0);
        nav.LinkChain(0, items.Count);
        nav.LinkBidi(submitIdx, FocusNavigator.NavDir.Right, cancelIdx);
    }

    private static FocusNavigator.FocusItem MakeTextFieldFocusItem(
        LabeledField field,
        System.Action onSubmit
    )
    {
        return new FocusNavigator.FocusItem
        {
            Element = field.Input,
            OnActivate =
                onSubmit != null
                    ? () =>
                    {
                        onSubmit();
                        return true;
                    }
                    : (Func<bool>)null,
            OnFocused = () => field.ActivateFromKeyboard(),
            OnBlurred = () =>
            {
                field.Input.Blur();
                if (KeybindManager.Instance != null)
                    KeybindManager.Instance.TextFieldFocused = false;
            },
        };
    }

    private static FocusNavigator.FocusItem MakeSliderFocusItem(SnapSlider slider)
    {
        return new FocusNavigator.FocusItem
        {
            Element = slider.Track,
            CustomFocusVisual = true,
            OnHorizontal = dir =>
            {
                slider.KeyboardStep(dir, false);
                return true;
            },
        };
    }

    private void BuildJoinModalNavGraph(FocusNavigator nav, List<FocusNavigator.FocusItem> items)
    {
        items.Add(MakeTextFieldFocusItem(_joinCodeField, OnJoinSubmit));
        int submitIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("join-submit-btn"),
                OnActivate = () =>
                {
                    OnJoinSubmit();
                    return true;
                },
            }
        );
        int cancelIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("join-cancel-btn"),
                OnActivate = () =>
                {
                    CloseJoinModal();
                    return true;
                },
            }
        );

        nav.SetItems(items, 0);
        nav.LinkChain(0, items.Count);
        nav.LinkBidi(submitIdx, FocusNavigator.NavDir.Right, cancelIdx);
    }

    private void BuildHostingModalNavGraph(FocusNavigator nav, List<FocusNavigator.FocusItem> items)
    {
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("hosting-cancel-btn"),
                OnActivate = () =>
                {
                    CloseHostingModal();
                    return true;
                },
            }
        );

        nav.SetItems(items, 0);
    }

    protected override void OnCancel()
    {
        switch (_state)
        {
            case HubState.HostModal:
                CloseHostModal();
                break;
            case HubState.JoinModal:
                CloseJoinModal();
                break;
            case HubState.HostingModal:
                CloseHostingModal();
                break;
            case HubState.List:
                SceneNav.Pop();
                break;
        }
    }

    protected override void Update()
    {
        base.Update();
        _hostingClient?.Update();
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        DisposeHostingClient();
    }

    // -- State management -------------------------------------------------------

    private void SetState(HubState state)
    {
        _state = state;
        RebuildNavigator();
    }

    // -- Modal open/close -------------------------------------------------------

    private void OpenHostModal()
    {
        _hostNameField.Value = "";
        SetVisible(_hostError, false);
        _hostModal.RemoveFromClassList("modal--hidden");
        SetState(HubState.HostModal);
    }

    private void CloseHostModal()
    {
        _hostModal.AddToClassList("modal--hidden");
        SetState(HubState.List);
    }

    private void OpenJoinModal()
    {
        _joinCodeField.Value = "";
        SetVisible(_joinError, false);
        _joinModal.RemoveFromClassList("modal--hidden");
        SetState(HubState.JoinModal);
    }

    private void CloseJoinModal()
    {
        _joinModal.AddToClassList("modal--hidden");
        SetState(HubState.List);
    }

    private void OpenHostingModal(string code)
    {
        _hostingCode = code;
        _progressPct.text = "Preparing...";
        _progressFill.style.width = new StyleLength(new Length(0, LengthUnit.Percent));
        _hostingLabel.text = "Generating your lobby...";
        _hostingModal.RemoveFromClassList("modal--hidden");
        SetState(HubState.HostingModal);
    }

    private void CloseHostingModal()
    {
        _hostingModal.AddToClassList("modal--hidden");
        DisposeHostingClient();
        SetState(HubState.List);
        RefreshListAsync();
    }

    // -- Filter -----------------------------------------------------------------

    private void SetFilter(string filter)
    {
        _currentFilter = filter;
        UpdateFilterHighlights();
        RefreshListAsync();
    }

    private void UpdateFilterHighlights()
    {
        SetFilterActive(_filterActive, _currentFilter == "active");
        SetFilterActive(_filterCompleted, _currentFilter == "completed");
    }

    private static void SetFilterActive(Button btn, bool active)
    {
        if (active)
            btn.AddToClassList("filter-row__btn--active");
        else
            btn.RemoveFromClassList("filter-row__btn--active");
    }

    // -- List refresh -----------------------------------------------------------

    private async void RefreshListAsync()
    {
        if (_api == null || !_api.IsLoggedIn)
        {
            _hubList.Clear();
            _rowFocus.Clear();
            SetVisible(_hubEmpty, true);
            _hubEmpty.text = "Log in to see your co-op lobbies.";
            if (_state == HubState.List)
                RebuildNavigator(preserveFocus: true);
            return;
        }

        var result = await _api.ListMyLobbiesAsync(_currentFilter);
        if (!result.Success)
        {
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError(result.Error ?? "Couldn't load lobbies");
            return;
        }

        _hubList.Clear();
        _rowFocus.Clear();
        var entries = result.Data?.entries;
        if (entries == null || entries.Length == 0)
        {
            SetVisible(_hubEmpty, true);
        }
        else
        {
            SetVisible(_hubEmpty, false);
            foreach (var entry in entries)
                _hubList.Add(BuildRow(entry));
        }

        // Rebuild the nav graph so the new rows are reachable with the keyboard.
        if (_state == HubState.List)
            RebuildNavigator(preserveFocus: true);
    }

    private VisualElement BuildRow(LobbyListEntryDto entry)
    {
        var row = new VisualElement();
        row.AddToClassList("lobby-row");

        // Status class
        string statusClass = entry.status switch
        {
            0 => "lobby-row--generating",
            1 => "lobby-row--active",
            2 => "lobby-row--completed",
            3 => "lobby-row--failed",
            _ => "",
        };
        if (!string.IsNullOrEmpty(statusClass))
            row.AddToClassList(statusClass);

        // Status dot
        var dot = new VisualElement();
        dot.AddToClassList("lobby-row__status-dot");
        row.Add(dot);

        // Main content
        var main = new VisualElement();
        main.AddToClassList("lobby-row__main");

        if (entry.youAreOwner)
        {
            var nameEdit = new EditableLabel();
            nameEdit.Value = entry.name;
            nameEdit.Root.AddToClassList("lobby-row__name");
            nameEdit.OnCommit += newName => OnRenameLobby(entry.id, newName, nameEdit);
            main.Add(nameEdit.Root);
        }
        else
        {
            var nameLabel = new Label(entry.name);
            nameLabel.AddToClassList("lobby-row__name");
            main.Add(nameLabel);
        }

        var metaRow = new VisualElement();
        metaRow.AddToClassList("lobby-row__meta-row");

        // Code group: hidden/shown toggle + text + copy button
        var codeGroup = new VisualElement();
        codeGroup.AddToClassList("lobby-row__code-group");

        bool codeHidden = true;
        var codeLabel = new Label(MaskCode(entry.code));
        codeLabel.AddToClassList("lobby-row__code");
        codeGroup.Add(codeLabel);

        var toggleBtn = new Button();
        toggleBtn.AddToClassList("lobby-row__code-toggle");
        var toggleIcon = new VisualElement();
        toggleIcon.AddToClassList("hud-icon-btn__icon");
        toggleIcon.AddToClassList("icon--visible-off");
        toggleBtn.Add(toggleIcon);
        System.Action toggleAction = () =>
        {
            codeHidden = !codeHidden;
            codeLabel.text = codeHidden ? MaskCode(entry.code) : entry.code;
            toggleIcon.RemoveFromClassList("icon--visible-on");
            toggleIcon.RemoveFromClassList("icon--visible-off");
            toggleIcon.AddToClassList(codeHidden ? "icon--visible-off" : "icon--visible-on");
        };
        toggleBtn.clicked += toggleAction;
        codeGroup.Add(toggleBtn);

        var copyBtn = new Button();
        copyBtn.AddToClassList("lobby-row__code-copy");
        var copyIcon = new VisualElement();
        copyIcon.AddToClassList("hud-icon-btn__icon");
        copyIcon.AddToClassList("icon--copy");
        copyBtn.Add(copyIcon);
        System.Action copyAction = () =>
        {
            var url = !string.IsNullOrEmpty(entry.shareUrl)
                ? entry.shareUrl
                : $"https://arrow-thing.com/?lobby={entry.code}";
            Clipboard.WriteText(url);
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowInfo("Lobby link copied");
        };
        copyBtn.clicked += copyAction;
        codeGroup.Add(copyBtn);

        metaRow.Add(codeGroup);

        var dimLabel = new Label($"{entry.width}x{entry.height}");
        dimLabel.AddToClassList("lobby-row__dim");
        metaRow.Add(dimLabel);

        var ownerLabel = new Label($"by {entry.ownerDisplayName}");
        ownerLabel.AddToClassList("lobby-row__owner");
        metaRow.Add(ownerLabel);

        main.Add(metaRow);

        // Activity timestamp
        var activityLabel = new Label(FormatTimeAgo(entry.lastActivityAt));
        activityLabel.AddToClassList("lobby-row__activity");
        main.Add(activityLabel);

        row.Add(main);

        // Main action: Retry (owner + failed) OR Play (active/generating).
        Button mainBtn = null;
        if (entry.youAreOwner && entry.status == 3)
        {
            mainBtn = new Button(() => OnRetryGeneration(entry.id)) { text = "Retry" };
            mainBtn.AddToClassList("lobby-row__retry-btn");
            row.Add(mainBtn);
        }
        else if (entry.status == 0 || entry.status == 1)
        {
            mainBtn = new Button(() => OnOpenLobby(entry.code, entry.status)) { text = "Play" };
            mainBtn.AddToClassList("lobby-row__open-btn");
            row.Add(mainBtn);
        }
        else if (entry.status == 2) // Completed → view results
        {
            mainBtn = new Button(() => OnOpenLobby(entry.code, entry.status)) { text = "Results" };
            mainBtn.AddToClassList("lobby-row__open-btn");
            row.Add(mainBtn);
        }

        // Delete button (owner only, non-completed)
        Button deleteBtn = null;
        if (entry.youAreOwner && entry.status != 2)
        {
            deleteBtn = new Button(() => OnDeletePressed(entry.id, entry.name)) { text = "Delete" };
            deleteBtn.AddToClassList("lobby-row__delete-btn");
            row.Add(deleteBtn);
        }

        // Register row's focus grid: [visibility, copy, main, delete].
        var capturedCode = entry.code;
        var capturedStatus = entry.status;
        var capturedId = entry.id;
        var capturedName = entry.name;
        _rowFocus.Add(
            new RowFocus
            {
                Visibility = new FocusNavigator.FocusItem
                {
                    Element = toggleBtn,
                    OnActivate = () =>
                    {
                        toggleAction();
                        return true;
                    },
                },
                Copy = new FocusNavigator.FocusItem
                {
                    Element = copyBtn,
                    OnActivate = () =>
                    {
                        copyAction();
                        return true;
                    },
                },
                Main =
                    mainBtn == null
                        ? (FocusNavigator.FocusItem?)null
                        : new FocusNavigator.FocusItem
                        {
                            Element = mainBtn,
                            OnActivate = () =>
                            {
                                if (capturedStatus == 3)
                                    OnRetryGeneration(capturedId);
                                else
                                    OnOpenLobby(capturedCode, capturedStatus);
                                return true;
                            },
                        },
                Delete =
                    deleteBtn == null
                        ? (FocusNavigator.FocusItem?)null
                        : new FocusNavigator.FocusItem
                        {
                            Element = deleteBtn,
                            OnActivate = () =>
                            {
                                OnDeletePressed(capturedId, capturedName);
                                return true;
                            },
                        },
            }
        );

        return row;
    }

    // -- Host flow --------------------------------------------------------------

    private async void OnHostSubmit()
    {
        var name = _hostNameField.Value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 40)
        {
            ShowError(_hostError, "Name must be 1-40 characters.");
            return;
        }

        if (!_api.IsLoggedIn)
        {
            ShowError(_hostError, "Log in first to host a lobby.");
            return;
        }

        int width = Mathf.RoundToInt(_hostWidthSlider.Value);
        int height = Mathf.RoundToInt(_hostHeightSlider.Value);
        var result = await _api.CreateLobbyAsync(name, width, height);
        if (!result.Success)
        {
            ShowError(_hostError, result.Error ?? "Failed to create lobby.");
            return;
        }

        _hostModal.AddToClassList("modal--hidden");
        OpenHostingModal(result.Data.code);

        // Connect WS to watch generation progress
        _hostingClient = new CoopClient();
        _hostingClient.MessageReceived += OnHostingMessage;
        _hostingClient.BinaryReceived += OnHostingBinary;
        _hostingClient.Disconnected += OnHostingDisconnected;

        try
        {
            await _api.EnsureFreshTokenAsync();
            await _hostingClient.ConnectAsync(_api.BaseWsUrl, result.Data.code, _api.Token);
            await _hostingClient.SendAsync(CoopMessage.Hello(0));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoopHub] WS connect failed: {e.Message}");
            CloseHostingModal();
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError(
                    "Connection failed. Try opening the lobby from the list."
                );
        }
    }

    private void OnHostingMessage(CoopMessage msg)
    {
        switch (msg.Type)
        {
            case "welcome":
                // Acknowledged — waiting for gen_progress or gen_complete.
                break;
            case "gen_progress":
                var pct = msg.Payload?.Value<int>("pct") ?? 0;
                _progressPct.text = $"{pct}%";
                _progressFill.style.width = new StyleLength(new Length(pct, LengthUnit.Percent));
                break;
            case "gen_complete":
                // Snapshot will follow as a binary frame after a snapshot meta JSON.
                _progressPct.text = "Board ready!";
                _progressFill.style.width = new StyleLength(new Length(100, LengthUnit.Percent));
                break;
            case "snapshot":
                // Binary frame with the snapshot follows — wait for BinaryReceived.
                break;
            case "lobby_failed":
                _hostingLabel.text = "Generation failed";
                _progressPct.text = "The server couldn't generate this board.";
                break;
            case "disconnect":
                CloseHostingModal();
                break;
        }
    }

    private void OnHostingBinary(byte[] data)
    {
        // Snapshot received — transition to Coop Game scene.
        GameSettings.ActiveLobbyCode = _hostingCode;
        DisposeHostingClient();
        _hostingModal.AddToClassList("modal--hidden");
        SceneNav.Push("Game");
    }

    private void OnHostingDisconnected(string reason)
    {
        if (_state == HubState.HostingModal)
        {
            Debug.Log($"[CoopHub] Hosting WS disconnected: {reason}");
            // Don't close the modal — the user might want to retry.
        }
    }

    // -- Join flow --------------------------------------------------------------

    private async void OnJoinSubmit()
    {
        var code = ExtractLobbyCode(_joinCodeField.Value);
        if (string.IsNullOrEmpty(code) || code.Length != 6)
        {
            ShowError(_joinError, "Enter a 6-character lobby code.");
            return;
        }

        if (!_api.IsLoggedIn)
        {
            ShowError(_joinError, "Log in first to join a lobby.");
            return;
        }

        var result = await _api.GetLobbyAsync(code);
        if (!result.Success)
        {
            ShowError(_joinError, result.Error ?? "Lobby not found.");
            return;
        }

        _joinModal.AddToClassList("modal--hidden");

        short status = result.Data.status;
        if (status == 1) // Active
        {
            GameSettings.ActiveLobbyCode = code;
            SceneNav.Push("Game");
        }
        else if (status == 0) // Generating — wait for it
        {
            OpenHostingModal(code);
            _hostingClient = new CoopClient();
            _hostingClient.MessageReceived += OnHostingMessage;
            _hostingClient.BinaryReceived += OnHostingBinary;
            _hostingClient.Disconnected += OnHostingDisconnected;

            try
            {
                await _api.EnsureFreshTokenAsync();
                await _hostingClient.ConnectAsync(_api.BaseWsUrl, code, _api.Token);
                await _hostingClient.SendAsync(CoopMessage.Hello(0));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CoopHub] WS connect failed: {e.Message}");
                CloseHostingModal();
                if (GlobalToast.Instance != null)
                    GlobalToast.Instance.ShowError("Connection failed.");
            }
        }
        else if (status == 2) // Completed — show final results
        {
            GameSettings.ActiveLobbyCode = code;
            GameSettings.IsCompletedLobbyView = true;
            SceneNav.Push("Game");
        }
        else if (status == 3) // GenerationFailed
        {
            ShowError(_joinError, "This lobby's board generation failed.");
            _joinModal.RemoveFromClassList("modal--hidden");
        }
        else
        {
            ShowError(_joinError, "This lobby is no longer available.");
            _joinModal.RemoveFromClassList("modal--hidden");
        }
    }

    private void OnOpenLobby(string code, short status)
    {
        if (status == 1) // Active
        {
            GameSettings.ActiveLobbyCode = code;
            SceneNav.Push("Game");
        }
        else if (status == 0) // Generating
        {
            OpenHostingModal(code);
            _ = ConnectHostingClientAsync(code);
        }
        else if (status == 2) // Completed — show results from persisted replay
        {
            GameSettings.ActiveLobbyCode = code;
            GameSettings.IsCompletedLobbyView = true;
            SceneNav.Push("Game");
        }
    }

    private async Task ConnectHostingClientAsync(string code)
    {
        _hostingClient = new CoopClient();
        _hostingClient.MessageReceived += OnHostingMessage;
        _hostingClient.BinaryReceived += OnHostingBinary;
        _hostingClient.Disconnected += OnHostingDisconnected;
        try
        {
            await _api.EnsureFreshTokenAsync();
            await _hostingClient.ConnectAsync(_api.BaseWsUrl, code, _api.Token);
            await _hostingClient.SendAsync(CoopMessage.Hello(0));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoopHub] WS connect failed: {e.Message}");
            CloseHostingModal();
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError("Connection failed.");
        }
    }

    // -- Delete flow ------------------------------------------------------------

    private void OnDeletePressed(string lobbyId, string lobbyName)
    {
        _pendingDeleteId = lobbyId;
        _deleteModal.Reconfigure(
            $"Delete \"{lobbyName}\"?",
            "Delete",
            "Cancel",
            "This cannot be undone.",
            isDismissable: false,
            isDanger: true
        );
        // ConfirmModal.Show() pushes its own modal onto FocusNavigator.Active;
        // the hub's state stays List so we don't rebuild the nav and drop the push.
        _deleteModal.Show();
    }

    private async void OnDeleteConfirm()
    {
        _deleteModal.Hide();
        if (!string.IsNullOrEmpty(_pendingDeleteId))
        {
            var result = await _api.DeleteLobbyAsync(_pendingDeleteId);
            if (!result.Success && GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError(result.Error ?? "Couldn't delete lobby.");
        }
        _pendingDeleteId = null;
        RefreshListAsync();
    }

    // -- Rename -----------------------------------------------------------------

    private async void OnRenameLobby(string lobbyId, string newName, EditableLabel label)
    {
        var trimmed = newName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 40)
        {
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError("Name must be 1-40 characters.");
            return;
        }

        var result = await _api.RenameLobbyAsync(lobbyId, trimmed);
        if (result.Success)
        {
            label.Value = trimmed;
        }
        else
        {
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError(result.Error ?? "Couldn't rename lobby.");
        }
    }

    // -- Retry generation -------------------------------------------------------

    private async void OnRetryGeneration(string lobbyId)
    {
        var result = await _api.RetryGenerationAsync(lobbyId);
        if (result.Success)
        {
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowInfo("Generation re-started.");
            RefreshListAsync();
        }
        else
        {
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError(result.Error ?? "Retry failed.");
        }
    }

    // -- Narrow layout ----------------------------------------------------------

    private void OnGeometryChanged(GeometryChangedEvent evt)
    {
        if (evt.newRect.width < 768f)
            _hubRoot.AddToClassList("hub--narrow");
        else
            _hubRoot.RemoveFromClassList("hub--narrow");
    }

    // -- Helpers ----------------------------------------------------------------

    private void DisposeHostingClient()
    {
        if (_hostingClient == null)
            return;
        _hostingClient.MessageReceived -= OnHostingMessage;
        _hostingClient.BinaryReceived -= OnHostingBinary;
        _hostingClient.Disconnected -= OnHostingDisconnected;
        _hostingClient.Dispose();
        _hostingClient = null;
    }

    private static void SetVisible(VisualElement el, bool visible)
    {
        if (visible)
            el.RemoveFromClassList("screen--hidden");
        else
            el.AddToClassList("screen--hidden");
    }

    private static void SetVisible(Label el, bool visible) =>
        SetVisible((VisualElement)el, visible);

    private static void ShowError(Label label, string message)
    {
        label.text = message;
        SetVisible(label, true);
    }

    private static string MaskCode(string code) =>
        string.IsNullOrEmpty(code) ? "" : new string('\u2022', code.Length);

    /// <summary>
    /// Extracts a 6-character lobby code from raw user input. Handles both
    /// plain codes ("A7FK92") and full share URLs ("https://arrow-thing.com/?lobby=A7FK92").
    /// Returns the uppercase code, or the trimmed input if no URL match was found.
    /// </summary>
    private static string ExtractLobbyCode(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";
        var trimmed = input.Trim();

        // Look for ?lobby= or &lobby= pattern.
        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"[?&]lobby=([A-Za-z0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (match.Success)
            return match.Groups[1].Value.ToUpperInvariant();

        return trimmed.ToUpperInvariant();
    }

    private static string FormatTimeAgo(string isoTimestamp)
    {
        if (string.IsNullOrEmpty(isoTimestamp))
            return "";
        if (
            !DateTime.TryParse(
                isoTimestamp,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dt
            )
        )
            return "";
        var delta = DateTime.UtcNow - dt.ToUniversalTime();
        if (delta.TotalMinutes < 1)
            return "just now";
        if (delta.TotalHours < 1)
            return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1)
            return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30)
            return $"{(int)delta.TotalDays}d ago";
        return dt.ToString("MMM d, yyyy");
    }
}
