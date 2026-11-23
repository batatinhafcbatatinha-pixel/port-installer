# Port launcher

One launcher for every recompiler: add the folder with your discs, pick a game, **INSTALL AND BUILD**, **PLAY**.
It is used by [PS2Recomp](https://github.com/batatinhafcbatatinha-pixel/PS2Recomp),
[PSPRecomp](https://github.com/batatinhafcbatatinha-pixel/PSPRecomp),
[xboxrecomp](https://github.com/batatinhafcbatatinha-pixel/xboxrecomp),
[rexglue-ports](https://github.com/batatinhafcbatatinha-pixel/rexglue-ports) (Xbox 360) and
[AnyPS5](https://github.com/batatinhafcbatatinha-pixel/AnyPS5).
Nothing from any game is in this repository or its releases: the game always comes from your own disc or dump.

## Download

| System | File |
| --- | --- |
| Windows | [`Launcher.exe`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher.exe) |
| Linux x86-64 | [`Launcher-linux-x64.AppImage`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-linux-x64.AppImage) or [`.tar.gz`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-linux-x64.tar.gz) |
| macOS 11+ | [`Launcher-macos.zip`](https://github.com/batatinhafcbatatinha-pixel/port-installer/releases/latest/download/Launcher-macos.zip) |

Each recompiler's release ZIP also has `Launcher.exe` at its root.

## Use

1. **ADD A GAME FOLDER** with your `.iso` images, extracted discs or dumps. The console and the game are recognised
   by themselves; a missing recompiler is offered for download.
2. **INSTALL AND BUILD**: copies and checks the game files, then translates and compiles the game (one to several
   hours the first time; finished steps are skipped later).
3. **PLAY**. The **PC settings** tab holds resolution, frame rate, graphics and controls.

**Patches and cheats** (Game menu) come from the emulator communities' repositories or any GitHub repository you
add. **Updates** (Updates menu): *Stable* or *Nightly*.

On Linux, `chmod +x` the AppImage and run it. On macOS, move **Port Launcher.app** to Applications and open it
the first time with Control-click > **Open** (it is not notarized). Today only PS2Recomp games build on Linux and
macOS; the other recompilers are Windows only.

## More

How it works, file layout, building and testing: [docs/DEVELOPING.md](docs/DEVELOPING.md).

## Legal

Use only games you own and dumped yourself from your own console or disc. This project does not include, link to or
explain how to obtain keys, firmware, decryption or dumping tools, jailbreaks or game files, and it does not
circumvent any protection. Not affiliated with or endorsed by Sony Interactive Entertainment, Microsoft, or any
game publisher; all trademarks belong to their owners.
