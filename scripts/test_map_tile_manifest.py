#!/usr/bin/env python3
"""Storage round trips and portable rebuilds for split map manifests."""
import copy
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

import map_tile_manifest as storage
import test_build_map_tile_pack as fixtures
import verify_map_tile_source_package as verifier


class PublicMapSourceTests(unittest.TestCase):
    def test_public_manifest_uses_machine_independent_evidence_paths(self):
        index = Path(__file__).resolve().parents[1] / 'dict/placenames/tile_regions.json'
        machine_path = re.compile(rb'(?:\b[a-z]:[/\\]+users[/\\]+|(?<![\w:/])/(?:home|Users)/[^/\s]+/)', re.I)
        for path in storage.manifest_files(index):
            data = path.read_bytes()
            match = machine_path.search(data)
            if match:
                line = data.count(b'\n', 0, match.start()) + 1
                self.fail(f'{path.relative_to(index.parent)} line {line} contains a machine-specific path')


class BashMapSelectionTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which('bash'), 'requires Bash on PATH')
    def test_caption_arguments_reach_both_map_commands_as_a_pair(self):
        capture_command = r'''
            PY=record_args
            record_args() {
                case "${1##*/}" in
                    "$TARGET") shift; printf '%s\0' "$@" > "$TRACE"; return 73 ;;
                    release_check.py|verify_no_secrets.py|verify_placenames.py|-) return 0 ;;
                    *) return 97 ;;
                esac
            }
            script=$1
            shift
            source "$script"
        '''
        with tempfile.TemporaryDirectory(prefix='map args ') as name:
            root = Path(name).resolve()
            scripts = root / 'scripts'
            scripts.mkdir()
            (root / 'VERSION').write_text('fixture\n', encoding='ascii')
            out = root / 'build'
            out.mkdir()
            for file in ('CMO_Chinese_Setup_vfixture.exe', 'CMOLocalizer.dll', 'CMO_Updater.exe', 'Scenarios_HTML.zip'):
                (out / file).write_bytes(b'fixture')
            env = {k: v for k, v in os.environ.items() if not k.startswith('CMO_MAP_')}
            env.update(CMO_MAP_MANIFEST='map.json', CMO_MAP_ORIGINAL_ROOT='originals', CMO_MAP_FONT='regular.ttc',
                       CMO_MAP_BOLD_FONT='bold.ttc', CMO_MAP_SOURCE_PACKAGE='source package')
            path_arg = 'CMO_MAP_CANONICAL_CAPTION_APPROVALS'
            hash_arg = path_arg + '_SHA256'
            values = {path_arg: 'review data/selected captions.json', hash_arg: 'a' * 64}
            for script, target in [('build.sh', 'build_map_tile_pack.py'),
                                   ('package.sh', 'verify_map_tile_source_package.py')]:
                entry = scripts / script
                shutil.copyfile(Path(__file__).with_name(script), entry)
                if script == 'package.sh':
                    (scripts / 'build.sh').write_text('exit 0\n', encoding='ascii')
                for keys in [(), (path_arg,), (hash_arg,), (path_arg, hash_arg)]:
                    with self.subTest(script=script, keys=keys):
                        trace = root / f'{script}-{len(keys)}.argv'
                        args_env = dict(env, TARGET=target, TRACE=trace.as_posix())
                        args_env.update((key, values[key]) for key in keys)
                        run = subprocess.run([shutil.which('bash'), '-c', capture_command, 'test', entry.as_posix()],
                                             cwd=root, env=args_env, capture_output=True)
                        if len(keys) == 1:
                            self.assertNotIn(run.returncode, (0, 73))
                            self.assertIn((hash_arg if keys == (path_arg,) else path_arg).encode() + b':', run.stderr)
                            self.assertFalse(trace.exists())
                        else:
                            self.assertEqual(73, run.returncode, run.stderr)
                            argv = trace.read_bytes().decode('utf-8').split('\0')[:-1]
                            expected = ['--canonical-caption-approvals', values[path_arg],
                                        '--canonical-caption-approvals-sha256', values[hash_arg]]
                            if keys:
                                self.assertEqual(expected, argv[-4:])
                            else:
                                self.assertTrue(all(flag not in argv for flag in expected[::2]))


class SplitManifestTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.TilePackBuilderTests('test_original_unnamed_peak_renders_elevation_once')
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.base = self.fixture.base
        self.path = self.base / 'split' / 'tile_regions.json'

    def write(self):
        storage.write_manifest(self.fixture.manifest, self.path)

    def test_roundtrip_preserves_every_logical_field_and_review(self):
        self.fixture.region['context_tiles'] = [dict(self.fixture.region['original_tiles'][0],
                                                   origin='cached', source_alpha_count=123)]
        before = copy.deepcopy(self.fixture.manifest)
        self.write()
        self.assertEqual(before, storage.load_manifest(self.path))
        sources = (self.path.parent / 'map_tiles/sources.jsonl').read_text(encoding='utf-8').splitlines()
        tiles = (self.path.parent / 'map_tiles/tiles.jsonl').read_text(encoding='utf-8').splitlines()
        self.assertEqual(4, len(sources))
        self.assertEqual(2, len(tiles))

    def test_writer_does_not_approve_an_edited_translation(self):
        self.fixture.region['translations'][1]['zh'] = '改名'
        self.write()
        manifest = storage.load_manifest(self.path)
        wire, report = self.fixture.builder.compile_manifest(
            manifest, self.fixture.original, fixtures.FONT, fixtures.BOLD_FONT)
        self.assertIsNone(wire)
        self.assertIn('region_fingerprint', json.dumps(report))

    def test_unknown_source_reference_is_rejected(self):
        self.write()
        region_path = self.path.parent / 'map_tiles/regions/fixture.json'
        region = json.loads(region_path.read_bytes())
        region['features'][0]['source_url'] = '@source/missing'
        region_path.write_text(json.dumps(region), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'unknown source'):
            storage.load_manifest(self.path)

    def test_duplicate_review_is_rejected(self):
        self.write()
        path = self.path.parent / 'map_tiles/reviews.jsonl'
        path.write_bytes(path.read_bytes() * 2)
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            storage.load_manifest(self.path)

    def test_fragment_cannot_escape_manifest_directory(self):
        self.write()
        index = json.loads(self.path.read_bytes())
        index['region_files'][0] = '../outside.json'
        self.path.write_text(json.dumps(index), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'relative'):
            storage.load_manifest(self.path)

    def test_legacy_manifest_is_still_read_without_rewriting_it(self):
        self.path.parent.mkdir()
        self.path.write_text(json.dumps(self.fixture.manifest), encoding='utf-8')
        self.assertEqual(self.fixture.manifest, storage.load_manifest(self.path))
        self.assertEqual([self.path.resolve()], storage.manifest_files(self.path))

    def build_source_package(self, selection=None):
        region = self.fixture.region
        for tile in region['original_tiles']:
            original = self.fixture.original / tile['path']
            tile['x'] += region['x']
            tile['y'] += region['y']
            tile['path'] = f"{region['z']}/{tile['x']}/{tile['y']}.png"
            destination = self.fixture.original / tile['path']
            destination.parent.mkdir(parents=True, exist_ok=True)
            original.rename(destination)
        self.fixture.approve()
        params = (self.fixture.manifest, self.fixture.original, fixtures.FONT, fixtures.BOLD_FONT)
        _, preview = self.fixture.builder.compile_manifest(*params, canonical_caption_approvals=selection)
        region['review']['render_sha256'] = preview['regions'][0]['atlas_rgba_sha256']
        expected, report = self.fixture.builder.compile_manifest(*params, canonical_caption_approvals=selection)
        self.assertTrue(report['ready'], report['gaps'])
        self.write()
        package = self.base / 'package'
        command = [sys.executable, '-B', str(fixtures.SCRIPT), '--manifest', str(self.path),
                   '--original-root', str(self.fixture.original), '--font', str(fixtures.FONT),
                   '--bold-font', str(fixtures.BOLD_FONT), '--output', str(self.base / 'actual.dat'),
                   '--report', str(self.base / 'report.json'), '--source-package-dir', str(package)]
        if selection is not None:
            path = self.base / 'independent selection.json'
            path.write_text(json.dumps(selection), encoding='utf-8')
            command += ['--canonical-caption-approvals', str(path),
                        '--canonical-caption-approvals-sha256', fixtures.sha(path)]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, (self.base / 'actual.dat').read_bytes())
        return package, expected

    def assert_relocated_rebuild(self, package, expected):
        recipe = json.loads((package / 'rebuild_inputs.json').read_bytes())
        relocated = self.base / 'relocated'
        package.rename(relocated)
        shutil.rmtree(self.path.parent)
        self.fixture.original.rename(self.base / 'original_unavailable')
        result = subprocess.run([sys.executable, '-B', *recipe['argv']], cwd=relocated,
                                capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, (relocated / 'out/ChineseMapTiles.dat').read_bytes())

    def test_split_cli_and_relocated_source_package_rebuild_identical_bytes(self):
        package, expected = self.build_source_package()
        asset = self.base / 'actual.dat'
        self.assertFalse(verifier.verify(self.path, asset, None)['source_package_required'])
        checked = verifier.verify(self.path, asset, package)
        self.assertGreater(checked['files'], 8)
        self.assertFalse(checked['source_package_required'])
        self.assertFalse((package / 'inputs/canonical_caption_approvals.json').exists())
        self.assert_relocated_rebuild(package, expected)

    def test_selected_wwp_caption_requires_independent_selection_and_rebuilds(self):
        canonical = fixtures.CanonicalCaptionTests('test_original_member_is_separate_from_source_and_native')
        canonical.setUp()
        # The first actual Cities.wwp member, Ras Al Khaimah, lies in 3/5/3.
        self.fixture.region.update(x=5, y=3, canonical_captions=[canonical.caption])
        canonical.region = self.fixture.region
        selection = canonical.selection()
        selection.update(schema='root-approved-canonical-WWP-caption-v1', role='full_current')
        evidence = {}
        for role in ('model_selection', 'manifest', 'review', 'target_signature'):
            path = self.base / f'{role}.txt'
            path.write_text(f'Synthetic {role} evidence for the package test.\n', encoding='utf-8')
            evidence[role] = dict(path=str(path), sha256=fixtures.sha(path))
        selection['instances'][0]['evidence'] = evidence
        package, expected = self.build_source_package(selection)
        path = self.base / 'independent selection.json'
        asset = self.base / 'actual.dat'
        approved = dict(canonical_caption_approvals=path, canonical_caption_approvals_sha256=fixtures.sha(path))
        self.assertTrue(verifier.verify(self.path, asset, package, **approved)['source_package_required'])
        review = Path(evidence['review']['path'])
        review_bytes = review.read_bytes()
        review.write_bytes(review_bytes + b'updated after approval')
        with self.assertRaisesRegex(ValueError, 'original caption review evidence SHA changed'):
            verifier.verify(self.path, asset, package, **approved)
        review.unlink()
        with self.assertRaisesRegex(ValueError, 'missing original caption review evidence'):
            verifier.verify(self.path, asset, package, **approved)
        review.write_bytes(review_bytes)
        copied = package / 'inputs/canonical_caption_approvals.json'
        original = path.read_bytes()
        self.assertEqual(original, copied.read_bytes())
        copied.write_bytes(original + b' ')
        with self.assertRaisesRegex(ValueError, 'input bytes changed'):
            verifier.verify(self.path, asset, package, **approved)
        copied.write_bytes(original)
        report = json.loads((self.base / 'report.json').read_bytes())
        self.assertEqual(['拉斯海马'], [c['zh'] for c in report['regions'][0]['canonical_captions']])
        with self.assertRaisesRegex(ValueError, 'requires its reproducible source package'):
            verifier.verify(self.path, asset, None, **approved)
        with self.assertRaisesRegex(ValueError, 'requires the original caption selection'):
            verifier.verify(self.path, asset, package)
        for field in approved:
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'must be supplied together'):
                verifier.verify(self.path, asset, package, **{field: approved[field]})
        with self.assertRaisesRegex(ValueError, 'SHA256 changed'):
            verifier.verify(self.path, asset, package, canonical_caption_approvals=path,
                            canonical_caption_approvals_sha256='0' * 64)
        for role in ('TEST_ONLY', 'named_private'):
            private = canonical.selection()
            if role == 'named_private':
                private.update(schema=selection['schema'], role=role)
                private['instances'][0]['evidence'] = dict.fromkeys(evidence, None)
                private['instances'][0]['evidence']['model_selection'] = evidence['model_selection']
            path.write_text(json.dumps(private), encoding='utf-8')
            with self.subTest(role=role), self.assertRaisesRegex(ValueError, 'requires a full_current'):
                verifier.verify(self.path, asset, package, canonical_caption_approvals=path,
                                canonical_caption_approvals_sha256=fixtures.sha(path))
        selection['instances'][0]['caption_id'] = 'different-city'
        path.write_text(json.dumps(selection), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'selection must exactly equal'):
            verifier.verify(self.path, asset, package, canonical_caption_approvals=path,
                            canonical_caption_approvals_sha256=fixtures.sha(path))
        path.unlink()
        for ref in evidence.values():
            Path(ref['path']).unlink()
        self.assert_relocated_rebuild(package, expected)


if __name__ == '__main__':
    unittest.main()
