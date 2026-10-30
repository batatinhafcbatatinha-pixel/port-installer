import hashlib
import io
import json
from pathlib import Path
import struct
import shutil
import subprocess
import tempfile
import threading
import unittest

from engine import Cancelled, delete_source, install
from layout import build_layout
from media import Entry, inventory, safe_path
from prepare_folder import prepare

PAYLOAD = b'game executable fixture'
SHA = hashlib.sha256(PAYLOAD).hexdigest()
CONFIG = dict(name='Fixture', filesystem='iso9660', required_files={'GAME.ELF': SHA})


def iso_fixture():
    entries = [Entry('GAME.ELF', len(PAYLOAD), [])]
    layout, metadata = build_layout(entries)
    lines = layout.decode().splitlines()
    data = bytearray(int(lines[0].split('\t')[1]))
    for line in lines[1:]:
        offset, count, position, path = line.split('\t')
        offset, count, position = int(offset), int(count), int(position)
        blob = metadata if path.startswith('.disc/') else PAYLOAD
        chunk = blob[position:position+count]
        data[offset:offset+len(chunk)] = chunk
    return bytes(data)


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'source'
        self.source.mkdir()
        (self.source / 'GAME.ELF').write_bytes(PAYLOAD)
        self.out = self.root / 'port'

    def test_folder_copy_and_verified_report(self):
        events = []
        result = install(self.source, self.out, CONFIG, events.append)
        self.assertEqual((self.out / 'game_files/GAME.ELF').read_bytes(), PAYLOAD)
        self.assertTrue(result['report']['verified'])
        self.assertFalse(result['canDeleteSource'])
        self.assertTrue((self.out / 'game_files/.disc/layout.tsv').is_file())
        self.assertEqual(events[-1]['type'], 'status')

    def test_expected_resource_missing_is_rejected(self):
        config = {**CONFIG, 'expected_files': {'DATA.BIN': 10}}
        with self.assertRaises(ValueError):
            install(self.source, self.out, config)
        self.assertFalse((self.out / 'game_files/GAME.ELF').exists())

    @unittest.skipUnless(shutil.which('g++'), 'g++ not available')
    def test_native_reader(self):
        install(self.source, self.out, CONFIG)
        executable = self.root / 'reader'
        source = Path(__file__).with_name('disc_reader_test.cpp')
        subprocess.run(['g++', '-std=c++17', str(source), '-o', str(executable)], check=True, capture_output=True)
        result = subprocess.run([str(executable), str(self.out / 'game_files')], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_prepare_without_iso(self):
        install(self.source, self.out, CONFIG)
        result = prepare(self.out, CONFIG)
        self.assertFalse(result['isoRequired'])
        before = (self.out / 'game_files/.disc/layout.tsv').read_bytes()
        prepare(self.out, CONFIG)
        self.assertEqual(before, (self.out / 'game_files/.disc/layout.tsv').read_bytes())

    def test_prepare_rejects_incomplete_folder(self):
        install(self.source, self.out, CONFIG)
        with self.assertRaises(ValueError):
            prepare(self.out, {**CONFIG, 'expected_files': {'MISSING.DAT': 4}})

    def test_repeat_verifies_existing_files(self):
        install(self.source, self.out, CONFIG)
        install(self.source, self.out, CONFIG)

    def test_conflict_preserves_both_copies(self):
        install(self.source, self.out, CONFIG)
        target = self.out / 'game_files/GAME.ELF'
        target.write_bytes(b'x' * len(PAYLOAD))
        with self.assertRaises(ValueError):
            install(self.source, self.out, CONFIG)
        self.assertEqual(target.read_bytes(), b'x' * len(PAYLOAD))
        self.assertEqual((self.source / 'GAME.ELF').read_bytes(), PAYLOAD)

    def test_wrong_revision_writes_no_payload(self):
        (self.source / 'GAME.ELF').write_bytes(b'wrong')
        with self.assertRaises(ValueError):
            install(self.source, self.out, CONFIG)
        self.assertFalse((self.out / 'game_files/GAME.ELF').exists())

    def test_cancel_preserves_source_and_cleans_lock(self):
        event = threading.Event()
        event.set()
        with self.assertRaises(Cancelled):
            install(self.source, self.out, CONFIG, cancel=event)
        self.assertTrue((self.source / 'GAME.ELF').is_file())
        self.assertFalse((self.out / '.game-install.lock').exists())

    def test_nested_destination_rejected(self):
        with self.assertRaises(ValueError):
            install(self.source, self.source, CONFIG)

    def test_iso_extract_then_explicit_delete(self):
        iso = self.root / 'test.iso'
        iso.write_bytes(iso_fixture())
        result = install(iso, self.out, CONFIG)
        self.assertTrue(iso.is_file())
        self.assertTrue(result['canDeleteSource'])
        delete_source(result)
        self.assertFalse(iso.exists())
        self.assertTrue(json.loads((self.out / 'game-install-report.json').read_text())['sourceDeleted'])

    def test_changed_iso_cannot_be_deleted(self):
        iso = self.root / 'test.iso'
        iso.write_bytes(iso_fixture())
        result = install(iso, self.out, CONFIG)
        iso.write_bytes(b'changed')
        with self.assertRaises(ValueError):
            delete_source(result)
        self.assertTrue(iso.exists())

    def test_truncated_iso(self):
        blob = iso_fixture()[:-2048]
        with self.assertRaises(ValueError):
            inventory(io.BytesIO(blob), len(blob), 'iso9660')

    def test_xdvdfs(self):
        blob = bytearray(40 * 2048)
        magic = b'MICROSOFT*XBOX*MEDIA'
        blob[32*2048:32*2048+20] = magic
        blob[33*2048-20:33*2048] = magic
        struct.pack_into('<II', blob, 32*2048+20, 34, 2048)
        name = b'default.xex'
        struct.pack_into('<HHIIBB', blob, 34*2048, 0, 0, 36, len(PAYLOAD), 0, len(name))
        blob[34*2048+14:34*2048+14+len(name)] = name
        entries = inventory(io.BytesIO(blob), len(blob), 'xdvdfs')
        self.assertEqual(entries[0].path, 'default.xex')
        self.assertEqual(entries[0].extents, [(36*2048, len(PAYLOAD))])

    def test_unsafe_paths(self):
        for name in ('../bad', '/bad', 'C:/bad', 'A/../B', 'NUL.txt', 'a\\b', 'file.', 'x\ty'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                safe_path(name)

    def test_layout_metadata_roundtrip(self):
        blob = iso_fixture()
        entries = inventory(io.BytesIO(blob), len(blob), 'iso9660')
        self.assertEqual([(e.path, e.size) for e in entries], [('GAME.ELF', len(PAYLOAD))])

    def test_cancel_during_copy_leaves_no_partial_file(self):
        event = threading.Event()
        def emit(item):
            if item['type'] == 'progress':
                event.set()
        with self.assertRaises(Cancelled):
            install(self.source, self.out, CONFIG, emit, event)
        self.assertFalse(list(self.out.rglob('*.part')))
        self.assertFalse((self.out / 'game_files/GAME.ELF').exists())


if __name__ == '__main__':
    unittest.main()
