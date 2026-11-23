# NientumStudio — Chart Editor

An unofficial chart editor and in-game custom-chart mod for **Nientum - Op.ZERO**:

- `src/NientumStudio/` — editor (WPF, .NET 10, Windows)
- `mod/NientumCustomCharts/` — BepInEx 5 plugin that loads custom charts in game
- `install/` — installer / uninstaller scripts shipped with the release build

No game files are included: no game executable, official charts, audio, or artwork.

## Requirements

- Windows x64
- .NET 10 SDK
- A local installation of Nientum - Op.ZERO (used for managed assembly references)
- BepInEx 5 to run the mod

## Build

Point `NientumGameDir` at the folder containing `Nientum.exe`:

```bat
dotnet build src\NientumStudio\NientumStudio.csproj -c Release ^
  -p:NientumGameDir="D:\Games\Liora"
```

Build the mod (copied to `BepInEx\plugins` automatically):

```bat
dotnet build mod\NientumCustomCharts\NientumCustomCharts.csproj -c Release ^
  -p:GameDir="D:\Games\Liora"
```

Both projects fail with a clear message if the game path is not supplied.

## Chart format

Each chart is a folder under the game's `CustomCharts\` directory:

```
CustomCharts\<song>\
  meta.json      title, BPM, audio file, cover
  normal.json    note data
  <audio>        bring your own (copyright is yours)
  <cover>
```

## Release

`NientumStudio_Exp-v1_2026.11.22.zip`: self-contained editor (.NET runtime bundled) + mod + BepInEx framework + installer scripts. Run `安装.bat` after extracting. Local music is not included.

## Copyright and takedown

This is a fan-made, unofficial project and is not affiliated with the developers or publishers of Nientum - Op.ZERO. All game content (CG, character art, music, fonts, script text) is copyright QueseraGames. Release packages are for personal, non-commercial study and sharing only. If a rights holder believes a release infringes their rights, please open an Issue in this repository and the material will be removed promptly.

## License

MIT for the code, see [LICENSE](LICENSE). Game assets are not covered.