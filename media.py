"""Bounded ISO9660 and XDVDFS readers. No external extraction programs."""
from dataclasses import dataclass
from pathlib import PurePosixPath
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
    else:
        raise ValueError('Sistema de arquivos não suportado')
    for entry in entries:
        if any(offset + length > size for offset, length in entry.extents):
            raise ValueError('Arquivo fora da imagem')
    if not entries:
        raise ValueError('Nenhum arquivo na imagem')
    return entries
