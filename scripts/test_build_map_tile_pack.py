#!/usr/bin/env python3
"""Synthetic fixtures exercise the reviewed tile builder without game files."""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest

from PIL import Image, ImageDraw
from global_map_catalog import record_fingerprint

SCRIPT = Path(__file__).with_name("build_map_tile_pack.py")
FONT = Path("C:/Windows/Fonts/msyh.ttc")
BOLD_FONT = Path("C:/Windows/Fonts/msyhbd.ttc")


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class TilePackBuilderTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.is_file(), "reviewed tile builder is not implemented")
        spec = importlib.util.spec_from_file_location("tile_builder", SCRIPT)
        self.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.builder)
        self.temp = tempfile.TemporaryDirectory(prefix="tile-pack-builder-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.original = self.base / "original"
        self.original.mkdir()
        atlas = Image.new("RGBA", (512, 256))
        draw = ImageDraw.Draw(atlas)
        draw.rectangle((80, 20, 119, 39), fill="white")
        draw.rectangle((240, 80, 271, 99), fill="white")
        draw.ellipse((380, 100, 389, 109), fill="white")
        tiles = []
        for x in range(2):
            path = self.original / f"{x}.png"
            atlas.crop((x * 256, 0, (x + 1) * 256, 256)).save(path)
            tiles.append(dict(x=x, y=0, path=path.name, sha256=sha(path),
                              source_url="https://example.com/source"))
        features = [dict(id="peak", kind="peak", original="Original Peak",
                         original_elevation_m="8848", original_bbox=[80, 20, 120, 40],
                         source_url="https://example.com/peak"),
                    dict(id="city", kind="city", original="Original City",
                         original_bbox=[240, 80, 272, 100],
                         source_url="https://example.com/city")]
        self.region = dict(id="fixture", z=3, x=0, y=0, width_tiles=2, height_tiles=1,
                           original_tiles=tiles, context_tiles=[], features=features,
                           translations=[dict(id=f["id"], zh=zh, status="approved",
                                              source="https://example.com/translation",
                                              review_notes="Synthetic translation fixture",
                                              record_sha256=record_fingerprint(f))
                                         for f, zh in zip(features, ["汉峰", "城市"])],
                           layouts=[dict(id="peak", x=100, y=80, font_size=18,
                                         font_style="regular"),
                                    dict(id="city", x=256, y=150, font_size=22,
                                         font_style="bold", lines=["城", "市"])],
                           symbols=[dict(id="dot", feature_id="city", kind="circle",
                                         x=385, y=105, size=9, outline_width=1,
                                         original_bbox=[380, 100, 390, 110])])
        self.approve()
        self.manifest = dict(schema_version=1,
                             scope=dict(type="cached_regions", global_complete=False),
                             font_sha256=sha(FONT), bold_font_sha256=sha(BOLD_FONT),
                             regions=[self.region])
        self.sign_render()

    def approve(self):
        self.region["review"] = dict(status="approved", source_verified=True,
                                     render_verified=True, reviewer="Fixture reviewer",
                                     notes="Synthetic fixture only",
                                     record_sha256=record_fingerprint(
                                         {k: v for k, v in self.region.items() if k != "review"}))

    def compile(self, preview=False):
        return self.builder.compile_manifest(self.manifest, self.original, FONT, BOLD_FONT,
                                             self.base / "preview" if preview else None)

    def sign_render(self):
        # Synthetic fixture approval only; real visual evidence is signed by a
        # separate human review and never by the production builder.
        _, report = self.compile()
        self.region["review"]["render_sha256"] = report["regions"][0]["atlas_rgba_sha256"]

    def rejected(self, reason):
        wire, report = self.compile()
        self.assertTrue(wire is None, "expected rejected manifest to produce no pack")
        self.assertFalse(report["ready"])
        self.assertIn(reason, json.dumps(report, ensure_ascii=False))
        return report

    def add_original_slash(self):
        frame = Image.new("RGBA", (5, 6), (43, 56, 72, 0))
        ImageDraw.Draw(frame).line((4, 0, 0, 5), fill=(33, 100, 179, 38), width=2)
        box = [254, 205, 259, 211]
        self.region["source_punctuation"] = [dict(id="seam-slash", kind="slash", status="approved",
            original_bbox=box, source_rgba_sha256=self.builder.sha256(frame.tobytes()),
            review_notes="Verified isolated punctuation on the two source tiles")]
        for t in self.region["original_tiles"]:
            path = self.original / t["path"]
            with Image.open(path) as im:
                tile = im.convert("RGBA")
            tile.paste(frame, (box[0] - 256 * t["x"], box[1]))
            tile.save(path)
            t["sha256"] = sha(path)
        self.approve()
        self.sign_render()
        return frame

    def test_original_punctuation_preserves_only_visible_rgba_and_keeps_counts(self):
        _, before = self.compile(preview=True)
        preview = self.base / "preview/fixture.zh.png"
        with Image.open(preview) as im:
            expected = im.convert("RGBA")
        frame = self.add_original_slash()
        expected.paste(frame, (254, 205), frame.getchannel("A").point(lambda a: 255 if a else 0))
        wire, report = self.compile(preview=True)
        self.assertIsNotNone(wire, report["gaps"])
        with Image.open(preview) as im:
            self.assertEqual(expected.tobytes(), im.convert("RGBA").tobytes())
        current, previous = report["regions"][0], before["regions"][0]
        self.assertEqual(previous["labels"], current["labels"])
        self.assertEqual(previous["symbols"], current["symbols"])
        self.assertEqual([t["label_count"] for t in previous["tiles"]], [t["label_count"] for t in current["tiles"]])
        self.assertEqual([[3, 0, 0], [3, 1, 0]], current["source_punctuation"][0]["tiles"])
        self.assertEqual(0, current["source_alpha"]["unaccounted_pixels"])

    def test_original_punctuation_rejects_unapproved_changed_cut_or_overlapping_marks(self):
        frame = self.add_original_slash()
        approved = copy.deepcopy(self.region)
        for case in ("unapproved", "unknown_kind", "named", "wrong_hash", "cut", "tile", "collision", "claimed_source"):
            with self.subTest(case=case):
                self.region = copy.deepcopy(approved)
                self.manifest["regions"] = [self.region]
                mark = self.region["source_punctuation"][0]
                if case == "unapproved":
                    mark["status"] = "pending"
                elif case == "unknown_kind":
                    mark["kind"] = "unknown"
                elif case == "named":
                    mark["name"] = "an unverified name"
                elif case == "wrong_hash":
                    mark["source_rgba_sha256"] = "0" * 64
                elif case == "cut":
                    mark["original_bbox"][2] -= 1
                    mark["source_rgba_sha256"] = self.builder.sha256(frame.crop((0, 0, 4, 6)).tobytes())
                elif case == "tile":
                    self.region["original_tiles"][1]["sha256"] = "0" * 64
                elif case == "collision":
                    self.region["symbols"][0].update(x=256, y=207)
                else:
                    self.region["features"][1]["original_bbox"] = [240, 80, 272, 211]
                self.approve()
                self.rejected("source_punctuation")

    def elevation_only_peak(self):
        feature = self.region["features"][0]
        feature.update(original="8848 m", elevation_only=True)
        self.region["translations"][0].update(zh="8848米", record_sha256=record_fingerprint(feature))
        self.approve()
        self.sign_render()

    def test_original_unnamed_peak_renders_elevation_once(self):
        self.elevation_only_peak()
        wire, report = self.compile()
        self.assertIsNotNone(wire)
        self.assertTrue(report["ready"], report["gaps"])
        label = report["regions"][0]["labels"][0]
        self.assertEqual(["8848米"], label["lines"])
        self.assertEqual("8848米", label["elevation"])
        self.assertEqual(1, len(label["line_bounds"]))
        self.assertTrue(label["elevation_only"])

    def test_elevation_only_requires_exact_source_and_no_invented_name(self):
        original = copy.deepcopy(self.region)
        for change in ("invented_name", "wrong_source_number", "named_source", "split_height", "non_boolean", "city"):
            with self.subTest(change=change):
                self.region = copy.deepcopy(original)
                self.manifest["regions"] = [self.region]
                self.elevation_only_peak()
                feature, translation = self.region["features"][0], self.region["translations"][0]
                if change == "invented_name":
                    translation["zh"] = "无名峰"
                elif change == "wrong_source_number":
                    feature["original"] = "8849 m"
                elif change == "named_source":
                    feature["original"] = "Original Peak 8848 m"
                elif change == "split_height":
                    self.region["layouts"][0]["lines"] = ["8848", "米"]
                elif change == "non_boolean":
                    feature["elevation_only"] = 1
                else:
                    feature["kind"] = "city"
                translation["record_sha256"] = record_fingerprint(feature)
                self.approve()
                self.sign_render()
                self.rejected("elevation_only")

    def test_elevation_only_flag_change_invalidates_old_approval(self):
        self.elevation_only_peak()
        self.region["features"][0]["elevation_only"] = False
        self.rejected("region_fingerprint")

    def unlabeled_symbol(self):
        self.region["features"] = []
        self.region["translations"] = []
        self.region["layouts"] = []
        symbol = self.region["symbols"][0]
        symbol.pop("feature_id", None)
        symbol.update(unlabeled=True, source_url="https://example.com/original-dot",
                      review_notes="Original source contains this dot and no text")
        for tile in self.region["original_tiles"]:
            image = Image.new("RGBA", (256, 256))
            if tile["x"] == 1:
                ImageDraw.Draw(image).ellipse((124, 100, 133, 109), fill="white")
            path = self.original / tile["path"]
            image.save(path)
            tile["sha256"] = sha(path)
        self.approve()
        self.sign_render()

    def test_original_unlabeled_symbol_has_geometry_without_invented_name(self):
        self.unlabeled_symbol()
        wire, report = self.compile()
        self.assertTrue(report["ready"], report["gaps"])
        self.assertIsNotNone(wire)
        self.assertEqual(0, report["label_count"])
        self.assertEqual(1, report["symbol_count"])
        symbol = report["regions"][0]["symbols"][0]
        self.assertIsNone(symbol["feature_id"])
        self.assertTrue(symbol["unlabeled"])
        self.assertEqual([[3, 1, 0]], symbol["tiles"])
        self.assertEqual(0, report["regions"][0]["source_alpha"]["unaccounted_pixels"])

    def test_unlabeled_symbol_requires_explicit_independent_source_review(self):
        self.unlabeled_symbol()
        original = copy.deepcopy(self.region)
        for change in ("non_boolean", "named", "source", "notes"):
            with self.subTest(change=change):
                self.region = copy.deepcopy(original)
                self.manifest["regions"] = [self.region]
                symbol = self.region["symbols"][0]
                if change == "non_boolean":
                    symbol["unlabeled"] = 1
                elif change == "named":
                    symbol["feature_id"] = "city"
                elif change == "source":
                    symbol["source_url"] = ""
                else:
                    symbol["review_notes"] = ""
                self.approve()
                self.sign_render()
                self.rejected("unlabeled_symbol")

    def test_binary_payload_and_cross_tile_reassembly(self):
        wire, report = self.compile(preview=True)
        self.assertTrue(report["ready"], report)
        self.assertFalse(report["global_complete"])
        self.assertEqual(b"CMOTIL01", wire[:8])
        self.assertEqual(64, wire[8])
        self.assertEqual(hashlib.sha256(wire[73:]).hexdigest(), wire[9:73].decode())
        stream = io.BytesIO(wire[73:])
        self.assertEqual(2, struct.unpack("<i", stream.read(4))[0])
        joined = Image.new("RGBA", (512, 256))
        counts = []
        for _ in range(2):
            z, x, y, labels, length = struct.unpack("<iiiii", stream.read(20))
            self.assertEqual((3, 0), (z, y))
            tile = Image.open(io.BytesIO(stream.read(length))).convert("RGBA")
            joined.paste(tile, (x * 256, 0))
            counts.append(labels)
        self.assertEqual(b"", stream.read())
        self.assertEqual([2, 1], counts)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.assertEqual(preview.tobytes(), joined.tobytes())
        region = report["regions"][0]
        self.assertEqual("8848米", region["labels"][0]["elevation"])
        self.assertEqual(["汉峰", "8848米"], region["labels"][0]["lines"])
        self.assertEqual(1, len(region["cross_tile_instances"]))
        self.assertEqual(0, region["source_alpha"]["unaccounted_pixels"])

    def test_source_hash_change(self):
        path = self.original / "0.png"
        Image.new("RGBA", (256, 256)).save(path)
        self.rejected("source_sha256")

    def test_unaccounted_source_alpha(self):
        path = self.original / "0.png"
        with Image.open(path) as source:
            image = source.convert("RGBA")
        image.putpixel((10, 10), (255, 255, 255, 1))
        image.save(path)
        self.region["original_tiles"][0]["sha256"] = sha(path)
        self.approve()
        report = self.rejected("unaccounted_alpha")
        self.assertEqual(1, report["regions"][0]["source_alpha"]["unaccounted_pixels"])

    def add_unclassified_source(self, points):
        path = self.original / "0.png"
        with Image.open(path) as source:
            image = source.convert("RGBA")
        mask = Image.new("L", (512, 256))
        for point in points:
            image.putpixel(point, (38, 82, 145, 2))
            mask.putpixel(point, 255)
        image.save(path)
        self.region["original_tiles"][0]["sha256"] = sha(path)
        self.region["source_unclassified"] = dict(
            pixels=len(points), bbox=list(mask.getbbox()),
            mask_sha256=hashlib.sha256(mask.tobytes()).hexdigest())

    def test_reviewed_unclassified_source_has_separate_accounting_and_no_render(self):
        previous_pack, previous = self.compile()
        render_sha = self.region["review"]["render_sha256"]
        self.add_unclassified_source([(10, 10), (11, 10), (12, 12)])
        self.approve()
        self.region["review"]["render_sha256"] = render_sha
        pack, report = self.compile()
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual(previous_pack, pack)
        original = previous["regions"][0]["source_alpha"]
        audit = report["regions"][0]["source_alpha"]
        self.assertEqual(original["total_pixels"] + 3, audit["total_pixels"])
        self.assertEqual(original["accounted_pixels"], audit["accounted_pixels"])
        self.assertEqual(0, audit["background_pixels"])
        self.assertEqual(0, audit["unaccounted_pixels"])
        self.assertEqual(self.region["source_unclassified"], audit["unclassified"])

    def test_unclassified_source_mask_rejects_same_count_and_bounds_when_shape_changes(self):
        self.add_unclassified_source([(10, 10), (11, 10), (12, 12)])
        path = self.original / "0.png"
        with Image.open(path) as source:
            image = source.convert("RGBA")
        image.putpixel((11, 10), (0, 0, 0, 0))
        image.putpixel((10, 11), (38, 82, 145, 2))
        image.save(path)
        self.region["original_tiles"][0]["sha256"] = sha(path)
        self.approve()
        self.sign_render()
        audit = self.rejected("source_unclassified")["regions"][0]["source_alpha"]
        self.assertEqual(3, audit["unaccounted_pixels"])

    def test_missing_and_duplicate_record_sets(self):
        for field in ("features", "translations", "layouts"):
            for duplicate in (False, True):
                with self.subTest(field=field, duplicate=duplicate):
                    saved = copy.deepcopy(self.region[field])
                    self.region[field] = saved + [saved[0]] if duplicate else saved[1:]
                    self.approve()
                    self.rejected("record_set")
                    self.region[field] = saved

    def test_non_chinese_and_stale_translation(self):
        self.region["translations"][0]["zh"] = "English"
        self.region["translations"][0]["record_sha256"] = "0" * 64
        self.region["translations"][1]["record_sha256"] = "0" * 64
        self.approve()
        report = self.rejected("non_chinese")
        self.assertIn("translation_fingerprint", json.dumps(report))
        self.assertEqual(2, sum(g["code"] == "translation_fingerprint" for g in report["gaps"]))

    def test_unapproved_translation_is_not_hidden_by_approved_region(self):
        self.region["translations"][0]["status"] = "draft"
        self.approve()
        self.rejected("translation_review")

    def test_nonfinite_layout_is_structure_error(self):
        self.region["layouts"][0]["x"] = float("nan")
        self.approve()
        with self.assertRaises(ValueError):
            self.compile()

    def test_triangle_has_real_source_and_does_not_add_label_count(self):
        self.region["symbols"][0]["kind"] = "triangle"
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertIsNotNone(wire, report)
        self.assertEqual("triangle", report["regions"][0]["symbols"][0]["kind"])
        self.assertEqual([2, 1], [t["label_count"] for t in report["regions"][0]["tiles"]])

    def source_rgba_triangle(self):
        self.region["features"] = self.region["features"][:1]
        self.region["translations"] = self.region["translations"][:1]
        self.region["layouts"] = self.region["layouts"][:1]
        frame = Image.new("RGBA", (10, 9), (71, 112, 76, 0))
        ImageDraw.Draw(frame).polygon([(5, 0), (9, 8), (0, 8)], fill="white")
        frame.putpixel((0, 0), (33, 44, 55, 1))
        atlas = Image.new("RGBA", (512, 256))
        ImageDraw.Draw(atlas).rectangle((80, 20, 119, 39), fill="white")
        atlas.paste(frame, (252, 180))  # Exact crop crosses an official XYZ seam.
        for tile in self.region["original_tiles"]:
            tile.update(path=f"3/{tile['x']}/0.png",
                        source_url=f"http://warfaresims.slitherine.com/MapTiles/Labels/3/{tile['x']}/0.png")
            path = self.original / tile["path"]
            path.parent.mkdir(parents=True, exist_ok=True)
            atlas.crop((tile["x"] * 256, 0, (tile["x"] + 1) * 256, 256)).save(path)
            tile["sha256"] = sha(path)
        self.region["symbols"] = [dict(id="triangle", feature_id="peak", kind="triangle",
            x=257, y=184.5, original_bbox=[252, 180, 262, 189], render_mode="source_rgba",
            source_rgba_sha256=hashlib.sha256(frame.tobytes()).hexdigest(), source_symbol_only=True,
            review_notes="Synthetic independently reviewed triangle-only crop; source text is separate.")]
        self.approve()
        return frame

    def test_source_rgba_triangle_preserves_every_channel_across_tile_seam(self):
        frame = self.source_rgba_triangle()
        try:
            self.sign_render()
        except ValueError as error:
            self.fail(f"source_rgba triangle must bypass vector size/outline rendering: {error}")
        wire, report = self.compile(preview=True)
        self.assertTrue(report["ready"], report["gaps"])
        self.assertIsNotNone(wire)
        actual = Image.open(self.base / "preview/fixture.zh.png").convert("RGBA")
        self.assertEqual(frame.tobytes(), actual.crop((252, 180, 262, 189)).tobytes())
        self.assertEqual((71, 112, 76, 0), actual.getpixel((253, 180)))
        stream = io.BytesIO(wire[73:])
        joined = Image.new("RGBA", actual.size)
        for _ in range(struct.unpack("<i", stream.read(4))[0]):
            _, x, y, _, length = struct.unpack("<iiiii", stream.read(20))
            tile = Image.open(io.BytesIO(stream.read(length))).convert("RGBA")
            joined.paste(tile, (x * 256, y * 256))
        self.assertEqual(actual.tobytes(), joined.tobytes())
        row = report["regions"][0]["symbols"][0]
        self.assertEqual("source_rgba", row["native_mode"])
        self.assertEqual(hashlib.sha256(frame.tobytes()).hexdigest(), row["source_rgba_sha256"])
        self.assertEqual([[3, 0, 0], [3, 1, 0]], row["tiles"])
        self.assertEqual((1, 1), (report["label_count"], report["symbol_count"]))

    def test_source_rgba_requires_exact_qualified_triangle_and_source(self):
        self.source_rgba_triangle()
        original = copy.deepcopy(self.region)
        changes = [dict(source_symbol_only=False), dict(source_symbol_only=1),
                   dict(review_notes=""), dict(kind="circle"), dict(feature_id="city"),
                   dict(unlabeled=True), dict(shared_source_id="absent"),
                   dict(source_rgba_sha256="0" * 64), dict(x=257.25),
                   dict(original_bbox=[252.0, 180, 262, 189]),
                   dict(original_bbox=[252, 180, 600, 189]),
                   dict(original_bbox=[252, 180, 262]), dict(size=10), dict(outline_width=1)]
        for changeset in changes:
            with self.subTest(changes=changeset):
                self.region = copy.deepcopy(original)
                self.manifest["regions"] = [self.region]
                self.region["symbols"][0].update(changeset)
                self.approve()
                self.rejected("source_rgba")
        for field in ("source_symbol_only", "source_rgba_sha256"):
            with self.subTest(missing=field):
                self.region = copy.deepcopy(original)
                self.manifest["regions"] = [self.region]
                del self.region["symbols"][0][field]
                self.approve()
                self.rejected("source_rgba")
        self.region = copy.deepcopy(original)
        self.manifest["regions"] = [self.region]
        self.region["features"][0]["kind"] = "city"
        self.region["translations"][0]["record_sha256"] = record_fingerprint(self.region["features"][0])
        self.approve()
        self.rejected("source_rgba")
        for field, value in (("sha256", "0" * 64), ("path", "missing.png"),
                             ("source_url", "https://example.com/source")):
            with self.subTest(source_field=field):
                self.region = copy.deepcopy(original)
                self.manifest["regions"] = [self.region]
                self.region["original_tiles"][0][field] = value
                self.approve()
                self.rejected("source_rgba")
        self.region = copy.deepcopy(original)
        self.manifest["regions"] = [self.region]
        (self.original / self.region["original_tiles"][0]["path"]).unlink()
        self.approve()
        self.rejected("source_rgba")

    def test_source_rgba_never_falls_back_or_overwrites_even_low_alpha(self):
        from unittest.mock import patch
        frame = self.source_rgba_triangle()
        symbols = {row["id"]: row for row in self.region["symbols"]}
        features = {row["id"]: row for row in self.region["features"]}
        gaps = []
        args = (self.region, features, {}, {}, symbols, {}, {}, lambda code, **kw: gaps.append(code))
        atlas, _, rows, _ = self.builder.render_region(*args)
        self.assertIn("source_rgba", gaps)
        self.assertEqual([], rows)
        self.assertIsNone(atlas.getchannel("A").getbbox())
        symbols["ordinary"] = dict(id="ordinary", feature_id="peak", kind="triangle",
                                    x=252.5, y=180.5, size=1, outline_width=.5)
        gaps.clear()
        with patch.object(self.builder, "symbol_patch", return_value=Image.new("RGBA", (1, 1), (2, 3, 4, 1))):
            atlas, _, rows, _ = self.builder.render_region(*args, source_rgba_frames={"triangle": frame})
        self.assertIn("source_rgba", gaps)
        self.assertEqual((2, 3, 4, 1), atlas.getpixel((252, 180)))
        self.assertEqual(["ordinary"], [row["id"] for row in rows])

    def test_source_rgba_final_hash_detects_later_transparent_RGB_overwrite(self):
        self.source_rgba_triangle()
        first = Image.new("RGBA", (10, 9), (71, 112, 76, 0))
        first.putpixel((0, 0), (255, 255, 255, 1))
        second = Image.new("RGBA", (10, 9), (11, 22, 33, 0))
        second.putpixel((9, 0), (255, 255, 255, 1))
        symbol = self.region["symbols"][0]
        symbol["source_rgba_sha256"] = hashlib.sha256(first.tobytes()).hexdigest()
        other = dict(symbol, id="later", x=266, original_bbox=[261, 180, 271, 189],
                     source_rgba_sha256=hashlib.sha256(second.tobytes()).hexdigest())
        gaps = []
        self.builder.render_region(self.region, {"peak": self.region["features"][0]}, {}, {},
            {"triangle": symbol, "later": other}, {}, {}, lambda code, **kw: gaps.append((code, kw)),
            source_rgba_frames={"triangle": first, "later": second})
        self.assertTrue(any(code == "source_rgba" and row["id"] == "triangle"
                            and "final" in row.get("reason", "") for code, row in gaps))

    def test_source_rgba_compound_peak_source_bbox_is_not_text_pollution_evidence(self):
        self.source_rgba_triangle()
        feature = self.region["features"][0]
        feature["original_bbox"] = [80, 20, 262, 189]
        self.region["translations"][0]["record_sha256"] = record_fingerprint(feature)
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertIsNotNone(wire, report["gaps"])
        self.assertTrue(report["ready"])

    def test_symbol_render_mode_is_explicit_and_legacy_output_bytes_stay_fixed(self):
        wire, report = self.compile()
        self.assertEqual("5d6f12c903a44c3645a2dc135574fb648d670ac56d55e1157f3348a3e8615853",
                         report["regions"][0]["atlas_rgba_sha256"])
        self.assertEqual("5eba8a81613681c2f5ba43c3ec00430d8425ca5232afc498d4bb6c8887f51aa4",
                         hashlib.sha256(wire).hexdigest())
        self.region["symbols"][0]["render_mode"] = "typo"
        self.approve()
        self.rejected("symbol_render_mode")
        with self.assertRaises(ValueError):
            self.builder.symbol_patch(self.region["symbols"][0])

    def test_preview_cannot_overwrite_later_region_source(self):
        later = copy.deepcopy(self.region)
        later.update(id="later", x=2)
        for tile in later["original_tiles"]:
            tile["x"] += 2
        path = self.original / "fixture.zh.png"
        path.write_bytes((self.original / "0.png").read_bytes())
        original_bytes = path.read_bytes()
        later["original_tiles"][0].update(path=path.name, sha256=sha(path))
        later["review"]["record_sha256"] = record_fingerprint({k: v for k, v in later.items() if k != "review"})
        self.manifest["regions"].append(later)
        with self.assertRaises(ValueError):
            self.builder.compile_manifest(self.manifest, self.original, FONT, BOLD_FONT, self.original)
        self.assertEqual(original_bytes, path.read_bytes())

    def test_unapproved_and_changed_review(self):
        self.region["review"]["status"] = "draft"
        self.rejected("region_review")
        self.approve()
        self.region["layouts"][0]["x"] += 1
        self.rejected("region_fingerprint")

    def test_peak_elevation_and_lines_cannot_omit_text(self):
        self.region["layouts"][0]["lines"] = ["汉", "8848米"]
        self.approve()
        self.rejected("layout_lines")

    def test_missing_font_character(self):
        self.region["translations"][0]["zh"] = "汉\U000323af"
        self.approve()
        self.rejected("missing_glyph")

    def test_font_sha_change(self):
        self.manifest["font_sha256"] = "0" * 64
        self.rejected("font_sha256")

    def test_duplicate_tiles_and_invalid_bounds(self):
        self.region["original_tiles"].append(self.region["original_tiles"][0].copy())
        self.approve()
        self.rejected("tile_set")
        self.region["original_tiles"].pop()
        self.region["x"] = 8
        with self.assertRaises(ValueError):
            self.compile()

    def test_overlapping_regions(self):
        other = copy.deepcopy(self.region)
        other["id"] = "other"
        self.manifest["regions"].append(other)
        self.rejected("overlapping_tile")

    def test_region_boundary_clipping(self):
        self.region["layouts"][0]["x"] = 0
        self.approve()
        self.rejected("clipped")

    def test_strong_pixel_collision(self):
        self.region["layouts"][1].update(x=100, y=80)
        self.approve()
        self.rejected("collision")

    def test_source_cross_region_needs_context(self):
        self.region["features"][0]["original_bbox"][0] = -10
        self.region["translations"][0]["record_sha256"] = record_fingerprint(
            self.region["features"][0])
        self.approve()
        self.rejected("context_incomplete")

    def test_empty_original_bbox_and_unknown_symbol_feature(self):
        self.region["features"][0]["original_bbox"] = [0, 0, 10, 10]
        self.region["symbols"][0]["feature_id"] = "unknown"
        self.approve()
        report = self.rejected("empty_original_bbox")
        self.assertIn("symbol_feature", json.dumps(report))

    def test_path_escape_and_source_size(self):
        self.region["original_tiles"][0]["path"] = "../escape.png"
        self.approve()
        self.rejected("source_path")
        self.region["original_tiles"][0]["path"] = "0.png"
        Image.new("RGBA", (32, 32)).save(self.original / "0.png")
        self.region["original_tiles"][0]["sha256"] = sha(self.original / "0.png")
        self.approve()
        self.rejected("source_dimensions")

    def test_draft_preview_is_not_pack(self):
        self.region["review"]["render_verified"] = False
        self.region["review"].pop("render_sha256")
        wire, report = self.compile(preview=True)
        self.assertIsNone(wire)
        self.assertFalse(report["ready"])
        self.assertTrue((self.base / "preview/fixture.zh.png").is_file())
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.assertEqual(hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest(),
                             report["regions"][0]["atlas_rgba_sha256"])

    def test_cli_failure_preserves_previous_output(self):
        self.region["review"]["status"] = "draft"
        manifest = self.base / "manifest.json"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        output = self.base / "pack.dat"
        output.write_bytes(b"previous")
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(output),
                                 "--report", str(self.base / "report.json")],
                                capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual(b"previous", output.read_bytes())
        self.assertFalse(json.loads((self.base / "report.json").read_text(encoding="utf-8"))["ready"])

    def test_cli_preview_cannot_overwrite_manifest(self):
        manifest = self.base / "fixture.zh.png"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        previous = manifest.read_bytes()
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(self.base / "pack.dat"),
                                 "--report", str(self.base / "report.json"),
                                 "--preview-dir", str(self.base)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual(previous, manifest.read_bytes())

    def test_scope_cannot_use_numeric_false(self):
        self.manifest["scope"]["global_complete"] = 0
        with self.assertRaises(ValueError):
            self.compile()

    def test_approved_review_requires_render_pixel_digest(self):
        self.region["review"].pop("render_sha256", None)
        self.rejected("render_fingerprint")

    def test_font_swap_invalidates_visual_review_until_resigned(self):
        _, before = self.compile(preview=True)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.region["review"]["render_sha256"] = hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest()
        old_review = copy.deepcopy(self.region["review"])
        self.manifest.update(font_sha256=sha(BOLD_FONT), bold_font_sha256=sha(FONT))
        wire, report = self.builder.compile_manifest(self.manifest, self.original, BOLD_FONT, FONT)
        self.assertIsNone(wire)
        self.assertFalse(report["ready"])
        self.assertIn("render_fingerprint", json.dumps(report))
        self.assertEqual(old_review, self.region["review"])
        actual = report["regions"][0]["atlas_rgba_sha256"]
        self.assertNotEqual(old_review["render_sha256"], actual)
        self.region["review"]["render_sha256"] = actual
        wire, report = self.builder.compile_manifest(self.manifest, self.original, BOLD_FONT, FONT)
        self.assertIsNotNone(wire, report)

    def test_renderer_change_invalidates_visual_review_until_resigned(self):
        self.compile(preview=True)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.region["review"]["render_sha256"] = hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest()
        self.builder.SCALE = 4
        report = self.rejected("render_fingerprint")
        self.region["review"]["render_sha256"] = report["regions"][0]["atlas_rgba_sha256"]
        wire, report = self.compile()
        self.assertIsNotNone(wire, report)

    def test_png_encoding_change_preserves_pixel_approval(self):
        _, before = self.compile(preview=True)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.region["review"]["render_sha256"] = hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest()

        def different_png_encoding(image):
            stream = io.BytesIO()
            image.save(stream, format="PNG", compress_level=0)
            return stream.getvalue()

        self.builder.png_bytes = different_png_encoding
        wire, report = self.compile()
        self.assertIsNotNone(wire, report)
        self.assertEqual(self.region["review"]["render_sha256"], report["regions"][0]["atlas_rgba_sha256"])
        self.assertNotEqual(before["regions"][0]["atlas_png_sha256"], report["regions"][0]["atlas_png_sha256"])

    def test_case_alias_rejected_before_any_preview_write(self):
        second = copy.deepcopy(self.region)
        second.update(id="FIXTURE", x=2)
        for tile in second["original_tiles"]:
            tile["x"] += 2
        second["review"]["record_sha256"] = record_fingerprint({k: v for k, v in second.items() if k != "review"})
        self.manifest["regions"].append(second)
        preview = self.base / "preview"
        preview.mkdir()
        evidence = preview / "fixture.zh.png"
        evidence.write_bytes(b"existing review evidence")
        with self.assertRaises(ValueError):
            self.compile(preview=True)
        self.assertEqual(b"existing review evidence", evidence.read_bytes())
        self.assertEqual(1, len(list(preview.iterdir())))

    def test_reserved_device_ids_rejected_before_preview_write(self):
        for identity in ("NUL", "con", "COM1", "LPT9", "AUX.notes", "PRN"):
            with self.subTest(identity=identity):
                self.region["id"] = identity
                self.approve()
                with self.assertRaises(ValueError):
                    self.compile(preview=True)
                self.assertFalse((self.base / "preview").exists())

    def test_cli_case_alias_preserves_existing_preview(self):
        second = copy.deepcopy(self.region)
        second["id"] = "FIXTURE"
        self.manifest["regions"].append(second)
        preview = self.base / "preview"
        preview.mkdir()
        evidence = preview / "fixture.zh.png"
        evidence.write_bytes(b"existing review evidence")
        manifest = self.base / "manifest.json"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(self.base / "pack.dat"),
                                 "--report", str(self.base / "report.json"),
                                 "--preview-dir", str(preview)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual(b"existing review evidence", evidence.read_bytes())
        self.assertEqual(1, len(list(preview.iterdir())))

    def legacy_rule(self):
        from wwp import load
        old = load(SCRIPT.parent.parent / "assets/placenames/Cities.wwp")[0]
        return dict(feature_id="city", name=old["name"], lat=old["lat"], lon=old["lon"],
                    level=1, source="https://example.com/legacy-identity", review_notes="Exact synthetic identity review")

    def test_reviewed_legacy_rules_emit_v2_and_are_reported(self):
        old_wire, _ = self.compile()
        rule = self.legacy_rule()
        self.region["legacy_suppressions"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual(b"CMOTIL02", wire[:8])
        self.assertEqual(1, len(report["legacy_suppressions"]))
        self.assertEqual(rule["name"], report["legacy_suppressions"][0]["name"])
        payload = wire[73:]
        self.assertEqual(hashlib.sha256(payload).hexdigest(), wire[9:73].decode())
        self.assertEqual(old_wire[73:], payload[:len(old_wire) - 73])
        stream = io.BytesIO(payload[len(old_wire) - 73:])
        self.assertEqual((1, 3), struct.unpack("<ii", stream.read(8)))
        from wwp import _read_7bit_length
        remainder = stream.read()
        length, offset = _read_7bit_length(remainder, 0)
        self.assertEqual(rule["name"], remainder[offset:offset + length].decode("utf-8"))
        self.assertEqual(struct.pack("<ffi", rule["lat"], rule["lon"], rule["level"]),
                         remainder[offset + length:])
        self.assertEqual([3, 5, 3], report["legacy_suppressions"][0]["legacy_tile"])

    def test_unverified_or_unknown_legacy_rule_cannot_suppress(self):
        for field, value in (("name", "并不存在的旧地名"), ("lat", 89),
                             ("feature_id", "absent"), ("review_notes", ""),
                             ("source", "local-only"), ("level", 0)):
            with self.subTest(field=field):
                rule = self.legacy_rule()
                rule[field] = value
                self.region["legacy_suppressions"] = [rule]
                self.approve()
                self.sign_render()
                self.rejected("legacy_suppression")

    def test_legacy_rule_changes_require_fresh_region_review(self):
        self.region["legacy_suppressions"] = [self.legacy_rule()]
        self.rejected("region_fingerprint")

    def test_empty_rules_preserve_v1_bytes_without_loading_wwp(self):
        old_wire, _ = self.compile()

        def forbidden_load():
            self.fail("CMOTIL01 must not depend on canonical WWP files")

        self.builder.canonical_legacy_points = forbidden_load
        self.region["legacy_suppressions"] = []
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual(old_wire, wire)

    def test_rules_deduplicate_across_reviewed_placements(self):
        self.region["legacy_suppressions"] = [self.legacy_rule()]
        self.approve()
        self.sign_render()
        second = copy.deepcopy(self.region)
        second.update(id="second-placement", x=2)
        for tile in second["original_tiles"]:
            tile["x"] += 2
        second["review"]["record_sha256"] = record_fingerprint(
            {key: value for key, value in second.items() if key != "review"})
        self.manifest["regions"].append(second)
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        self.assertEqual(1, len(report["legacy_suppressions"]))
        self.assertEqual(2, len(report["legacy_suppressions"][0]["references"]))
        stream = io.BytesIO(wire[73:])
        for _ in range(struct.unpack("<i", stream.read(4))[0]):
            *_, length = struct.unpack("<iiiii", stream.read(20))
            stream.seek(length, io.SEEK_CUR)
        self.assertEqual(1, struct.unpack("<i", stream.read(4))[0])

    def test_rule_wire_order_is_deterministic(self):
        from wwp import load
        other = load(SCRIPT.parent.parent / "assets/placenames/Cities.wwp")[1]
        second = dict(self.legacy_rule(), name=other["name"], lat=other["lat"], lon=other["lon"])
        self.region["legacy_suppressions"] = [self.legacy_rule(), second]
        self.approve()
        self.sign_render()
        first_wire, report = self.compile()
        self.assertTrue(first_wire is not None, report["gaps"])
        self.region["legacy_suppressions"].reverse()
        self.approve()
        self.sign_render()
        second_wire, _ = self.compile()
        self.assertEqual(first_wire, second_wire)

    def test_one_float32_bit_or_nonfinite_coordinate_is_rejected(self):
        rule = self.legacy_rule()
        bits = struct.unpack("<I", struct.pack("<f", rule["lat"]))[0]
        for latitude in (struct.unpack("<f", struct.pack("<I", bits + 1))[0],
                         float("nan"), float("inf"), -float("inf")):
            with self.subTest(latitude=latitude):
                self.region["legacy_suppressions"] = [dict(rule, lat=latitude)]
                self.approve()
                self.sign_render()
                self.rejected("legacy_suppression")

    def test_negative_zero_does_not_match_positive_zero(self):
        rule = dict(self.legacy_rule(), lat=0.0)
        key = self.builder.legacy_key(0, rule["name"], rule["lat"], rule["lon"], rule["level"])[1:]
        self.builder.canonical_legacy_points = lambda: ({key}, [])
        self.region["legacy_suppressions"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        rule["lat"] = -0.0
        self.approve()
        self.sign_render()
        self.rejected("legacy_suppression")

    def test_rule_requires_actual_chinese_label_output(self):
        self.region["legacy_suppressions"] = [self.legacy_rule()]
        self.region["layouts"] = [self.region["layouts"][0]]
        self.approve()
        self.sign_render()
        self.rejected("legacy_suppression")

    def test_noncity_rule_requires_synoptic_level_zero(self):
        from wwp import load
        old = load(SCRIPT.parent.parent / "assets/placenames/Synoptic.wwp")[0]
        rule = dict(self.legacy_rule(), feature_id="peak", level=0,
                    name=old["name"], lat=old["lat"], lon=old["lon"])
        self.region["legacy_suppressions"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        rule["level"] = 1
        self.approve()
        self.sign_render()
        self.rejected("legacy_suppression")

    def test_invalid_rule_cli_preserves_previous_output(self):
        self.region["legacy_suppressions"] = [dict(self.legacy_rule(), name="并不存在的旧地名")]
        self.approve()
        self.sign_render()
        manifest = self.base / "manifest.json"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        output = self.base / "pack.dat"
        output.write_bytes(b"previous")
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(output),
                                 "--report", str(self.base / "report.json")],
                                capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual(b"previous", output.read_bytes())

    def test_complete_66_tile_region_is_supported(self):
        self.region.update(z=4, width_tiles=11, height_tiles=6)
        empty = self.original / "empty.png"
        Image.new("RGBA", (256, 256)).save(empty)
        original = copy.deepcopy(self.region["original_tiles"])
        self.region["original_tiles"] = [
            original[x] if y == 0 and x < 2 else
            dict(x=x, y=y, path=empty.name, sha256=sha(empty), source_url="https://example.com/empty")
            for y in range(6) for x in range(11)]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertIsNotNone(wire, report["gaps"])
        self.assertEqual(66, report["tile_count"])

    def test_expanded_limits_keep_width_grid_and_total_bounds(self):
        self.assertEqual((256, 16384), (self.builder.MAX_REGION_TILES, self.builder.MAX_TOTAL_TILES))
        self.region.update(z=10, width_tiles=17)
        with self.assertRaisesRegex(ValueError, "width_tiles"):
            self.compile()
        self.region.update(width_tiles=2, x=1023)
        with self.assertRaisesRegex(ValueError, "tile bounds"):
            self.compile()
        self.manifest["regions"] = [dict(self.region, id=f"region-{index}",
                                         x=(index % 64) * 16, y=(index // 64) * 16,
                                         width_tiles=16, height_tiles=16)
                                     for index in range(65)]
        with self.assertRaisesRegex(ValueError, "16384-tile limit"):
            self.compile()

    def shared_source_fixture(self):
        # One original word crosses adjacent source tiles. Only its owner
        # contributes a Chinese label; the borrower accounts for its fragment.
        atlas = Image.new("RGBA", (512, 256))
        ImageDraw.Draw(atlas).rectangle((240, 80, 271, 99), fill="white")
        for index, tile in enumerate(self.region["original_tiles"]):
            path = self.original / tile["path"]
            atlas.crop((index * 256, 0, (index + 1) * 256, 256)).save(path)
            tile["sha256"] = sha(path)
        left, right = copy.deepcopy(self.region["original_tiles"])
        self.region.update(id="owner", width_tiles=1, original_tiles=[left], context_tiles=[right],
                           features=[self.region["features"][1]],
                           translations=[self.region["translations"][1]],
                           layouts=[dict(id="city", x=128, y=128, font_size=22, font_style="bold")], symbols=[])
        borrower = dict(id="borrower", z=3, x=1, y=0, width_tiles=1, height_tiles=1,
                        original_tiles=[right], context_tiles=[left], features=[], translations=[],
                        layouts=[], symbols=[], shared_sources=[dict(id="city-shared", owner_region="owner",
                            feature_id="city", kind="city", original="Original City",
                            original_bbox=[-16, 80, 16, 100], source_url="https://example.com/city",
                            review_notes="Same complete original word across the adjacent source tile")])
        self.manifest["regions"] = [borrower, self.region]
        self.sign_shared_regions()
        return borrower

    def sign_shared_regions(self):
        self.approve()
        for region in self.manifest["regions"]:
            region["review"] = dict(self.region["review"], record_sha256=record_fingerprint(
                {key: value for key, value in region.items() if key != "review"}))
        _, report = self.compile()
        for region, audit in zip(self.manifest["regions"], report["regions"]):
            region["review"]["render_sha256"] = audit["atlas_rgba_sha256"]

    def test_shared_source_accounts_both_fragments_and_renders_once(self):
        self.shared_source_fixture()
        wire, report = self.compile(preview=True)
        self.assertTrue(wire is not None, report["gaps"])
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual(1, report["label_count"])
        self.assertEqual(2, report["tile_count"])
        self.assertTrue(all(region["source_alpha"]["unaccounted_pixels"] == 0 for region in report["regions"]))
        borrower = report["regions"][0]
        self.assertEqual([], borrower["labels"])
        self.assertEqual("owner", borrower["shared_sources"][0]["owner_region"])
        self.assertEqual(0, borrower["tiles"][0]["label_count"])
        with Image.open(self.base / "preview/borrower.zh.png") as preview:
            self.assertIsNone(preview.getchannel("A").getbbox())

    def native_circle_source_fixture(self, named):
        atlas = Image.new("RGBA", (512, 256))
        draw = ImageDraw.Draw(atlas)
        draw.ellipse((252, 80, 260, 88), fill="white")
        owner = self.region
        feature = dict(id="city", kind="city", original="Original City", original_bbox=[20, 30, 50, 44],
                       source_url="https://example.com/city")
        translation = dict(id="city", zh="城市", status="approved", source="https://example.com/translation",
                           review_notes="Synthetic translation fixture", record_sha256=record_fingerprint(feature))
        if named:
            draw.rectangle((20, 30, 49, 43), fill="white")
        tiles = []
        for x in range(2):
            relative = f"3/{x}/0.png"
            path = self.original / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            atlas.crop((x * 256, 0, (x + 1) * 256, 256)).save(path)
            tiles.append(dict(x=x, y=0, path=relative, sha256=sha(path),
                source_url=f"https://warfaresims.slitherine.com/MapTiles/Labels/{relative}"))
        left, right = tiles
        symbol = dict(id="dot", kind="circle", x=248, y=84, size=9, outline_width=1,
                      original_bbox=[252, 80, 261, 89])
        if named:
            symbol["feature_id"] = "city"
        else:
            symbol.update(unlabeled=True, source_url=left["source_url"], review_notes="Unlabeled complete circle")
        owner.update(id="owner", width_tiles=1, original_tiles=[left], context_tiles=[right],
                     features=[feature] if named else [], translations=[translation] if named else [],
                     layouts=[dict(id="city", x=90, y=100, font_size=22, font_style="bold")] if named else [],
                     symbols=[symbol])
        source = dict(id="borrowed-circle", owner_region="owner", owner_symbol_id="dot",
            original_bbox=[-4, 80, 5, 89], owner_symbol_record_sha256=record_fingerprint(symbol),
            source_rgba_sha256=hashlib.sha256(atlas.crop((252, 80, 261, 89)).tobytes()).hexdigest(),
            review_notes="Same complete supplier circle across the source seam")
        borrower = dict(id="borrower", z=3, x=1, y=0, width_tiles=1, height_tiles=1,
                        original_tiles=[right], context_tiles=[left], features=[], translations=[],
                        layouts=[], symbols=[], shared_sources=[source])
        self.manifest["regions"] = [borrower, owner]
        self.sign_shared_regions()
        return borrower, symbol, source

    def test_borrowed_native_circle_accounts_source_without_rendering_again(self):
        for named in (False, True):
            with self.subTest(named=named):
                self.native_circle_source_fixture(named)
                wire, report = self.compile(preview=True)
                self.assertIsNotNone(wire, report["gaps"])
                self.assertEqual(int(named), report["label_count"])
                self.assertEqual(1, report["symbol_count"])
                borrower, owner = report["regions"]
                self.assertEqual(0, borrower["source_alpha"]["unaccounted_pixels"])
                self.assertGreater(borrower["source_alpha"]["accounted_pixels"], 0)
                self.assertEqual([], borrower["symbols"])
                self.assertEqual([[3, 0, 0]], owner["symbols"][0]["tiles"])
                self.assertEqual(0, borrower["tiles"][0]["label_count"])
                with Image.open(self.base / "preview/borrower.zh.png") as preview:
                    self.assertIsNone(preview.getchannel("A").getbbox())

    def test_borrowed_native_circle_rejects_identity_frame_or_target_mismatch(self):
        for change in ("owner_hash", "raw_hash", "bbox", "target_crossing", "duplicate", "named_alias"):
            with self.subTest(change=change):
                borrower, symbol, source = self.native_circle_source_fixture(named=True)
                if change == "owner_hash":
                    source["owner_symbol_record_sha256"] = "a" * 64
                elif change == "raw_hash":
                    source["source_rgba_sha256"] = "a" * 64
                elif change == "bbox":
                    source["original_bbox"] = [-4, 80, 6, 89]
                elif change == "target_crossing":
                    symbol["x"] = 255
                    source["owner_symbol_record_sha256"] = record_fingerprint(symbol)
                elif change == "duplicate":
                    borrower["shared_sources"].append(dict(source, id="second-circle"))
                else:
                    source["feature_id"] = "city"
                self.sign_shared_regions()
                self.rejected("owner_native_source")

    def test_shared_source_rejects_wrong_owner_identity_bbox_and_notes(self):
        borrower = self.shared_source_fixture()
        source = borrower["shared_sources"][0]
        original = copy.deepcopy(source)
        for field, value in (("owner_region", "unknown"), ("owner_region", "borrower"),
                             ("feature_id", "absent"), ("kind", "admin0"),
                             ("original", "Different City"), ("source_url", "https://example.com/other"),
                             ("original_bbox", [-16, 80, 15, 100]),
                             ("original_bbox", [-16.5, 80, 16, 100]), ("review_notes", "")):
            with self.subTest(field=field, value=value):
                source.clear()
                source.update(original)
                source[field] = value
                self.sign_shared_regions()
                self.rejected("shared_source")

    def test_shared_source_rejects_zoom_local_id_and_reference_chain(self):
        borrower = self.shared_source_fixture()
        borrower["z"] = 4
        self.sign_shared_regions()
        self.rejected("shared_source")
        borrower["z"] = 3
        borrower["features"] = [dict(self.region["features"][0], id="city-shared", original_bbox=[0, 80, 16, 100])]
        self.sign_shared_regions()
        self.rejected("shared_source")
        borrower["features"] = []
        # References can only target normal features, never another reference.
        self.region["shared_sources"] = [dict(borrower["shared_sources"][0], id="chain", owner_region="borrower",
                                             feature_id="city-shared", original_bbox=[240, 80, 272, 100])]
        borrower["shared_sources"][0]["feature_id"] = "chain"
        self.sign_shared_regions()
        self.rejected("shared_source")

    def test_shared_source_requires_owner_output_and_review_but_allows_draft_preview(self):
        borrower = self.shared_source_fixture()
        self.region["layouts"] = []
        self.sign_shared_regions()
        self.rejected("shared_source_owner")
        self.region["layouts"] = [dict(id="city", x=128, y=128, font_size=22, font_style="bold")]
        self.region["translations"][0]["status"] = "draft"
        self.sign_shared_regions()
        self.rejected("shared_source_owner")
        self.region["translations"][0]["status"] = "approved"
        self.sign_shared_regions()
        self.region["review"]["status"] = "draft"
        wire, report = self.compile(preview=True)
        self.assertTrue(wire is None, "draft owner must not certify a pack")
        self.assertFalse(report["ready"])
        self.assertIn("shared_source_owner", json.dumps(report))
        self.assertTrue((self.base / "preview/owner.zh.png").is_file())
        self.assertTrue((self.base / "preview/borrower.zh.png").is_file())
        self.assertEqual(borrower["review"]["status"], "approved")

    def test_shared_source_requires_local_alpha_and_boundary_context(self):
        borrower = self.shared_source_fixture()
        borrower["context_tiles"] = []
        self.sign_shared_regions()
        self.rejected("context_incomplete")
        borrower["context_tiles"] = self.region["original_tiles"]
        tile = borrower["original_tiles"][0]
        Image.new("RGBA", (256, 256)).save(self.original / tile["path"])
        tile["sha256"] = sha(self.original / tile["path"])
        self.sign_shared_regions()
        self.rejected("empty_original_bbox")

    def test_shared_source_change_invalidates_region_approval(self):
        borrower = self.shared_source_fixture()
        borrower["shared_sources"][0]["review_notes"] += " changed"
        self.rejected("region_fingerprint")

    def flat_background(self):
        tile = self.region["original_tiles"][0]
        path = self.original / tile["path"]
        with Image.open(path) as source:
            image = source.convert("RGBA")
        background = Image.new("RGBA", image.size, (1, 1, 1, 1))
        background.paste(image, mask=image.getchannel("A"))
        background.save(path)
        tile["sha256"] = sha(path)
        self.approve()
        return dict(x=tile["x"], y=tile["y"], sha256=tile["sha256"], rgba=[1, 1, 1, 1],
                    source_url=tile["source_url"], review_notes="Reviewed exact flat source background")

    def test_exact_source_background_accounts_without_changing_source_or_pack(self):
        old_wire, _ = self.compile()
        rule = self.flat_background()
        self.rejected("unaccounted_alpha")
        source = (self.original / "0.png").read_bytes()
        self.region["source_backgrounds"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile(preview=True)
        self.assertTrue(wire is not None, report["gaps"])
        self.assertEqual(old_wire, wire)
        self.assertEqual(source, (self.original / "0.png").read_bytes())
        audit = report["regions"][0]["source_alpha"]
        self.assertEqual(64416, audit["background_pixels"])
        self.assertEqual(audit["total_pixels"], audit["accounted_pixels"])
        self.assertEqual(0, audit["unaccounted_pixels"])
        self.assertEqual(64416, report["regions"][0]["source_backgrounds"][0]["pixels"])
        with Image.open(self.base / "preview/fixture.original.png") as preview, Image.open(io.BytesIO(source)) as original:
            self.assertEqual(original.convert("RGBA").tobytes(), preview.crop((0, 0, 256, 256)).tobytes())

    def test_background_requires_exact_verified_core_source_and_evidence(self):
        valid = self.flat_background()
        for field, value in (("sha256", "0" * 64), ("source_url", "https://example.com/other"),
                             ("rgba", [0, 0, 0, 1]), ("rgba", [True, 1, 1, 1]),
                             ("x", 2), ("y", 1), ("x", True), ("review_notes", "")):
            with self.subTest(field=field, value=value):
                self.region["source_backgrounds"] = [dict(valid, **{field: value})]
                self.approve()
                self.rejected("source_background")
        self.region["source_backgrounds"] = [valid, valid.copy()]
        self.approve()
        self.rejected("source_background")
        self.region["source_backgrounds"] = [valid]
        Image.new("RGBA", (256, 256), (1, 1, 1, 1)).save(self.original / "0.png")
        # Recorded source SHA and the rule still agree, but actual bytes differ.
        self.approve()
        report = self.rejected("source_background")
        self.assertIn("source_sha256", json.dumps(report))

    def test_background_sparse_or_other_rgba_cannot_exempt_ink(self):
        tile = self.region["original_tiles"][0]
        rule = dict(x=0, y=0, sha256=tile["sha256"], rgba=[1, 1, 1, 1],
                    source_url=tile["source_url"], review_notes="Synthetic sparse candidate")
        self.region["source_backgrounds"] = [rule]
        self.approve()
        self.rejected("source_background")
        rule = self.flat_background()
        self.region["source_backgrounds"] = [rule]
        for rgba in ((255, 255, 255, 1), (1, 1, 1, 2)):
            with self.subTest(rgba=rgba):
                with Image.open(self.original / "0.png") as source:
                    image = source.convert("RGBA")
                image.putpixel((200, 200), rgba)
                image.save(self.original / "0.png")
                tile["sha256"] = rule["sha256"] = sha(self.original / "0.png")
                self.approve()
                audit = self.rejected("unaccounted_alpha")["regions"][0]["source_alpha"]
                self.assertEqual(1, audit["unaccounted_pixels"])

    def test_background_only_bboxes_do_not_prove_feature_or_symbol_exists(self):
        rule = self.flat_background()
        self.region["source_backgrounds"] = [rule]
        for category in ("features", "symbols"):
            with self.subTest(category=category):
                row = self.region[category][0]
                saved = row["original_bbox"]
                row["original_bbox"] = [0, 0, 10, 10]
                if category == "features":
                    self.region["translations"][0]["record_sha256"] = record_fingerprint(row)
                self.approve()
                report = self.rejected("empty_original_bbox")
                self.assertTrue(any(g["code"] == "empty_original_bbox" and g["category"] == category[:-1]
                                    for g in report["gaps"]))
                row["original_bbox"] = saved

    def test_background_metadata_change_invalidates_region_approval(self):
        self.region["source_backgrounds"] = [self.flat_background()]
        self.approve()
        self.region["source_backgrounds"][0]["review_notes"] += " changed"
        self.rejected("region_fingerprint")

    def test_background_requires_at_least_half_the_exact_tile(self):
        rule = self.flat_background()
        with Image.open(self.original / "0.png") as source:
            image = source.convert("RGBA")
        for y in range(128):
            for x in range(256):
                if image.getpixel((x, y)) == (1, 1, 1, 1):
                    image.putpixel((x, y), (0, 0, 0, 0))
        image.save(self.original / "0.png")
        self.region["original_tiles"][0]["sha256"] = rule["sha256"] = sha(self.original / "0.png")
        self.region["source_backgrounds"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        self.assertEqual(32768, report["regions"][0]["source_alpha"]["background_pixels"])
        image.putpixel((0, 128), (0, 0, 0, 0))
        image.save(self.original / "0.png")
        self.region["original_tiles"][0]["sha256"] = rule["sha256"] = sha(self.original / "0.png")
        self.approve()
        self.rejected("source_background")

    def test_background_cannot_prove_a_shared_source_bbox_has_ink(self):
        borrower = self.shared_source_fixture()
        tile = borrower["original_tiles"][0]
        with Image.open(self.original / tile["path"]) as source:
            image = source.convert("RGBA")
        background = Image.new("RGBA", image.size, (1, 1, 1, 1))
        background.paste(image, mask=image.getchannel("A"))
        background.save(self.original / tile["path"])
        tile["sha256"] = sha(self.original / tile["path"])
        borrower["source_backgrounds"] = [dict(x=1, y=0, sha256=tile["sha256"], rgba=[1, 1, 1, 1],
            source_url=tile["source_url"], review_notes="Synthetic exact source background")]
        feature = self.region["features"][0]
        feature["original_bbox"] = [250, 10, 270, 20]
        self.region["translations"][0]["record_sha256"] = record_fingerprint(feature)
        borrower["shared_sources"][0]["original_bbox"] = [-6, 10, 14, 20]
        self.sign_shared_regions()
        report = self.rejected("empty_original_bbox")
        self.assertTrue(any(g["code"] == "empty_original_bbox" and g["category"] == "shared_source"
                            for g in report["gaps"]))


    def shared_native_fixture(self):
        borrower = self.shared_source_fixture()
        tile = borrower["original_tiles"][0]
        path = self.original / tile["path"]
        with Image.open(path) as source:
            image = source.convert("RGBA")
        ImageDraw.Draw(image).ellipse((40, 110, 48, 118), fill="white")
        image.save(path)
        tile["sha256"] = sha(path)
        self.manifest["regions"] = [self.region]
        self.approve()
        self.sign_render()
        wire, report = self.compile(preview=True)
        self.assertIsNotNone(wire, report["gaps"])
        self.old_owner = copy.deepcopy(self.region)
        self.old_owner_png = (self.base / "preview/owner.zh.png").read_bytes()
        borrower["symbols"] = [dict(id="shared-dot", shared_source_id="city-shared", kind="circle",
                                    x=44.5, y=114.5, size=7, outline_width=1,
                                    original_bbox=[40, 110, 49, 119])]
        self.manifest["regions"] = [borrower, self.region]
        self.sign_shared_regions()
        return borrower

    def test_shared_native_named_point_renders_once_without_another_label(self):
        borrower = self.shared_native_fixture()
        wire, report = self.compile(preview=True)
        self.assertIsNotNone(wire, report["gaps"])
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual((1, 1), (report["label_count"], report["symbol_count"]))
        self.assertEqual(self.old_owner, self.region)
        self.assertEqual(self.old_owner_png, (self.base / "preview/owner.zh.png").read_bytes())
        self.assertEqual([], borrower["features"])
        self.assertEqual([], borrower["translations"])
        self.assertEqual([], borrower["layouts"])
        row = report["regions"][0]
        self.assertEqual([], row["labels"])
        self.assertEqual(0, row["tiles"][0]["label_count"])
        self.assertEqual("city-shared", row["symbols"][0]["shared_source_id"])
        self.assertIsNone(row["symbols"][0]["feature_id"])
        self.assertFalse(row["symbols"][0].get("unlabeled", False))
        self.assertEqual(0, row["source_alpha"]["unaccounted_pixels"])
        with Image.open(self.base / "preview/borrower.zh.png") as image:
            self.assertIsNotNone(image.getchannel("A").getbbox())
            self.assertIsNotNone(image.getchannel("A").crop((40, 110, 49, 119)).getbbox())
        stream = io.BytesIO(wire[73:])
        packed = {}
        for _ in range(struct.unpack("<i", stream.read(4))[0]):
            z, x, y, count, length = struct.unpack("<iiiii", stream.read(20))
            packed[z, x, y] = count, stream.read(length)
        self.assertEqual((0, (self.base / "preview/borrower.zh.png").read_bytes()), packed[3, 1, 0])
        self.assertEqual((1, self.old_owner_png), packed[3, 0, 0])

    def test_shared_native_binding_modes_are_exclusive_and_never_fall_back(self):
        borrower = self.shared_native_fixture()
        saved = copy.deepcopy(borrower["symbols"][0])
        cases = [dict(shared_source_id="absent"), dict(shared_source_id=None),
                 dict(shared_source_id=""), dict(shared_source_id=1),
                 dict(feature_id="city"), dict(feature_id=None),
                 dict(unlabeled=True, source_url="https://example.com/dot", review_notes="Not unnamed")]
        for changes in cases:
            with self.subTest(changes=changes):
                borrower["symbols"] = [dict(saved, **changes)]
                self.sign_shared_regions()
                self.rejected("symbol_binding")
        borrower["symbols"] = [dict(saved)]
        del borrower["symbols"][0]["shared_source_id"]
        self.sign_shared_regions()
        self.rejected("symbol_feature")

    def test_shared_native_unlabeled_cannot_hide_a_present_null_reference(self):
        self.unlabeled_symbol()
        self.region["symbols"][0]["shared_source_id"] = None
        self.approve()
        self.sign_render()
        self.rejected("symbol_binding")

    def test_shared_native_reuses_owner_identity_and_output_guards(self):
        self.shared_native_fixture()
        baseline = copy.deepcopy(self.manifest)
        for change in ("zoom", "owner", "chain", "bbox", "url", "review", "output"):
            with self.subTest(change=change):
                self.manifest = copy.deepcopy(baseline)
                borrower, self.region = self.manifest["regions"]
                source = borrower["shared_sources"][0]
                if change == "zoom":
                    borrower["z"] = 4
                elif change == "owner":
                    source["owner_region"] = "absent"
                elif change == "chain":
                    source["owner_region"] = "borrower"
                    source["feature_id"] = "city-shared"
                elif change == "bbox":
                    source["original_bbox"][0] -= 0.5
                elif change == "url":
                    source["source_url"] = "https://example.com/different"
                elif change == "output":
                    self.region["layouts"] = []
                self.sign_shared_regions()
                if change == "review":
                    self.region["review"]["status"] = "draft"
                self.rejected("shared_source_owner" if change in ("review", "output") else "shared_source")

    def test_shared_native_kind_must_match_the_named_source_kind(self):
        borrower = self.shared_native_fixture()
        borrower["symbols"][0]["kind"] = "triangle"
        self.sign_shared_regions()
        self.rejected("symbol_binding")

    def test_shared_native_aliases_cannot_duplicate_a_physical_point(self):
        borrower = self.shared_native_fixture()
        borrower["shared_sources"].append(dict(borrower["shared_sources"][0], id="alias"))
        borrower["symbols"].append(dict(borrower["symbols"][0], id="alias-dot", shared_source_id="alias"))
        self.sign_shared_regions()
        self.rejected("duplicate_native_bbox")

    def test_shared_native_physical_index_spans_different_regions(self):
        borrower = self.shared_source_fixture()
        atlas = Image.new("RGBA", (512, 256))
        for tile in self.region["original_tiles"] + borrower["original_tiles"]:
            with Image.open(self.original / tile["path"]) as source:
                atlas.paste(source.convert("RGBA"), (tile["x"] * 256, 0))
        ImageDraw.Draw(atlas).ellipse((252, 110, 260, 118), fill="white")
        for tile in self.region["original_tiles"] + borrower["original_tiles"]:
            atlas.crop((tile["x"] * 256, 0, (tile["x"] + 1) * 256, 256)).save(self.original / tile["path"])
            tile["sha256"] = sha(self.original / tile["path"])
        self.region["symbols"] = [dict(id="local-dot", feature_id="city", kind="circle", x=248, y=114.5,
                                       size=7, outline_width=1, original_bbox=[252, 110, 261, 119])]
        borrower["symbols"] = [dict(id="shared-dot", shared_source_id="city-shared", kind="circle", x=8, y=114.5,
                                    size=7, outline_width=1, original_bbox=[-4, 110, 5, 119])]
        self.sign_shared_regions()
        self.rejected("duplicate_native_bbox")

    def test_shared_native_local_and_shared_points_use_one_physical_index(self):
        borrower = self.shared_native_fixture()
        tile = borrower["original_tiles"][0]
        with Image.open(self.original / tile["path"]) as source:
            image = source.convert("RGBA")
        ImageDraw.Draw(image).rectangle((90, 150, 109, 169), fill="white")
        image.save(self.original / tile["path"])
        tile["sha256"] = sha(self.original / tile["path"])
        feature = dict(id="local", kind="city", original="Local City", original_bbox=[90, 150, 110, 170], source_url="https://example.com/local")
        borrower["features"] = [feature]
        borrower["translations"] = [dict(id="local", zh="本城", status="approved", source="https://example.com/translation", review_notes="Synthetic local city", record_sha256=record_fingerprint(feature))]
        borrower["layouts"] = [dict(id="local", x=100, y=180, font_size=12, font_style="regular")]
        local = dict(borrower["symbols"][0], id="local-dot", feature_id="local")
        del local["shared_source_id"]
        borrower["symbols"].append(local)
        self.sign_shared_regions()
        self.rejected("duplicate_native_bbox")

    def test_shared_native_unlabeled_points_use_one_physical_index(self):
        borrower = self.shared_native_fixture()
        other = dict(borrower["symbols"][0], id="unlabeled-dot", unlabeled=True,
                     source_url="https://example.com/native", review_notes="Synthetic independently recorded point")
        del other["shared_source_id"]
        borrower["symbols"].append(other)
        self.sign_shared_regions()
        self.rejected("duplicate_native_bbox")

    def test_shared_native_different_kinds_cannot_claim_the_same_source_frame(self):
        self.unlabeled_symbol()
        self.region["symbols"].append(dict(self.region["symbols"][0], id="triangle-alias", kind="triangle",
                                           x=200, y=200))
        self.approve()
        self.sign_render()
        self.rejected("duplicate_native_bbox")

    def test_shared_native_equal_output_center_rejects_altered_source_box(self):
        borrower = self.shared_native_fixture()
        borrower["symbols"].append(dict(borrower["symbols"][0], id="shifted-box", original_bbox=[40.5, 110, 49, 119]))
        self.sign_shared_regions()
        self.rejected("duplicate_native_center")

    def test_shared_native_subpixel_aliases_use_the_actual_rendered_center(self):
        borrower = self.shared_native_fixture()
        borrower["symbols"].append(dict(borrower["symbols"][0], id="subpixel-alias", x=44.6,
                                         original_bbox=[40.5, 110, 49, 119]))
        self.sign_shared_regions()
        self.rejected("duplicate_native_center")

    def test_shared_native_reference_change_invalidates_region_approval(self):
        borrower = self.shared_native_fixture()
        borrower["symbols"][0]["shared_source_id"] = "changed"
        self.rejected("region_fingerprint")


class CanonicalCaptionTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("canonical_tile_builder", SCRIPT)
        self.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.builder)
        from wwp import load
        path = SCRIPT.parent.parent / "assets/placenames/Cities.wwp"
        point = load(path)[0]
        bits = lambda value: struct.unpack("<I", struct.pack("<f", value))[0]
        self.caption = dict(id="original-city", origin="canonical_wwp", source_bbox=None,
            source_png=None, source_native=None, kind="region",
            canonical_record=dict(layer="Cities", wwp_file=dict(path="assets/placenames/Cities.wwp", sha256=sha(path)),
                record_index_zero_based=0, original_Chinese_name=point["name"], lat_f32_bits=bits(point["lat"]),
                lon_f32_bits=bits(point["lon"]), flags=point["flags"], level=1),
            layout=dict(x=128, y=128, font_style="regular", font_size=12, lines=[point["name"]]))
        self.region = dict(id="canonical-fixture", z=3, x=0, y=0, width_tiles=8, height_tiles=8,
                           canonical_captions=[self.caption])

    def selection(self):
        return dict(schema="explicit-canonical-WWP-caption-selection-v1", role="TEST_ONLY",
            instances=[dict(region=self.region["id"], z=3, caption_id=self.caption["id"],
                caption_body_sha256=record_fingerprint(self.caption))])

    def test_original_member_is_separate_from_source_and_native(self):
        captions, sources = self.builder.audit_canonical_captions([self.region], self.selection())
        self.assertEqual(self.caption, captions[self.region["id"]][self.caption["id"]])
        self.assertEqual([dict(file="Cities.wwp", sha256=self.caption["canonical_record"]["wwp_file"]["sha256"], level=1)], sources)

    def test_canonical_caption_requires_exact_external_selection(self):
        with self.assertRaises(ValueError):
            self.builder.audit_canonical_captions([self.region], None)
        selection = self.selection()
        self.caption["layout"]["x"] += 1
        with self.assertRaisesRegex(ValueError, "selection must exactly equal"):
            self.builder.audit_canonical_captions([self.region], selection)

    def test_original_name_float_bits_and_flags_cannot_be_rewritten(self):
        original = copy.deepcopy(self.caption["canonical_record"])
        for key, value in (("original_Chinese_name", "错误地名"),
                           ("lat_f32_bits", original["lat_f32_bits"] ^ 1), ("flags", original["flags"] ^ 1)):
            with self.subTest(field=key):
                self.caption["canonical_record"] = dict(original, **{key: value})
                with self.assertRaisesRegex(ValueError, "exact selected original WWP member"):
                    self.builder.audit_canonical_captions([self.region], self.selection())

    def test_canonical_caption_cannot_claim_supplier_ink_or_native(self):
        for key in ("source_bbox", "source_png", "source_native"):
            with self.subTest(field=key):
                self.caption[key] = [0, 0, 1, 1]
                with self.assertRaisesRegex(ValueError, "null Source fields"):
                    self.builder.audit_canonical_captions([self.region], self.selection())
                self.caption[key] = None

    def test_full_current_selection_requires_all_four_evidence_roles(self):
        selection = self.selection()
        selection.update(schema="root-approved-canonical-WWP-caption-v1", role="full_current")
        selection["instances"][0]["evidence"] = dict(model_selection=None, manifest=None, review=None, target_signature=None)
        with self.assertRaisesRegex(ValueError, "exact original canonical evidence reference"):
            self.builder.audit_canonical_captions([self.region], selection)

    def translated_initial_fixture(self, original="古斯塔沃·A.马德罗"):
        from wwp import save
        scope = tempfile.TemporaryDirectory(prefix="canonical-initial-")
        self.addCleanup(scope.cleanup)
        base = Path(scope.name)
        path = base / "assets/placenames/Cities.wwp"
        path.parent.mkdir(parents=True)
        save(path, [dict(name=original, lat=22.8694, lon=-98.75886, flags=0),
                    dict(name=original, lat=19.49, lon=-99.11, flags=0)])
        self.builder.__file__ = str(base / "scripts/build_map_tile_pack.py")
        self.caption["canonical_record"].update(original_Chinese_name=original, flags=0,
            lat_f32_bits=self.builder.float32_bits(22.8694), lon_f32_bits=self.builder.float32_bits(-98.75886))
        self.caption["canonical_record"]["wwp_file"]["sha256"] = sha(path)
        self.caption["display_translation"] = dict(zh="古斯塔沃·阿道夫·马德罗", source="https://example.com/translated-name",
                                                   review_notes="Fixture exact legacy initial expanded in Chinese.")
        self.caption["layout"]["lines"] = ["古斯塔沃·阿道夫·马德罗"]
        return base

    def test_explicit_legacy_initial_translation_keeps_exact_raw_identity(self):
        self.translated_initial_fixture()
        original = copy.deepcopy(self.caption["canonical_record"])
        captions, _ = self.builder.audit_canonical_captions([self.region], self.selection())
        caption = captions[self.region["id"]][self.caption["id"]]
        self.assertEqual(caption["canonical_record"], original)
        self.assertEqual(caption["layout"]["lines"], ["古斯塔沃·阿道夫·马德罗"])

    def test_untranslated_mixed_original_still_fails(self):
        self.translated_initial_fixture()
        del self.caption["display_translation"]
        self.caption["layout"]["lines"] = [self.caption["canonical_record"]["original_Chinese_name"]]
        with self.assertRaisesRegex(ValueError, "exact selected original WWP member"):
            self.builder.audit_canonical_captions([self.region], self.selection())

    def test_explicit_translation_does_not_loosen_selection_or_identity(self):
        self.translated_initial_fixture()
        selected = self.selection()
        original = copy.deepcopy(self.caption)
        self.caption["display_translation"]["review_notes"] = "Changed after exact selection."
        with self.assertRaisesRegex(ValueError, "selection must exactly equal"):
            self.builder.audit_canonical_captions([self.region], selected)
        for field, value in (("record_index_zero_based", 1), ("lat_f32_bits", 0), ("flags", 1),
                             ("original_Chinese_name", "古斯塔沃·阿道夫·马德罗")):
            with self.subTest(field=field):
                self.caption.clear()
                self.caption.update(copy.deepcopy(original))
                self.caption["canonical_record"][field] = value
                with self.assertRaisesRegex(ValueError, "exact selected original WWP member"):
                    self.builder.audit_canonical_captions([self.region], self.selection())

    def test_explicit_translation_is_limited_to_one_legacy_initial(self):
        for raw in ("古斯塔沃·阿道夫·马德罗", "Gustavo A. Madero", "古斯塔沃·a.马德罗",
                    "古斯塔沃·A.B.马德罗", "古斯塔沃·Ａ.马德罗", "古斯塔沃·A."):
            with self.subTest(raw=raw):
                self.translated_initial_fixture(raw)
                with self.assertRaisesRegex(ValueError, "legacy initial"):
                    self.builder.audit_canonical_captions([self.region], self.selection())

    def test_explicit_translation_requires_checked_display_and_review_evidence(self):
        self.translated_initial_fixture()
        proposed = copy.deepcopy(self.caption["display_translation"])
        for field, value in (("zh", "古斯塔沃·A.马德罗"), ("source", "not-a-url"),
                             ("review_notes", "  "), ("unexpected", True)):
            with self.subTest(field=field):
                self.caption["display_translation"] = dict(proposed, **{field: value})
                with self.assertRaisesRegex(ValueError, "reviewed Chinese display translation"):
                    self.builder.audit_canonical_captions([self.region], self.selection())
        self.caption["display_translation"] = proposed
        self.caption["layout"]["lines"] = ["古斯塔沃·阿道弗·马德罗"]
        with self.assertRaisesRegex(ValueError, "exactly the selected Chinese display"):
            self.builder.audit_canonical_captions([self.region], self.selection())

    def test_source_package_rejects_forged_translated_caption_display(self):
        base = self.translated_initial_fixture()
        model = dict(schema_version=1, regions=[self.region])
        path = base / "manifest.json"
        raw = json.dumps(model).encode("utf-8")
        path.write_bytes(raw)
        selection = base / "canonical_selection.json"
        selection.write_text(json.dumps(self.selection()), encoding="utf-8")
        args = SimpleNamespace(manifest=path, canonical_caption_approvals=selection,
                               canonical_caption_approvals_sha256=sha(selection))
        marked = dict(id=self.caption["id"], origin="canonical_wwp", canonical_record=self.caption["canonical_record"],
                      source_bbox=None, elevation=None, zh="伪造显示")
        report = dict(regions=[dict(id=self.region["id"], canonical_captions=[marked])], canonical_caption_count=1,
                      canonical_sources=[dict(file="Cities.wwp", sha256=self.caption["canonical_record"]["wwp_file"]["sha256"], level=1)])
        with self.assertRaisesRegex(ValueError, "report must match exact selected captions"):
            self.builder.write_map_source_package(base / "bundle", args, model, raw, report)
        self.assertFalse((base / "bundle").exists())

if __name__ == "__main__":
    unittest.main()
