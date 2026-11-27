"""Replay real Windows/GDI+ place names without installing or modifying the game.

The default run checks selected actual PNGs. --audit also renders the WWP and R88
history from the upstream Git object and checks the migrated active drawings
against src/tests/fixtures/map_label_history.tsv.gz. The fixture records each old
member's layer, index and observed tile, plus approved replacement captions, their
world-pixel centres and the tiles where they painted. Its input was the final
reviewed atlas (Git 52ee827d13f8fd41c10fe1c11a6f806f8cc8abbf) before migration.
No historical atlas, original download cache, or external GIS inputs are required.

Exact historical GDI+ pixel counts were measured with Microsoft YaHei. The SHA-256
digests of the regular and bold Windows font collections were, respectively,
d79c55e68b1131eea0cc1c47be4f572d964f28c682e143db2ad09c1e4cb07a3f and
4508821b3dffe01f0ef5e5326a3e60df705a44633858811f67b6982dce3f6ee6.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = "d7a54b8fae0f295932e6564525832c34844832e3"
RENDERER = "src/shared/MapLabelRenderer.cs"


def replace_once(text, pattern, replacement):
    text, count = re.subn(pattern, lambda match: match[0] + replacement, text)
    if count != 1:
        raise ValueError(f"Expected one GDI+ observation point for {pattern!r}, found {count}")
    return text


def observe_current(source):
    # Instrument the completed drawing calls. ReviewedMarks indexed at a tile edge
    # can have bounding boxes that intersect the tile without painting a single pixel.
    source = replace_once(source, r"graphics\.FillPath\(fill,\s*path\);(?=\s*rendered\+\+)",
                          " HistoryTrace.Legacy(place, path, halo, fill);")
    # Only the public draw contributes ink to the output. Layout calls Paint
    # directly when measuring obstacles, including the temporarily muted label.
    source = replace_once(source, r"public void Draw\(Graphics graphics,[^)]*\)\s*\{",
                          "\n            if (!Symbol && HistoryTrace.MutedLabel == Id) return;")
    return replace_once(source, r"(?m)^\s*Paint\(graphics, family, format, halo, fill, x, y\);",
                        "\n            HistoryTrace.Label(this, family, format, halo, fill, x, y);")


def git_blob(repo, path):
    return subprocess.check_output(["git", "show", f"{UPSTREAM}:{path}"], cwd=repo)


def run(repo, audit):
    framework = Path(os.environ["WINDIR"]) / "Microsoft.NET"
    compiler = next((p for arch in ("Framework64", "Framework")
                     if (p := framework / arch / "v4.0.30319/csc.exe").is_file()), None)
    if not compiler:
        raise SystemExit("Install the .NET Framework 4.x compiler and the Microsoft YaHei font.")
    tests = Path(__file__).resolve().parents[1] / "src/tests"
    with tempfile.TemporaryDirectory(prefix="cmo-map-history-") as scratch:
        temp = Path(scratch)
        game = temp / "game"
        for layer in ("Synoptic", "Cities"):
            target = game / "WW/PlacenameSets" / layer / "0000_0000.wwp"
            target.parent.mkdir(parents=True)
            shutil.copyfile(repo / f"assets/placenames/{layer}.wwp", target)
        current = temp / "Current.cs"
        current.write_text(observe_current((repo / RENDERER).read_text(encoding="utf-8-sig")), encoding="utf-8")
        sources = [current, tests / "MapLabelHistoryTests.cs", tests / "MapLabelHistorySmoke.cs"]
        options = []
        arguments = [repo, game]
        if audit:
            original = git_blob(repo, RENDERER).decode("utf-8-sig").replace("namespace CMOShared", "namespace Original")
            original = replace_once(original, r"public static class MapLabelRenderer\s*\{",
                                    "\n        internal static Action<MapLabelPlace> Painted;")
            original = replace_once(original, r"graphics\.FillPath\(fill, path\);", " Painted(place);")
            source = temp / "Original.cs"
            source.write_text(original, encoding="utf-8")
            prior = temp / "original-places.json"
            prior.write_text(json.dumps({"places": json.loads(git_blob(repo, "dict/CMOLocalizer.json"))["places"]},
                                        ensure_ascii=False), encoding="utf-8")
            options.append("/define:HISTORY_AUDIT")
            sources.extend((source, tests / "MapLabelHistoryAudit.cs"))
            arguments.extend((prior, tests / "fixtures/map_label_history.tsv.gz"))
        exe = temp / "MapLabelHistoryTests.exe"
        subprocess.run([compiler, "/nologo", "/langversion:5", "/codepage:65001", "/optimize+", "/target:exe",
                        "/reference:System.Drawing.dll", f"/out:{exe}", *options, *sources], check=True)
        subprocess.run([exe, *arguments], check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audit", action="store_true", help="also replay all historical draws (requires Git history)")
    parser.add_argument("--repo", type=Path, default=ROOT, help="source checkout under test (default: this checkout)")
    args = parser.parse_args()
    run(args.repo.resolve(), args.audit)
