"""Runs the C# unit tests of the launcher's shared logic (launcher-tests) when a .NET 8+ SDK is installed.

They cover the per-system parts (configuration blocks, scripts, ini files, preference folders, xrandr) on the
system that runs them; CI runs them on Windows, Linux and macOS.
"""
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class LauncherCoreTests(unittest.TestCase):
    def test_launcher_core(self):
        dotnet = shutil.which('dotnet')
        if not dotnet:
            self.skipTest('no .NET SDK')
        sdks = subprocess.run([dotnet, '--list-sdks'], capture_output=True, text=True).stdout
        if not any(line.split('.')[0].isdigit() and int(line.split('.')[0]) >= 8 for line in sdks.splitlines()):
            self.skipTest('needs the .NET 8 SDK or newer')
        result = subprocess.run([dotnet, 'run', '-c', 'Release', '--project', str(ROOT / 'launcher-tests')],
                                capture_output=True, text=True, timeout=900)
        self.assertEqual(result.returncode, 0, result.stdout[-4000:] + result.stderr[-4000:])
        self.assertIn('0 failed', result.stdout)


if __name__ == '__main__':
    unittest.main()
