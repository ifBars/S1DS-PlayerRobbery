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
- `local.build.props`, `bin/`, `obj/`, and IDE files are intentionally ignored.
