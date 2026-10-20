"""Pure offline CLI smoke tests; Python standard library only, no browser/input injection."""
from pathlib import Path
import json
import random
import struct
import subprocess
import tempfile
import zlib

ROOT = Path(__file__).resolve().parent
WIDTH, HEIGHT = 128, 120

def png(path, pixels):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    raw = b"".join(b"\0" + pixels[y*WIDTH*4:(y+1)*WIDTH*4] for y in range(HEIGHT))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))

rng = random.Random(71623)
rows = [b"\xff\xff\xff\xff" * 24 + bytes(sum(([rng.randrange(256), rng.randrange(256), rng.randrange(256), 255] for _ in range(WIDTH-24)), [])) for _ in range(400)]
def frame(offset):
    return b"".join(rows[offset:offset+HEIGHT])

def run(case, offsets, expected=None, csv_file=False, corrupt=None, pixel_change=False, metadata=False, default_output=False):
    folder = work / case
    folder.mkdir()
    # Names intentionally cross the lexical/numeric ordering boundary.
    names = ["1.png", "10.png", "2.png", "3.png"]
    for i, offset in enumerate(offsets):
        pixels = frame(offset)
        if pixel_change and i == 1:
            data = bytearray(pixels)
            data[(40*WIDTH+60)*4] ^= 1
            pixels = bytes(data)
        png(folder / names[i], pixels)
    if corrupt is not None:
        (folder / names[corrupt]).write_bytes(b"broken PNG")
    if metadata:
        (folder / "session.json").write_text(json.dumps({"minMatchRows": 12}), encoding="utf-8")
    output = folder / "replay.png" if default_output else work / (case + ".png")
    args = ["dotnet", "run", "--no-build", "--project", str(ROOT), "--", str(folder)]
    if not default_output:
        args.append(str(output))
    if expected is not None:
        if csv_file:
            csv = folder / "expected.csv"
            csv.write_text("\ufeff" + expected, encoding="utf-8")
            args.append(str(csv))
        else:
            args.append(expected)
    process = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    records = [json.loads(line) for line in process.stdout.splitlines() if line.startswith("{")]
    assert records and records[-1]["type"] == "summary", (case, process.stdout, process.stderr)
    summary = records[-1]
    assert summary["exitCode"] == process.returncode, (case, summary, process.returncode)
    assert summary["bottomConfirmed"] is False
    frames = [record for record in records if record["type"] == "frame"]
    for record in frames:
        assert all(key in record for key in ("status", "appended", "overlap", "LastScrollDelta", "LastMatchedRows", "LastRequiredRows"))
    assert output.exists(), (case, summary)
    data = output.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    assert struct.unpack(">II", data[16:24]) == (WIDTH, summary["totalHeight"])
    print(json.dumps({"case": case, "exitCode": process.returncode, "height": summary["totalHeight"], "statuses": [r["status"] for r in frames]}))
    return process.returncode, records, frames, summary

(ROOT / "obj").mkdir(exist_ok=True)
# Keep generated evidence in ignored obj; all writes remain under the owned tool directory.
work = Path(tempfile.mkdtemp(prefix="smoke-", dir=ROOT / "obj"))

code, records, frames, summary = run("exact-and-nochange", [0,25,25,90], "120,25,0,65", metadata=True)
assert code == 0 and summary["totalHeight"] == 210 and len(frames) == 4
assert all(r["pixelsVerified"] for r in frames)
assert records[0]["minMatchRows"] == 12
assert [r["file"] for r in frames] == ["1.png", "10.png", "2.png", "3.png"]

code, _, frames, _ = run("csv-file-n-minus-one", [0,25,90], "25\n65", csv_file=True)
assert code == 0 and all(r["pixelsVerified"] for r in frames)

code, _, frames, summary = run("alignment-failure-stops", [0,200,25])
assert code == 1 and len(frames) == 2 and summary["totalHeight"] == HEIGHT and summary["partial"]

code, _, frames, summary = run("decode-failure-stops", [0,25,90], corrupt=1)
assert code == 1 and len(frames) == 2 and frames[-1]["status"] == "Error" and summary["totalHeight"] == HEIGHT

code, _, frames, summary = run("wrong-delta-stops", [0,25,90], "120,26,65")
assert code == 2 and len(frames) == 2 and "Added-row mismatch" in frames[-1]["error"]

code, _, frames, _ = run("single-pixel-strict-rejection", [0,25,90], "120,25,65", pixel_change=True)
assert code == 2 and len(frames) == 2 and "Full-pixel overlap mismatch" in frames[-1]["error"]

code, _, frames, summary = run("default-output", [0,25], default_output=True)
assert code == 0 and summary["frameCount"] == 2
# Repeat the same directory: prior default output must never become an input frame.
repeat = subprocess.run(["dotnet", "run", "--no-build", "--project", str(ROOT), "--", str(work / "default-output")], capture_output=True, text=True, encoding="utf-8", errors="replace")
assert repeat.returncode == 0
assert json.loads(repeat.stdout.splitlines()[-1])["frameCount"] == 2
print("PASS: 8 offline CLI smoke checks; evidence: " + str(work))
