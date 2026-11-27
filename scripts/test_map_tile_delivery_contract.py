"""Run shell-embedded Python gates with temporary inputs; never deploy/package."""
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest
import zipfile

import release_check

ROOT = pathlib.Path(__file__).resolve().parents[1]


def blocks(name):
    return re.findall(r"<<'PYEOF'\n(.*?)\nPYEOF", (ROOT / "scripts" / name).read_text(encoding="utf-8"), re.S)


def run_block(code, *args):
    return subprocess.run([sys.executable, "-X", "utf8", "-c", code, *map(str, args)], capture_output=True, text=True, encoding="utf-8")


class DeliveryContract(unittest.TestCase):
    def test_installer_call_order_and_failure(self):
        text = (ROOT / "src/installer/CMO_Chinese_Setup.cs").read_text(encoding="utf-8")
        install = text.split("public static bool Install(", 1)[1].split("private static void DeploySymbols", 1)[0]
        self.assertIn("if (!DeployMapTilePack(gamePath, curDir, out writeErr))", install)
        deploy = install.index("if (!DeployMapTilePack(gamePath, curDir, out writeErr))")
        self.assertLess(deploy, install.index("LocalizeMapLabelTiles(gamePath)"))
        self.assertRegex(install[deploy:], r"out writeErr\)\)\s*\{\s*errorMsg = writeErr;\s*return false;")
        uninstall = text.split("public static bool Uninstall(", 1)[1]
        self.assertIn("RestoreMapLabelTiles(gamePath)", uninstall)

    def test_build_embeds_formal_asset_unconditionally(self):
        text = (ROOT / "scripts/build.sh").read_text(encoding="utf-8")
        self.assertIn('RES+=(-resource:"$(W "$ROOT/assets/placenames/ChineseMapTiles.dat")",MAP_TILES)', text)

    def test_deploy_copies_pack_atomically(self):
        text = (ROOT / "scripts/deploy.sh").read_text(encoding="utf-8")
        self.assertIn('deploy_file "$ROOT/assets/placenames/ChineseMapTiles.dat" "$GAME/ChineseMapTiles.dat"', text)

    def test_deploy_byte_verification_detects_stale_pack(self):
        code = next(block for block in blocks("deploy.sh") if "stale = []" in block)
        with tempfile.TemporaryDirectory(dir=ROOT / "build") as temp:
            base = pathlib.Path(temp)
            root, game = base / "repo", base / "game"
            for path in [root / "build", root / "dict", root / "assets/placenames", game]:
                path.mkdir(parents=True, exist_ok=True)
            (game / "Command.exe.config").write_bytes(b"<configuration/>")
            for src, dst, data in [(root / "build/CMOLocalizer.dll", game / "CMOLocalizer.dll", b"dll"),
                                   (root / "dict/CMOLocalizer.json", game / "CMOLocalizer.json", json.dumps({"offsets": {}, "exact": {}, "_meta": {"version": "fixture"}}).encode()),
                                   (root / "assets/placenames/ChineseMapTiles.dat", game / "ChineseMapTiles.dat", b"verified")]:
                src.write_bytes(data)
                dst.write_bytes(data)
            args = root, game, root / "build/CMOLocalizer.dll", root / "dict/CMOLocalizer.json"
            self.assertEqual(run_block(code, *args).returncode, 0)
            (game / "ChineseMapTiles.dat").write_bytes(b"stale")
            result = run_block(code, *args)
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("ChineseMapTiles.dat", result.stdout)

    def test_package_copy_and_missing_pack_gate(self):
        text = (ROOT / "scripts/package.sh").read_text(encoding="utf-8")
        self.assertIn('cp "$ROOT/assets/placenames/ChineseMapTiles.dat" "$PACK/"', text)
        with tempfile.TemporaryDirectory(dir=ROOT / "build") as temp:
            root = pathlib.Path(temp)
            asset = root / "assets/placenames/ChineseMapTiles.dat"
            asset.parent.mkdir(parents=True)
            asset.write_bytes(b"verified")
            pack = root / "portable.zip"
            with zipfile.ZipFile(pack, "w"):
                pass
            self.assertTrue(release_check.placename_payload_problems(None, pack, root))
            for payload in (b"stale", asset.read_bytes()):
                with zipfile.ZipFile(pack, "w") as archive:
                    archive.writestr(asset.name, payload)
                problems = release_check.placename_payload_problems(None, pack, root)
                self.assertEqual(not problems, payload == asset.read_bytes())


if __name__ == "__main__":
    unittest.main()
