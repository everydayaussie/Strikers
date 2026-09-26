# Strikers

Online multiplayer for Machine Strike, the board game inside Horizon Forbidden West.

Download it from Nexus Mods: https://www.nexusmods.com/horizonforbiddenwest/mods/209. That page has the instructions to play.

To check a download, run `Get-FileHash Strikers.zip` in PowerShell and compare the hash with the SHA-256 in this repository's tag for that version (Tags, then the three dots beside the version).

## Build it yourself

Needs Windows and the x64 .NET SDK, version 10.0.401 or newer, from https://dotnet.microsoft.com/download/dotnet/10.0 (pick the x64 installer, not x86 or Arm64). From this folder:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The result is `out\Strikers.zip`, the same eight files as the Nexus download.

## Problems

Press Send report in Strikers. It shows on the screen where a match stopped with an error, and Settings has it for any other time. It opens this repository's new issue page and the folder that holds the report. Drag the report into the issue, and say what happened in the game just before it stopped.

## Game memory

Where Machine Strike keeps its information in the game's memory, for modders: [`docs/memory-map.md`](docs/memory-map.md), and the short version as tables in [`docs/memory-tables.md`](docs/memory-tables.md).

## Licence

MIT, in `LICENSE`. The licences of the libraries inside the exes are in `THIRD-PARTY-NOTICES.txt`. Horizon Forbidden West and Machine Strike belong to Guerrilla Games and Sony Interactive Entertainment, and Strikers is not affiliated with either.
