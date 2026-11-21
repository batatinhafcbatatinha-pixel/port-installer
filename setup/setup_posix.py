"""Linux/macOS version of setup.ps1 (steps 1-5), driven by setup.sh.

The Windows script is the behaviour reference. This module keeps its step order,
texts and play choices, using POSIX package managers, the system C/C++ compiler
and portable tools from setup_tools.py. Standard library only.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys

OS_KEY = 'macos' if sys.platform == 'darwin' else 'linux'
LOG_NAME = 'setup-log.txt'
STEP_NAMES = {
    1: ('Python', 'Python'),
    2: ('Build tools (C/C++ compiler)', 'Ferramentas de build (compilador C/C++)'),
    3: ('Build tools and project parts', 'Ferramentas de build e partes do projeto'),
    5: ('Building your PC version', 'Montando sua versao para PC'),
}

PACKAGE_COMMANDS = {
    'apt-get': ['apt-get', 'install', '-y'],
    'dnf': ['dnf', 'install', '-y'],
    'pacman': ['pacman', '-S', '--noconfirm'],
    'zypper': ['zypper', 'install', '-y'],
}
COMPILER_PACKAGES = {
    'apt-get': 'g++',
    'dnf': 'gcc-c++',
    'pacman': 'gcc',
    'zypper': 'gcc-c++',
}
PKGCONFIG_PACKAGES = {
    'apt-get': 'pkg-config',
    'dnf': 'pkgconf-pkg-config',
    'pacman': 'pkgconf',
    'zypper': 'pkg-config',
}


class SetupError(Exception):
    """A stopped Setup step, with the message in both languages."""

    def __init__(self, en, pt=None):
        self.en = en
        self.pt = pt if pt is not None else en
        super().__init__(en)


def is_portuguese():
    return any(os.environ.get(key, '').lower().startswith('pt')
               for key in ('LANG', 'LC_ALL', 'LC_MESSAGES'))


def t(en, pt):
    return pt if is_portuguese() else en


def os_label(os_key):
    return 'macOS' if os_key == 'macos' else 'Linux'


def header(number, text):
    return f'== {number}/5 {text}'


def step_name(number):
    en, pt = STEP_NAMES[number]
    return t(en, pt)


class Output:
    """Everything printed goes to stdout and, if given, to setup-log.txt."""

    def __init__(self, log_path=None):
        self.log = Path(log_path).open('w', encoding='utf-8') if log_path else None

    def write(self, text):
        sys.stdout.write(text)
        sys.stdout.flush()
        if self.log:
            self.log.write(text)
            self.log.flush()

    def print(self, text=''):
        self.write(f'{text}\n')

    def close(self):
        if self.log:
            self.log.close()
            self.log = None


def say(out, en, pt=None):
    out.print(t(en, pt if pt is not None else en))


def step(out, number, en=None, pt=None):
    out.print()
    if en is None:
        out.print(header(number, step_name(number)))
    else:
        out.print(header(number, t(en, pt if pt is not None else en)))


def ask(out, en, pt, yes):
    if yes:
        return True
    out.write(t(en, pt) + t(' [Y/n] ', ' [S/n] '))
    try:
        answer = input()
    except EOFError:
        return False
    return answer == '' or answer[:1].lower() in ('s', 'y')


def refuse(config, os_key):
    """The message for a game blocked on this OS, with the config's reason if any."""
    label = os_label(os_key)
    reason_en = config.get('reason_en') or ''
    reason_pt = config.get('reason_pt') or ''
    en = f'This game cannot be built on {label} yet'
    pt = f'Este jogo ainda nao pode ser montado no {label}'
    if reason_en:
        en += f': {reason_en}'
    if reason_pt:
        pt += f': {reason_pt}'
    return SetupError(en, pt)


WINDOWS_SHELLS = ('cmd', 'cmd.exe', 'powershell', 'powershell.exe', 'pwsh')


def windows_only_steps(config):
    """True when a build step runs a Windows script (cmd /c x.cmd, powershell, a .cmd/.bat/.ps1 file)."""
    for item in config.get('steps') or []:
        run = item.get('run') or []
        if not run:
            continue
        first = str(run[0]).lower()
        if first in WINDOWS_SHELLS or first.endswith(('.cmd', '.bat', '.ps1')):
            return True
    return False


def merged_config(config, os_key):
    """Shallow-merge the OS block over the top level; refuse OS false / available false, and a game whose
    build steps are Windows scripts with no block for this system (it has not been ported to it yet)."""
    if config.get(os_key) is False:
        raise refuse(config, os_key)
    merged = dict(config)
    override = config.get(os_key)
    if isinstance(override, dict):
        merged.update(override)
    if merged.get('available') is False:
        raise refuse(merged, os_key)
    if windows_only_steps(merged):
        merged.setdefault('reason_en', 'its build steps are Windows scripts')
        merged.setdefault('reason_pt', 'as etapas de montagem dele sao scripts do Windows')
        raise refuse(merged, os_key)
    return merged


def recompiler_config(root, os_key):
    """The launcher.json of the recompiler holding this game (games/<id> or .local/ports/<id>), merged for this
    system; {} for a single-game kit. Its "packages" are what every game of the recompiler needs."""
    for parent in list(root.parents)[:4]:
        candidate = parent / 'launcher.json'
        if candidate.is_file():
            try:
                data = json.loads(candidate.read_text(encoding='utf-8-sig'))
            except (OSError, ValueError):
                return {}
            if 'games_dir' not in data:
                continue
            merged = dict(data)
            if isinstance(data.get(os_key), dict):
                merged.update(data[os_key])
            return merged
    return {}


def load_configs(root):
    setup = json.loads((root / 'setup.json').read_text(encoding='utf-8'))
    install = json.loads((root / 'installer.json').read_text(encoding='utf-8'))
    return setup, install


def detect_package_manager(path=None):
    """Find the first supported package manager on PATH (also usable with a fake PATH)."""
    if path is None:
        path = os.environ.get('PATH', '')
    for name in ('apt-get', 'dnf', 'pacman', 'zypper'):
        for entry in str(path).split(os.pathsep):
            folder = Path(entry) if entry else Path('.')
            for candidate in (folder / name, folder / (name + '.exe')):
                if candidate.is_file() and os.access(str(candidate), os.X_OK):
                    return name
    return None


def privileged_prefix():
    if hasattr(os, 'geteuid') and os.geteuid() == 0:
        return []
    graphical = bool(os.environ.get('DISPLAY') or os.environ.get('WAYLAND_DISPLAY'))
    if graphical and shutil.which('pkexec'):
        return ['pkexec']
    return ['sudo']


def package_installed(manager, package):
    """Return True when the package query says the package is installed."""
    try:
        if manager == 'apt-get':
            result = subprocess.run(['dpkg-query', '-W', '-f=${Status}', package],
                                    capture_output=True, text=True)
            return result.returncode == 0 and 'install ok installed' in result.stdout
        if manager in ('dnf', 'zypper'):
            return subprocess.run(['rpm', '-q', package],
                                  capture_output=True).returncode == 0
        if manager == 'pacman':
            return subprocess.run(['pacman', '-Q', package],
                                  capture_output=True).returncode == 0
    except OSError:
        return False
    return False


def unique(items):
    result = []
    for item in items:
        if item and item not in result:
            result.append(str(item))
    return result


def step_build_tools(config, args, out):
    if args.only not in ('', 'buildtools'):
        return
    packages = config.get('packages') or {}
    if not isinstance(packages, dict):
        packages = {}
    shared = config.get('recompiler_packages') or {}
    if isinstance(shared, dict):
        packages = {k: list(shared.get(k, [])) + list(packages.get(k, [])) for k in set(shared) | set(packages)}
    if config.get('buildtools') is False and not packages:
        return
    step(out, 2)
    if sys.platform == 'darwin':
        try:
            installed = subprocess.run(['xcode-select', '-p'], capture_output=True).returncode == 0
        except OSError:
            installed = False
        if not installed:
            try:
                subprocess.run(['xcode-select', '--install'], capture_output=True)
            except OSError:
                pass
            raise SetupError(
                'Run xcode-select --install, finish the Command Line Tools install, then run Setup again.',
                'Rode xcode-select --install, termine a instalacao das Command Line Tools e rode o Setup de novo.')
        wanted = unique(list(packages.get('brew', [])))
        if wanted:
            brew = shutil.which('brew') or next((b for b in ('/opt/homebrew/bin/brew', '/usr/local/bin/brew') if os.path.isfile(b)), None)
            if not brew:
                raise SetupError(
                    'Homebrew is needed for: ' + ' '.join(wanted) + '. Install it from https://brew.sh, then run Setup again.',
                    'O Homebrew e necessario para: ' + ' '.join(wanted) + '. Instale pelo https://brew.sh e rode o Setup de novo.')
            missing = [pkg for pkg in wanted if subprocess.run([brew, 'list', '--versions', pkg], capture_output=True).returncode != 0]
            if missing:
                say(out, ' '.join([brew, 'install'] + missing))
                if not ask(out, 'Install them now?', 'Instalar agora?', args.yes):
                    raise SetupError('These Homebrew packages are required.', 'Estes pacotes do Homebrew sao necessarios.')
                if run_command([brew, 'install'] + missing, None, out) != 0:
                    raise SetupError('Installing the Homebrew packages failed.', 'A instalacao dos pacotes do Homebrew falhou.')
        say(out, 'OK', 'OK')
        return

    manager = detect_package_manager()
    compiler = shutil.which('clang++') or shutil.which('g++')
    pkgconfig = shutil.which('pkg-config')
    wanted = unique(list(packages.get(manager, [])) if manager else [])
    if not compiler and manager:
        wanted.insert(0, COMPILER_PACKAGES.get(manager, 'g++'))
    if not pkgconfig and manager:
        wanted.append(PKGCONFIG_PACKAGES.get(manager, 'pkg-config'))
    missing = [pkg for pkg in wanted if not package_installed(manager, pkg)]
    if missing:
        if not manager:
            raise SetupError(
                'No supported package manager was found (apt-get, dnf, pacman or zypper); install the C/C++ build tools and pkg-config, then run Setup again.',
                'Nenhum gerenciador de pacotes suportado foi encontrado (apt-get, dnf, pacman ou zypper); instale as ferramentas C/C++ e o pkg-config e rode o Setup de novo.')
        command = PACKAGE_COMMANDS[manager] + missing
        say(out, 'The C/C++ build tools and missing packages will be installed (needs administrator permission).',
            'As ferramentas de build C/C++ e os pacotes faltantes serao instalados (pede permissao de administrador).')
        say(out, ' '.join(privileged_prefix() + command))
        if not ask(out, 'Install them now?', 'Instalar agora?', args.yes):
            raise SetupError('The C/C++ build tools are required.', 'O C++ Build Tools e necessario.')
        if run_command(privileged_prefix() + command, None, out) != 0:
            raise SetupError('Installing the build tools failed.', 'A instalacao das ferramentas de build falhou.')
    if not (shutil.which('clang++') or shutil.which('g++')) or not shutil.which('pkg-config'):
        raise SetupError('A C/C++ compiler and pkg-config are required.',
                         'Sao necessarios um compilador C/C++ e o pkg-config.')
    say(out, 'OK', 'OK')


def run_command(command, cwd, out):
    """Run a command with its output copied line by line to stdout and the log."""
    process = subprocess.Popen(command, cwd=cwd, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True,
                               errors='replace', bufsize=1)
    assert process.stdout is not None
    for line in process.stdout:
        out.write(line)
    return process.wait()


def step_tools(root, args, out):
    if args.only not in ('', 'tools'):
        return {}
    step(out, 3)
    download_gb = config_download_gb(root)
    say(out, f'Kept inside this folder; the first download is about {download_gb} GB.',
        f'Ficam dentro desta pasta; o primeiro download tem cerca de {download_gb} GB.')
    command = [sys.executable, str(Path(__file__).with_name('setup_tools.py')),
               '--root', str(root)]
    if args.tools_dir:
        command += ['--dir', str(args.tools_dir)]
    result = subprocess.run(command, cwd=root, capture_output=True, text=True)
    if result.stderr:
        out.write(result.stderr)
    if result.returncode != 0:
        raise SetupError('Downloading the build tools failed. Check your internet connection.',
                         'O download das ferramentas falhou. Confira sua conexao com a internet.')
    tool = {}
    for line in result.stdout.splitlines():
        match = re.match(r'^(\w+)=(.*)$', line)
        if match:
            tool[match.group(1)] = match.group(2)
    front = [entry for entry in tool.get('PATH', '').split(os.pathsep) if entry]
    if tool.get('CC'):
        os.environ['CC'] = tool['CC']
        os.environ['CXX'] = tool.get('CXX', 'c++')
    if tool.get('JAVA_HOME'):
        os.environ['JAVA_HOME'] = tool['JAVA_HOME']
        front.append(str(Path(tool['JAVA_HOME']) / 'bin'))
    if tool.get('GHIDRA'):
        os.environ['GHIDRA'] = tool['GHIDRA']
    if tool.get('LLVM'):
        os.environ['LLVM'] = tool['LLVM']
    python_dir = str(Path(sys.executable).parent)
    current = os.environ.get('PATH', '')
    os.environ['PATH'] = os.pathsep.join(unique(front + [python_dir] + current.split(os.pathsep)))
    say(out, 'OK', 'OK')
    return tool


def config_download_gb(root):
    try:
        config = json.loads((root / 'setup.json').read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return '?'
    return config.get('download_gb', '?')


def file_sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as source:
        while chunk := source.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def game_ok(root, install):
    """True when every required file under game_files has the pinned SHA-256."""
    folder = root / 'game_files'
    for name, sha in (install.get('required_files') or {}).items():
        path = folder / name
        if not path.is_file() or file_sha256(path).lower() != str(sha).lower():
            return False
    return True


def link_game_files(root, source, create_symlink=None):
    """Make game_files a symlink to source, replacing an existing link only."""
    link = root / 'game_files'
    if link.is_symlink():
        link.unlink()
    elif link.exists():
        raise SetupError(f'{link} exists and is not a link; move it away and run Setup again.',
                         f'{link} existe e nao e um link; mova-a e rode o Setup de novo.')
    if not source:
        raise SetupError('A game folder is required with --source for this game.',
                         'E necessario informar a pasta do jogo com --source neste jogo.')
    target = Path(source).expanduser().resolve()
    if not target.is_dir():
        raise SetupError(f'Not a folder: {source}', f'Nao e uma pasta: {source}')
    if create_symlink:
        create_symlink(link, target)
    else:
        link.symlink_to(target, target_is_directory=True)
    return target


def step_game(config, install, root, args, out):
    if args.only not in ('', 'game'):
        return
    step(out, 4, f'Your game ({config.get("disc", "")})',
         f'Seu jogo ({config.get("disc", "")})')
    if not game_ok(root, install) and install.get('mode') == 'link':
        target = link_game_files(root, args.source)
        say(out, f'game_files -> {target}', f'game_files -> {target}')
    if not game_ok(root, install) and os.environ.get('PORT_LAUNCHER') == '1':
        # The launcher copies and checks the game files itself before it starts this script; no installer window here.
        raise SetupError(f'game_files does not hold the supported version: {config.get("disc", "")}. Choose the disc of the game in the launcher.',
                         f'game_files nao contem a versao suportada: {config.get("disc", "")}. Escolha o disco do jogo no launcher.')
    if not game_ok(root, install):
        own = Path(__file__).resolve().parents[1] / 'installer.py'
        installer = own if own.is_file() else root / 'tools/port-installer/installer.py'
        if not installer.is_file():
            raise SetupError('installer.py was not found.', 'O installer.py nao foi encontrado.')
        say(out, 'A window opens: choose your disc image (.iso) or extracted game folder, then wait for it to finish.',
            'Uma janela vai abrir: escolha a imagem do disco (.iso) ou a pasta extraida do jogo e espere terminar.')
        command = [sys.executable, str(installer), '--config', str(root / 'installer.json')]
        if args.source:
            command += ['--source', args.source]
        if run_command(command, root, out) != 0 or not game_ok(root, install):
            raise SetupError(f'game_files does not hold the supported version: {config.get("disc", "")}.',
                             f'game_files nao contem a versao suportada: {config.get("disc", "")}.')
    say(out, 'OK', 'OK')


def free_memory_bytes():
    if sys.platform == 'darwin':
        try:
            output = subprocess.check_output(['vm_stat'], text=True)
            page_size = int(os.sysconf('SC_PAGE_SIZE'))
            pages = 0
            for key in ('Pages free:', 'Pages inactive:'):
                match = re.search(re.escape(key) + r'\s+(\d+)\.', output)
                if match:
                    pages += int(match.group(1))
            return pages * page_size
        except (OSError, ValueError, subprocess.CalledProcessError):
            return 0
    try:
        for line in Path('/proc/meminfo').read_text(encoding='utf-8').splitlines():
            if line.startswith('MemAvailable:'):
                return int(line.split()[1]) * 1024
    except OSError:
        pass
    return 0


def compute_jobs(config, jobs=0, cpu_count=None, free_bytes=None):
    """jobs = max(1, min(cpu-2, free RAM / gb_per_job)), capped by max_jobs."""
    if jobs and jobs > 0:
        return int(jobs)
    cpus = cpu_count if cpu_count is not None else (os.cpu_count() or 1)
    if free_bytes is None:
        free_bytes = free_memory_bytes()
    gb_per_job = float(config.get('gb_per_job') or 1)
    by_ram = int(free_bytes / (gb_per_job * (1024 ** 3)))
    jobs = max(1, min(cpus - 2, by_ram))
    max_jobs = config.get('max_jobs')
    if max_jobs:
        jobs = min(jobs, int(max_jobs))
    return jobs


def expand(text, python, root, game_files, jobs, llvm):
    return (str(text)
            .replace('{python}', str(python))
            .replace('{root}', str(root))
            .replace('{game_files}', str(game_files))
            .replace('{jobs}', str(jobs))
            .replace('{llvm}', str(llvm or '')))


def step_requirements(config, root, out):
    requirements = config.get('requirements')
    if not requirements:
        return sys.executable
    venv = root / '.local/venv'
    venv_python = venv / 'bin/python'
    if not venv_python.is_file():
        say(out, 'Creating the Python environment ...', 'Criando o ambiente Python ...')
        if run_command([sys.executable, '-m', 'venv', str(venv)], root, out) != 0:
            raise SetupError('Creating the Python environment failed.',
                             'A criacao do ambiente Python falhou.')
    if run_command([str(venv_python), '-m', 'pip', 'install',
                    '--disable-pip-version-check', '-q', '-r',
                    str(root / requirements)], root, out) != 0:
        raise SetupError('Installing the Python packages failed.',
                         'A instalacao dos pacotes Python falhou.')
    os.environ['PATH'] = os.pathsep.join([str(venv_python.parent),
                                          os.environ.get('PATH', '')])
    return venv_python


def step_build(config, tool, root, args, out):
    if args.only:
        return
    step(out, 5)
    say(out, f'This takes a while (about {config.get("minutes", "?")} minutes on a recent PC). Keep this window open.',
        f'Isso demora (cerca de {config.get("minutes", "?")} minutos num PC recente). Deixe esta janela aberta.')
    python = step_requirements(config, root, out)
    jobs = compute_jobs(config, args.jobs)
    game_files = root / 'game_files'
    llvm = tool.get('LLVM', '')

    for item in config.get('steps') or []:
        if args.prepare_only and not item.get('prepare'):
            continue
        done_if = root / item['done_if'] if item.get('done_if') else None
        if not args.rebuild and done_if and done_if.exists():
            say(out, f'- {item.get("name", "")}: already done',
                f'- {item.get("name", "")}: ja feito')
            continue
        for name, value in (item.get('env') or {}).items():
            os.environ[name] = expand(value, python, root, game_files, jobs, llvm)
        while True:
            say(out, f'- {item.get("name", "")} ({jobs} jobs)',
                f'- {item.get("name", "")} ({jobs} compilacoes ao mesmo tempo)')
            run = item.get('run') or []
            if isinstance(run, str):
                run = shlex.split(run)
            command = [expand(value, python, root, game_files, jobs, llvm) for value in run]
            reuse_if = root / item['reuse_if'] if item.get('reuse_if') else None
            if reuse_if and not args.rebuild and reuse_if.exists():
                reuse_arg = item.get('reuse_arg') or []
                if isinstance(reuse_arg, str):
                    reuse_arg = [reuse_arg]
                command += [expand(value, python, root, game_files, jobs, llvm)
                            for value in reuse_arg]
            if not command:
                raise SetupError(f'The build step has no command: {item.get("name", "")}.',
                                 f'A etapa de build nao tem comando: {item.get("name", "")}.')
            if run_command(command, root, out) == 0:
                break
            retry_if = root / item['retry_if'] if item.get('retry_if') else None
            if jobs <= 1 or not retry_if or not retry_if.exists():
                raise SetupError(f'The build failed at: {item.get("name", "")}.',
                                 f'O build falhou em: {item.get("name", "")}.')
            jobs = max(1, jobs // 2)
            say(out, 'The build stopped; trying again with fewer parallel jobs.',
                'O build parou; tentando de novo com menos compilacoes ao mesmo tempo.')
    if args.prepare_only:
        out.print()
        say(out, 'Done: the build files are prepared from your disc. setup.sh compiles them (finished steps are skipped).',
            'Pronto: os arquivos do build foram preparados a partir do seu disco. O setup.sh compila (as etapas prontas sao puladas).')
        return False
    return True


def safe_name(name):
    cleaned = re.sub(r'[^A-Za-z0-9._-]+', '_', str(name)).strip('._') or 'game'
    return cleaned


def play_script(config):
    """The game's start script on this system: Play.cmd -> Play.sh (the launcher does the same)."""
    play = config.get('play') or ''
    base, ext = os.path.splitext(play)
    return base + '.sh' if ext.lower() in ('.cmd', '.bat', '.ps1') else play


def make_shortcut(config, root, args, out):
    if args.no_shortcut or args.only or args.prepare_only:
        return
    play = play_script(config)
    if not play or not (root / play).exists():
        return
    if sys.platform == 'darwin':
        say(out, 'macOS does not use .desktop shortcuts; the play script is in this folder.',
            'O macOS nao usa atalhos .desktop; o script de jogar esta nesta pasta.')
        return
    if not ask(out, 'Create a desktop shortcut to play?',
               'Criar um atalho na area de trabalho para jogar?', args.yes):
        return
    folder = Path.home() / '.local/share/applications'
    folder.mkdir(parents=True, exist_ok=True)
    play_path = str((root / play).resolve()).replace('"', '\\"')
    desktop = folder / f'{safe_name(config.get("game", "game"))}.desktop'
    desktop.write_text(
        '[Desktop Entry]\n'
        'Type=Application\n'
        f'Name={config.get("game", "Game")}\n'
        f'Exec=/bin/bash "{play_path}"\n'
        f'Path={root}\n'
        'Terminal=false\n'
        'Categories=Game;\n',
        encoding='utf-8')
    say(out, f'Shortcut: {desktop}', f'Atalho: {desktop}')


def check_disk(config, root, args, out):
    if args.only:
        return
    needed = int(config.get('disk_gb') or 0)
    free = shutil.disk_usage(root).free // (1024 ** 3)
    if free >= needed:
        return
    if config.get('buildtools') is False:
        say(out, f'Only {free} GB free on {root}:. About {needed} GB are needed here.',
            f'So ha {free} GB livres em {root}:. Sao necessarios cerca de {needed} GB aqui.')
    else:
        say(out, f'Only {free} GB free on {root}:. About {needed} GB are needed here, plus about 7 GB for the build tools.',
            f'So ha {free} GB livres em {root}:. Sao necessarios cerca de {needed} GB aqui, mais cerca de 7 GB para o build tools.')
    if not args.yes and not ask(out, 'Continue anyway?', 'Continuar mesmo assim?', False):
        raise SetupError('Not enough disk space.', 'Nao ha espaco em disco suficiente.')


def make_parser():
    class Parser(argparse.ArgumentParser):
        def error(self, message):
            raise SetupError(f'Setup arguments: {message}',
                             f'Argumentos do Setup: {message}')

    parser = Parser(description='Linux/macOS port setup')
    parser.add_argument('--yes', action='store_true')
    parser.add_argument('--rebuild', action='store_true')
    parser.add_argument('--tools-only', action='store_true')
    parser.add_argument('--no-shortcut', action='store_true')
    parser.add_argument('--prepare-only', action='store_true')
    parser.add_argument('--jobs', type=int, default=0)
    parser.add_argument('--only', choices=('', 'python', 'buildtools', 'tools', 'game'), default='')
    parser.add_argument('--game-dir', default='')
    parser.add_argument('--tools-dir', default='')
    parser.add_argument('--source', default='')
    return parser


def finish(out, config):
    out.print()
    play = play_script(config)
    if config.get('pc_menu') is False:
        say(out, f'Done! Play with {play} (or the desktop shortcut). The PC settings are in the launcher.',
            f'Pronto! Jogue pelo {play} (ou pelo atalho na area de trabalho). As configuracoes de PC ficam no launcher.')
    else:
        say(out, f'Done! Play with {play} (or the desktop shortcut). F1 in the game opens the PC settings.',
            f'Pronto! Jogue pelo {play} (ou pelo atalho na area de trabalho). F1 no jogo abre as configuracoes de PC.')


def report_error(out, error):
    out.print()
    say(out, error.en, error.pt)
    say(out, 'Details are in setup-log.txt. You can run Setup again; finished steps are skipped.',
        'Os detalhes estao em setup-log.txt. Pode rodar o Setup de novo; as etapas prontas sao puladas.')


def main(argv=None):
    out = None
    try:
        args = make_parser().parse_args(argv)
        root = Path(args.game_dir).expanduser().resolve() if args.game_dir \
            else Path(__file__).resolve().parents[1]
        if not root.is_dir():
            raise SetupError(f'Game folder not found: {root}', f'Pasta do jogo nao encontrada: {root}')
        out = Output(root / LOG_NAME)
        setup, install = load_configs(root)
        config = merged_config(setup, OS_KEY)
        config['recompiler_packages'] = recompiler_config(root, OS_KEY).get('packages') or {}
        game = config.get('game', 'Port')
        say(out, t(f'Setup for {game} (PC port). Your game is never uploaded anywhere.',
                   f'Setup de {game} (port para PC). Seu jogo nunca e enviado para lugar nenhum.'))
        if args.prepare_only and not any(item.get('prepare') for item in config.get('steps') or []):
            say(out, 'Nothing to prepare separately for this game: setup.sh prepares and compiles in one step.',
                'Nada para preparar separadamente neste jogo: o setup.sh prepara e compila numa etapa so.')
            return 0
        check_disk(config, root, args, out)
        step(out, 1)
        say(out, f'OK: {sys.executable}', f'OK: {sys.executable}')
        if args.only == 'python':
            return 0
        step_build_tools(config, args, out)
        if args.only == 'buildtools':
            return 0
        tool = step_tools(root, args, out)
        if args.tools_only or args.only == 'tools':
            return 0
        step_game(config, install, root, args, out)
        if args.only == 'game':
            return 0
        if step_build(config, tool, root, args, out):
            make_shortcut(config, root, args, out)
            finish(out, config)
        return 0
    except SetupError as error:
        if out:
            report_error(out, error)
        else:
            print(error.en, flush=True)
        return 1
    except (OSError, ValueError) as error:
        message = f'Setup failed: {error}'
        if out:
            report_error(out, SetupError(message))
        else:
            print(message, flush=True)
        return 1
    finally:
        if out:
            out.close()


if __name__ == '__main__':
    sys.exit(main())
