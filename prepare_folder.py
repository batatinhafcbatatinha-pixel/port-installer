"""Prepare a PS2 extracted folder for runtime sector reads, without opening any ISO."""
import argparse
import hashlib
import json
from pathlib import Path
import uuid

from engine import target_path
from layout import build_layout
from media import Entry


def prepare(root, config, rebuild=False):
    root = Path(root).resolve()
    folder = target_path(root, config.get('destination', 'game_files'))
    required = config['required_files']
    expected = config.get('expected_files', {})
    for name, digest in required.items():
        file = target_path(folder, name)
        if not file.is_file():
            raise ValueError('Arquivo obrigatório ausente: ' + name)
        with file.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != digest:
                raise ValueError('Versão incompatível: ' + name)
    entries = []
    paths = list(expected) if expected else [p.relative_to(folder).as_posix() for p in sorted(folder.rglob('*')) if p.is_file() and not any(part in ('.disc', 'mc0', 'mc1') for part in p.relative_to(folder).parts)]
    for name in paths:
        file = target_path(folder, name)
        if not file.is_file() or (name in expected and file.stat().st_size != expected[name]):
            raise ValueError('Conteúdo ausente ou tamanho incorreto: ' + name)
        entries.append(Entry(name, file.stat().st_size, []))
    if not entries:
        raise ValueError('Pasta de jogo vazia')
    # Retain an installer-generated original layout when it still maps this exact folder.
    # This also preserves offsets of titles with absolute-sector tables inside their assets.
    index = folder / '.disc/layout.tsv'
    if not rebuild and index.is_file() and (folder / '.disc/metadata.bin').is_file():
        try:
            lines = index.read_text(encoding='utf-8').splitlines()
            magic, total = lines[0].split('\t')
            if magic != 'EXTRACTED_DISC_V1' or int(total) <= 0:
                raise ValueError('Invalid layout header')
            covered = {}
            previous_end = 0
            for row in lines[1:]:
                start, length, position, name = row.split('\t')
                start, length, position = int(start), int(length), int(position)
                target_path(folder, name)
                if start < previous_end or start + length > int(total) or min(start, length, position) < 0:
                    raise ValueError('Invalid extent')
                previous_end = start + length
                if name != '.disc/metadata.bin':
                    covered[name] = max(covered.get(name, 0), position + length)
            if set(covered) == {entry.path for entry in entries} and all(covered[e.path] >= e.size and covered[e.path] - e.size < 2048 for e in entries):
                return dict(files=len(entries), source='game_files', isoRequired=False, layoutReused=True)
        except (ValueError, IndexError, UnicodeError):
            pass
    layout, metadata = build_layout(entries)
    for name, payload in (('.disc/metadata.bin', metadata), ('.disc/layout.tsv', layout)):
        path = target_path(folder, name)
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists() and path.read_bytes() == payload:
            continue
        temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.part')
        temporary.write_bytes(payload)
        temporary.replace(path)
    return dict(files=len(entries), metadataBytes=len(layout)+len(metadata), source='game_files', isoRequired=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--rebuild-layout', action='store_true', help='Regenerate a deterministic layout from the extracted files')
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding='utf-8-sig'))
    if config['filesystem'] != 'iso9660':
        parser.error('Folder sector metadata is only needed for PS2 ports')
    print(json.dumps(prepare(args.root, config, args.rebuild_layout)))


if __name__ == '__main__':
    main()
