# S1DS-PlayerRobbery

A paired client/server addon for Schedule I dedicated servers that lets players raise their hands and allows nearby players to rob lootable inventory through the game's native pickpocket-style UI.

## Features

- Paired client and server companion addon built on the S1DS API
- Uses S1DS companion metadata so servers can require the matching client addon
- Press `X` on the client to toggle hands-up state when the local player can act
- Uses S1DS custom messaging for authoritative robbery session state
- Reuses the native pickpocket-style screen instead of a separate custom canvas
- Server validates robber and target state before transferring items

## Build

```bash
dotnet build S1DS-PlayerRobbery.csproj -c Mono_Client
dotnet build S1DS-PlayerRobbery.csproj -c Mono_Server
```

Build output lands in `bin/Mono_Client/netstandard2.1/` and `bin/Mono_Server/netstandard2.1/`. If deployment paths are configured, the DLL is also copied to the matching `Mods` folder.

## Test

```bash
dotnet run --project tests/S1DSPlayerRobbery.Tests/S1DSPlayerRobbery.Tests.csproj
```

The test harness validates shared message contracts and release metadata without launching the game. Runtime validation should still use a dedicated server plus client install because robbery behavior depends on live player state, inventory, and the native pickpocket screen.

For the release gate, run:

```powershell
./tests/Run-ReleaseValidation.ps1
```

## Source Layout

- `Client/`: client companion mod, interaction hook, and native pickpocket UI adapter
- `Server/`: server-authoritative robbery validation, session state, and inventory transfer
- `Shared/`: metadata and custom-message DTOs shared by both sides
- `tests/`: pure contract tests for release validation

## Setup

1. Build or install DedicatedServerMod for both client and server.
2. Copy `local.build.props.example` to `local.build.props`.
3. Set `S1DSApiPath` to the `bin` directory from a built DedicatedServerMod checkout.
4. Set `MonoClientGamePath` and `MonoServerGamePath` to your Schedule I client and dedicated server installs.
5. Build both configurations.
6. Install the client DLL on players that should join servers requiring the addon, and install the server DLL on the dedicated server.

When this project is kept next to the `DedicatedServerMod` checkout under the ScheduleOne workspace, it can inherit the root `local.build.props` and default `S1DSApiPath` automatically.

## Notes

- This is a companion addon: the server assembly declares the required client companion metadata.
- The addon currently targets Mono client/server builds.
- Robbery transfer is server-authoritative; the client only presents interaction and inventory UI.
- `local.build.props`, `bin/`, `obj/`, and IDE files are intentionally ignored.
