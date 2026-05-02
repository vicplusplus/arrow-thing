# Arrow Thing

A minimalist speed-clearing puzzle game built in Unity. Clear winding arrows from a grid as fast as you can — an arrow is clearable only when nothing blocks its forward ray. Compete on global leaderboards and watch replays of top solves.

Free to play at **https://arrow-thing.com/**

## Project Status

- Live on WebGL (Cloudflare Pages)
- Solo play with multiple board sizes
- Global leaderboards and replay viewer
- Accounts, personal bests, and score history
- Docs: [`docs/INDEX.md`](docs/INDEX.md) — start here.

## Tech Stack

- Unity `6000.3.8f1`
- C# domain logic in the local Unity package `Packages/com.arrowthing.domain/Runtime/`, with a standalone .NET project at `Domain/ArrowThing.Domain.csproj` (cross-platform; deterministic via `PortableRandom`; no `UnityEngine` references)
- ASP.NET Core server (`.NET 10`) under `server/` — references the standalone domain project, plus a standalone verification worker (`ArrowThing.Worker`) consuming a Redis queue
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

- `Packages/com.arrowthing.domain/Runtime/` - Core board/arrow domain logic (Unity-independent; canonical sources, the server references them from here)
- `Domain/ArrowThing.Domain.csproj` - Standalone .NET project that wraps the domain sources for the server and CI
- `Assets/Scripts/View` - Unity rendering, input, UI
- `Assets/Tests/EditMode` - Unit tests (Unity Test Framework)
- `Assets/Tests/PlayMode` - PlayMode tests (UI layout, API client)
- `server/` - ASP.NET Core server (auth, scoring); references the standalone domain project
- `docs/` - Game-side docs (architecture, generation, testing, releases). See [`docs/INDEX.md`](docs/INDEX.md).
- `server/docs/` - Server-side docs (setup, rotation, operations).
