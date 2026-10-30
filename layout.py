"""Small directory metadata plus extent map. Payload stays in ordinary game_files files."""
import struct
from pathlib import PurePosixPath
from media import SECTOR


def build_layout(entries):
    dirs = {'': {'children': [], 'parent': '', 'directory': True}}
    nodes = dict(dirs)
    for entry in entries:
        parts = PurePosixPath(entry.path).parts
        for depth in range(1, len(parts)):
            path = '/'.join(parts[:depth])
            if path not in nodes:
                parent = '/'.join(parts[:depth-1])
                node = dict(children=[], parent=parent, directory=True)
                nodes[path] = dirs[path] = node
                nodes[parent]['children'].append(path)
        parent = '/'.join(parts[:-1])
        if entry.path in nodes:
            raise ValueError('Conflito entre arquivo e diretório')
        nodes[entry.path] = dict(parent=parent, directory=False, size=entry.size, extents=entry.extents)
        nodes[parent]['children'].append(entry.path)
    def record_size(name):
        return 33 + len(name) + (1 if len(name) % 2 == 0 else 0)
    def identifier(path):
        return (PurePosixPath(path).name + ('' if nodes[path]['directory'] else ';1')).encode('ascii')
    def directory_size(node):
        offset = 68
        for child in sorted(node['children']):
            n = record_size(identifier(child))
            if n > 255:
                raise ValueError('Nome longo demais para ISO9660: ' + child)
            if offset % SECTOR + n > SECTOR:
                offset = (offset // SECTOR + 1) * SECTOR
            offset += n
        return ((offset + SECTOR - 1) // SECTOR) * SECTOR
    # Preserve original file LBNs for ISO input; folders receive a deterministic layout.
    cursor = max([24] + [(offset + length + SECTOR - 1) // SECTOR for e in entries for offset, length in e.extents])
    for path, node in sorted(dirs.items()):
        node['size'] = directory_size(node)
        node['lsn'] = cursor
        cursor += node['size'] // SECTOR
    rows = []
    for path, node in sorted(nodes.items()):
        if node['directory']:
            continue
        extents = node['extents'] or [(cursor * SECTOR, node['size'])]
        if not node['extents']:
            cursor += max(1, (node['size'] + SECTOR - 1) // SECTOR)
        node['lsn'] = extents[0][0] // SECTOR
        position = 0
        for offset, length in extents:
            rows.append((offset, ((length + SECTOR - 1) // SECTOR) * SECTOR, position, path))
            position += length
    def both16(value):
        return struct.pack('<H', value) + struct.pack('>H', value)
    def both32(value):
        return struct.pack('<I', value) + struct.pack('>I', value)
    def record(node, name):
        data = bytearray(record_size(name))
        data[0] = len(data)
        data[2:10], data[10:18] = both32(node['lsn']), both32(node['size'])
        data[18:25] = bytes([126, 1, 1, 0, 0, 0, 0])
        data[25] = 2 if node['directory'] else 0
        data[28:32] = both16(1)
        data[32] = len(name)
        data[33:33+len(name)] = name
        return data
    metadata = bytearray()
    def add_metadata(offset, data):
        position = len(metadata)
        metadata.extend(data)
        rows.append((offset, len(data), position, '.disc/metadata.bin'))
    # ISO9660 path tables are needed by games that enumerate directories via PVD offsets 132..155.
    directory_order = sorted(dirs, key=lambda name: (name.count('/') + (1 if name else 0), name))
    directory_ids = {name: index+1 for index, name in enumerate(directory_order)}
    tables = []
    for endian in ('<', '>'):
        table = bytearray()
        for path in directory_order:
            name = PurePosixPath(path).name.encode('ascii') if path else b'\0'
            if len(name) > 255:
                raise ValueError('Nome de diretório longo demais')
            table.extend(bytes([len(name), 0]))
            table.extend(struct.pack(endian+'IH', nodes[path]['lsn'], directory_ids[nodes[path]['parent']]))
            table.extend(name)
            if len(name) % 2:
                table.append(0)
        tables.append(table)
    path_table_bytes = len(tables[0])
    table_sectors = (path_table_bytes + SECTOR - 1) // SECTOR
    little_lsn, big_lsn = cursor, cursor + table_sectors
    cursor += table_sectors * 2
    for lsn, table in zip((little_lsn, big_lsn), tables):
        table.extend(bytes(table_sectors * SECTOR - len(table)))
        add_metadata(lsn * SECTOR, table)
    pvd = bytearray(SECTOR)
    pvd[:7] = b'\x01CD001\x01'
    pvd[80:88], pvd[120:124], pvd[124:128], pvd[128:132] = both32(cursor), both16(1), both16(1), both16(SECTOR)
    pvd[132:140] = both32(path_table_bytes)
    struct.pack_into('<I', pvd, 140, little_lsn)
    struct.pack_into('>I', pvd, 148, big_lsn)
    pvd[156:190] = record(nodes[''], b'\0')
    pvd[881] = 1
    add_metadata(16 * SECTOR, pvd)
    terminator = bytearray(SECTOR)
    terminator[:7] = b'\xffCD001\x01'
    add_metadata(17 * SECTOR, terminator)
    for path, node in sorted(dirs.items()):
        data, offset = bytearray(node['size']), 0
        records = [record(node, b'\0'), record(nodes[node['parent']], b'\1')]
        records += [record(nodes[c], identifier(c)) for c in sorted(node['children'])]
        for item in records:
            if offset % SECTOR + len(item) > SECTOR:
                offset = (offset // SECTOR + 1) * SECTOR
            data[offset:offset+len(item)] = item
            offset += len(item)
        add_metadata(node['lsn'] * SECTOR, data)
    rows.sort()
    for previous, current in zip(rows, rows[1:]):
        if previous[0] + previous[1] > current[0]:
            raise ValueError('Extensões sobrepostas no mapa do disco')
    text = f'EXTRACTED_DISC_V1\t{cursor * SECTOR}\n'
    text += ''.join(f'{offset}\t{length}\t{position}\t{path}\n' for offset, length, position, path in rows)
    return text.encode('utf-8'), bytes(metadata)
