#!/usr/bin/env bash
# One-click Setup entry point for Linux and macOS. Kept compatible with bash 3.2 (macOS).
# Finds Python 3.11+ with Tk, then hands the real work to setup_posix.py.
set -u

ARGS=("$@")
YES=0
ONLY=""
PT=0
case "${LANG:-} ${LC_ALL:-} ${LC_MESSAGES:-}" in
  pt*) PT=1 ;;
esac

say2() {
  if [ "$PT" -eq 1 ]; then
    printf '%s\n' "$2"
  else
    printf '%s\n' "$1"
  fi
}

usage() {
  say2 'Usage: setup.sh [--yes] [--rebuild] [--tools-only] [--no-shortcut] [--prepare-only] [--jobs N] [--only python|buildtools|tools|game] [--game-dir DIR] [--tools-dir DIR] [--source PATH]' \
       'Uso: setup.sh [--yes] [--rebuild] [--tools-only] [--no-shortcut] [--prepare-only] [--jobs N] [--only python|buildtools|tools|game] [--game-dir DIR] [--tools-dir DIR] [--source PATH]'
}

while [ $# -gt 0 ]; do
  case "$1" in
    --yes)
      YES=1
      ;;
    --rebuild|--tools-only|--no-shortcut|--prepare-only)
      ;;
    --jobs|--only|--game-dir|--tools-dir|--source)
      if [ $# -lt 2 ]; then
        say2 "Missing value for $1" "Falta o valor de $1"
        usage
        exit 1
      fi
      if [ "$1" = "--only" ]; then ONLY="$2"; fi
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      say2 "Unknown option: $1" "Opcao desconhecida: $1"
      usage
      exit 1
      ;;
  esac
  shift
done

ask() {
  if [ "$YES" -eq 1 ]; then return 0; fi
  if [ "$PT" -eq 1 ]; then
    printf '%s [S/n] ' "$1"
  else
    printf '%s [Y/n] ' "$1"
  fi
  read answer
  case "$answer" in
    ''|[SsYy]*) return 0 ;;
    *) return 1 ;;
  esac
}

python_ok() {
  "$1" -c 'import sys, tkinter; assert sys.version_info >= (3, 11)' >/dev/null 2>&1
}

find_python() {
  cmd=""
  for cmd in python3 python3.13 python3.12 python3.11; do
    if command -v "$cmd" >/dev/null 2>&1; then
      found=$(command -v "$cmd")
      if python_ok "$found"; then
        printf '%s\n' "$found"
        return 0
      fi
    fi
  done
  if [ "$(uname -s)" = "Darwin" ]; then
    for found in /opt/homebrew/bin/python3 /usr/local/bin/python3 /Library/Frameworks/Python.framework/Versions/3.*/bin/python3; do
      if [ -x "$found" ] && python_ok "$found"; then
        printf '%s\n' "$found"
        return 0
      fi
    done
  fi
  return 1
}

if PY=$(find_python); then
  script_dir=$(cd "$(dirname "$0")" && pwd)
  exec "$PY" "$script_dir/setup_posix.py" ${ARGS[@]+"${ARGS[@]}"}
fi

printf '%s\n' '== 1/5 Python'
if [ "$(uname -s)" = "Darwin" ]; then
  say2 'Python 3.11+ with Tk was not found. Install Python 3.12 from python.org, or run: brew install python-tk@3.12' \
       'Nao foi encontrado Python 3.11+ com Tk. Instale o Python 3.12 do python.org, ou rode: brew install python-tk@3.12'
  exit 1
fi

PM=""
if command -v apt-get >/dev/null 2>&1; then PM=apt-get
elif command -v dnf >/dev/null 2>&1; then PM=dnf
elif command -v pacman >/dev/null 2>&1; then PM=pacman
elif command -v zypper >/dev/null 2>&1; then PM=zypper
fi
if [ -z "$PM" ]; then
  say2 'No supported package manager was found (apt-get, dnf, pacman or zypper). Install Python 3.11+ with Tk yourself and run Setup again.' \
       'Nenhum gerenciador de pacotes suportado foi encontrado (apt-get, dnf, pacman ou zypper). Instale Python 3.11+ com Tk e rode o Setup de novo.'
  exit 1
fi

case "$PM" in
  apt-get) INSTALL=(apt-get install -y python3 python3-tk python3-venv) ;;
  dnf) INSTALL=(dnf install -y python3 python3-tkinter) ;;
  pacman) INSTALL=(pacman -S --noconfirm python tk) ;;
  zypper) INSTALL=(zypper install -y python3 python3-tk) ;;
esac

if { [ -n "${DISPLAY:-}" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; } && command -v pkexec >/dev/null 2>&1; then
  PRIV=(pkexec)
else
  PRIV=(sudo)
fi

say2 'Python 3.11+ with Tk will be installed.' 'O Python 3.11+ com Tk sera instalado.'
printf '%s %s\n' "${PRIV[*]}" "${INSTALL[*]}"
if ! ask 'Install Python now?' 'Instalar o Python agora?'; then
  say2 'Python is required.' 'O Python e necessario.'
  exit 1
fi
if ! "${PRIV[@]}" "${INSTALL[@]}"; then
  say2 'Installing Python failed. Run Setup again.' 'A instalacao do Python falhou. Rode o Setup de novo.'
  exit 1
fi

hash -r 2>/dev/null || true
if PY=$(find_python); then
  if [ "$ONLY" = "python" ]; then
    printf '%s\n' "OK: $PY"
    exit 0
  fi
  script_dir=$(cd "$(dirname "$0")" && pwd)
  exec "$PY" "$script_dir/setup_posix.py" ${ARGS[@]+"${ARGS[@]}"}
fi
say2 'Python was installed but not found. Run Setup again.' 'O Python foi instalado mas nao foi encontrado. Rode o Setup de novo.'
exit 1
