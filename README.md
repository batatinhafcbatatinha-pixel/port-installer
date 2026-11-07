# Port launcher / installer

The launcher and game-file installer used by the recompilers (PS2Recomp `games/`) and by the single-game port kits.
Nothing from any game is in this repository or in its releases: the game always comes from your own disc.

## Play a game (recompilers)

1. Get a recompiler, either way:
   - clone it: `git clone --recurse-submodules https://github.com/batatinhafcbatatinha-pixel/PS2Recomp.git` and run `Launcher.cmd`
     at its root (it downloads `Launcher.exe` from the [releases](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases)), or
   - download only `Launcher.exe` from the [releases](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases) and run it:
     it asks for a folder and downloads the recompiler's source there.
2. Click **CHOOSE ISO...** and pick your disc image. The game is recognised from the disc (`SYSTEM.CNF`); the launcher then
   installs what is missing (Python, C++ Build Tools, CMake, Ninja, Ghidra), copies and checks the game files, translates and
   compiles the game. It takes one to several hours the first time; finished steps are skipped when you run it again.
3. **PLAY**. The **PC settings** tab holds the same options as the in-game F1 menu.

The **Game** list at the top switches between the games of the recompiler; **Dependencies** shows what is installed and
installs single parts; **Install** shows the log (also in `games/<game>/setup-log.txt`).

## What is here

| Path | What |
| --- | --- |
| `launcher/` | `Launcher.exe` source (C#, .NET Framework 4.8, built by CI). Recompiler mode: finds `launcher.json` with `games_dir` in its folder or a parent (or `--root`), lists `games/*/setup.json`, `--game <id>` opens one. Kit mode: `launcher.json` next to it or built in. Alone: downloads a recompiler from `recomps.json`. |
| `setup/setup.ps1`, `setup/setup_tools.py` | The install and build steps (shared by every port). `-GameDir` = one game folder, `-ToolsDir` = shared tools, `-Source` = the disc image; `-Only python\|buildtools\|tools\|game`. |
| `installer.py`, `engine.py`, `media.py`, `layout.py` | Game-file installer: ISO 9660 (PS2) and XDVDFS (Xbox/360) or a folder, SHA-256 check against `installer.json`, progress, resume. `--source` starts at once. |
| `prepare_folder.py` | Sector layout for an extracted folder (`game_files/.disc/`), used by the PS2 runtime instead of an ISO. |
| `runtime/` | CMake glue that makes the PS2Recomp CD readers use the extracted folder. |
| `integrations/` | `installer.json` of the single-game kits. |

A recompiler that uses it has `tools/port-installer` as a submodule, `launcher.json` at its root (shared PC settings and
tools) and per game `games/<game>/setup.json` (name, disc, steps) and `installer.json` (supported disc hashes). If the
submodule folder is empty (ZIP download), the launcher writes its own copy of these scripts there.

## Developing

```sh
python -m unittest discover -s tests -v
dotnet build launcher/Launcher.csproj -c Release -o build/launcher
```

Test aids: `Launcher.exe --root <recomp> --game <id> --shot out.png [--shot-tab N] [--theme dark|light]` renders the window
to a PNG; `--detect <iso or folder> out.txt` writes the boot executable and the matching game. A tag `v*` publishes
`Launcher.exe` as a release.

## Single-game kits (older ports)

`Install-Files.cmd` at the root of a port runs `python tools/port-installer/installer.py --config installer.json`; the files
go to `game_files/`. The ISO is never copied; after everything is verified it asks whether to delete it (default No).
CHD, BIN/CUE and encrypted images must be converted first.

No game files, ISOs, keys, original binaries or recompiled code are distributed here.
