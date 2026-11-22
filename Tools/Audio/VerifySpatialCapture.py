"""Verify actual Recorder stereo output: python Tools/Audio/VerifySpatialCapture.py <live-output-dir>.

This verifies CONTROLLED acoustic evidence only; it does not replace ordinary raid checks.
Unity Recorder's installed WAVEncoder writes float32 RIFF and overstates data size by 8 bytes.
"""

import argparse
import array
import csv
import json
import math
from pathlib import Path
import struct
import sys
import wave


CASE_NAMES = (
    "near 2m", "mid 8m", "far 18m", "left", "right", "blocked 8m",
    "clear again 8m", "attached moving left to right",
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_recorded_wav(path):
    raw = path.read_bytes()
    require(len(raw) >= 44 and raw[:4] == b"RIFF" and raw[8:12] == b"WAVE", "Not a RIFF WAVE recording")
    require(struct.unpack_from("<I", raw, 4)[0] == len(raw) - 8, "RIFF length does not match completed recording")
    position, fmt, payload, oversize = 12, None, None, 0
    while position + 8 <= len(raw):
        kind, size = struct.unpack_from("<4sI", raw, position)
        position += 8
        available = len(raw) - position
        if kind == b"fmt ":
            require(size >= 16 and size <= available, "Incomplete WAV format chunk")
            fmt = struct.unpack_from("<HHIIHH", raw, position)
        elif kind == b"data":
            require(payload is None, "Multiple WAV data chunks are unsupported")
            # Installed WAVEncoder.Stop writes total_length - 36 instead of total_length - 44.
            oversize = max(0, size - available)
            require(oversize in (0, 8), "Truncated audio data beyond Recorder's known 8-byte header discrepancy")
            payload = raw[position:position + min(size, available)]
        else:
            require(size <= available, "Incomplete WAV metadata chunk")
        position += size + (size & 1)
    require(fmt is not None and payload is not None, "Missing WAV format/data")
    encoding, channels, rate, byte_rate, block_align, bits = fmt
    require((encoding, channels, bits, block_align) == (3, 2, 32, 8), "Expected Recorder float32 stereo WAV")
    require(8000 <= rate <= 192000 and byte_rate == rate * block_align, "Invalid audio sample rate/header")
    require(len(payload) > 0 and len(payload) % block_align == 0, "Incomplete stereo sample frame")
    samples = array.array("f")
    samples.frombytes(payload)
    if sys.byteorder != "little":
        samples.byteswap()
    require(all(math.isfinite(value) for value in samples), "Recording contains nonfinite samples")
    peak = max(abs(value) for value in samples)
    require(0 < peak < 1.0, f"Recording is silent or clipped: peak={peak}")
    return samples, rate, oversize, peak


def write_pcm16(path, samples, rate):
    pcm = array.array("h", (round(value * 32767) for value in samples))
    if sys.byteorder != "little":
        pcm.byteswap()
    with wave.open(str(path), "wb") as output:
        output.setnchannels(2)
        output.setsampwidth(2)
        output.setframerate(rate)
        output.writeframes(pcm.tobytes())


def verify(folder):
    metadata = json.loads((folder / "CONTROLLED-cases.json").read_text(encoding="utf-8-sig"))
    require(metadata.get("schema") == 1 and metadata.get("fps") == 30, "Unsupported case schema/frame rate")
    require(metadata.get("sourceGeometryPassed") is True, "Source/geometry helper did not finish successfully")
    require(metadata.get("wav") == "CONTROLLED-mix.wav", "Unexpected capture filename")
    cases = metadata.get("cases", [])
    require(tuple(case.get("name") for case in cases) == CASE_NAMES, "Missing, duplicate or reordered controlled cases")
    samples, rate, oversize, peak = read_recorded_wav(folder / metadata["wav"])
    fps = metadata["fps"]
    duration = len(samples) / (rate * 2)
    end_frame = metadata.get("captureEndFrame")
    require(type(end_frame) is int and end_frame > 0, "Missing final capture frame")
    require(abs(duration - end_frame / fps) <= 2 / fps, "WAV duration does not align with recorded case frames")
    metrics = []
    previous_end = -1
    for case in cases:
        start, end, event = (case.get(key) for key in ("startFrame", "endFrame", "eventFrame"))
        require(all(type(value) is int for value in (start, end, event)), "Noninteger case frame bounds")
        require(start > previous_end and event - start == 3 and end - event == 60 and end <= end_frame,
                f"Invalid case timing: {case['name']}")
        previous_end = end
        # One-frame margin accommodates the Recorder's first end-of-frame capture. The cue has a
        # three-frame lead and cases have fifteen silent frames between them, so margins do not overlap.
        first = max(0, round((start - 1) * rate / fps))
        last = round((end + 1) * rate / fps)
        require(last * 2 <= len(samples), f"Recording ends before {case['name']}")
        segment = samples[first * 2:last * 2]
        left = math.sqrt(math.fsum(value * value for value in segment[0::2]) / (last - first))
        right = math.sqrt(math.fsum(value * value for value in segment[1::2]) / (last - first))
        rms = math.sqrt((left * left + right * right) * .5)
        require(math.isfinite(rms) and rms > 1e-7, f"No measurable output for {case['name']}")
        row = dict(name=case["name"], start_frame=start, end_frame=end, first_sample=first, last_sample=last,
                   left_rms=left, right_rms=right, combined_rms=rms,
                   peak=max(abs(value) for value in segment), source_gain=case["Gain"], lowpass_hz=case["Cutoff"])
        metrics.append(row)
        write_pcm16(folder / ("CONTROLLED-" + case["name"].replace(" ", "-") + ".wav"), segment, rate)

    by_name = {row["name"]: row for row in metrics}
    rms = lambda name: by_name[name]["combined_rms"]
    checks = {
        "near_louder_than_mid": rms("near 2m") > rms("mid 8m") * 1.7,
        "mid_louder_than_far": rms("mid 8m") > rms("far 18m") * 1.7,
        "left_channel_dominates_left_case": by_name["left"]["left_rms"] > by_name["left"]["right_rms"] * 1.25,
        "right_channel_dominates_right_case": by_name["right"]["right_rms"] > by_name["right"]["left_rms"] * 1.25,
        "wall_reduces_output": rms("blocked 8m") < rms("mid 8m") * .65,
        "removing_wall_restores_output": rms("clear again 8m") > rms("blocked 8m") * 1.5,
    }
    with (folder / "CONTROLLED-acoustics.csv").open("w", newline="", encoding="utf-8") as output:
        writer = csv.DictWriter(output, fieldnames=list(metrics[0]))
        writer.writeheader()
        writer.writerows(metrics)
    result = dict(status="PASS" if all(checks.values()) else "FAIL", scope="Actual recorded CONTROLLED stereo output only",
                  sample_rate=rate, channels=2, duration_seconds=duration, peak=peak,
                  recorder_data_header_oversize_bytes=oversize, frame_margin=1, checks=checks, cases=metrics)
    (folder / "CONTROLLED-metrics.json").write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    (folder / "CONTROLLED-audio-result.json").write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    require(all(checks.values()), "Recorded output failed: " + ", ".join(key for key, passed in checks.items() if not passed))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()
    try:
        result = verify(args.output_directory)
    except (OSError, ValueError, KeyError, TypeError) as error:
        # Never leave a previous successful result standing after an invalid or incomplete capture.
        result_path = args.output_directory / "CONTROLLED-audio-result.json"
        if args.output_directory.is_dir():
            failure = dict(status="FAIL", error=str(error), scope="Actual recorded CONTROLLED stereo output only")
            result_path.write_text(json.dumps(failure, indent=2) + "\n", encoding="utf-8")
        print("FAIL actual captured audio: " + str(error), file=sys.stderr)
        return 1
    print(f"PASS actual captured audio: {len(result['cases'])} stereo cases, {len(result['checks'])} comparisons; "
          f"{result['sample_rate']} Hz, peak={result['peak']:.6f}. Ordinary raid proof remains separate.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
