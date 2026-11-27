"""Validate reviewed geographic labels against the same member order the game loads."""
from __future__ import annotations

import json
import math
import hashlib
import struct
from pathlib import Path

import wwp


def in_range(value, low, high):
    return type(value) in (int, float) and math.isfinite(value) and low <= value <= high


def index_ok(index, sequence):
    return type(index) is int and 0 <= index < len(sequence)


def coordinates_ok(latitude, longitude):
    return in_range(latitude, -90, 90) and in_range(longitude, -180, 180)


def zoom_ok(z):
    return type(z) is int and 3 <= z <= 10


def checked_data(data, members, layers):
    required = {"entities", "labels", "symbols", "joins", "source_sha256"}
    if set(data) - {"display_names", "retained"} != required:
        raise ValueError("Unexpected reviewed_map sections")
    entities, labels = data["entities"], data["labels"]
    references = set()
    for number, row in enumerate(entities):
        if len(row) == 2:
            layer, index = row
            if layer not in layers or not index_ok(index, layers[layer]):
                raise ValueError(f"Unknown map member for entity {number}: {row}")
            references.add((layer, index))
        else:
            origin, kind, name, latitude, longitude = row
            if origin != "local" or not kind or not name or not coordinates_ok(latitude, longitude):
                raise ValueError(f"Invalid geographic entity {number}: {row}")

    claimed = {}

    def bind(label, z, layer, index):
        if layer not in layers or not index_ok(index, layers[layer]):
            raise ValueError(f"Unknown member {layer} {index} for label {label}")
        member = (layer, index)
        if (z, member) in claimed:
            raise ValueError(f"Two labels claim {member} at zoom {z}")
        claimed[z, member] = label
        references.add(member)

    for number, (entity, z, dx, dy, lines, size, style) in enumerate(labels):
        if not index_ok(entity, entities) or not zoom_ok(z):
            raise ValueError(f"Unknown entity or zoom for label {number}")
        if not all(isinstance(n, (int, float)) and math.isfinite(n) for n in (dx, dy)):
            raise ValueError(f"Invalid placement for label {number}")
        if not in_range(size, 1, 128) or type(style) is not int or style not in (0, 1):
            raise ValueError(f"Invalid text size or style for label {number}")
        if lines is not None and (not isinstance(lines, list) or not 1 <= len(lines) <= 16
                                  or any(not isinstance(line, str) or not line or "\n" in line for line in lines)):
            raise ValueError(f"Invalid line break in label {number}")
        if len(entities[entity]) == 2:
            bind(number, z, *entities[entity])

    for label, z, layer, index in data["joins"]:
        if not index_ok(label, labels) or z != labels[label][1]:
            raise ValueError(f"Unknown label/zoom for additional member {layer} {index}")
        bind(label, z, layer, index)

    display_members = set()
    for layer, index, name in data.get("display_names", []):
        member = layer, index
        if (layer not in layers or not index_ok(index, layers[layer]) or
                not isinstance(name, str) or not name.strip() or member in display_members):
            raise ValueError(f"Invalid display name for {member}")
        display_members.add(member)
    references.update(display_members)

    retained = set()
    for layer, index, z, x, y in data.get("retained", []):
        key = layer, index, z, x, y
        if (layer not in layers or not index_ok(index, layers[layer])
                or not zoom_ok(z)
                or not all(type(v) is int and 0 <= v < 2**z for v in (x, y)) or key in retained):
            raise ValueError(f"Invalid retained R88 placement {key}")
        retained.add(key)
        references.add((layer, index))

    for number, (z, latitude, longitude, dx, dy, kind, size, stroke, owner, shape) in enumerate(data["symbols"]):
        if (not zoom_ok(z) or not coordinates_ok(latitude, longitude)
                or not all(isinstance(n, (int, float)) and math.isfinite(n) for n in (dx, dy))
                or kind not in ("circle", "triangle", "slash") or not in_range(size, 0, 128) or size == 0
                or not in_range(stroke, 0, 128) or stroke == 0):
            raise ValueError(f"Invalid map symbol {number}")
        if owner is not None and (not index_ok(owner, labels) or labels[owner][1] != z):
            raise ValueError(f"Unknown owner for map symbol {number}")
        if shape is not None and (len(shape) != 2 or not all(in_range(v, 0, 128) and v > 0 for v in shape)):
            raise ValueError(f"Invalid dimensions for map symbol {number}")

    approved = set()
    for layer, index, name, latitude, longitude in members:
        if layer not in layers or not index_ok(index, layers[layer]):
            raise ValueError(f"Missing approved {layer} member {index}")
        original = layers[layer][index]
        if name != original[0] or struct.pack("<ff", latitude, longitude) != struct.pack("<ff", *original[1:]):
            raise ValueError(f"{layer} member {index} changed: expected {(name, latitude, longitude)}, found {original}")
        approved.add((layer, index))
    if len(approved) != len(members) or approved != references:
        raise ValueError("Reviewed member evidence differs from the referenced members")
    return data


def load_reviewed_map(root: Path, places: list) -> dict:
    source = root / "dict/placenames"
    data = json.loads((source / "reviewed_map.json").read_bytes())
    evidence = json.loads((source / "reviewed_map_evidence.json").read_bytes())
    canonical = json.dumps(data, sort_keys=True, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode()
    if hashlib.sha256(canonical).hexdigest() != evidence.get("runtime_sha256"):
        raise ValueError("Map row order or content differs from the approved evidence")
    for rows, proof in (("labels", "labels"), ("symbols", "symbols"), ("retained", "retained_footprints")):
        if len(data.get(rows, [])) != len(evidence.get(proof, [])):
            raise ValueError(f"Missing {rows} evidence")
    layers = {"Regions": [tuple(row[:3]) for row in places]}
    region_hash = hashlib.sha256(struct.pack("<i", len(places)))
    for name, latitude, longitude, min_zoom in places:
        word = name.encode("utf-8")
        region_hash.update(struct.pack("<i", len(word)) + word + struct.pack("<fff", latitude, longitude, min_zoom))
    hashes = {"Regions": region_hash.hexdigest()}
    display_flags = {(layer, index): flags for layer, index, flags in evidence.get("display_name_flags", [])}
    if set(display_flags) != {(layer, index) for layer, index, name in data.get("display_names", [])}:
        raise ValueError("Reviewed WWP display names and their member records differ")
    for layer in ("Cities", "Synoptic"):
        path = root / "assets/placenames" / (layer + ".wwp")
        records = wwp.load(path)
        hashes[layer] = hashlib.sha256(path.read_bytes()).hexdigest()
        layers[layer] = [(r["name"], r["lat"], r["lon"]) for r in records]
        for (source_layer, index), flags in display_flags.items():
            if source_layer == layer and records[index]["flags"] != flags:
                raise ValueError(f"{layer} member {index} has different WWP flags")
    if data.get("source_sha256") != hashes:
        raise ValueError("Reviewed map source fingerprints differ from the WWP or R88 province table")
    return checked_data(data, evidence["members"], layers)


def verify_dictionary(root: Path):
    dictionary = json.loads((root / "dict/CMOLocalizer.json").read_bytes())
    expected = load_reviewed_map(root, dictionary["places"])
    if dictionary.get("reviewed_map") != expected:
        raise ValueError("The dictionary's geographic data differs from its reviewed source")
    return len(expected["labels"]), len(expected["symbols"])
