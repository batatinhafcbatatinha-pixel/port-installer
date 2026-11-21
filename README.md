# Port launcher / installer

The launcher and game-file installer used by the recompilers ([PS2Recomp](https://github.com/batatinhafcbatatinha-pixel/PS2Recomp) `games/`,
[xboxrecomp](https://github.com/batatinhafcbatatinha-pixel/xboxrecomp) `games/`, [PSPRecomp](https://github.com/batatinhafcbatatinha-pixel/PSPRecomp) `profiles/`,
[rexglue-ports](https://github.com/batatinhafcbatatinha-pixel/rexglue-ports), [AnyPS5](https://github.com/batatinhafcbatatinha-pixel/AnyPS5) for PS5 dumps)
and by the single-game port kits.
Nothing from any game is in this repository or in its releases: the game always comes from your own disc.

## Play a game (recompilers)

1. Get the launcher, either way:
   - the release ZIP of a recompiler, from that recompiler's own releases (for example
     [PS2Recomp-windows.zip](https://github.com/batatinhafcbatatinha-pixel/PS2Recomp/releases/latest/download/PS2Recomp-windows.zip)):
     unzip it and open `Launcher.exe` at its root; or
   - the launcher alone, from this repository's releases, then get recompilers from **Recompilers** (toolbar or Ctrl+R):
     [`Launcher.exe`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher.exe) (Windows),
     [`Launcher-linux-x64.AppImage`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-linux-x64.AppImage) or
     [`.tar.gz`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-linux-x64.tar.gz) (Linux),
     [`Launcher-macos.zip`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-macos.zip) (macOS);
     see [Linux and macOS](#linux-and-macos).
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
   makes at boot or every frame. For PS5 games the patches of
   [recomp-patches](https://github.com/batatinhafcbatatinha-pixel/recomp-patches) (our own, JSON) are written to the
   game's `patches` folder and AnyPS5's `tools/ps5port` applies them to the converted modules when the game starts.
   Other recompilers keep the choice until they apply it.
4. **PLAY**. The **PC settings** tab holds the same options as the in-game F1 menu.

The window works like an emulator's game list: a menu bar, a toolbar (open ISO, game folders, refresh, play, stop,
install, settings, dependencies, log), the library as a list or a grid of covers with search and sorting, the selected
game in a panel at the right, the setup log at the bottom and a status bar.

- **Game folders** (File > Add game folders, or drop a folder on the window): the launcher searches them and their
  subfolders for `.iso` images and extracted disc folders and recognises each game from the disc; a found disc is used
  by INSTALL without asking. Results are cached (`%LOCALAPPDATA%\port-launcher`), so a rescan (F5) only reads new or
  changed images. PS2 discs without a profile show up too and can be built with the generic path.
- **Covers**: your own picture (Game > Choose cover), `games/<game>/cover.png|jpg`, the disc's icon (PSP `ICON0.PNG`), or,
  when View > Download covers is on, a picture by disc serial from the recompiler's `cover_url` (`{serial}` = `SLUS-12345`;
  PS2 default: the xlenore/ps2-covers collection, as PCSX2 uses). Without one, a tile is drawn from the name.
- **Updates** (Updates menu): channel *Stable* (the newest release) or *Nightly* (the build of the newest commit, kept by
  CI in the `nightly` pre-release); check on start, update the launcher automatically, and optionally update the
  recompiler (git pull for a clone, the source ZIP otherwise; stable follows its newest release when it has one).

PC settings and dependencies open in their own windows; the log is also in `games/<game>/setup-log.txt`.

**PlayStation 5 (AnyPS5).** A decrypted dump folder (`eboot.bin` + `sce_sys/param.json`) is recognised by its title id
(`PPSA12345`) and cover (`sce_sys/icon0.png`). Every PS5 game takes the generic path: the port folder
(`.local/ports/<title id>`) links `game_files` to the dump (installer.json `"mode": "link"`, nothing is copied), and
`tools/ps5port` converts the executable and modules with the AnyPS5 relinker in a few minutes; no C++ Build Tools are
needed (setup.json `"buildtools": false`). The PC settings become AnyPS5 variables when the game starts.

## Linux and macOS

The same launcher (library with console tabs, recompilers, patches and cheats, PC settings, dependencies, Portuguese or
English) runs on Linux and macOS as a native program: the window is drawn with [Avalonia](https://avaloniaui.net/) and the
program is compiled ahead of time (.NET NativeAOT), so nothing else is installed to run it. Windows keeps its
Windows Forms launcher; both share the logic in `launcher/Core`.

**Linux (x86-64).** Download
[`Launcher-linux-x64.AppImage`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-linux-x64.AppImage),
then `chmod +x Launcher-linux-x64.AppImage && ./Launcher-linux-x64.AppImage` (or open it from the file manager; distributions
without FUSE 2 run it with `APPIMAGE_EXTRACT_AND_RUN=1`). The
[`.tar.gz`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-linux-x64.tar.gz)
holds the same program unpacked: `tar xzf Launcher-linux-x64.tar.gz && port-launcher/install.sh` puts it in
`~/.local/share/port-launcher` and in the applications menu (no administrator). Built on Ubuntu 22.04: any distribution with
glibc 2.35 or newer, X11 or XWayland. Preferences: `~/.config/port-launcher`, caches: `~/.cache/port-launcher` (XDG).
The AppImage updates itself (Updates menu); the `.tar.gz` copy shows the new version and its download.

**macOS 11 or newer (Apple Silicon and Intel).** Download
[`Launcher-macos.zip`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-macos.zip),
unzip it and move **Port Launcher.app** to Applications. It is not notarized: the first time, Control-click it and choose
**Open** (or run `xattr -dr com.apple.quarantine "/Applications/Port Launcher.app"`). The menus are in the system menu bar.
Preferences: `~/Library/Application Support/port-launcher`, caches: `~/Library/Caches/port-launcher`. A new version is
offered in the Updates menu as a download.

What differs from Windows:

- The setup is `setup/setup.sh` (bash) + `setup/setup_posix.py`, the same five steps as `setup.ps1`: Python 3.11+ with Tk,
  the compiler and libraries (Linux: the packages a recompiler lists for apt, dnf, pacman or zypper, installed with
  `pkexec`/`sudo`; macOS: Apple's Command Line Tools and the Homebrew packages it lists), the portable tools of
  `setup_tools.py` (CMake, Ninja, Java, Ghidra for Linux x86-64/arm64 and macOS, SHA-256 pinned; the compiler is the
  system's Clang), your game files (`installer.json` `"mode": "link"` makes `game_files` a symlink) and the build.
- Games start with `Play.sh` next to `Play.cmd`; desktop shortcuts are a `.desktop` entry (Linux: applications menu and
  desktop) or a small app on the desktop (macOS).
- A recompiler says it runs on a system with a block in its `launcher.json` (`"linux": {...}`, `"macos": {...}`: the
  executable, the packages, the dependency checks of that system; its keys replace the top-level ones there). Without it,
  and for games whose `setup.json` build steps are Windows scripts (`cmd /c ...`), the library shows **No version for
  Linux / macOS** and explains it instead of trying. `recomps.json` lists the systems of each recompiler the launcher can
  download (`"platforms"`).
- Today: PS2Recomp declares Linux and macOS; its games built with `tools/ps2port` (and discs without a profile, the generic
  path) install there. Tried: a PlayStation 2 game installed and built through the launcher on Ubuntu 24.04 (WSL). macOS:
  the launcher is built, tested and started by CI; no game install has been tried on a Mac. The other recompilers
  (xboxrecomp, rexglue-ports, PSPRecomp, AnyPS5) are Windows only for now.
- The Windows graphics-card preference and the DXGI card list are Windows only; the monitor list comes from the window
  system and the refresh rates from `xrandr` (Linux).

## What is here

| Path | What |
| --- | --- |
| `launcher/Core/` | The launcher's logic shared by the Windows and the Linux/macOS programs (configuration, recompilers, library, patches, updates, per-system parts in `Platform.cs`, a small JSON reader). |
| `launcher-avalonia/` | The Linux/macOS launcher (Avalonia 11, .NET 8 NativeAOT): the same windows as `launcher/`, drawn with the same colours and icons. |
| `launcher-tests/` | C# unit tests of `launcher/Core` (run by `tests/test_launcher_core.py` and by CI on Windows, Linux and macOS). |
| `tools/package_launcher.sh` | Packages the Linux build (AppImage, `.tar.gz` with `install.sh`) and the macOS build (universal `.app` in a ZIP). |
| `launcher/` | `Launcher.exe` source (C#, .NET Framework 4.8, built by CI). Recompiler mode: finds `launcher.json` with `games_dir` in its folder or a parent (or `--root`), lists `games/*/setup.json`, `--game <id>` opens one. Kit mode: `launcher.json` next to it or built in. Library mode: one library for every recompiler installed (`%APPDATA%\port-launcher\recomps.txt`, the one it sits in added by itself; more from `recomps.json` in the Recompilers window). |
| `tools/make_bundle.py` | Builds a recompiler's player ZIP: source + `Launcher.exe` + Python (python-build-standalone, pinned) + CMake/Ninja (pins of `setup/setup_tools.py`), for a local build; releases no longer carry these bundles (each recompiler's own release workflow makes its ZIPs). |
| `setup/setup.ps1`, `setup/setup_tools.py` | The install and build steps (shared by every port). `-GameDir` = one game folder, `-ToolsDir` = shared tools, `-Source` = the disc image; `-Only python\|buildtools\|tools\|game`. |
| `setup/setup.sh`, `setup/setup_posix.py` | The same steps on Linux and macOS: `--game-dir`, `--tools-dir`, `--source`, `--only ...`, `--yes`. |
| `launcher/Core/GameInstall.cs` | The launcher's own game-file installer (no window, no Python): the same work as `engine.py` (ISO 9660 / XDVDFS image or folder, SHA-256 check against `installer.json`, copy with read-back check, `.disc` index), byte for byte the same output. The launcher runs it before the setup, whose game step then finds the files in place. |
| `installer.py`, `engine.py`, `media.py`, `layout.py` | Game-file installer for `Setup.cmd` run without the launcher: ISO 9660 (PS2) and XDVDFS (Xbox/360) or a folder, SHA-256 check against `installer.json`, progress, resume. `--source` starts at once. Under the launcher (`PORT_LAUNCHER=1`) the setup never opens it. |
| `prepare_folder.py` | Sector layout for an extracted folder (`game_files/.disc/`), used by the PS2 runtime instead of an ISO. |
| `runtime/` | CMake glue that makes the PS2Recomp CD readers use the extracted folder. |

A recompiler that uses it has `tools/port-installer` as a submodule, `launcher.json` at its root (shared PC settings and
tools) and per game `games/<game>/setup.json` (name, disc, steps) and `installer.json` (supported disc hashes).
launcher.json may also hold `generic` (setup.json template for discs without a profile; `play_cmd` = the Play.cmd it
writes), `generic_label` (regex of this console's disc labels in the library) and `deps.buildtools_gb: 0` (no C++ Build Tools).
setup.json `"pc_menu": false` says the game has no in-game F1 menu. If the
submodule folder is empty (ZIP download), the launcher writes its own copy of these scripts there.

## Developing

```sh
python -m unittest discover -s tests -v
dotnet build launcher/Launcher.csproj -c Release -o build/launcher                    # Windows
dotnet publish launcher-avalonia/Launcher.Avalonia.csproj -c Release -r linux-x64 -o build/linux   # on Linux (clang, zlib1g-dev)
dotnet publish launcher-avalonia/Launcher.Avalonia.csproj -c Release -r osx-arm64 -o build/macos   # on macOS (Xcode tools)
bash tools/package_launcher.sh linux build/linux dist
```

Test aids: `Launcher.exe --root <recomp> --game <id> --shot out.png [--shot-tab N] [--theme dark|light]` renders the window
to a PNG (tab 0 library, 1 dependencies, 2 log, 3 PC settings, 4 list view); `--detect <iso or folder> out.txt` writes the
boot executable and the matching game; `--check-updates out.txt` writes what each update channel offers;
`PORT_LAUNCHER_HOME=<folder>` keeps preferences, game folders and caches there instead of the user profile; `--left`
opens the window at the left edge. The Linux/macOS launcher also has `--list out.txt` (the library after one scan, no window)
and `--install <key> --install-log out.txt [--only part]` (installs one game with every question answered yes).
A tag `v*` publishes `Launcher.exe`, `Launcher-linux-x64.AppImage`, `Launcher-linux-x64.tar.gz` and `Launcher-macos.zip`
as a release (version from the tag); every push to `main` replaces the `nightly` pre-release with the same files.

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
