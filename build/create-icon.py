"""Rebuild the small Mosaic app mark using only the Python standard library."""
from pathlib import Path
import struct

size = 64
pixels = bytearray()
palette = [(45, 101, 80), (126, 161, 137), (177, 199, 155), (220, 178, 111)]
for y in range(size - 1, -1, -1):
    for x in range(size):
        if 8 <= x < 56 and 8 <= y < 56 and not (30 <= x < 34 or 30 <= y < 34):
            r, g, b = palette[(x >= 34) + 2 * (y >= 34)]
            pixels += bytes([b, g, r, 255])
        else:
            pixels += bytes([0, 0, 0, 0])
mask = bytes(((size + 31) // 32) * 4 * size)
dib = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0) + pixels + mask
ico = struct.pack('<HHH', 0, 1, 1) + struct.pack('<BBBBHHII', size, size, 0, 0, 1, 32, len(dib), 22) + dib
target = Path(__file__).resolve().parent.parent / 'src/Mosaic.Desktop/Assets/Mosaic.ico'
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(ico)
