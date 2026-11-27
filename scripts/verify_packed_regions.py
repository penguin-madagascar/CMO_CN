"""Verify and regenerate the fingerprint-bound R88 administrative label overlay index."""
import argparse
from collections import defaultdict
import gzip
import hashlib
import json
import math
from pathlib import Path
import struct

from wwp import load as load_wwp

ROOT = Path(__file__).resolve().parents[1]
START = '        // BEGIN GENERATED PACKED REGION COVERAGE\n'
END = '        // END GENERATED PACKED REGION COVERAGE\n'


def read_json(path):
    return json.loads(path.read_bytes())


def read_pack(path):
    # Both versions store a BinaryWriter string containing the hex SHA256, followed by the payload.
    with path.open('rb') as stream:
        header = stream.read(73)
        if header[:9] not in (b'CMOTIL01\x40', b'CMOTIL02\x40'):
            raise ValueError('invalid map pack or fingerprint length')
        hashed = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            hashed.update(block)
        digest = hashed.hexdigest()
        if header[9:].decode('ascii') != digest:
            raise ValueError('map pack payload fingerprint differs from the actual bytes')
        stream.seek(73)
        count, = struct.unpack('<i', stream.read(4))
        tiles = {}
        for _ in range(count):
            z, x, y, labels, size = struct.unpack('<5i', stream.read(20))
            if size <= 0 or (z, x, y) in tiles:
                raise ValueError('invalid or duplicate packed tile')
            tiles[z, x, y] = labels
            stream.seek(size, 1)
    return digest, tiles


def single_bits(value):
    return struct.unpack('<I', struct.pack('<f', value))[0]


def region_identity(name, lat, lon):
    return f'{name}/{single_bits(lat):08X}/{single_bits(lon):08X}'


def report_owners(path, fingerprint):
    with (gzip.open if path.suffix == '.gz' else open)(path, 'rt', encoding='utf-8') as stream:
        report = json.load(stream)
    if not report.get('ready') or report.get('gaps') or report.get('payload_sha256') != fingerprint:
        raise ValueError('map build report is incomplete or belongs to another payload')
    owners = {(r['id'], label['id']): sorted(label['tiles']) for r in report['regions'] for label in r['labels']
              if label.get('bounds') and label.get('tiles')}
    for region in report['regions']:
        for label in region.get('canonical_captions', []):
            if label['origin'] == 'canonical_wwp' and label.get('bounds') and label.get('tiles'):
                record = label['canonical_record']
                owners[region['id'], label['id'], record['layer'], record['record_index_zero_based']] = sorted(label['tiles'])
    return owners


def verify_canonical_owner(root, part, line):
    """Verify the identity on a reviewed WWP caption against both its binary and its bilingual source."""
    layer, index = line['wwp_layer'], line['wwp_index']
    caption = next((item for item in part.get('canonical_captions', []) if item['id'] == line['label_id']), None)
    if (layer not in ('Cities', 'Synoptic') or type(index) is not int or index < 0 or not caption
            or caption['origin'] != 'canonical_wwp' or ''.join(caption['layout']['lines']) != line['source_zh']):
        raise ValueError(f'invalid canonical WWP coverage owner: {line["label_id"]}')
    record = caption['canonical_record']
    relative = f'assets/placenames/{layer}.wwp'
    asset = root / relative
    if (record['layer'] != layer or record['record_index_zero_based'] != index
            or record['wwp_file'] != dict(path=relative, sha256=hashlib.sha256(asset.read_bytes()).hexdigest())):
        raise ValueError(f'canonical WWP coverage identity mismatch: {layer} #{index}')
    point = load_wwp(asset)[index]
    bilingual = read_json(root / 'dict/placenames' / (layer.lower() + '.json'))[index]
    if (bilingual['index'] != index or bilingual['name'] != line['source_original']
            or not point['name'] == record['original_Chinese_name'] == bilingual['zh'] == line['source_zh']
            or point['flags'] != record['flags'] or record['level'] != int(layer == 'Cities')
            or any(single_bits(point[k]) != record[k + '_f32_bits'] or single_bits(point[k]) != single_bits(bilingual[k])
                   for k in ('lat', 'lon'))):
        raise ValueError(f'canonical WWP coverage differs from its original record: {layer} #{index}')
    return part['id'], line['label_id'], layer, index


def generate(root, report=None):
    source = root / 'dict/placenames'
    mapping = [json.loads(line) for line in (source / 'packed_region_coverage.jsonl').read_text(
        encoding='utf-8').splitlines() if line]
    if not mapping:
        raise ValueError('packed region coverage table is empty')
    fingerprint, tiles = read_pack(root / 'assets/placenames/ChineseMapTiles.dat')
    rendered = report_owners(report, fingerprint) if report else None
    dictionary = read_json(root / 'dict/CMOLocalizer.json')['places']
    allowed = {region_identity(name, lat, lon): math.ceil(zoom) for name, lat, lon, zoom in dictionary}
    if len(allowed) != len(dictionary):
        raise ValueError('R88 dictionary has duplicate float32 identities')
    index = read_json(source / 'tile_regions.json')
    files = {Path(path).stem: source / path for path in index['region_files']}
    owners = {}
    seen = set()
    masks = defaultdict(int)
    for line in mapping:
        name, lat, lon, z = (line[field] for field in ('zh', 'lat', 'lon', 'z'))
        key = region_identity(name, lat, lon)
        if key not in allowed or not allowed[key] <= z <= 8:
            raise ValueError(f'covered region outside the published dictionary or its zooms: {key} z{z}')
        region, label = line['region_id'], line['label_id']
        if (region, label) in seen:
            raise ValueError(f'duplicate label owner: {region}/{label}')
        seen.add((region, label))
        if region not in owners:
            part = read_json(files[region])
            if part['id'] != region:
                raise ValueError(f'map manifest region identity mismatch: {region}')
            owners[region] = (part, {p['id']: p for p in part['features']},
                              {p['id']: p for p in part['translations']}, {p['id'] for p in part['layouts']})
        part, features, translations, layouts = owners[region]
        if part['z'] != z:
            raise ValueError(f'covered entity differs from its approved map source or layout: {region}/{label}')
        report_key = (region, label)
        if line['source_kind'] == 'canonical_wwp':
            report_key = verify_canonical_owner(root, part, line)
        elif (features[label]['original'] != line['source_original'] or features[label]['kind'] != line['source_kind']
                or translations[label]['status'] != 'approved' or translations[label]['zh'] != line['source_zh']
                or label not in layouts):
            raise ValueError(f'covered entity differs from its approved map source or layout: {region}/{label}')
        owner_tiles = sorted(line['owner_tiles'])
        if not owner_tiles or (rendered is not None and rendered.get(report_key) != owner_tiles):
            raise ValueError(f'build report does not render the declared owner tiles: {region}/{label}')
        for tz, x, y in owner_tiles:
            if (tz != z or not part['x'] <= x < part['x'] + part['width_tiles']
                    or not part['y'] <= y < part['y'] + part['height_tiles'] or tiles.get((z, x, y), 0) <= 0):
                raise ValueError(f'covered owner has no labelled packed tile: {region}/{label} {tz}/{x}/{y}')
        masks[key] |= 1 << z
    audit = sorted(mapping, key=lambda row: (row['region_id'], row['label_id']))
    audit_hash = hashlib.sha256(json.dumps(audit, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    code = [f'        // Reviewed map/administrative entity joins: {audit_hash}',
            f'        internal const string Fingerprint = "{fingerprint}";',
            '        private static readonly Dictionary<string, int> ZoomMasks = new Dictionary<string, int>(StringComparer.Ordinal)',
            '        {']
    for key, mask in sorted(masks.items()):
        code.append('            { %s, %d },' % (json.dumps(key, ensure_ascii=False), mask))
    code.extend(['        };', ''])
    return '\n'.join(code), len(mapping), sum(mask.bit_count() for mask in masks.values())


def verify(root=ROOT, report=None, write=False):
    generated, records, zooms = generate(root, report)
    path = root / 'src/shared/MapLabelRenderer.cs'
    original = path.read_text(encoding='utf-8')
    if original.count(START) != 1 or original.count(END) != 1:
        raise ValueError('missing or duplicated generated packed region boundaries')
    before, old = original.split(START)
    current, after = old.split(END)
    if current != generated:
        if not write:
            raise ValueError('packed R88 coverage is stale for this DAT or its reviewed rows')
        path.write_bytes((before + START + generated + END + after).encode('utf-8'))
    return f'{records} approved labels cover {zooms} region/zoom instances'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, help='matching build report (.json or .json.gz)')
    parser.add_argument('--write', action='store_true', help='regenerate the C# index against an audited map report')
    args = parser.parse_args()
    if args.write and not args.report:
        parser.error('--write requires --report')
    print(verify(report=args.report, write=args.write))
