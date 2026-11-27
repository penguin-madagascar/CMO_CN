"""Verify the packed replacements for individual WWP records at each zoom."""
import argparse
from collections import defaultdict
import gzip
import hashlib
import json
from pathlib import Path

from global_map_catalog import record_fingerprint
from map_tile_manifest import load_manifest
from verify_packed_regions import read_pack, single_bits
from wwp import load

ROOT = Path(__file__).resolve().parents[1]
START = '        // BEGIN GENERATED PACKED WWP COVERAGE\n'
END = '        // END GENERATED PACKED WWP COVERAGE\n'


def read_report(path, fingerprint):
    with (gzip.open if path.suffix == '.gz' else open)(path, 'rt', encoding='utf8') as stream:
        report = json.load(stream)
    if not report.get('ready') or report.get('gaps') or report.get('payload_sha256') != fingerprint:
        raise ValueError('map report must be ready and match the published DAT payload')
    return {(r['id'], p['id']): p for r in report['regions'] for key in ('labels', 'canonical_captions')
            for p in r.get(key, []) if p.get('bounds') and p.get('tiles')}


def verify(root=ROOT, report=None, write=False):
    if write and report is None:
        raise ValueError('regenerating the WWP index requires actual map render results')
    base = root / 'dict/placenames'
    fingerprint, tiles = read_pack(root / 'assets/placenames/ChineseMapTiles.dat')
    rendered = read_report(report, fingerprint) if report else None
    manifest = load_manifest(base / 'tile_regions.json')
    records = {}
    file_hashes = {}
    for level, name in enumerate(('Synoptic', 'Cities')):
        asset = root / 'assets/placenames' / (name + '.wwp')
        file_hashes[name] = hashlib.sha256(asset.read_bytes()).hexdigest()
        for index, p in enumerate(load(asset)):
            records[name, index] = (p, level)

    masks = defaultdict(int)
    packed = {}
    canonical_count = 0
    seen = set()

    def register(layer, index, z, region, identity, owner_tiles):
        if type(index) is not int or type(z) is not int or not 3 <= z <= 10:
            raise ValueError('invalid WWP index or zoom')
        p, level = records[layer, index]
        if z <= 5 and level:
            raise ValueError('city WWP record is outside its source zoom')
        if owner_tiles is not None and (not owner_tiles or
                any(tz != z or tiles.get((tz, x, y), 0) <= 0 for tz, x, y in owner_tiles)):
            raise ValueError(f'missing rendered owner tile: {region}/{identity}')
        if rendered is not None and sorted(rendered[region, identity]['tiles']) != sorted(owner_tiles):
            raise ValueError(f'WWP owner tiles differ from the actual render: {region}/{identity}')
        key = f'{level}/{p["name"]}/{single_bits(p["lat"]):08X}/{single_bits(p["lon"]):08X}'
        masks[key] |= 1 << z
        seen.add((layer, index, z))

    for region in manifest['regions']:
        rid, z = region['id'], region['z']
        approved = region['review']
        if approved.get('status') != 'approved' or not approved.get('source_verified') or not approved.get('render_verified'):
            raise ValueError(f'WWP index requires approved map source and pixels: {rid}')
        features = {row['id']: row for row in region['features']}
        translations = {row['id']: row for row in region['translations']}
        layouts = {row['id'] for row in region['layouts']}
        packed[rid] = (z, features, translations, layouts)
        for caption in region.get('canonical_captions', []):
            rec = caption['canonical_record']
            layer, index, identity = rec['layer'], rec['record_index_zero_based'], caption['id']
            p, level = records[layer, index]
            if (rec['wwp_file'] != {'path': f'assets/placenames/{layer}.wwp', 'sha256': file_hashes[layer]}
                    or rec['original_Chinese_name'] != p['name'] or rec['level'] != level or rec['flags'] != p['flags']
                    or rec['lat_f32_bits'] != single_bits(p['lat']) or rec['lon_f32_bits'] != single_bits(p['lon'])):
                raise ValueError(f'canonical map caption differs from the WWP member: {rid}/{identity}')
            # The ordinary verification can check the bound generated index without a private report.
            # Regeneration requires the actual caption pixels, including nonempty cross-tile output.
            owner_tiles = None
            if rendered is not None:
                item = rendered[rid, identity]
                if item.get('canonical_record') != rec:
                    raise ValueError(f'rendered canonical identity changed: {rid}/{identity}')
                owner_tiles = item['tiles']
            register(layer, index, z, rid, identity, owner_tiles)
            canonical_count += 1
    evidence = base / 'packed_wwp_coverage.jsonl'
    normal_count = 0
    uses = defaultdict(list)
    for line in evidence.read_text(encoding='utf8').splitlines():
        row = json.loads(line)
        rid, identity = row['region'], row['feature_id']
        z, features, tr, layouts = packed[rid]
        f, translation = features[identity], tr[identity]
        layer, index = row['layer'], row['index']
        p, level = records[layer, index]
        generic = level == 1 and f['kind'] == 'region' and row.get('reviewed_generic_settlement') is True
        if (row['z'] != z or (layer, index, z) in seen
                or row['wwp_zh'] != p['name'] or row['lat_bits'] != single_bits(p['lat'])
                or row['lon_bits'] != single_bits(p['lon'])
                or row['source_record_sha256'] != record_fingerprint(f)
                or row['source_zh'] != translation['zh'] or translation['status'] != 'approved'
                or identity not in layouts or (not generic and (f['kind'] == 'city') != bool(level))
                or not row['review'].strip()):
            raise ValueError(f'invalid reviewed WWP replacement: {rid}/{identity}')
        register(layer, index, z, rid, identity, row['tiles'])
        uses[rid, identity].append(row)
        normal_count += 1
    for owner, group in uses.items():
        if len(group) == 1 and 'same_wwp_entity' not in group[0]:
            continue
        assertion = group[0].get('same_wwp_entity')
        if (not isinstance(assertion, dict) or len(group) < 2
                or not assertion.get('source', '').startswith('https://')
                or assertion.get('indices') != sorted(row['index'] for row in group)
                or any(row.get('same_wwp_entity') != assertion for row in group)
                or len({(row['layer'], row['wwp_zh']) for row in group}) != 1):
            raise ValueError(f'multiple WWP records require one reviewed entity for {owner}')
    lines = [f'        internal const string Fingerprint = "{fingerprint}";',
             '        private static readonly Dictionary<string, int> ZoomMasks = new Dictionary<string, int>(StringComparer.Ordinal)',
             '        {']
    for key, mask in sorted(masks.items()):
        lines.append('            { %s, %d },' % (json.dumps(key, ensure_ascii=False), mask))
    lines.extend(['        };', ''])
    generated = '\n'.join(lines)
    path = root / 'src/shared/MapLabelRenderer.cs'
    raw = path.read_bytes()
    old = raw.decode('utf8')
    eol = '\r\n' if '\r\n' in old else '\n'
    old = old.replace('\r\n', '\n')
    if old.count(START) != 1 or old.count(END) != 1:
        raise ValueError('missing or duplicated packed WWP index markers')
    before, body = old.split(START)
    current, after = body.split(END)
    if current != generated:
        if not write:
            raise ValueError('packed WWP index differs from the published DAT and reviewed place owners')
        path.write_bytes((before + START + generated + END + after).replace('\n', eol).encode('utf8'))
    return f'{canonical_count} canonical captions and {normal_count} reviewed labels cover {len(seen)} WWP place/zoom records'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, help='matching map render report (.json or .json.gz)')
    parser.add_argument('--write', action='store_true', help='regenerate the packed index from verified map output')
    args = parser.parse_args()
    print(verify(report=args.report, write=args.write))
