#!/usr/bin/env python3
"""Build the existing 49-cue contract from selected CC0 recordings, without synthesis.

Python + numpy + FFmpeg. Run with --verify to compare without rewriting assets.
The explicit take windows below are in seconds in the ORIGINAL recording. Wareya's
multi-take recordings use exact 37800-frame slots at 44100 Hz (not whole files).
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import wave

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "SourceArt/Audio/RecordedFoley"
OUT = ROOT / "Assets/Emberdelve/Resources/Emberdelve/Audio/Sfx"
REVIEW = ROOT / "Logs/acceptance/natural-audio-20261122"
RATE = 48000
VERSION = 1


def layer(pack, name, start=0., end=None, gain=1., offset=0., speed=1., reverse=False):
    return dict(pack=pack, original=name, window_seconds=[start, end], gain=gain,
                offset_seconds=offset, playback_rate=speed, reverse=reverse)


def weapon(name, i, **kw):
    return layer("vehicle-weapons", f"sfx/{name}-{i:02}.wav", **kw)


def accessory(name, i, **kw):
    return layer("vehicle-accessory", f"sfx/{name}-{i:02}.wav", **kw)


def foley(name, take, **kw):
    return layer("wareya-foley", f"foley - {name}.wav", start=take * 37800 / 44100,
                 end=(take + 1) * 37800 / 44100, **kw)


def voice(name, **kw):
    return layer("hael-voices", f"yelling sounds/{name}.wav", **kw)


def door(action, i, **kw):
    return layer("qubodup-doors", f"qubodup-DoorSet/flac/qubodup-Door{action}{i:02}.flac", **kw)


def step(surface, i, **kw):
    foot = "L" if i < 3 else "R"
    return layer("fantozzi", f"Fantozzi-footsteps/flac/Fantozzi-{surface}{foot}{i % 3 + 1}.flac", **kw)


def hit(i, **kw):
    return layer("independent-hits", f"hits/hit{i:02}.mp3.flac", **kw)


def medieval(pack, name, window, **kw):
    return layer(f"medieval-{pack}", name + ".wav", start=window[0], end=window[1], **kw)


# Per-family target whole-event RMS and maximum sample peak. Static gain only:
# no compressor, limiter distortion, denoising, noise oscillator or synthetic tail.
LEVELS = {
    "movement": (.065, .34), "cloth": (.035, .20), "equipment": (.065, .40),
    "weapon": (.105, .63), "impact": (.16, .82), "guard": (.15, .82),
    "actor": (.13, .70), "interaction": (.10, .62), "pickup": (.065, .42),
    "ui": (.035, .20), "consumable": (.065, .40), "designed": (.09, .58),
}


def recipes():
    result = {}

    def cue(name, category, description, variants, levels=None):
        result[name] = dict(category=category, description=description,
                            target_rms=(levels or LEVELS[category])[0],
                            peak_ceiling=(levels or LEVELS[category])[1], variants=variants)

    cue("Swing", "weapon", "One recorded seax pass through air per variant", [
        [medieval(2, "Seax Swing", w)] for w in [(3.28, 3.55), (4., 4.28), (6.91, 7.18)]])
    cue("Impact", "impact", "Recorded bodily hit with quiet leather contact", [
        [hit(i), weapon("quiver-leather-squeeze", i, end=.55, gain=.14)] for i in [1, 2, 4]])
    cue("Metal", "impact", "Individual recorded sword against knife contacts", [
        [weapon("sword-knife-clash", i)] for i in [1, 2, 3]])
    cue("Step", "movement", "Six different recorded stone bootfalls, alternating feet", [[step("Stone", i)] for i in range(6)])
    cue("Bow", "weapon", "Single actual English longbow releases, cut before the next performance", [
        [medieval(1, "English Longbow Shoot", w)] for w in [(.075, .32), (.745, .99), (1.225, 1.48)]])
    cue("Parry", "guard", "Longer recorded blade binds with their natural metallic decay", [
        [weapon("sword-knife-clash", i)] for i in [4, 5, 7]])
    cue("DoorCreak", "interaction", "Individual recorded door opening movements", [[door("Open", i)] for i in [1, 3, 7]])
    cue("DoorThud", "interaction", "Individual recorded door closures and latch/body resonance", [[door("Close", i)] for i in [4, 6, 9]])
    cue("StepWood", "movement", "Recorded footwear on a recorded wooden body; material Foley composite", [
        [weapon("boots-leather-step", i % 4 + 1),
         accessory("wood-bowl-put-wood", i + 3, start=0., end=.30, gain=.44)] for i in range(6)])
    cue("StepDirt", "movement", "Six recorded sand footfalls for the dirt surface", [[step("Sand", i)] for i in range(6)])
    cue("Jump", "movement", "Recorded leather boot jump movement with cloth", [
        [weapon("boots-leather-jump", i + 1), foley("clothes", i, gain=.2)] for i in range(3)])
    cue("Land", "movement", "Two recorded stone sole contacts closely aligned into one landing", [
        [step("Stone", i), step("Stone", i + 3, gain=.65, offset=.025)] for i in range(3)], (.095, .50))
    cue("LandHeavy", "impact", "Paired recorded bootfalls with a low bodily weight contact", [
        [step("Stone", i), step("Stone", i + 3, gain=.75, offset=.025), hit(i + 4, gain=.3)] for i in range(3)], (.12, .68))
    cue("Cloth", "cloth", "Single separate recorded fabric movements", [[foley("clothes", i)] for i in [0, 1, 2]])
    cue("Equip", "equipment", "Recorded belt buckle and quiet leather sheath movement", [
        [weapon("belt-buckle", i + 1, end=.38), weapon("sheath-squeeze", i + 1, end=.4, gain=.35)] for i in range(3)])
    cue("Draw", "equipment", "Actual bow draw texture, isolated movement from longer recording", [
        [medieval(1, "Scythian Recurve Draw", w)] for w in [(11.58, 12.32), (12.64, 13.31), (14.58, 15.30)]])
    cue("Reload", "equipment", "One recorded arrow taken from a quiver, with a real mechanism latch", [
        [weapon("arrow-grab-from-quiver", i + 1), foley("metal shlunk", i, gain=.3, offset=.14)] for i in range(3)])
    cue("DryFire", "equipment", "Single recorded dry mechanical latch, no shot layer", [
        [medieval(1, "Crossbow Lever Trigger", w)] for w in [(.15, .40), (.68, 1.0), (4.67, 5.005)]])
    cue("Hurt", "actor", "Natural short male grunts, performer 3; no pitch/formant replacement", [[voice(f"3grunt{i}")] for i in [1, 2, 4]])
    cue("Death", "actor", "Natural longer male vocal distress, performer 3", [[voice(f"3yell{i}")] for i in [1, 6, 9]])
    cue("EnemyHurt", "actor", "Different recorded male performer for nearby enemy reactions", [[voice(f"1yell{i}")] for i in [1, 2, 6]])
    cue("EnemyDeath", "actor", "Recorded enemy vocal distress, different individual takes", [
        [voice("2yell3")], [voice("2yell5")], [voice("2yell6", start=.37, end=1.07)]])
    cue("Pickup", "pickup", "Leather handling and a small buckle contact", [
        [weapon("quiver-leather-squeeze", i + 4, end=.55), weapon("belt-buckle", i + 2, end=.38, gain=.25)] for i in range(3)])
    cue("Coins", "pickup", "One real handful of coins per take", [[accessory("coins-shake", i)] for i in [1, 2, 3]])
    cue("ChestOpen", "interaction", "Recorded lockbox unlock and short door/lid movement", [
        [accessory("keyhole-lockbox-unlock", i + 1, end=.85), door("Open", i + 2, offset=.12, gain=.6)] for i in range(3)])
    cue("ChestClose", "interaction", "Recorded small door body closure and lockbox latch", [
        [door("Close", i + 1), foley("door and latch", i + 3, gain=.24, offset=.06)] for i in range(3)])
    cue("Lever", "interaction", "Recorded mechanism movement and chain weight", [
        [foley("metal shlunk", i + 4), foley("chain b", i, gain=.35, offset=.05)] for i in range(3)])
    cue("SecretStone", "interaction", "Recorded scrape, grit and chain arranged as a heavy sliding mechanism; not a stone-door field recording", [
        [weapon("sword-table-leg-scrape", i + 1, speed=.87),
         step("Sand", i, gain=.4, offset=.16), foley("chain a", i + 2, gain=.2, offset=.1)] for i in range(3)])
    # Nonphysical game notifications use restrained material arrangements. All energy
    # still comes from documented recordings, not procedural tones/noise/sweeps.
    cue("Portal", "designed", "Designed transition from reversed recorded iron decay into glass resonance", [
        [weapon("sword-knife-clash", i + 4, reverse=True, gain=.6),
         accessory("vials-glass-rattle", i + 1, gain=.45, offset=.35)] for i in range(3)])
    cue("Extract", "designed", "Recorded coin settling and glass touch for successful departure", [
        [accessory("coin-spin-fall", i + 1), accessory("vial-glass-small-drop", i + 1, gain=.35, offset=.12)] for i in range(3)])
    cue("Fail", "designed", "Restrained recorded metal weight settling into leather", [
        [foley("metal shlunk", i + 8), weapon("sheath-squeeze", i + 4, end=.55, gain=.25, offset=.10)] for i in range(3)])
    cue("UiHover", "ui", "Quiet real wood touch", [[accessory("wood-bowl-put-wood", i, end=.27)] for i in [3, 4]], (.018, .085))
    cue("UiClick", "ui", "Quiet real lockbox key turn contact", [[accessory("keyhole-lockbox-turn", i, end=.28)] for i in [1, 2]])
    cue("UiOpen", "ui", "Small recorded leather movement and buckle", [
        [weapon("sheath-unbuckle", i, end=.60), foley("clothes", i, gain=.25)] for i in [1, 2]])
    cue("UiClose", "ui", "Small recorded leather buckle closure", [[weapon("belt-buckle", i)] for i in [4, 5]])
    cue("UiError", "ui", "Dull mechanical stop, recorded latch contact", [[foley("door and latch", i)] for i in [4, 5]], (.04, .23))
    cue("ItemMove", "ui", "Single recorded leather item movement", [[weapon("sheath-squeeze", i, end=.55)] for i in [6, 7, 8]])
    cue("Potion", "consumable", "Actual clay bottle uncork and recorded quaff", [
        [accessory("bottle-clay-uncork", i + 1), layer("wareya-foley", "foley - quaff.wav", gain=.45, offset=.20)] for i in range(3)])
    cue("Bandage", "consumable", "Recorded cloth pull/wrap movement", [[foley("clothes", i)] for i in [3, 4, 5]])
    cue("SkillDash", "weapon", "Recorded heavy air pass and cloth movement", [
        [medieval(2, "Axe Swing", w), foley("clothes", i + 3, gain=.18)]
        for i, w in enumerate([(1.53, 1.97), (2.47, 2.82), (5.14, 5.53)])])
    cue("SkillCry", "actor", "Real short male exertion/shout performance", [[voice(f"1yell{i}")] for i in [4, 5, 7]])
    cue("SkillShot", "weapon", "Actual recurve releases, one shot per variant", [
        [medieval(1, "Scythian Recurve Shoot", w)] for w in [(.17, .43), (1.86, 2.14), (3.36, 3.65)]])
    cue("SkillBuff", "designed", "Designed ritual cue from real glass and a soft metallic ring", [
        [accessory("vials-glass-rattle", i + 3), weapon("sword-knife-clash", i + 4, gain=.18)] for i in range(3)])
    cue("ZoneWarning", "designed", "One warning strike made from recorded iron contacts", [
        [weapon("sword-knife-clash", i + 4), foley("metal clank", i + 3, gain=.45)] for i in range(3)])
    cue("ArrowImpact", "impact", "Recorded wooden contact with a quiet actual string decay", [
        [accessory("wood-bowl-put-wood", i + 3, end=.32),
         medieval(1, "English Longbow Shoot", w, gain=.22)]
        for i, w in enumerate([(.075, .32), (.745, .99), (1.225, 1.48)])])
    cue("ShieldBreak", "guard", "Recorded crunchy contact and metal debris/chain; no invented noise layer", [
        [foley("crunchy impacts", i), weapon("sword-knife-clash", i + 4, gain=.5),
         foley("chain b", i, gain=.2, offset=.04)] for i in range(3)])
    cue("BluntSwing", "weapon", "Individual recorded axe air passes for heavy weapons", [
        [medieval(2, "Axe Swing", w)] for w in [(1.53, 1.97), (2.47, 2.82), (5.14, 5.53)]])
    cue("BluntImpact", "impact", "Recorded bodily weight and small material crunch", [
        [hit(i + 5), foley("crunchy impacts", i + 3, gain=.25)] for i in range(3)])
    cue("Stab", "impact", "Recorded close bodily impact with a small blade contact", [
        [hit(i + 9), weapon("knife-sheathe", i + 1, gain=.25)] for i in range(3)])
    assert len(result) == 49
    assert sum(len(c["variants"]) for c in result.values()) == 151
    return result


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def selected_sources(definitions):
    return sorted({(l["pack"], l["original"]) for c in definitions.values() for v in c["variants"] for l in v})


def import_sources(directory, definitions):
    """Explicit one-time import; archive extraction/download is intentionally not automated."""
    downloads = json.loads((directory / "downloads.json").read_text(encoding="utf-8-sig"))
    prior_path = SOURCE / "provenance.json"
    prior = json.loads(prior_path.read_text(encoding="utf-8")) if prior_path.exists() else {"files": {}}
    provenance = {"schema": "dakadaka.recorded-foley-sources/v1", "packs": {}, "files": {}}
    for pack, original in selected_sources(definitions):
        record = downloads[pack]
        assert record["license"] == "CC0-1.0", pack
        src = directory / pack / original
        assert src.is_file(), src
        dest = SOURCE / pack / original
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dest)
        key = f"{pack}/{original}"
        provenance["files"][key] = {"sha256": sha(dest.read_bytes()), "bytes": dest.stat().st_size}
        if pack not in provenance["packs"]:
            provenance["packs"][pack] = {k: record[k] for k in ("page", "download", "author", "license", "archive_sha256")}
            html = directory / (pack + "-source.html")
            provenance["packs"][pack]["source_page_sha256"] = sha(html.read_bytes())
            provenance["packs"][pack]["license_evidence"] = ("Usage: Creative Commons Zero" if pack == "wareya-foley"
                else "License(s): OGA-BY 3.0 CC0" if pack == "hael-voices" else "License(s): CC0")
            provenance["packs"][pack]["license_url"] = "https://creativecommons.org/publicdomain/zero/1.0/"
            readme = directory / pack / "readme.txt"
            if readme.exists():
                shutil.copyfile(readme, SOURCE / pack / "readme.txt")
    # Only retire originals explicitly owned by the previous manifest, never unknown files.
    for key in set(prior["files"]) - set(provenance["files"]):
        unused = (SOURCE / key).resolve()
        if not unused.is_relative_to(SOURCE.resolve()):
            raise ValueError("Unsafe old source path: " + key)
        if unused.is_file():
            if sha(unused.read_bytes()) != prior["files"][key]["sha256"]:
                raise ValueError("Unused original has local edits; preserve it: " + key)
            unused.unlink()
    write_json(SOURCE / "provenance.json", provenance)
    lines = ["# Selected recorded Foley: source licenses", "",
        "Selected originals are unmodified bytes extracted from the listed archives. Their SHA256 hashes and archive/page hashes are in provenance.json. These are third-party performances, not project-original recordings.", "",
        "License evidence was checked against the downloaded source pages on 22 November 2026. Full HTML stays in ignored Logs/acceptance/natural-audio-20261122/sources; this file retains the short relevant evidence. CC0 is the chosen license where alternatives are offered. Attribution is preserved voluntarily.", "",
        "[CC0 1.0 dedication](https://creativecommons.org/publicdomain/zero/1.0/)", ""]
    for pack, record in provenance["packs"].items():
        lines += ["## " + pack, "", "Author: " + record["author"] + ".", "",
            "Source: [publisher page](" + record["page"] + "). [Original archive](" + record["download"] + ").", "",
            "Page evidence: > " + record["license_evidence"], "",
            "Chosen license: CC0-1.0. Page SHA256: `" + record["source_page_sha256"] + "`.", ""]
    lines += ["## Editing and boundaries", "",
        "Tools/Audio/BuildRecordedFoley.py defines each take and edit. The runtime bank manifest maps every output to source filenames, source hashes, original time windows, final used sample bounds, gains, and any speed/reversal edit. No external recording is credited to this project.", "",
        "Stone and sand footsteps are Fantozzi recordings. Wood footsteps combine recorded footwear with recorded wood contact; they are not claimed to be a wood-floor field recording. SecretStone is a material Foley composition, not a recorded stone door. Nonphysical transition cues are designed arrangements of recorded materials. Human vocals are HaelDB performers without pitch/formant replacement.", ""]
    (SOURCE / "LICENSES.md").write_text("\n".join(lines), encoding="utf-8")


def find_ffmpeg(explicit):
    found = explicit or os.environ.get("FFMPEG") or shutil.which("ffmpeg")
    if not found:
        # Read-only workstation tool fallback, never writes to the legacy checkout.
        fallback = ROOT.parent / "DakaDaka/Logs/acceptance/dark-fantasy-ambience/audio-tools/imageio_ffmpeg/binaries/ffmpeg-win-x86_64-v7.1.exe"
        if fallback.is_file():
            found = str(fallback)
    if not found:
        raise RuntimeError("Pass --ffmpeg /path/to/ffmpeg (FFmpeg 7.1 used for this bank).")
    return str(found)


class RecordingReader:
    def __init__(self, ffmpeg, provenance):
        self.ffmpeg, self.provenance, self.cache = ffmpeg, provenance, {}

    def read(self, specification):
        key = specification["pack"] + "/" + specification["original"]
        if key not in self.cache:
            path = SOURCE / key
            source_hash = sha(path.read_bytes())
            if source_hash != self.provenance["files"][key]["sha256"]:
                raise ValueError("Original recording changed: " + key)
            # FFmpeg float decode preserves over-range reconstructed MP3 peaks until final gain.
            data = subprocess.check_output([self.ffmpeg, "-v", "error", "-threads", "1", "-i", str(path),
                "-map_metadata", "-1", "-ac", "1", "-ar", str(RATE), "-f", "f32le", "-"])
            self.cache[key] = np.frombuffer(data, dtype="<f4").astype(np.float64)
        source = self.cache[key]
        start, end = specification["window_seconds"]
        first = round(start * RATE)
        last = min(len(source), round(end * RATE)) if end is not None else len(source)
        x = source[first:last].copy()
        if len(x) < 96 or not np.isfinite(x).all():
            raise ValueError("Invalid take: " + key)
        x -= np.mean(x)
        # Trim only the outer quiet margins. Internal pauses and real decay are untouched.
        block = 240
        padded = np.pad(x, (0, (-len(x)) % block))
        rms = np.sqrt(np.mean(padded.reshape(-1, block) ** 2, axis=1))
        threshold = max(float(rms.max()) * .018, 0.000035)
        active = np.flatnonzero(rms > threshold)
        if not len(active):
            raise ValueError("Silent take: " + key)
        a = max(0, int(active[0]) * block - 288)
        b = min(len(x), (int(active[-1]) + 1) * block + 2880)
        x = x[a:b]
        if specification["reverse"]:
            x = x[::-1].copy()
        speed = specification["playback_rate"]
        if speed != 1.:
            x = np.interp(np.arange(math.ceil(len(x) / speed)) * speed, np.arange(len(x)), x)
        # Gentle edge ramps prevent edit clicks. They do not erase the recorded attack.
        attack, release = min(96, len(x) // 4), min(720, len(x) // 4)
        x[:attack] *= np.linspace(0., 1., attack)
        x[-release:] *= np.linspace(1., 0., release)
        # Balance recordings made at different microphone gains before physical layers mix.
        reference_gain = min(8., .65 / max(float(abs(x).max()), 1e-9))
        x *= reference_gain * specification["gain"]
        evidence = dict(specification)
        evidence.update(source=key, source_sha256=self.provenance["files"][key]["sha256"],
                        decoded_sample_rate=RATE, used_source_frames=[first + a, first + b],
                        used_source_seconds=[round((first + a) / RATE, 6), round((first + b) / RATE, 6)],
                        reference_gain=round(reference_gain, 9), source_peak_ceiling=.65)
        return x, evidence


def wav_bytes(pcm):
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as stream:
        stream.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
        stream.writeframes(pcm.astype("<i2").tobytes())
    return buffer.getvalue()


def db(value):
    return round(20 * math.log10(max(value, 1e-8)), 3)


def build_take(reader, definition, layers):
    rendered = [reader.read(l) for l in layers]
    offsets = [round(l["offset_seconds"] * RATE) for l in layers]
    size = max(len(x) + offset for (x, _), offset in zip(rendered, offsets))
    x = np.zeros(size, dtype=np.float64)
    for (samples, _), offset in zip(rendered, offsets):
        x[offset:offset + len(samples)] += samples
    rms = math.sqrt(float(np.mean(x ** 2)))
    adjustment = min(8., definition["target_rms"] / max(rms, 1e-9),
                     definition["peak_ceiling"] / max(float(abs(x).max()), 1e-9))
    x *= adjustment
    x = np.pad(x, (96, 240))
    pcm = np.rint(x * 32767.).astype("<i2")
    pcm[0] = pcm[-1] = 0
    decoded = pcm.astype(np.float64) / 32768.
    peak = float(abs(decoded).max())
    assert .03 <= len(pcm) / RATE <= 8., (len(pcm) / RATE, layers)
    assert .001 <= peak < .9
    data = wav_bytes(pcm)
    return pcm, data, dict(
        sha256=sha(data), bytes=len(data), mix_gain=round(adjustment, 9),
        metrics=dict(frames=len(pcm), seconds=round(len(pcm) / RATE, 6), peak_dbfs=db(peak),
                     rms_dbfs=db(math.sqrt(float(np.mean(decoded ** 2)))), mean=round(float(np.mean(decoded)), 9),
                     first_sample=int(pcm[0]), last_sample=int(pcm[-1]),
                     max_sample_delta=round(float(abs(np.diff(decoded)).max()), 6),
                     nonzero_frames=int(np.count_nonzero(pcm))),
        layers=[evidence for _, evidence in rendered])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg")
    parser.add_argument("--import-from", type=Path, help="Import ONLY selected originals from an existing extracted downloads directory")
    parser.add_argument("--verify", action="store_true", help="Regenerate in memory and compare every WAV and complete manifest")
    args = parser.parse_args()
    definitions = recipes()
    if args.verify and args.import_from:
        parser.error("--verify cannot import or modify originals")
    if args.import_from:
        import_sources(args.import_from, definitions)
    provenance = json.loads((SOURCE / "provenance.json").read_text(encoding="utf-8"))
    ffmpeg = find_ffmpeg(args.ffmpeg)
    ffmpeg_version = subprocess.check_output([ffmpeg, "-version"], text=True, encoding="utf-8").splitlines()[0]
    reader = RecordingReader(ffmpeg, provenance)
    manifest = dict(schema="dakadaka.recorded-sfx/v1", generator="Tools/Audio/BuildRecordedFoley.py",
        generator_sha256=sha(Path(__file__).read_bytes()), recipe_version=VERSION,
        sample_rate=RATE, channels=1, bits_per_sample=16,
        provenance="Selected real CC0 recordings. Physical actions use recorded takes; designed magic/UI transitions arrange recorded materials. No mathematical sound generation.",
        rights="Source recordings CC0-1.0; original authors and pages in source_packs. No claim of original performances by this project.",
        processing="Explicit single-take windows; outer silence trim; DC removal; 2ms/15ms edge fades; float mono resample; category gain/peak calibration. Only SecretStone slowed to 0.87; Portal reverses one recorded iron layer. No added reverb or procedural noise.",
        runtime_note="Mono dry sources; runtime owns positioning, occlusion and room reflections. Recordings may contain their original room decay.",
        toolchain=dict(ffmpeg=ffmpeg_version, numpy=np.__version__), source_packs=provenance["packs"], cues={})
    preview = []
    timings = []
    cursor = 0
    total_bytes = total_frames = 0
    hashes = set()
    for name, definition in definitions.items():
        record = {k: v for k, v in definition.items() if k != "variants"}
        record["variants"] = []
        for i, layers in enumerate(definition["variants"], 1):
            pcm, data, evidence = build_take(reader, definition, layers)
            filename = f"{name}_{i:02}.wav"
            evidence = dict(file=filename, **evidence)
            record["variants"].append(evidence)
            if args.verify:
                if not (OUT / filename).is_file() or sha((OUT / filename).read_bytes()) != evidence["sha256"]:
                    raise ValueError("Reproduction mismatch: " + filename)
            else:
                OUT.mkdir(parents=True, exist_ok=True)
                (OUT / filename).write_bytes(data)  # Existing Unity .meta files deliberately untouched.
            total_bytes += len(data)
            total_frames += len(pcm)
            hashes.add(evidence["sha256"])
            if i == 1:
                timings.append(dict(cue=name, file=filename, category=definition["category"],
                                    start_seconds=round(cursor / RATE, 6), end_seconds=round((cursor + len(pcm)) / RATE, 6)))
                preview.extend([pcm, np.zeros(19200, dtype="<i2")])
                cursor += len(pcm) + 19200
        manifest["cues"][name] = record
    manifest["totals"] = dict(cue_families=len(definitions), clips=151, unique_hashes=len(hashes),
                              bytes=total_bytes, seconds=round(total_frames / RATE, 6),
                              selected_originals=len(provenance["files"]),
                              selected_original_bytes=sum(f["bytes"] for f in provenance["files"].values()))
    assert len(hashes) == 151, "Every variant should be a distinct edit/performance, not duplicate bytes"
    if args.verify:
        if json.loads((OUT / "manifest.json").read_text(encoding="utf-8")) != manifest:
            raise ValueError("Manifest differs from current generator/toolchain; rebuild intentionally before verifying")
    else:
        write_json(OUT / "manifest.json", manifest)
        REVIEW.mkdir(parents=True, exist_ok=True)
        reel = wav_bytes(np.concatenate(preview))
        (REVIEW / "RecordedSfx-preview.wav").write_bytes(reel)
        write_json(REVIEW / "RecordedSfx-preview-timings.json", dict(file="RecordedSfx-preview.wav", sha256=sha(reel),
            seconds=round(cursor / RATE, 6), note="First variant of every cue, unchanged level, 0.4-second gaps; no spoken labels", cues=timings))
    print(json.dumps(dict(mode="verified" if args.verify else "built", **manifest["totals"]), indent=2))


if __name__ == "__main__":
    main()
