# Strikers

Online multiplayer for Machine Strike, the board game inside Horizon Forbidden West.

Download it from Nexus Mods: https://www.nexusmods.com/horizonforbiddenwest/mods/209. That page has the instructions to play.

## Build it yourself

Needs Windows and the .NET 10 SDK. From this folder:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The result is `out\Strikers.zip`, the same eight files as the Nexus download.

## Problems

Open an issue in this repository's Issues tab. If a match stopped with an error, attach the newest zip from the `reports` folder beside `Strikers.exe`.

## Licence

MIT, in `LICENSE`. The licences of the libraries inside the exes are in `THIRD-PARTY-NOTICES.txt`. Horizon Forbidden West and Machine Strike belong to Guerrilla Games and Sony Interactive Entertainment, and Strikers is not affiliated with either.
