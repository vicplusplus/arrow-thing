# Arrow Thing

A minimalist speed-clearing puzzle game built in Unity. Clear winding arrows from a grid as fast as you can — an arrow is clearable only when nothing blocks its forward ray. Compete on global leaderboards and watch replays of top solves.

Free to play at **https://arrow-thing.com/**

## Project Status

- Live on WebGL (Cloudflare Pages)
- Solo play with multiple board sizes
- Global leaderboards and replay viewer
- Accounts, personal bests, and score history
- Design docs:
  - [`docs/GDD.md`](docs/GDD.md) (game design)
  - [`docs/TechnicalDesign.md`](docs/TechnicalDesign.md) (technical architecture and class structure)
  - [`docs/BoardGeneration.md`](docs/BoardGeneration.md) (board generation algorithm)
  - [`docs/AndroidTesting.md`](docs/AndroidTesting.md) (Android testing guide)
  - [`server/docs/`](server/docs/) (server setup, rotation, and operations)

## Tech Stack

- Unity `6000.3.8f1`
- C# domain logic under `Assets/Scripts/Domain` (cross-platform; deterministic via `PortableRandom`)
- ASP.NET Core server (`.NET 10`) under `server/` — shared domain code via monorepo, plus a standalone verification worker (`ArrowThing.Worker`) consuming a Redis queue
- PostgreSQL + Redis behind Nginx + Cloudflare; Serilog/OpenTelemetry/Grafana observability stack
- NUnit tests via Unity Test Framework in `Assets/Tests/EditMode` and `Assets/Tests/PlayMode`
- xUnit server integration tests in `server/ArrowThing.Server.Tests`

## Local Development

1. Open this folder in Unity Hub using editor version `6000.3.8f1`.
2. Open the `Game` scene under `Assets/Scenes`.
3. Run tests via Unity's **Test Runner** window (Window > General > Test Runner, EditMode tab).
4. Install tools and hooks:

```bash
dotnet tool restore
git config core.hooksPath .githooks
```

The pre-commit hook runs:
- [CSharpier](https://csharpier.com/) formatting check on staged `.cs` files
- File size gate (rejects files >= 100 MB)
- Asset `.meta` file sync

The post-merge hook removes empty directories to prevent orphan `.meta` files.

To auto-fix formatting: `dotnet csharpier format Assets/Scripts/ Assets/Tests/`

5. (Optional) Set up Unity SmartMerge for better YAML conflict resolution:

```bash
git config merge.unityyamlmerge.driver '<path-to-Unity>/Editor/Data/Tools/UnityYAMLMerge merge -p %O %A %B %P'
```

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for expectations around architecture, tests, and coverage standards.

## License

This project is licensed under the [MIT License](LICENSE).

## Acknowledgements

Git configuration (`.gitattributes`, `.gitignore`, git hooks) is based on [NYU Game Center's Unity-Git-Config](https://github.com/NYUGameCenter/Unity-Git-Config) — a great open resource for Unity project setup.

## Repository Layout

- `Assets/Scripts/Domain` - Core board/arrow domain logic
- `Assets/Scripts/View` - Unity rendering, input, UI
- `Assets/Tests/EditMode` - Unit tests (Unity Test Framework)
- `Assets/Tests/PlayMode` - PlayMode tests (UI layout, API client)
- `server/` - ASP.NET Core server (auth, shared domain code)
- `server/docs/` - Server setup, rotation, and operations docs
- `docs/GDD.md` - Game design direction and scope
- `docs/TechnicalDesign.md` - Architecture and class-structure decisions
- `docs/BoardGeneration.md` - Board generation algorithm
- `docs/AndroidTesting.md` - Android testing guide
