import hashlib
import json
from pathlib import Path
import struct
from tempfile import TemporaryDirectory
import unittest

from verify_packed_regions import END, START, single_bits, verify
from wwp import save as save_wwp


class PackedRegionIndexTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / 'dict/placenames'
        self.source.mkdir(parents=True)
        renderer = self.root / 'src/shared/MapLabelRenderer.cs'
        renderer.parent.mkdir(parents=True)
        renderer.write_text(START + END, encoding='utf-8')
        self.asset = self.root / 'assets/placenames/ChineseMapTiles.dat'
        self.asset.parent.mkdir(parents=True)
        self.fingerprint = self.pack(b'first')
        self.report = self.root / 'report.json'
        self.evidence = dict(zh='甲省', lat=1, lon=2, z=6, adm1_code='test-1', iso3166_2='T-1',
                             region_id='east', label_id='province', source_kind='admin1',
                             source_original='Alpha', source_zh='甲地', owner_tiles=[[6, 4, 5]])
        self.write(self.source / 'packed_region_coverage.jsonl', self.evidence)
        self.write(self.root / 'dict/CMOLocalizer.json', {'places': [['甲省', 1, 2, 5.3]]})
        self.write(self.source / 'tile_regions.json', {'region_files': ['east.json']})
        self.write(self.source / 'east.json', dict(id='east', z=6, x=4, y=5, width_tiles=1, height_tiles=1,
                   features=[dict(id='province', original='Alpha', kind='admin1')], layouts=[dict(id='province')],
                   translations=[dict(id='province', zh='甲地', status='approved')]))
        self.write(self.report, dict(payload_sha256=self.fingerprint, ready=True, gaps=[],
                   regions=[dict(id='east', labels=[dict(id='province', bounds=[1, 1, 10, 10], tiles=[[6, 4, 5]])])]))
        verify(self.root, self.report, write=True)

    @staticmethod
    def write(path, document):
        path.write_text(json.dumps(document, ensure_ascii=False) + '\n', encoding='utf-8')

    def pack(self, content):
        payload = struct.pack('<6i', 1, 6, 4, 5, 1, len(content)) + content
        fingerprint = hashlib.sha256(payload).hexdigest()
        self.asset.write_bytes(b'CMOTIL01\x40' + fingerprint.encode() + payload)
        return fingerprint

    def test_regeneration_and_stale_pack(self):
        self.assertIn('1 region/zoom', verify(self.root))
        self.pack(b'second')
        with self.assertRaisesRegex(ValueError, 'stale'):
            verify(self.root)
        with self.assertRaisesRegex(ValueError, 'another payload'):
            verify(self.root, self.report, write=True)

    def test_every_owner_must_be_present_in_actual_render(self):
        report = json.loads(self.report.read_bytes())
        report['regions'][0]['labels'][0]['tiles'] = []
        self.write(self.report, report)
        with self.assertRaisesRegex(ValueError, 'does not render'):
            verify(self.root, self.report, write=True)

    def test_dictionary_identity_and_reviewed_map_text(self):
        for changed, message in ((dict(lon=2.1), 'dictionary'), (dict(source_zh='另一省'), 'map source'),
                                 (dict(adm1_code='test-2'), 'stale')):
            self.write(self.source / 'packed_region_coverage.jsonl', dict(self.evidence, **changed))
            with self.assertRaisesRegex(ValueError, message):
                verify(self.root)

    def test_canonical_owner_requires_the_bilingual_record_and_its_rendered_caption(self):
        evidence = dict(self.evidence, source_kind='canonical_wwp', wwp_layer='Cities', wwp_index=0)
        self.write(self.source / 'packed_region_coverage.jsonl', evidence)
        point = dict(name='甲地', lat=1.25, lon=2.5, flags=0)
        asset = self.asset.with_name('Cities.wwp')
        save_wwp(asset, [point])
        record = dict(layer='Cities', record_index_zero_based=0, original_Chinese_name=point['name'],
                      lat_f32_bits=single_bits(point['lat']), lon_f32_bits=single_bits(point['lon']), flags=0, level=1,
                      wwp_file=dict(path='assets/placenames/Cities.wwp', sha256=hashlib.sha256(asset.read_bytes()).hexdigest()))
        bilingual = dict(index=0, name='Alpha', zh='甲地', lat=1.25, lon=2.5)
        self.write(self.source / 'cities.json', [bilingual])
        owner = json.loads((self.source / 'east.json').read_bytes())
        caption = dict(id='province', origin='canonical_wwp', canonical_record=record, layout=dict(lines=['甲地']))
        owner['canonical_captions'] = [caption]
        self.write(self.source / 'east.json', owner)
        report = json.loads(self.report.read_bytes())
        # The ordinary label has the same id and tile; it must never stand in for the WWP caption.
        with self.assertRaisesRegex(ValueError, 'does not render'):
            verify(self.root, self.report, write=True)
        rendered = dict(caption, bounds=[1, 1, 10, 10], tiles=[[6, 4, 5]])
        report['regions'][0]['canonical_captions'] = [rendered]
        self.write(self.report, report)
        self.assertIn('1 region/zoom', verify(self.root, self.report, write=True))
        self.write(self.source / 'cities.json', [dict(bilingual, lon=2.6)])
        with self.assertRaisesRegex(ValueError, 'original record'):
            verify(self.root, self.report)


if __name__ == '__main__':
    unittest.main()
