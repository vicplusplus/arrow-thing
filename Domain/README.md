# Arrow Thing — Domain library

Unity-independent domain layer for Arrow Thing. Pure C# game logic
(`netstandard2.1`, `LangVersion 9`) shared by both the Unity client and the
.NET server. No `UnityEngine`, no `Unity.*`, no `Microsoft.Extensions.*`.

## Layout

Domain `.cs` files live in the Unity local package; everything else references them from there:

```
Packages/com.arrowthing.domain/
├── package.json
└── Runtime/
    ├── ArrowThing.Domain.asmdef        (noEngineReferences: true)
    ├── *.cs
    ├── Generation/
    └── Models/
```

This standalone .NET project lives at:

```
Domain/
├── ArrowThing.Domain.csproj
└── README.md   (this file)
```

`ArrowThing.Domain.csproj` globs `../Packages/com.arrowthing.domain/Runtime/**/*.cs`
so there is exactly one copy of every domain source file. Unity discovers the
sources through the package's `.asmdef` (which can only see files in its own
folder hierarchy); the server reaches into the package via the csproj glob.

## Building standalone

```sh
dotnet build Domain/ArrowThing.Domain.csproj
```

## Building from the server solution

```sh
dotnet build server/ArrowThing.sln
```

Both `ArrowThing.Server` and `ArrowThing.Server.Tests` reference
`../../Domain/ArrowThing.Domain.csproj`.

## Why sources sit in `Packages/com.arrowthing.domain/Runtime/`

Unity assembly definitions are folder-scoped: an `.asmdef` cannot include
sources from outside its own subtree. Putting the sources at the repo-root
`Domain/` directory and trying to glob them from the asmdef does not work,
and symlinks were ruled out (Windows CI runners choke on them). Co-locating
the sources with the asmdef is the only no-duplication layout that works
without symlinks; the standalone csproj at `Domain/` reaches in.

## Constraints / forbidden references

CI fails if the domain sources contain any of:

- `using UnityEngine` (or `UnityEngine.<X>`)
- `using Unity.<anything>` (e.g. `Unity.Collections`)
- `using Microsoft.Extensions.<anything>`

`Newtonsoft.Json` is allowed (it is the only NuGet dependency).

See `.github/workflows/domain-ci.yml`.

## Maintainer steps after pulling this change

The first time a maintainer opens the Unity project after this restructure,
Unity will:

1. Detect the new local package `com.arrowthing.domain` referenced by
   `Packages/manifest.json` and resolve it.
2. Regenerate `Packages/packages-lock.json`.
3. Recompile the `ArrowThing` assembly against the new
   `ArrowThing.Domain` assembly.

If the editor reports missing-script errors after the pull, close and reopen
the project once so Unity refreshes its package cache and asmdef graph.
There are no manual inspector assignments to make — `.meta` GUIDs were
preserved during the move, so all scene/prefab references continue to work.
