"""Draws the launcher's icon (a disc on a green rounded square, the launcher's own motif) as a PNG.

Standard library only, so CI and every system can run it:
    python tools/make_icon.py launcher-avalonia/Assets/port-launcher.png [size]
"""
import math
import struct
import sys
import zlib


def icon(size):
    s = 4  # supersampling per axis
    big = size * s
    radius = big * 0.22
    cx = cy = big / 2
    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(s):
                for sx in range(s):
                    px, py = x * s + sx + 0.5, y * s + sy + 0.5
                    # rounded square
                    dx = max(radius - px, 0, px - (big - radius))
                    dy = max(radius - py, 0, py - (big - radius))
                    if dx * dx + dy * dy > radius * radius:
                        continue
                    t = (px + py) / (2 * big)
                    r, g, b = 70 + (28 - 70) * t, 184 + (120 - 184) * t, 110 + (64 - 110) * t
                    d = math.hypot(px - cx, py - cy) / big
                    # the disc: a light ring, the label and the hole
                    if 0.13 < d < 0.36:
                        r, g, b = 240, 242, 245
                        if 0.27 < d < 0.29:
                            r, g, b = 200, 205, 214
                    elif d <= 0.13 and d > 0.055:
                        r, g, b = 24, 27, 33
                    acc[0] += r
                    acc[1] += g
                    acc[2] += b
                    acc[3] += 255
            n = s * s
            a = acc[3] / n
            if a > 0:
                k = 255 / acc[3]
                row += bytes((int(acc[0] * k), int(acc[1] * k), int(acc[2] * k), int(a)))
            else:
                row += b'\0\0\0\0'
        rows.append(bytes(row))
    return rows


def png(rows, size):
    raw = b''.join(b'\0' + r for r in rows)

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


if __name__ == '__main__':
    out = sys.argv[1]
    size = int(sys.argv[2]) if len(sys.argv) > 2 else 256
    with open(out, 'wb') as f:
        f.write(png(icon(size), size))
