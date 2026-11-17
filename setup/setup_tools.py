"""Portable build tools and missing project parts for Setup.cmd, pinned and SHA-256 checked.

Reads setup.json in the port folder (the repository root, or --root: one game's folder of a recompiler repository):
- "tools": any of llvm, llvm-mingw, cmake, ninja, ghidra (Ghidra + the PS2 extension + a Java 21 JDK), downloaded into
  .local/tools; nothing is installed system-wide. A complete Clang 20+ already installed is used instead.
- "submodules": {path: {"url", "commit"}}; a ZIP download has these folders empty. With Git they are checked
  out; without it the pinned commit's archive is downloaded.
Prints KEY=VALUE lines for setup.ps1: PATH (folders to put first), LLVM, GHIDRA, JAVA_HOME.
`--check-pins` compares the submodule pins with the repository's gitlinks (run it before publishing).
"""
import argparse
import gzip
import hashlib
import json
import lzma
import os
from pathlib import Path
import platform
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import time
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
GH = 'https://github.com/'
TOOLS = {
    'llvm': {
        'version': '22.1.2', 'sha256': '6550bcc308bf972f7f956001b73f6478da3d22f1ebd4b01653c978a6c7ff3698',
        'url': GH + 'llvm/llvm-project/releases/download/llvmorg-22.1.2/clang+llvm-22.1.2-x86_64-pc-windows-msvc.tar.xz',
        'check': 'bin/clang++.exe',
    },
    'llvm-mingw': {  # Clang + lld + mingw-w64 (headers and libraries, Direct3D and XAudio included): a whole
        # Windows C/C++ toolchain that may be redistributed, so it replaces the Microsoft C++ Build Tools.
        'version': '20261020', 'sha256': 'fd5462b7f5d93b2ff981fcd3f9c7e427e47f3203c64ea91c27bcc444eebd74cd',
        'url': GH + 'mstorsjo/llvm-mingw/releases/download/20261020/llvm-mingw-20261020-ucrt-x86_64.zip',
        'check': 'bin/clang++.exe',
    },
    'cmake': {
        'version': '3.31.12', 'sha256': '0c4baa40f28b3f8225eb3fdf6946c987b4fe901403b4eaf2fbbd9378100aaa0c',
        'url': GH + 'Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-windows-x86_64.zip',
        'check': 'bin/cmake.exe',
    },
    'ninja': {
        'version': '1.13.2', 'sha256': '07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65',
        'url': GH + 'ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip',
        'check': 'ninja.exe',
    },
    'jdk': {
        'version': '21.0.13', 'sha256': 'a4dbaa05fca4bd89d84584e58c459bd732efebb1722af80ab13c6516832004f9',
        'url': GH + 'adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_x64_windows_hotspot_21.0.13_9.zip',
        'check': 'bin/java.exe',
    },
    'ghidra': {
        'version': '12.1.4', 'sha256': 'ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db',
        'url': GH + 'NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip',
        'check': 'support/analyzeHeadless.bat',
    },
    'ghidra-ee': {  # ghidra-emotionengine-reloaded, the PlayStation 2 (R5900) extension for that Ghidra
        'version': '2.1.39', 'sha256': '088369c4d7de43cc95dbf254936dfed45eae5eddfcaf8b928189344bcc6a4dac',
        'url': GH + 'chaoticgd/ghidra-emotionengine-reloaded/releases/download/v2.1.39/'
                    'ghidra_12.1.4_PUBLIC_20261029_ghidra-emotionengine-reloaded.zip',
        'check': 'extension.properties',
    },
}
LLVM_NEEDED = ('clang.exe', 'clang++.exe', 'clang-cl.exe', 'lld-link.exe', 'llvm-ar.exe', 'llvm-rc.exe')

# Linux/macOS use the system Clang instead of the large Windows LLVM downloads. The other tools
# come from the same official releases as the Windows table, pinned to the published SHA-256.
POSIX_TOOLS = {
    ('linux', 'x86_64'): {
        'cmake': {
            'version': '3.31.12', 'sha256': '0dc2e9a6860f06bf10bd8fadc03e35d9eeb4df46e33763a7e480e987758f385c',
            'url': GH + 'Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-linux-x86_64.tar.gz',
            'check': 'bin/cmake', 'bin': 'bin',
        },
        'ninja': {
            'version': '1.13.2', 'sha256': '5749cbc4e668273514150a80e387a957f933c6ed3f5f11e03fb30955e2bbead6',
            'url': GH + 'ninja-build/ninja/releases/download/v1.13.2/ninja-linux.zip',
            'check': 'ninja', 'bin': '',
        },
        'jdk': {
            'version': '21.0.13', 'sha256': 'd7f5d5cf865a2902b70ff549b97586a7d3f234e016f041ab447ea740c7d66d1c',
            'url': GH + 'adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_x64_linux_hotspot_21.0.13_9.tar.gz',
            'check': 'bin/java',
        },
        'ghidra': {
            'version': '12.1.4', 'sha256': 'ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db',
            'url': GH + 'NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip',
            'check': 'support/analyzeHeadless',
        },
        'ghidra-ee': {
            'version': '2.1.39', 'sha256': '088369c4d7de43cc95dbf254936dfed45eae5eddfcaf8b928189344bcc6a4dac',
            'url': GH + 'chaoticgd/ghidra-emotionengine-reloaded/releases/download/v2.1.39/'
                        'ghidra_12.1.4_PUBLIC_20261029_ghidra-emotionengine-reloaded.zip',
            'check': 'extension.properties',
        },
    },
    ('linux', 'aarch64'): {
        'cmake': {
            'version': '3.31.12', 'sha256': '83f8fd91d2038a56556e1400390fcfe42f79602940c494f6c6f1cdae7f9e7f40',
            'url': GH + 'Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-linux-aarch64.tar.gz',
            'check': 'bin/cmake', 'bin': 'bin',
        },
        'ninja': {
            'version': '1.13.2', 'sha256': 'fd2cacc8050a7f12a16a2e48f9e06fca5c14fc4c2bee2babb67b58be17a607fc',
            'url': GH + 'ninja-build/ninja/releases/download/v1.13.2/ninja-linux-aarch64.zip',
            'check': 'ninja', 'bin': '',
        },
        'jdk': {
            'version': '21.0.13', 'sha256': 'e3522e707fc7dbe2a72c30746b1e7068301a0213ecd213d4491f35a14eb55578',
            'url': GH + 'adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_aarch64_linux_hotspot_21.0.13_9.tar.gz',
            'check': 'bin/java',
        },
        'ghidra': {
            'version': '12.1.4', 'sha256': 'ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db',
            'url': GH + 'NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip',
            'check': 'support/analyzeHeadless',
        },
        'ghidra-ee': {
            'version': '2.1.39', 'sha256': '088369c4d7de43cc95dbf254936dfed45eae5eddfcaf8b928189344bcc6a4dac',
            'url': GH + 'chaoticgd/ghidra-emotionengine-reloaded/releases/download/v2.1.39/'
                        'ghidra_12.1.4_PUBLIC_20261029_ghidra-emotionengine-reloaded.zip',
            'check': 'extension.properties',
        },
    },
    ('darwin', 'arm64'): {
        'cmake': {
            'version': '3.31.12', 'sha256': '799af7fd545db9bf1b9cfe72f8095880e727a2d4e0df0e3dffc3bc7b95c2d3b0',
            'url': GH + 'Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-macos-universal.tar.gz',
            'check': 'CMake.app/Contents/bin/cmake', 'bin': 'CMake.app/Contents/bin',
        },
        'ninja': {
            'version': '1.13.2', 'sha256': 'c99048673aa765960a99cf10c6ddb9f1fad506099ff0a0e137ad8960a88f321b',
            'url': GH + 'ninja-build/ninja/releases/download/v1.13.2/ninja-mac.zip',
            'check': 'ninja', 'bin': '',
        },
        'jdk': {
            'version': '21.0.13', 'sha256': 'bf036b35b071bfb2f54f2c0af8ee8d88afa95ee6eb3dce02c0f94cfc2524d04a',
            'url': GH + 'adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_aarch64_mac_hotspot_21.0.13_9.tar.gz',
            'check': 'Contents/Home/bin/java',  # the macOS JDK is a bundle
        },
        'ghidra': {
            'version': '12.1.4', 'sha256': 'ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db',
            'url': GH + 'NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip',
            'check': 'support/analyzeHeadless',
        },
        'ghidra-ee': {
            'version': '2.1.39', 'sha256': '088369c4d7de43cc95dbf254936dfed45eae5eddfcaf8b928189344bcc6a4dac',
            'url': GH + 'chaoticgd/ghidra-emotionengine-reloaded/releases/download/v2.1.39/'
                        'ghidra_12.1.4_PUBLIC_20261029_ghidra-emotionengine-reloaded.zip',
            'check': 'extension.properties',
        },
    },
    ('darwin', 'x86_64'): {
        'cmake': {
            'version': '3.31.12', 'sha256': '799af7fd545db9bf1b9cfe72f8095880e727a2d4e0df0e3dffc3bc7b95c2d3b0',
            'url': GH + 'Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-macos-universal.tar.gz',
            'check': 'CMake.app/Contents/bin/cmake', 'bin': 'CMake.app/Contents/bin',
        },
        'ninja': {
            'version': '1.13.2', 'sha256': 'c99048673aa765960a99cf10c6ddb9f1fad506099ff0a0e137ad8960a88f321b',
            'url': GH + 'ninja-build/ninja/releases/download/v1.13.2/ninja-mac.zip',
            'check': 'ninja', 'bin': '',
        },
        'jdk': {
            'version': '21.0.13', 'sha256': '9aa1d1c17ae5467a022679e7e0af5c00797d94ac462ed1a46f4db07a8a34a6dd',
            'url': GH + 'adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_x64_mac_hotspot_21.0.13_9.tar.gz',
            'check': 'Contents/Home/bin/java',  # the macOS JDK is a bundle
        },
        'ghidra': {
            'version': '12.1.4', 'sha256': 'ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db',
            'url': GH + 'NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip',
            'check': 'support/analyzeHeadless',
        },
        'ghidra-ee': {
            'version': '2.1.39', 'sha256': '088369c4d7de43cc95dbf254936dfed45eae5eddfcaf8b928189344bcc6a4dac',
            'url': GH + 'chaoticgd/ghidra-emotionengine-reloaded/releases/download/v2.1.39/'
                        'ghidra_12.1.4_PUBLIC_20261029_ghidra-emotionengine-reloaded.zip',
            'check': 'extension.properties',
        },
    },
}


def platform_key():
    machine = platform.machine().lower()
    if machine in ('amd64', 'x86_64'):
        machine = 'x86_64'
    elif machine in ('arm64', 'aarch64'):
        machine = 'arm64' if sys.platform == 'darwin' else 'aarch64'
    return sys.platform, machine


def tools_table():
    """The pinned table for this OS; Windows keeps the original TOOLS unchanged."""
    if os.name == 'nt':
        return TOOLS
    key = platform_key()
    if key not in POSIX_TOOLS:
        raise ValueError(f'Unsupported platform for portable tools: {key[0]} {key[1]}')
    return POSIX_TOOLS[key]


def system_llvm():
    """On POSIX, LLVM is the system Clang: return (LLVM prefix, directory with its binaries)."""
    clang = shutil.which('clang')
    clangpp = shutil.which('clang++')
    if not clang or not clangpp:
        raise ValueError('System Clang is required: install clang and clang++ (on macOS, the Command Line Tools)')
    resolved = Path(clang).resolve()
    prefix, binary_dir = resolved.parent.parent, resolved.parent
    log(f'Using system Clang from {binary_dir}')
    return prefix, binary_dir


def log(text):
    print(text, file=sys.stderr, flush=True)


def clang_major(clang):
    try:
        out = subprocess.run([str(clang), '--version'], capture_output=True, text=True, timeout=30).stdout
    except OSError:
        return 0
    match = re.search(r'clang version (\d+)', out)
    return int(match.group(1)) if match else 0


def existing_llvm():
    candidates = [shutil.which('clang++'), Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'LLVM/bin/clang++.exe']
    for clang in filter(None, candidates):
        clang = Path(clang)
        # Partial installs exist (seen: clang without lld-link); the build needs the linker and tools too.
        complete = all((clang.parent / name).is_file() for name in LLVM_NEEDED)
        if clang.is_file() and complete and clang_major(clang) >= 20:
            log(f'Using Clang {clang_major(clang)} from {clang.parent}')
            return clang.parent.parent
        if clang.is_file():
            log(f'Ignoring the incomplete or old LLVM in {clang.parent}')
    return None


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as f:
        while chunk := f.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def download(url, target, sha256=None):
    if target.is_file() and (sha256 is None or file_sha256(target) == sha256):
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + '.part')
    # Windows' own curl.exe uses the system certificate store fully (Python's ssl may miss intermediates).
    # Linux / macOS: the system's curl, which resumes a broken download (-C -) and retries a stalled one.
    curl = shutil.which('curl.exe') if os.name == 'nt' else shutil.which('curl')
    command = [curl, '-L', '--fail', '--retry', '3', '--progress-bar', '-o', str(partial), url] if os.name == 'nt' else \
              [curl, '-L', '--fail', '--retry', '8', '--retry-all-errors', '--retry-delay', '3', '-C', '-',
               '--speed-limit', '10000', '--speed-time', '60', '-sS', '-o', str(partial), url]
    if not (curl and subprocess.run(command, **({} if os.name == 'nt' else {'stdout': sys.stderr})).returncode == 0):
        request = urllib.request.Request(url, headers={'User-Agent': 'port-setup'})
        with urllib.request.urlopen(request, timeout=60) as response, partial.open('wb') as out:
            total = int(response.headers.get('Content-Length') or 0)
            done, shown = 0, 0.0
            while chunk := response.read(1 << 20):
                out.write(chunk)
                done += len(chunk)
                if time.monotonic() - shown > 2:
                    shown = time.monotonic()
                    log(f'  {done >> 20} / {total >> 20} MB' if total else f'  {done >> 20} MB')
    if sha256 and file_sha256(partial) != sha256:
        partial.unlink()
        raise ValueError(f'{url}: SHA-256 mismatch (download damaged or changed); run Setup again')
    partial.replace(target)


def extract_tar(tar, folder):
    try:
        tar.extractall(folder, filter='data')
    except TypeError:  # Python without extraction filters
        tar.extractall(folder)


def restore_zip_modes(zip_file, folder):
    """zipfile drops the POSIX mode: give each file the permissions stored in the archive (Ghidra's scripts,
    its native decompiler, the JDK's programs), when the archive was made on a POSIX system."""
    for info in zip_file.infolist():
        mode = (info.external_attr >> 16) & 0o777
        if mode and not info.is_dir():
            path = folder / info.filename
            if path.is_file():
                path.chmod(mode | stat.S_IRUSR | stat.S_IWUSR)


def fix_executable_bits(folder):
    """zipfile drops the POSIX mode; restore the bits needed to run tools (archives made without modes)."""
    for path in folder.rglob('*'):
        if path.is_file() and (path.parent.name == 'bin' or path.name in ('ninja', 'analyzeHeadless') or path.suffix == '.sh' or
                               any(part.startswith(('linux_', 'mac_')) for part in path.parent.parts[-1:])):
            path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def extract(archive, folder):
    """Unpack zip, tar.gz or tar.xz into folder; unwrap a single top folder."""
    temp = folder.with_name(folder.name + '.extracting')
    shutil.rmtree(temp, ignore_errors=True)
    temp.mkdir(parents=True)
    if archive.name.endswith('.zip'):
        with zipfile.ZipFile(archive) as z:
            z.extractall(temp)
            if os.name != 'nt':
                restore_zip_modes(z, temp)
        if os.name != 'nt':
            fix_executable_bits(temp)
    elif archive.name.endswith(('.tar.gz', '.tgz')):
        with gzip.open(archive) as gz, tarfile.open(fileobj=gz, mode='r|') as tar:
            extract_tar(tar, temp)
    else:
        with lzma.open(archive) as xz, tarfile.open(fileobj=xz, mode='r|') as tar:
            extract_tar(tar, temp)
    entries = list(temp.iterdir())
    inner = entries[0] if len(entries) == 1 and entries[0].is_dir() else temp
    folder.parent.mkdir(parents=True, exist_ok=True)
    shutil.rmtree(folder, ignore_errors=True)
    inner.replace(folder)
    shutil.rmtree(temp, ignore_errors=True)


def ensure(name, folder, table=None):
    tool = (table or TOOLS)[name]
    if (folder / tool['check']).is_file():
        return folder
    archive = folder.parent / Path(tool['url'].replace('%2B', '+')).name
    log(f'Downloading {name} {tool["version"]} ...')
    download(tool['url'], archive, tool['sha256'])
    log(f'Unpacking {name} (this can take a few minutes) ...')
    extract(archive, folder)
    if not (folder / tool['check']).is_file():
        raise ValueError(f'{name}: {tool["check"]} missing after unpacking')
    archive.unlink()
    return folder


def ghidra(base, table=None):
    table = table or TOOLS
    jdk = ensure('jdk', base / 'jdk-21', table)
    home = ensure('ghidra', base / f'ghidra-{table["ghidra"]["version"]}', table)
    if (jdk / 'Contents/Home').is_dir():   # macOS: JAVA_HOME is the bundle's Contents/Home
        jdk = jdk / 'Contents/Home'
    # Extensions under Ghidra/Extensions of the install are loaded for every user, headless included.
    ensure('ghidra-ee', home / 'Ghidra/Extensions/ghidra-emotionengine-reloaded', table)
    # Point Ghidra at this JDK so it never asks for one.
    props = home / 'support/launch.properties'
    text = props.read_text(encoding='utf-8')
    line = 'JAVA_HOME_OVERRIDE=' + jdk.as_posix()
    if line not in text:
        props.write_text(re.sub(r'^JAVA_HOME_OVERRIDE=.*$', line, text, flags=re.M), encoding='utf-8')
    return home, jdk


def submodules(config):
    for path, pin in config.get('submodules', {}).items():
        folder = ROOT / path
        if folder.is_dir() and any(folder.iterdir()):
            continue
        if (ROOT / '.git').exists() and shutil.which('git'):
            log(f'Checking out {path} ...')
            subprocess.run(['git', 'submodule', 'update', '--init', '--recursive', '--', path], cwd=ROOT, check=True)
            continue
        owner_repo = pin['url'].removeprefix(GH).removesuffix('.git')
        log(f'Downloading {path} ({owner_repo} {pin["commit"][:10]}) ...')
        archive = ROOT / '.local/downloads' / f'{owner_repo.replace("/", "-")}-{pin["commit"]}.zip'
        download(f'https://codeload.github.com/{owner_repo}/zip/{pin["commit"]}', archive)
        extract(archive, folder)


def check_pins(config):
    """The pins in setup.json must equal the gitlinks, or a ZIP download would get other submodule versions."""
    out = subprocess.check_output(['git', 'ls-files', '-s'], cwd=ROOT, text=True)
    links = {line.split('\t')[1]: line.split()[1] for line in out.splitlines() if line.startswith('160000')}
    pins = {path: pin['commit'] for path, pin in config.get('submodules', {}).items()}
    if links != pins:
        raise ValueError(f'setup.json submodule pins {pins} differ from the gitlinks {links}')
    print('Submodule pins match the gitlinks')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--root', type=Path, help='folder holding setup.json (default: the repository of this script)')
    parser.add_argument('--dir', type=Path, help='portable tools folder (default: <root>/.local/tools)')
    parser.add_argument('--check-pins', action='store_true')
    args = parser.parse_args()
    global ROOT
    if args.root:
        ROOT = args.root.resolve()
    if args.dir is None:
        args.dir = ROOT / '.local/tools'
    config = json.loads((ROOT / 'setup.json').read_text(encoding='utf-8'))
    if args.check_pins:
        return check_pins(config)
    base = args.dir.resolve()
    base.mkdir(parents=True, exist_ok=True)
    submodules(config)
    wanted = config.get('tools', [])
    table = tools_table()
    posix = os.name != 'nt'
    path, result = [], {}
    if posix and ('llvm' in wanted or 'llvm-mingw' in wanted):
        llvm_prefix, llvm_bin = system_llvm()
        path.append(llvm_bin)
        result['CC'], result['CXX'] = 'clang', 'clang++'
        result['LLVM'] = llvm_prefix
    if 'llvm' in wanted and not posix:
        llvm = existing_llvm() or ensure('llvm', base / f'llvm-{table["llvm"]["version"]}', table)
        path.append(llvm / 'bin')
        result['LLVM'] = llvm
    if 'llvm-mingw' in wanted and not posix:
        mingw = ensure('llvm-mingw', base / f'llvm-mingw-{table["llvm-mingw"]["version"]}', table)
        path.append(mingw / 'bin')
        result['CC'], result['CXX'] = 'clang', 'clang++'
    for name in ('cmake', 'ninja'):
        if name in wanted:
            folder = ensure(name, base / f'{name}-{table[name]["version"]}', table)
            relative = table[name].get('bin', 'bin' if name == 'cmake' else '')
            path.append(folder / relative if relative else folder)
    if 'ghidra' in wanted:
        result['GHIDRA'], result['JAVA_HOME'] = ghidra(base, table)
    separator = ';' if os.name == 'nt' else os.pathsep
    print('PATH=' + separator.join(str(p) for p in path))
    for key, value in result.items():
        print(f'{key}={value}')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        log(f'Setup tools failed: {error}')
        sys.exit(1)
