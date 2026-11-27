"""A map owner must represent the exact WWP member and actually render its label."""
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest

from global_map_catalog import record_fingerprint
import verify_packed_wwp as gate
from wwp import save


class WwpCoverageTest(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        assets = self.root / 'assets/placenames'
        data = self.root / 'dict/placenames'
        sources = self.root / 'src/shared'
        for path in (assets, data, sources):
            path.mkdir(parents=True)
        self.source_path = sources / 'MapLabelRenderer.cs'
        self.source_path.write_text(gate.START + gate.END, encoding='utf8')
        self.ordinary = data / 'packed_wwp_coverage.jsonl'
        self.manifest_path = data / 'tile_regions.json'
        self.report = self.root / 'report.json'
        point = dict(name='旧城', lat=15.0, lon=20.0, flags=0)
        save(assets / 'Cities.wwp', [point])
        save(assets / 'Synoptic.wwp', [dict(point, name='内海')])
        payload = struct.pack('<6i', 1, 7, 3, 4, 2, 1) + b'x'
        fingerprint = hashlib.sha256(payload).hexdigest()
        (assets / 'ChineseMapTiles.dat').write_bytes(b'CMOTIL02\x40' + fingerprint.encode('ascii') + payload)
        self.feature = dict(id='source-city', original='Old Town', kind='city')
        caption = dict(id='canonical-sea', canonical_record={
            'layer': 'Synoptic', 'record_index_zero_based': 0, 'original_Chinese_name': '内海',
            'lat_f32_bits': gate.single_bits(15), 'lon_f32_bits': gate.single_bits(20), 'flags': 0, 'level': 0,
            'wwp_file': {'path': 'assets/placenames/Synoptic.wwp',
                         'sha256': hashlib.sha256((assets / 'Synoptic.wwp').read_bytes()).hexdigest()}})
        region = dict(id='reviewed', z=7, features=[self.feature],
                      translations=[dict(id='source-city', zh='古城', status='approved')],
                      layouts=[dict(id='source-city')], review=dict(status='approved', source_verified=True, render_verified=True),
                      canonical_captions=[caption])
        self.manifest = {'regions': [region]}
        self.save(self.manifest_path, self.manifest)
        actual = dict(bounds=[1, 1, 2, 2], tiles=[[7, 3, 4]])
        self.render = dict(ready=True, gaps=[], payload_sha256=fingerprint, regions=[
            dict(id='reviewed', labels=[dict(actual, id='source-city')],
                 canonical_captions=[dict(actual, **caption)])])
        self.save(self.report, self.render)
        self.row = dict(layer='Cities', index=0, z=7, wwp_zh='旧城', lat_bits=gate.single_bits(15),
                        lon_bits=gate.single_bits(20), region='reviewed', feature_id='source-city', source_zh='古城',
                        source_record_sha256=record_fingerprint(self.feature), tiles=[[7, 3, 4]],
                        review='The reviewed source and WWP record identify the same town.')
        self.save(self.ordinary, self.row)

    @staticmethod
    def save(path, obj):
        path.write_text(json.dumps(obj, ensure_ascii=False) + '\n', encoding='utf8')

    def test_round_trip_binds_both_wwp_layers_and_the_dat(self):
        self.assertIn('2 WWP place/zoom records', gate.verify(self.root, self.report, write=True))
        self.assertIn('2 WWP place/zoom records', gate.verify(self.root))
        self.assertIn('1/旧城/', self.source_path.read_text(encoding='utf8'))
        self.assertIn('0/内海/', self.source_path.read_text(encoding='utf8'))
        self.row['lat_bits'] += 1
        self.save(self.ordinary, self.row)
        with self.assertRaisesRegex(ValueError, 'invalid reviewed'):
            gate.verify(self.root)

    def test_missing_source_or_canonical_ink_cannot_generate_coverage(self):
        for category in ('labels', 'canonical_captions'):
            with self.subTest(category=category):
                record = self.render['regions'][0][category][0]
                record['bounds'] = None
                self.save(self.report, self.render)
                with self.assertRaises(KeyError):
                    gate.verify(self.root, self.report, write=True)
                record['bounds'] = [1, 1, 2, 2]

    def test_same_named_administrative_region_cannot_suppress_a_city(self):
        self.feature['kind'] = 'admin1'
        self.row.update(source_record_sha256=record_fingerprint(self.feature), reviewed_generic_settlement=True)
        self.save(self.manifest_path, self.manifest)
        self.save(self.ordinary, self.row)
        with self.assertRaisesRegex(ValueError, 'invalid reviewed'):
            gate.verify(self.root, self.report, write=True)

    def test_two_records_for_one_source_need_a_shared_reviewed_identity(self):
        first = dict(name='旧城', lat=15.0, lon=20.0, flags=0)
        second = dict(first, lat=15.001)
        save(self.root / 'assets/placenames/Cities.wwp', [first, second])
        other = dict(self.row, index=1, lat_bits=gate.single_bits(second['lat']))

        def write_rows():
            self.ordinary.write_text(''.join(json.dumps(row)+'\n' for row in (self.row, other)), encoding='utf8')

        write_rows()
        with self.assertRaisesRegex(ValueError, 'one reviewed entity'):
            gate.verify(self.root, self.report, write=True)
        for row in (self.row, other):
            row['same_wwp_entity'] = {'indices': [0, 1], 'source': 'https://example.org/gazetteer/place'}
        write_rows()
        self.assertIn('3 WWP place/zoom records', gate.verify(self.root, self.report, write=True))
