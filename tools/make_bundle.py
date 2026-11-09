"""One ZIP a player downloads and opens: a recompiler's source with Launcher.exe at its root and the portable
tools its builds need already inside (Python, CMake, Ninja), so nothing has to be installed first.

    python tools/make_bundle.py --launcher Launcher.exe --out PS2Recomp.zip (--source . | --repo owner/name [--branch main])

--source uses a checkout (a recompiler's own CI); --repo downloads the branch from GitHub. The recompiler's
tools/port-installer is filled from this repository when it is empty. Only source code and openly licensed tools
go in: nothing from any game.
"""
import argparse
import hashlib
import io
import json
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE / 'setup'))
import setup_tools  # noqa: E402  (TOOLS pins and the download/extract helpers)

PYTHON = {
    'version': '3.12.12',
    'url': 'https://github.com/astral-sh/python-build-standalone/releases/download/20251014/'
           'cpython-3.12.12+20251014-x86_64-pc-windows-msvc-install_only.tar.gz',
    'sha256': '2d670beb3b930d30e3a13cc909923a001dbdfcb5537692d5da40b6b41643ce1c',
}
INSTALLER_FILES = ['*.py', 'LICENSE', 'setup/*.ps1', 'setup/*.py', 'runtime/*.py', 'runtime/*.cmake', 'runtime/*.h']
SKIP = {'.git', '.github', '.local', 'build', 'out', '__pycache__'}


def fetch(url, sha256=None):
    # curl retries (GitHub's codeload answers 504 now and then); urllib when there is no curl.
    curl = shutil.which('curl.exe') or shutil.which('curl')
    if curl:
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / 'download'
            cmd = [curl, '-sL', '--fail', '--retry', '10', '--retry-all-errors', '--retry-delay', '15', '-A', 'port-bundle', '-o', str(out), url]
            if subprocess.run(cmd).returncode != 0:
                raise OSError(f'{url}: download failed')
            data = out.read_bytes()
    else:
        request = urllib.request.Request(url, headers={'User-Agent': 'port-bundle'})
        with urllib.request.urlopen(request, timeout=300) as r:
            data = r.read()
    if sha256 and hashlib.sha256(data).hexdigest() != sha256:
        raise ValueError(f'{url}: SHA-256 mismatch')
    return data


def source_tree(args, stage):
    if args.source:
        src = args.source.resolve()
        for item in src.iterdir():
            if item.name in SKIP:
                continue
            (shutil.copytree if item.is_dir() else shutil.copy2)(item, stage / item.name)
        return None
    sha = json.loads(fetch(f'https://api.github.com/repos/{args.repo}/commits/{args.branch}'))['sha']
    print(f'{args.repo} {sha}')
    with zipfile.ZipFile(io.BytesIO(fetch(f'https://codeload.github.com/{args.repo}/zip/{sha}'))) as z:
        top = z.namelist()[0].split('/')[0]
        for name in z.namelist():
            rel = name[len(top) + 1:]
            if not rel or name.endswith('/'):
                continue
            target = stage / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(z.read(name))
    return sha


def fill_installer(stage):
    target = stage / 'tools' / 'port-installer'
    if (target / 'setup' / 'setup.ps1').is_file():
        return
    for pattern in INSTALLER_FILES:
        for f in HERE.glob(pattern):
            dest = target / f.relative_to(HERE)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, dest)


def add_python(stage, archive=None):
    folder = stage / '.local' / 'python'
    print(f'Python {PYTHON["version"]}')
    data = archive.read_bytes() if archive else fetch(PYTHON['url'], PYTHON['sha256'])
    if hashlib.sha256(data).hexdigest() != PYTHON['sha256']:
        raise ValueError('Python archive: SHA-256 mismatch')
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as tar:
        with tempfile.TemporaryDirectory() as tmp:
            try:
                tar.extractall(tmp, filter='data')
            except TypeError:
                tar.extractall(tmp)
            shutil.move(str(Path(tmp) / 'python'), folder)
    if not (folder / 'python.exe').is_file():
        raise ValueError('python.exe missing in the Python archive')


def add_tools(stage, names):
    # The pins of setup_tools.py, fetched with fetch()'s patient retries (GitHub release downloads answer 504 at times).
    base = stage / '.local' / 'tools'
    base.mkdir(parents=True, exist_ok=True)
    for name in names:
        tool = setup_tools.TOOLS[name]
        folder = base / f'{name}-{tool["version"]}'
        archive = base / Path(tool['url'].replace('%2B', '+')).name
        print(f'{name} {tool["version"]}')
        archive.write_bytes(fetch(tool['url'], tool['sha256']))
        setup_tools.extract(archive, folder)
        archive.unlink()
        if not (folder / tool['check']).is_file():
            raise ValueError(f'{name}: {tool["check"]} missing after unpacking')


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--source', type=Path)
    p.add_argument('--repo')
    p.add_argument('--branch', default='main')
    p.add_argument('--launcher', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--python-archive', type=Path, help='the pinned Python archive, already downloaded')
    p.add_argument('--tools', default='cmake,ninja', help='portable tools to put in .local/tools (empty: none)')
    args = p.parse_args()
    if bool(args.source) == bool(args.repo):
        p.error('give --source or --repo')
    with tempfile.TemporaryDirectory() as tmp:
        stage = Path(tmp) / 'bundle'
        stage.mkdir()
        sha = source_tree(args, stage)
        fill_installer(stage)
        if sha:
            (stage / '.local').mkdir(exist_ok=True)
            (stage / '.local' / 'source-version.txt').write_text(sha + '\n')
        add_python(stage, args.python_archive)
        add_tools(stage, [t for t in args.tools.split(',') if t])
        shutil.copy2(args.launcher, stage / 'Launcher.exe')
        args.out.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(args.out, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for f in sorted(stage.rglob('*')):
                if f.is_file():
                    z.write(f, f.relative_to(stage).as_posix())
    print(f'{args.out}  {args.out.stat().st_size >> 20} MB')


if __name__ == '__main__':
    main()
