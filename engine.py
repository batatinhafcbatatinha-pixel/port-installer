"""Shared installer engine; all progress is emitted to the caller, never to Tk."""
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path
import shutil
import threading
import uuid

from media import Entry, inventory, safe_path
from layout import build_layout

BLOCK = 4 << 20


class Cancelled(Exception):
    pass


def fingerprint(path):
    s = path.stat()
    return s.st_size, s.st_mtime_ns


def target_path(root, name):
    safe_path(name)
    path = root.joinpath(*name.split('/'))
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError(f'Destino fora da pasta do jogo: {name}')
    for p in (path, *path.parents):
        if p.is_symlink() or (hasattr(p, 'is_junction') and p.is_junction()):
            raise ValueError(f'Link/junção não permitido: {p}')
        if p == root:
            break
    return path


def install(source, root, config, emit=lambda event: None, cancel=None):
    source, root = Path(source).resolve(), Path(root).resolve()
    cancel = cancel or threading.Event()
    root.mkdir(parents=True, exist_ok=True)
    lock = root / '.game-install.lock'
    with lock.open('x'):
        pass
    try:
        return _install(source, root, config, emit, cancel)
    finally:
        lock.unlink()


def _install(source, root, config, emit, cancel):
    def check():
        if cancel.is_set():
            raise Cancelled('Instalação cancelada. A origem foi preservada.')

    emit(dict(type='status', text='Lendo arquivos da origem…'))
    output = target_path(root, config.get('destination', 'game_files'))
    is_iso = source.is_file()
    if not is_iso and not source.is_dir():
        raise ValueError('Selecione uma ISO ou pasta existente')
    if source == output or (source.is_dir() and (output.is_relative_to(source) or source.is_relative_to(output))):
        raise ValueError('A pasta de origem e o destino devem ser separados e não podem conter um ao outro')
    source_states = {}
    with ExitStack() as stack:
        stream = None
        if is_iso:
            source_states[source] = fingerprint(source)
            stream = stack.enter_context(source.open('rb'))
            entries = inventory(stream, source_states[source][0], config['filesystem'])
        else:
            entries = []
            for folder, dirs, files in os.walk(source, followlinks=False):
                check()
                for name in dirs + files:
                    p = Path(folder) / name
                    if p.is_symlink() or (hasattr(p, 'is_junction') and p.is_junction()):
                        raise ValueError('A origem contém um link/junção: ' + str(p))
                for name in files:
                    p = Path(folder) / name
                    rel = p.relative_to(source).as_posix()
                    safe_path(rel)
                    if rel.startswith('.disc/'):
                        continue
                    source_states[p] = fingerprint(p)
                    entries.append(Entry(rel, source_states[p][0], []))
            if not entries:
                raise ValueError('A pasta está vazia')
        by_name = {e.path.casefold(): e for e in entries}
        if len(by_name) != len(entries):
            raise ValueError('Nomes duplicados sem distinção de maiúsculas')
        required = config['required_files']
        if not required:
            raise ValueError('Configuração sem identificação do jogo')
        for name, size in config.get('expected_files', {}).items():
            entry = by_name.get(name.casefold())
            if entry is None or entry.size != size:
                raise ValueError('Conteúdo incompleto ou versão diferente: ' + name)
        total = sum(e.size for e in entries)
        done = 0

        def chunks(entry):
            if stream is not None:
                for offset, count in entry.extents:
                    stream.seek(offset)
                    while count:
                        check()
                        blob = stream.read(min(BLOCK, count))
                        if not blob:
                            raise ValueError('Imagem truncada')
                        count -= len(blob)
                        yield blob
            else:
                with (source / entry.path).open('rb') as handle:
                    for blob in iter(lambda: handle.read(BLOCK), b''):
                        check()
                        yield blob

        for name, expected in required.items():
            check()
            entry = by_name.get(name.casefold())
            if entry is None:
                raise ValueError('Arquivo obrigatório ausente: ' + name)
            emit(dict(type='status', text='Identificando versão: ' + name))
            digest = hashlib.sha256()
            for blob in chunks(entry):
                digest.update(blob)
            if digest.hexdigest() != expected:
                raise ValueError('Jogo/região/revisão incompatível: ' + name)
        for name in config.get('required_paths', []):
            if not any(e.path.casefold() == name.casefold() or e.path.casefold().startswith(name.casefold().rstrip('/') + '/') for e in entries):
                raise ValueError('Conteúdo obrigatório ausente: ' + name)
        targets = [(e, target_path(output, e.path)) for e in entries]
        needed = sum(e.size for e, p in targets if not p.exists())
        if shutil.disk_usage(root).free < needed:
            raise ValueError('Espaço insuficiente para os arquivos e a mídia local')
        # Copy and readback are separate work units so progress includes verification.
        work_total = total * 2
        report_files = []

        def materialize(path, count, blocks, label):
            nonlocal done
            check()
            exists = path.exists()
            if exists and (not path.is_file() or path.stat().st_size != count):
                raise ValueError('Destino diferente; preservado: ' + str(path))
            path.parent.mkdir(parents=True, exist_ok=True)
            temporary = path.with_name('.' + path.name + '.' + uuid.uuid4().hex + '.part')
            emit(dict(type='status', text=('Comparando ' if exists else label + ' ') + str(path.relative_to(root))))
            digest, actual_count = hashlib.sha256(), 0
            try:
                with (path.open('rb') if exists else temporary.open('xb')) as handle:
                    for blob in blocks:
                        check()
                        digest.update(blob)
                        if exists:
                            if handle.read(len(blob)) != blob:
                                raise ValueError('Destino com conteúdo diferente; preservado: ' + str(path))
                        else:
                            handle.write(blob)
                        actual_count += len(blob)
                        done += len(blob)
                        emit(dict(type='progress', done=done, total=work_total))
                if actual_count != count:
                    raise ValueError('Tamanho da origem mudou: ' + str(path))
                emit(dict(type='status', text='Verificando ' + str(path.relative_to(root))))
                verified = hashlib.sha256()
                with (path if exists else temporary).open('rb') as handle:
                    for blob in iter(lambda: handle.read(BLOCK), b''):
                        check()
                        verified.update(blob)
                        done += len(blob)
                        emit(dict(type='progress', done=done, total=work_total))
                if verified.digest() != digest.digest():
                    raise ValueError('Falha na verificação: ' + str(path))
                if not exists:
                    if path.exists():
                        raise ValueError('Destino criado por outro processo: ' + str(path))
                    temporary.rename(path)
                return digest.hexdigest()
            finally:
                if temporary.exists():
                    temporary.unlink()

        for entry, path in targets:
            digest = materialize(path, entry.size, chunks(entry), 'Extraindo' if is_iso else 'Copiando')
            report_files.append(dict(path=entry.path, bytes=entry.size, sha256=digest))
        if config['filesystem'] == 'iso9660':
            emit(dict(type='status', text='Gerando índice de acesso aos arquivos extraídos…'))
            layout, metadata = build_layout(entries)
            for name, data in (('.disc/metadata.bin', metadata), ('.disc/layout.tsv', layout)):
                path = target_path(output, name)
                # Index artifacts are installer-owned; replace atomically on reinstallation.
                path.parent.mkdir(parents=True, exist_ok=True)
                temp = path.with_name(path.name + '.' + uuid.uuid4().hex + '.part')
                temp.write_bytes(data)
                temp.replace(path)
        for path, state in source_states.items():
            if fingerprint(path) != state:
                raise ValueError('A origem mudou durante a instalação: ' + str(path))
        check()
        report = dict(game=config['name'], verified=True, files=report_files,
                      discRetained=False, sourceDeleted=False)
        report_path = root / 'game-install-report.json'
        temp = root / ('.install-report.' + uuid.uuid4().hex)
        temp.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        temp.replace(report_path)
    return dict(report=report, source=str(source), sourceState=source_states.get(source),
                canDeleteSource=is_iso and not source.is_relative_to(output),
                reportPath=str(report_path))


def delete_source(result):
    """Called only after the UI asks for and receives explicit confirmation."""
    source = Path(result['source'])
    if not result['canDeleteSource'] or fingerprint(source) != result['sourceState']:
        raise ValueError('A origem mudou ou não pode ser apagada')
    source.unlink()
    result['report']['sourceDeleted'] = True
    Path(result['reportPath']).write_text(json.dumps(result['report'], indent=2) + '\n', encoding='utf-8')
