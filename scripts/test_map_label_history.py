"""Windows: python scripts/test_map_label_history.py

Replay the original WWP and upstream R88 draws against the current package at
zooms 3–10. Requires Git history, Python 3 and the .NET Framework 4.x compiler.
The only temporary game inputs are the two WWP files; no installed game is used.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[1]
BASE = 'd7a54b8fae0f295932e6564525832c34844832e3'
RENDERER = 'src/shared/MapLabelRenderer.cs'


def upstream(path):
    return subprocess.check_output(['git', 'show', f'{BASE}:{path}'], cwd=ROOT)


def instrument(data, namespace):
    """Observe the actual filled glyph, after collision checks and measurement passes."""
    text = data.decode('utf-8-sig')
    for marker, replacement in (
        ('namespace CMOShared', 'namespace ' + namespace),
        ('private const int TileSize = 256;',
         'internal static Action<MapLabelPlace> Painted;\n        private const int TileSize = 256;'),
        ('graphics.FillPath(fill, path);', 'graphics.FillPath(fill, path); Painted(place);'),
    ):
        if text.count(marker) != 1:
            raise ValueError(f'{namespace}: expected exactly one {marker!r} instrumentation site')
        text = text.replace(marker, replacement)
    return text


def main():
    if os.name != 'nt':
        raise SystemExit('Run this test on Windows to use the production GDI+ font and collision metrics.')
    framework = Path(os.environ['WINDIR']) / 'Microsoft.NET'
    compiler = next((p for arch in ('Framework64', 'Framework')
                     if (p := framework / arch / 'v4.0.30319' / 'csc.exe').is_file()), None)
    if not compiler:
        raise SystemExit('Install the .NET Framework 4.x compiler (csc.exe).')
    with tempfile.TemporaryDirectory(prefix='cmo-map-history-') as scratch:
        temp = Path(scratch)
        original = temp / 'Original.cs'
        current = temp / 'Current.cs'
        original.write_text(instrument(upstream(RENDERER), 'Original'), encoding='utf-8')
        current.write_text(instrument((ROOT / RENDERER).read_bytes(), 'Current'), encoding='utf-8')
        places = json.loads(upstream('dict/CMOLocalizer.json'))['places']
        prior_dict = temp / 'upstream-places.json'
        prior_dict.write_text(json.dumps({'places': places}, ensure_ascii=False), encoding='utf-8')
        for layer in ('Synoptic', 'Cities'):
            dest = temp / 'WW' / 'PlacenameSets' / layer / '0000_0000.wwp'
            dest.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / 'assets' / 'placenames' / (layer + '.wwp'), dest)
        exe = temp / 'MapLabelHistoryTests.exe'
        subprocess.run([compiler, '/nologo', '/langversion:5', '/codepage:65001', '/target:exe',
                        '/reference:System.Drawing.dll', '/out:' + str(exe), original, current,
                        ROOT / 'src/tests/MapLabelHistoryTests.cs'], check=True)
        subprocess.run([exe, ROOT, temp, prior_dict], check=True)


if __name__ == '__main__':
    main()
