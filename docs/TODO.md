# Phase 0 — Unicode Font Coverage

## Context

Co-op lobbies need display names and lobby names to render correctly across scripts (Latin, CJK, Arabic, Hebrew, Thai, Devanagari, Emoji). Currently the project has **no font configuration** — all three `PanelSettings` assets have `textSettings: {fileID: 0}`, meaning Unity's built-in default font is used everywhere. Non-Latin characters render as tofu.

## Design

### Approach: PanelTextSettings + Font Asset fallback chain

The co-op roadmap proposed a `Fonts.uss` with `-unity-font-definition`. In Unity 6, `PanelTextSettings` is cleaner — it provides a **global default font** for all UI Toolkit text without per-UXML stylesheet includes. The Font Asset's built-in fallback list handles script coverage.

**Why PanelTextSettings over USS:**
- Single point of configuration (3 PanelSettings assignments vs. editing every UXML)
- Font Asset fallback chain is the standard Unity 6 mechanism for multi-script coverage
- USS `-unity-font-definition` can only reference one font — no fallback syntax
- Theme USS files remain font-agnostic (they set colors, not typefaces)

### Font selection

All fonts are SIL OFL 1.1, MIT-compatible.

| Font file | Script coverage | Role |
|-----------|----------------|------|
| `NotoSans-Regular.ttf` | Latin, Greek, Cyrillic | Primary |
| `NotoSans-Bold.ttf` | Latin, Greek, Cyrillic | Primary bold |
| `NotoSansJP-Regular.otf` | CJK (JP subset) | Fallback 1 |
| `NotoSansArabic-Regular.ttf` | Arabic | Fallback 2 |
| `NotoSansHebrew-Regular.ttf` | Hebrew | Fallback 3 |
| `NotoSansThai-Regular.ttf` | Thai | Fallback 4 |
| `NotoSansDevanagari-Regular.ttf` | Devanagari | Fallback 5 |
| `NotoEmoji-Regular.ttf` | Emoji | Fallback 6 |

Bold variants only for the primary font. Fallback scripts appear in display names/lobby names where bold is not critical.

### Configuration flow

1. `.ttf`/`.otf` files committed under `Assets/Fonts/`.
2. **Unity Editor** (manual): create Font Assets from each `.ttf` with **Dynamic** atlas population mode (SDF rendering, runtime-populated atlas — no upfront bake).
3. **Unity Editor** (manual): on the primary `NotoSans-Regular` Font Asset, add the other Font Assets to its **Fallback Font Asset List** in order.
4. **Unity Editor** (manual): create a `PanelTextSettings` asset at `Assets/UI/Shared/TextSettings.asset`. Set its **Default Font Asset** to the primary Noto Sans Font Asset.
5. **Unity Editor** (manual): assign this `TextSettings` to all three `PanelSettings`:
   - `Assets/UI/Shared/PanelSettings.asset`
   - `Assets/UI/Shared/SettingsPanelSettings.asset`
   - `Assets/UI/Shared/GlobalToastPanelSettings.asset`

No USS changes. No UXML changes. No code changes beyond tests.

### What about bold?

UI Toolkit's SDF font rendering supports font-weight switching when bold variants are in the same Font Asset family. The `NotoSans-Bold.ttf` Font Asset should be created and linked as the **Bold Typeface** on the primary Font Asset (in the Font Weights section of the inspector). Existing `--unity-font-style: bold` in USS will then resolve correctly.

## Implementation plan

### Step 1: Download font files
- [ ] Create `Assets/Fonts/` directory
- [ ] Download all 8 font files listed above
- [ ] Add `Assets/Fonts/LICENSE_OFL.txt` (SIL OFL text)

### Step 2: Unity Editor configuration (manual — user does this)
- [ ] Create Font Assets from each `.ttf`/`.otf` (Right-click > Create > Text Core > Font Asset, select Dynamic population mode)
- [ ] On `NotoSans-Regular` Font Asset: set Fallback list to [CJK, Arabic, Hebrew, Thai, Devanagari, Emoji]
- [ ] On `NotoSans-Regular` Font Asset: set Bold Typeface to `NotoSans-Bold` Font Asset
- [ ] Create `PanelTextSettings` at `Assets/UI/Shared/TextSettings.asset`, set Default Font Asset to `NotoSans-Regular`
- [ ] Assign `TextSettings` to all 3 PanelSettings assets
- [ ] Verify in Play mode: all existing text renders in Noto Sans (no visual regression)

### Step 3: Glyph coverage test
- [ ] Add `Assets/Tests/EditMode/FontCoverageTests.cs`
- [ ] Test loads the primary Font Asset from Resources (or by path) and asserts it can resolve representative characters: `A`, `Ω`, `Д`, `山`, `م`, `ש`, `ส`, `न`, `😊`
- [ ] Direct glyph lookup on the Font Asset (TryGetCharacter) for the primary font
- [ ] Fallback resolution verified by checking `HasCharacter` with search-fallbacks flag

### Step 4: Manual testing
- [ ] Register account with Japanese display name → leaderboard renders correctly
- [ ] Register account with Arabic display name → renders RTL correctly
- [ ] Verify all 4 themes (dark, light, dark mono, light mono) render correctly
- [ ] Verify bold text (e.g. modal titles) uses Noto Sans Bold
- [ ] Verify GlobalToast text uses Noto Sans

### Step 5: Cleanup
- [ ] Update `docs/TechnicalDesign.md` with font architecture
- [ ] Update `docs/CoopRoadmap.md` Phase 0 with "Implemented" note
- [ ] Delete this file

## Deviations from roadmap

1. **No `Fonts.uss`** — PanelTextSettings is the Unity 6 standard and avoids touching every UXML. Roadmap predates this decision.
2. **No screenshot test** — the roadmap proposed an editor screenshot test rendering mixed-script labels. Font Asset's `HasCharacter` API is a more reliable and faster test than pixel comparison. Manual visual verification covers rendering.
3. **No CJK Bold** — CJK bold fonts are 15+ MB. Display names don't need bold CJK; the faux-bold from SDF rendering is acceptable.

## Open questions

None — all resolved in this design.
