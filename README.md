# Port launcher / installer

The launcher and game-file installer used by the recompilers ([PS2Recomp](https://github.com/batatinhafcbatatinha-pixel/PS2Recomp) `games/`,
[xboxrecomp](https://github.com/batatinhafcbatatinha-pixel/xboxrecomp) `games/`, [PSPRecomp](https://github.com/batatinhafcbatatinha-pixel/PSPRecomp) `profiles/`,
[rexglue-ports](https://github.com/batatinhafcbatatinha-pixel/rexglue-ports), [AnyPS5](https://github.com/batatinhafcbatatinha-pixel/AnyPS5) for PS5 dumps)
and by the single-game port kits.
Nothing from any game is in this repository or in its releases: the game always comes from your own disc.

## Play a game (recompilers)

1. Get the launcher, either way:
   - from a [release](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases), the ZIP of a recompiler
     (`PS2Recomp.zip`, `xboxrecomp.zip`, `rexglue-ports.zip`, `PSPRecomp.zip`, `AnyPS5.zip`): unzip it and open `Launcher.exe`
     at its root. Python, CMake and Ninja are already inside (`.local/`), and for PS2Recomp and PSPRecomp the
     compiler too (llvm-mingw: Clang + mingw-w64), so nothing from Microsoft has to be installed; or
   - `Launcher.zip` alone: open `Launcher.exe` and get recompilers from **Recompilers** (toolbar or Ctrl+R).
   - Developers: `git clone --recurse-submodules` a recompiler and run `Launcher.cmd`.
2. The library starts empty. Click **ADD A GAME FOLDER** and pick one or more folders with your discs (`.iso` images or
   extracted discs; subfolders are searched too). Each disc's console is recognised (PS2 `SYSTEM.CNF`, PSP `UMD_DATA.BIN`,
   Xbox `default.xbe`, Xbox 360 `default.xex`, PS5 dump `sce_sys/param.json`) and then its game (PS2 boot file; others:
   SHA-256 of the small files in the game's `installer.json`). Games are split in tabs: **All**, PS2, Xbox, Xbox 360, PSP, PS5.
   A disc whose recompiler is not on the PC offers to download it; one the recompiler has no profile for is listed as
   experimental (generic path) or not supported yet. **INSTALL AND BUILD** copies and checks the game files, translates and
   compiles the game. It takes one to several hours the first time; finished steps are skipped when you run it again.
3. **Patches and cheats** (Game menu or **More...**): entries from the emulator communities' repositories or any
   GitHub repository you add: [PCSX2/pcsx2_patches](https://github.com/PCSX2/pcsx2_patches) (PS2, matched by serial and
   the boot executable's CRC), [CWCheat Database Plus](https://github.com/Saramagrean/CWCheat-Database-Plus-) (PSP),
   [xenia-canary/game-patches](https://github.com/xenia-canary/game-patches) (Xbox 360),
   [PS4-PS5-Game-Patch](https://github.com/illusion0001/PS4-PS5-Game-Patch) and
   [GoldHEN_Cheat_Repository](https://github.com/GoldHEN/GoldHEN_Cheat_Repository) (PS5). For PS2 games **Apply** writes
   `patches.pnach` / `cheats.pnach` next to the game and runs `tools/ps2port hotpatch`: a patch that changes code
   retranslates only that function into a small library (seconds, no rebuild), the rest are memory writes the runtime
   makes at boot or every frame. Other recompilers keep the choice until they apply it.
4. **PLAY**. The **PC settings** tab holds the same options as the in-game F1 menu.

The window works like an emulator's game list: a menu bar, a toolbar (open ISO, game folders, refresh, play, stop,
install, settings, dependencies, log), the library as a list or a grid of covers with search and sorting, the selected
game in a panel at the right, the setup log at the bottom and a status bar.

- **Game folders** (File > Add game folders, or drop a folder on the window): the launcher searches them and their
  subfolders for `.iso` images and extracted disc folders and recognises each game from the disc; a found disc is used
  by INSTALL without asking. Results are cached (`%LOCALAPPDATA%\port-launcher`), so a rescan (F5) only reads new or
  changed images. PS2 discs without a profile show up too and can be built with the generic path.
- **Covers**: your own picture (Game > Choose cover), `games/<game>/cover.png|jpg`, the disc's icon (PSP `ICON0.PNG`), or,
  when View > Download covers is on, a picture by disc serial from the recompiler's `cover_url` (`{serial}` = `SLUS-21423`;
  PS2 default: the xlenore/ps2-covers collection, as PCSX2 uses). Without one, a tile is drawn from the name.
- **Updates** (Updates menu): channel *Stable* (the newest release) or *Nightly* (the build of the newest commit, kept by
  CI in the `nightly` pre-release); check on start, update the launcher automatically, and optionally update the
  recompiler (git pull for a clone, the source ZIP otherwise; stable follows its newest release when it has one).

PC settings and dependencies open in their own windows; the log is also in `games/<game>/setup-log.txt`.

**PlayStation 5 (AnyPS5).** A decrypted dump folder (`eboot.bin` + `sce_sys/param.json`) is recognised by its title id
(`PPSA01949`) and cover (`sce_sys/icon0.png`). Every PS5 game takes the generic path: the port folder
(`.local/ports/<title id>`) links `game_files` to the dump (installer.json `"mode": "link"`, nothing is copied), and
`tools/ps5port` converts the executable and modules with the AnyPS5 relinker in a few minutes; no C++ Build Tools are
needed (setup.json `"buildtools": false`). The PC settings become AnyPS5 variables when the game starts.

## What is here

| Path | What |
| --- | --- |
| `launcher/` | `Launcher.exe` source (C#, .NET Framework 4.8, built by CI). Recompiler mode: finds `launcher.json` with `games_dir` in its folder or a parent (or `--root`), lists `games/*/setup.json`, `--game <id>` opens one. Kit mode: `launcher.json` next to it or built in. Library mode: one library for every recompiler installed (`%APPDATA%\port-launcher\recomps.txt`, the one it sits in added by itself; more from `recomps.json` in the Recompilers window). |
| `tools/make_bundle.py` | Builds a recompiler's player ZIP: source + `Launcher.exe` + Python (python-build-standalone, pinned) + CMake/Ninja (pins of `setup/setup_tools.py`). CI runs it for every recompiler on a `v*` tag. |
| `setup/setup.ps1`, `setup/setup_tools.py` | The install and build steps (shared by every port). `-GameDir` = one game folder, `-ToolsDir` = shared tools, `-Source` = the disc image; `-Only python\|buildtools\|tools\|game`. |
| `installer.py`, `engine.py`, `media.py`, `layout.py` | Game-file installer: ISO 9660 (PS2) and XDVDFS (Xbox/360) or a folder, SHA-256 check against `installer.json`, progress, resume. `--source` starts at once. |
| `prepare_folder.py` | Sector layout for an extracted folder (`game_files/.disc/`), used by the PS2 runtime instead of an ISO. |
| `runtime/` | CMake glue that makes the PS2Recomp CD readers use the extracted folder. |
| `integrations/` | `installer.json` of the single-game kits. |

A recompiler that uses it has `tools/port-installer` as a submodule, `launcher.json` at its root (shared PC settings and
tools) and per game `games/<game>/setup.json` (name, disc, steps) and `installer.json` (supported disc hashes).
launcher.json may also hold `generic` (setup.json template for discs without a profile; `play_cmd` = the Play.cmd it
writes), `generic_label` (regex of this console's disc labels in the library) and `deps.buildtools_gb: 0` (no C++ Build Tools).
setup.json `"pc_menu": false` says the game has no in-game F1 menu. If the
submodule folder is empty (ZIP download), the launcher writes its own copy of these scripts there.

## Developing

```sh
python -m unittest discover -s tests -v
dotnet build launcher/Launcher.csproj -c Release -o build/launcher
```

Test aids: `Launcher.exe --root <recomp> --game <id> --shot out.png [--shot-tab N] [--theme dark|light]` renders the window
to a PNG (tab 0 library, 1 dependencies, 2 log, 3 PC settings, 4 list view); `--detect <iso or folder> out.txt` writes the
boot executable and the matching game; `--check-updates out.txt` writes what each update channel offers;
`PORT_LAUNCHER_HOME=<folder>` keeps preferences, game folders and caches there instead of the user profile. A tag `v*`
publishes `Launcher.exe` as a release (version from the tag); every push to `main` replaces the `nightly` pre-release.

## Single-game kits (older ports)

`Install-Files.cmd` at the root of a port runs `python tools/port-installer/installer.py --config installer.json`; the files
go to `game_files/`. The ISO is never copied; after everything is verified it asks whether to delete it (default No).
CHD, BIN/CUE and encrypted images must be converted first.

No game files, ISOs, keys, original binaries or recompiled code are distributed here.

## Legal

Use only games you own and dumped yourself from your own console or disc. This project does not include, link to or
explain how to obtain keys, firmware, decryption or dumping tools, jailbreaks or game files, and it does not
circumvent any protection: it only reads files the user already has. Not affiliated with or endorsed by Sony
Interactive Entertainment, Microsoft, or any game publisher; all trademarks belong to their owners.
