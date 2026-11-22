"""Bounded ISO9660 and XDVDFS readers. No external extraction programs."""
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
import struct

SECTOR = 2048


def safe_path(name):
    path = PurePosixPath(name)
    if not name or path.is_absolute() or '\\' in name or any(
        p in ('', '.', '..') or p.endswith((' ', '.')) or
        any(c in p for c in ':<>"|?*') or any(ord(c) < 32 for c in p) or
        p.split('.')[0].upper() in {'CON', 'PRN', 'AUX', 'NUL', *(f'COM{i}' for i in range(1, 10)), *(f'LPT{i}' for i in range(1, 10))}
        for p in name.split('/')
    ):
        raise ValueError(f'Caminho inseguro: {name!r}')
    return path


@dataclass
class Entry:
    path: str
    size: int
    extents: list


STFS_MAGIC = (b'LIVE', b'PIRS', b'CON ')
STFS_BLOCK = 0x1000


def is_stfs(path):
    """An Xbox 360 package (STFS: Xbox Live Arcade games, games on demand, title updates)."""
    try:
        if path.stat().st_size < 0xB000:
            return False
        with path.open('rb') as f:
            return f.read(4) in STFS_MAGIC
    except OSError:
        return False


def stfs_package(path):
    """The package a source names: the file itself, or the first package in a folder laid out as the console keeps
    them (<title id>/<content type>/<package>, file names without an extension); else None."""
    path = Path(path)
    if path.is_file():
        return path if is_stfs(path) else None
    if not path.is_dir():
        return None
    checked, level = 0, [path]
    for _ in range(4):
        nxt = []
        for folder in level:
            for p in sorted(folder.iterdir()):
                if p.is_dir() and not p.is_symlink():
                    nxt.append(p)
                elif p.is_file() and '.' not in p.name:
                    checked += 1
                    if checked > 64:
                        return None
                    if is_stfs(p):
                        return p
        level = nxt
    return None


def stfs_inventory(read, add, size):
    """Files of an STFS package. Data blocks are numbered without the hash tables interleaved with them: a level-0
    table before every 170 blocks, a level-1 table before every 170*170 and one level-2 table, each one block long
    (read-only packages, flag bit 0) or two. A file is a run of blocks (contiguous flag) or a chain through the
    level-0 hash entries."""
    head = read(0, 0x1000)
    if head[:4] not in STFS_MAGIC:
        raise ValueError('Pacote Xbox 360 (STFS) não encontrado')
    header_size, = struct.unpack_from('>I', head, 0x340)
    volume_type, = struct.unpack_from('>I', head, 0x3A9)
    if head[0x379] != 0x24 or volume_type != 0:
        raise ValueError('Pacote Xbox 360 sem volume STFS')
    flags = head[0x37B]
    table_blocks, = struct.unpack_from('<H', head, 0x37C)
    table_start = int.from_bytes(head[0x37E:0x381], 'little')
    allocated, = struct.unpack_from('>I', head, 0x395)
    shift = 0 if flags & 1 else 1
    base = (header_size + 0xFFF) & ~0xFFF
    if not 0 < table_blocks <= 4096 or allocated > 0xFFFFFF:
        raise ValueError('Tabela de arquivos STFS inválida')

    def offset(block):
        if block >= allocated:
            raise ValueError('Bloco STFS fora do pacote')
        b = block + (((block // 170) + 1) << shift)
        if block >= 170:
            b += ((block // 28900) + 1) << shift
            if block >= 28900:
                b += 1 << shift
        return base + b * STFS_BLOCK

    def following(block):
        if shift:
            raise ValueError('Pacote STFS com duas tabelas de hash por nível: arquivo fragmentado não suportado')
        entry = read(offset(block // 170 * 170) - STFS_BLOCK + block % 170 * 0x18, 0x18)
        return int.from_bytes(entry[0x15:0x18], 'big')

    table, block = b'', table_start
    for i in range(table_blocks):
        table += read(offset(block), STFS_BLOCK)
        if i + 1 < table_blocks:
            block = following(block) if not shift else block + 1
    records = {}
    for i in range(len(table) // 0x40):
        e = table[i * 0x40:(i + 1) * 0x40]
        if not e[0x28] & 0x3F:
            continue
        records[i] = (e[:e[0x28] & 0x3F].decode('ascii'), e[0x28],
                      int.from_bytes(e[0x2F:0x32], 'little'), struct.unpack_from('>h', e, 0x32)[0],
                      struct.unpack_from('>I', e, 0x34)[0])

    def full(i):
        parts, seen = [], set()
        while i != -1:
            if i in seen or i not in records or len(parts) > 64:
                raise ValueError('Estrutura de diretórios inválida')
            seen.add(i)
            parts.append(records[i][0])
            i = records[i][3]
        return '/'.join(reversed(parts))

    for i, (name, fl, start, parent, length) in sorted(records.items()):
        safe_path(name)
        if fl & 0x80:
            continue
        extents, block, left = [], start, length
        for _ in range(-(-length // STFS_BLOCK)):
            o, n = offset(block), min(STFS_BLOCK, left)
            if extents and extents[-1][0] + extents[-1][1] == o:
                extents[-1] = (extents[-1][0], extents[-1][1] + n)
            else:
                extents.append((o, n))
            left -= n
            if left:
                block = block + 1 if fl & 0x40 else following(block)
        add(full(i), length, extents)


def stfs_title(package):
    """The display name an Xbox 360 package carries (UTF-16 BE at 0x411), or None."""
    try:
        with Path(package).open('rb') as f:
            f.seek(0x411)
            name = f.read(0x80).decode('utf-16-be', 'replace').split('\0')[0].strip()
        return name or None
    except OSError:
        return None


def inventory(stream, size, filesystem):
    def read(offset, count):
        if offset < 0 or count < 0 or offset + count > size:
            raise ValueError('Imagem truncada ou extensão inválida')
        stream.seek(offset)
        data = stream.read(count)
        if len(data) != count:
            raise ValueError('Leitura incompleta')
        return data

    entries, paths, visited = [], set(), set()

    def add(name, count, extents):
        safe_path(name)
        key = name.casefold()
        if key in paths or len(paths) >= 200000:
            raise ValueError('Caminhos duplicados ou arquivos demais')
        for offset, length in extents:
            if offset < 0 or offset + length > size:
                raise ValueError('Arquivo fora da imagem')
        paths.add(key)
        entries.append(Entry(name, count, extents))

    def directory_guard(sector, count, depth):
        if depth > 64 or count > 64 << 20 or (sector, count) in visited:
            raise ValueError('Estrutura de diretórios inválida')
        visited.add((sector, count))

    if filesystem == 'xdvdfs':
        magic = b'MICROSOFT*XBOX*MEDIA'
        for base in (0, 0xFD90000, 0x2080000, 0x18300000):
            if base + 33 * SECTOR > size:
                continue
            header = read(base + 32 * SECTOR, SECTOR)
            if header[:20] == magic and header[-20:] == magic:
                break
        else:
            raise ValueError('Volume Xbox/Xbox 360 XDVDFS não encontrado')

        def walk(sector, count, prefix='', depth=0):
            directory_guard(sector, count, depth)
            table = read(base + sector * SECTOR, count)
            pending, seen = ([0] if count else []), set()
            while pending:
                offset = pending.pop()
                if offset in seen:
                    raise ValueError('Ciclo na árvore XDVDFS')
                seen.add(offset)
                if offset + 14 > len(table):
                    raise ValueError('Registro XDVDFS truncado')
                left, right, start, length, attr, n = struct.unpack_from('<HHIIBB', table, offset)
                if left == right == 0xffff:
                    continue
                if offset + 14 + n > len(table):
                    raise ValueError('Nome XDVDFS truncado')
                name = table[offset+14:offset+14+n].decode('ascii')
                safe_path(name)
                path = prefix + name
                if attr & 0x10:
                    walk(start, length, path + '/', depth + 1)
                else:
                    add(path, length, [(base + start * SECTOR, length)])
                for child in (left, right):
                    if child and child != 0xffff:
                        pending.append(child * 4)
        walk(*struct.unpack_from('<II', header, 20))
    elif filesystem == 'iso9660':
        pvd = read(16 * SECTOR, SECTOR)
        if pvd[:7] != b'\x01CD001\x01' or struct.unpack_from('<H', pvd, 128)[0] != SECTOR:
            raise ValueError('Volume ISO9660 de 2048 bytes não encontrado')
        volume_sectors = struct.unpack_from('<I', pvd, 80)[0]
        if volume_sectors != struct.unpack_from('>I', pvd, 84)[0] or volume_sectors * SECTOR > size:
            raise ValueError('Volume ISO9660 truncado ou tamanho inválido')

        def record(data):
            if len(data) < 34 or data[0] != len(data) or 33 + data[32] > len(data):
                raise ValueError('Registro ISO9660 inválido')
            sector, count = struct.unpack_from('<I', data, 2)[0], struct.unpack_from('<I', data, 10)[0]
            if sector != struct.unpack_from('>I', data, 6)[0] or count != struct.unpack_from('>I', data, 14)[0] or data[26] or data[27]:
                raise ValueError('Extensão ISO9660 inválida/intercalada')
            return sector + data[1], count, data[25], data[33:33+data[32]]

        def walk(raw, prefix='', depth=0):
            sector, count, _, _ = record(raw)
            directory_guard(sector, count, depth)
            data = read(sector * SECTOR, count)
            offset, continuation = 0, None
            while offset < len(data):
                n = data[offset]
                if not n:
                    offset = (offset // SECTOR + 1) * SECTOR
                    continue
                raw = data[offset:offset+n]
                offset += n
                start, length, flags, rawname = record(raw)
                if rawname in (b'\0', b'\1'):
                    continue
                name = rawname.decode('ascii').split(';', 1)[0]
                safe_path(name)
                path = prefix + name
                if flags & 2:
                    if continuation:
                        raise ValueError('Multiextensão incompleta')
                    walk(raw, path + '/', depth + 1)
                else:
                    if continuation and continuation.path != path:
                        raise ValueError('Multiextensão fora de ordem')
                    if continuation:
                        continuation.size += length
                        continuation.extents.append((start * SECTOR, length))
                    else:
                        add(path, length, [(start * SECTOR, length)])
                        continuation = entries[-1]
                    if not flags & 0x80:
                        continuation = None
            if continuation:
                raise ValueError('Multiextensão incompleta')
        walk(pvd[156:156+pvd[156]])
    elif filesystem == 'stfs':
        stfs_inventory(read, add, size)
    else:
        raise ValueError('Sistema de arquivos não suportado')
    for entry in entries:
        if any(offset + length > size for offset, length in entry.extents):
            raise ValueError('Arquivo fora da imagem')
    if not entries:
        raise ValueError('Nenhum arquivo na imagem')
    return entries
