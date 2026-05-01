# Contributing

Keep contributions small, focused, and easy to review.

## Git Hooks

The repository uses `.githooks/` for Git hooks. After cloning, configure Git to use them:

```bash
git config core.hookspath .githooks
```

The pre-commit hook enforces:
- No private key headers in staged files
- No public IPv4 addresses in staged files (use `<vps-ip>` placeholders in docs)
- C# formatting via CSharpier (run `dotnet csharpier format Assets/Scripts/ Assets/Tests/ server/` to fix)
- No files >= 100 MB (use Git LFS for large assets)
- Unity Asset `.meta` file pairing (every added/removed asset must have its `.meta` staged too)

## Core Expectations

- Prefer Unity-independent implementations for game rules, board logic, and generation logic.
- Keep Unity-facing code thin (input/view/adapters), with domain behavior in plain C# classes where possible.
- Add or update tests with every behavior change to Unity-independent classes.
- Add a regression test for every bug fix.

## Testing Standards

- Changes to Unity-independent classes must include NUnit coverage in `Assets/Tests/EditMode/`.
- Run tests via Unity's **Test Runner** window (Window > General > Test Runner, EditMode tab).
- Pull requests should not reduce coverage for touched Unity-independent classes.
- Target at least `90%` line coverage on touched Unity-independent classes, and aim higher for core rules.
- If a class cannot be covered well, document why in the pull request.
- UI changes (UXML/USS) must be reflected in PlayMode layout tests in `Assets/Tests/PlayMode/UILayout/`.
- New buttons added to UXML must be wired into the scene's `FocusNavigator` for keyboard navigation — `NavigationCoverageTests` will catch missing buttons.

## Feature Workflow

- Larger features are tracked as GitHub issues. Open an issue with the design and scope before opening implementation PRs; keep the issue updated as the work evolves.
- Implementation PRs link back to the issue and stay focused on a single coherent slice.

## Docs Consistency

- If you spot a docs inconsistency, either fix it in the same PR or call it out in the PR and open an issue.
- Keep `docs/TechnicalDesign.md` aligned with architecture/class-structure changes.

## Technical Design Document (TDDoc)

- We use "TDD" to mean **Technical Design Document**, not test-driven development.
- `docs/TechnicalDesign.md` is the spec for technical decisions:
  - architecture boundaries
  - core class responsibilities
  - key invariants and data flow
- If your change affects architecture or class structure, update `docs/TechnicalDesign.md` in the same pull request.
