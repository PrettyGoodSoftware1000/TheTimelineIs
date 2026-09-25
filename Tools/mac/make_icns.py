#!/usr/bin/env python3
"""Turn Desktop/Icon.bmp into Desktop/AppIcon.icns.

Run once, by hand, when the icon changes. The .icns it writes is COMMITTED,
so building the Mac app needs no Python, no Xcode and no image tools — the
build script only copies the file.

    python3 Tools/mac/make_icns.py

An .icns is a tagged container: four-byte type, four-byte big-endian length
including that header, then the data. The modern types hold plain PNGs, one
per size, which is all this writes.
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOURCE = os.path.join(ROOT, "Desktop", "Icon.bmp")
TARGET = os.path.join(ROOT, "Desktop", "AppIcon.icns")

# icns type -> pixel size. macOS picks whichever fits; anything not here is
# scaled from the nearest one that is.
SIZES = {
    b"icp4": 16,
    b"icp5": 32,
    b"ic11": 32,    # 16 at 2x
    b"ic12": 64,    # 32 at 2x
    b"ic07": 128,
    b"ic13": 256,   # 128 at 2x
    b"ic08": 256,
}


def read_bmp(path):
    """A 32-bit BMP as (width, height, rows of RGBA). Enough for one icon."""
    d = open(path, "rb").read()
    if d[:2] != b"BM":
        raise SystemExit(f"{path} is not a BMP")
    data_at = struct.unpack("<I", d[10:14])[0]
    w, h = struct.unpack("<ii", d[18:26])
    bpp = struct.unpack("<H", d[28:30])[0]
    if bpp != 32:
        raise SystemExit(f"{path} is {bpp}-bit; this only reads 32-bit BMPs")
    bottom_up = h > 0
    h = abs(h)
    rows = []
    for y in range(h):
        off = data_at + y * w * 4
        row = [tuple(d[off + x * 4: off + x * 4 + 4]) for x in range(w)]  # BGRA
        rows.append([(b[2], b[1], b[0], b[3]) for b in row])
    if bottom_up:
        rows.reverse()
    return w, h, rows


def scale(rows, w, h, size):
    """Box average down to size x size. Safe for pixel art and for painted art."""
    out = []
    for y in range(size):
        y0, y1 = y * h // size, max(y * h // size + 1, (y + 1) * h // size)
        line = []
        for x in range(size):
            x0, x1 = x * w // size, max(x * w // size + 1, (x + 1) * w // size)
            r = g = b = a = n = 0
            for sy in range(y0, y1):
                for sx in range(x0, x1):
                    p = rows[sy][sx]
                    # weight colour by alpha so transparent edges do not
                    # drag the colour toward black
                    r += p[0] * p[3]; g += p[1] * p[3]; b += p[2] * p[3]
                    a += p[3]; n += 1
            line.append((r // a, g // a, b // a, a // n) if a else (0, 0, 0, 0))
        out.append(line)
    return out


def png(rows):
    size = len(rows)
    raw = bytearray()
    for row in rows:
        raw.append(0)
        for p in row:
            raw += bytes(p)

    def chunk(tag, body):
        return (struct.pack(">I", len(body)) + tag + body
                + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def main():
    w, h, rows = read_bmp(SOURCE)
    print(f"{SOURCE}: {w}x{h}")
    cache, body = {}, b""
    for tag, size in SIZES.items():
        if size not in cache:
            cache[size] = png(rows if size == w == h else scale(rows, w, h, size))
        data = cache[size]
        body += tag + struct.pack(">I", len(data) + 8) + data
        print(f"  {tag.decode()}  {size}x{size}  {len(data)} bytes")
    out = b"icns" + struct.pack(">I", len(body) + 8) + body
    open(TARGET, "wb").write(out)
    print(f"wrote {TARGET}  ({len(out)} bytes)")


if __name__ == "__main__":
    sys.exit(main())
