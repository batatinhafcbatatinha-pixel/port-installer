"""Offline tests for the Linux/macOS Setup implementation."""

import hashlib
import io
import os
from pathlib import Path
import re
import sys
import tempfile
import unittest
from unittest import mock

SETUP = Path(__file__).resolve().parents[1] / 'setup'
sys.path.insert(0, str(SETUP))
import setup_posix  # noqa: E402
import setup_tools  # noqa: E402


class SetupPosixTests(unittest.TestCase):
    def test_os_block_merges_shallowly(self):
        config = {
            'game': 'Base',
            'available': True,
            'steps': [{'name': 'build'}],
            'linux': {'game': 'Linux game', 'packages': {'apt': ['libgtk-3-dev']}},
        }
        merged = setup_posix.merged_config(config, 'linux')
        self.assertEqual(merged['game'], 'Linux game')
        self.assertEqual(merged['steps'], config['steps'])
        self.assertEqual(merged['packages'], {'apt': ['libgtk-3-dev']})
        self.assertTrue(merged['available'])

    def test_os_false_and_available_false_refuse(self):
        with self.assertRaises(setup_posix.SetupError) as blocked:
            setup_posix.merged_config(
                {'linux': False, 'reason_en': 'No Linux binary', 'reason_pt': 'Sem binario Linux'},
                'linux')
        self.assertIn('cannot be built on Linux yet', str(blocked.exception))
        self.assertIn('No Linux binary', str(blocked.exception))
        with self.assertRaises(setup_posix.SetupError):
            setup_posix.merged_config(
                {'macos': {'available': False, 'reason_en': 'blocked'}}, 'macos')
        with mock.patch.dict(os.environ, {'LANG': 'pt_BR.UTF-8'}, clear=False):
            with self.assertRaises(setup_posix.SetupError) as pt:
                setup_posix.merged_config({'linux': False}, 'linux')
            self.assertIn('nao pode ser montado no Linux', pt.exception.pt)

    def test_header_format_and_log_copy(self):
        text = setup_posix.header(3, 'Build tools and project parts')
        match = re.match(r'^== (\d)/(\d) (.*)$', text)
        self.assertTrue(match)
        self.assertEqual(match.groups(), ('3', '5', 'Build tools and project parts'))
        buffer = io.StringIO()
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / 'setup-log.txt'
            with mock.patch.object(sys, 'stdout', buffer):
                output = setup_posix.Output(log)
                setup_posix.step(output, 2)
                output.close()
            self.assertIn('== 2/5 Build tools (C/C++ compiler)', buffer.getvalue())
            self.assertIn('== 2/5 Build tools (C/C++ compiler)', log.read_text(encoding='utf-8'))

    def test_jobs_computation(self):
        config = {'gb_per_job': 2, 'max_jobs': 6}
        self.assertEqual(setup_posix.compute_jobs(
            config, cpu_count=8, free_bytes=10 * 1024 ** 3), 5)
        self.assertEqual(setup_posix.compute_jobs(
            config, jobs=3, cpu_count=8, free_bytes=10 * 1024 ** 3), 3)
        self.assertEqual(setup_posix.compute_jobs(
            config, cpu_count=8, free_bytes=1 * 1024 ** 3), 1)
        self.assertEqual(setup_posix.compute_jobs(
            config, cpu_count=1, free_bytes=100 * 1024 ** 3), 1)
        self.assertEqual(setup_posix.compute_jobs(
            config, cpu_count=16, free_bytes=100 * 1024 ** 3), 6)

    def test_placeholder_expansion(self):
        text = '{python} {root} {game_files} {jobs} {llvm}'
        result = setup_posix.expand(text, '/venv/bin/python', '/game', '/game/game_files', 4, '/usr')
        self.assertEqual(result, '/venv/bin/python /game /game/game_files 4 /usr')

    def test_game_hash_check(self):
        payload = b'game executable fixture'
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'game_files').mkdir()
            (root / 'game_files/GAME.ELF').write_bytes(payload)
            sha = hashlib.sha256(payload).hexdigest()
            install = {'required_files': {'GAME.ELF': sha}}
            self.assertTrue(setup_posix.game_ok(root, install))
            self.assertFalse(setup_posix.game_ok(root, {'required_files': {'GAME.ELF': '0' * 64}}))
            self.assertFalse(setup_posix.game_ok(root, {'required_files': {'MISSING.ELF': sha}}))

    def test_link_mode_replaces_link_but_not_folder(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            root = base / 'game'
            root.mkdir()
            source = base / 'source'
            source.mkdir()
            (source / 'GAME.ELF').write_bytes(b'x')
            calls = []

            def fake_symlink(link, target):
                calls.append((Path(link), Path(target)))

            target = setup_posix.link_game_files(root, source, fake_symlink)
            self.assertEqual(target, source.resolve())
            self.assertEqual(calls[-1], (root / 'game_files', source.resolve()))

            (root / 'game_files').mkdir()
            with self.assertRaises(setup_posix.SetupError) as real_folder:
                setup_posix.link_game_files(root, source, fake_symlink)
            self.assertIn('exists and is not a link', str(real_folder.exception))
            (root / 'game_files').rmdir()

            with mock.patch.object(Path, 'is_symlink', return_value=True), \
                    mock.patch.object(Path, 'unlink') as unlink:
                setup_posix.link_game_files(root, source, fake_symlink)
                unlink.assert_called_once_with()

    @unittest.skipUnless(hasattr(os, 'symlink'), 'no os.symlink on this OS')
    def test_link_mode_real_symlink_when_supported(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            root = base / 'game'
            root.mkdir()
            source = base / 'source'
            source.mkdir()
            (source / 'GAME.ELF').write_bytes(b'x')
            try:
                setup_posix.link_game_files(root, source)
            except OSError as error:
                self.skipTest(f'symlink creation not permitted: {error}')
            self.assertTrue((root / 'game_files').is_symlink())
            self.assertEqual((root / 'game_files').resolve(), source.resolve())

    def test_package_manager_detection_with_fake_path(self):
        with tempfile.TemporaryDirectory() as folder:
            bin_dir = Path(folder) / 'bin'
            bin_dir.mkdir()
            fake = bin_dir / 'apt-get'
            fake.write_text('')
            fake.chmod(0o755)
            self.assertEqual(setup_posix.detect_package_manager(path=str(bin_dir)), 'apt-get')
            (bin_dir / 'dnf').write_text('')
            (bin_dir / 'dnf').chmod(0o755)
            self.assertEqual(setup_posix.detect_package_manager(path=str(bin_dir)), 'apt-get')
            empty = Path(folder) / 'empty'
            empty.mkdir()
            self.assertIsNone(setup_posix.detect_package_manager(path=str(empty)))

    def test_windows_table_unchanged(self):
        self.assertEqual(
            setup_tools.TOOLS['cmake']['url'],
            'https://github.com/Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-windows-x86_64.zip')
        self.assertEqual(
            setup_tools.TOOLS['ninja']['sha256'],
            '07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65')
        self.assertEqual(setup_tools.TOOLS['llvm']['check'], 'bin/clang++.exe')
        self.assertEqual(setup_tools.TOOLS['ghidra']['check'], 'support/analyzeHeadless.bat')

    def test_posix_tables_pinned(self):
        expected = {('linux', 'x86_64'), ('linux', 'aarch64'),
                    ('darwin', 'arm64'), ('darwin', 'x86_64')}
        self.assertEqual(set(setup_tools.POSIX_TOOLS), expected)
        for key, table in setup_tools.POSIX_TOOLS.items():
            for name in ('cmake', 'ninja', 'jdk', 'ghidra', 'ghidra-ee'):
                with self.subTest(platform=key, tool=name):
                    self.assertRegex(table[name]['sha256'], r'^[0-9a-f]{64}$')
                    self.assertTrue(table[name]['url'].startswith('https://github.com/'))
            self.assertIn('cmake', table['cmake']['check'])
            self.assertTrue(table['ninja']['check'].endswith('ninja'))
            self.assertFalse(table['ghidra']['check'].endswith('.bat'))

    def test_setup_sh_is_bash_32(self):
        text = (SETUP / 'setup.sh').read_text(encoding='utf-8')
        self.assertTrue(text.startswith('#!/usr/bin/env bash\n'))
        self.assertNotIn('declare -A', text)
        self.assertNotIn('mapfile', text)
        self.assertNotRegex(text, r'\$\{[^}]+,,')
        self.assertNotRegex(text, r'\$\{[^}]+\^\^')


if __name__ == '__main__':
    unittest.main()
