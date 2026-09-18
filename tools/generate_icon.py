#!/usr/bin/env python3
"""Generates src/Timerlight/Assets/timerlight.ico - the icon of the executable itself.

The shape mirrors HourglassIconRenderer.cs, drawn on the same 100x100 design canvas,
so the file icon and the tray icon are recognisably the same object. The tray icon is
painted at run time; only this static file needs generating.

Usage: python3 tools/generate_icon.py
"""

from __future__ import annotations

import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

# Geometry, in the 100x100 design space shared with the C# renderer.
CENTER_X = 50.0
NECK_Y = 50.0
UPPER_BASE_Y = 14.5
LOWER_BASE_Y = 85.5
HALF_BASE_WIDTH = 29.0
BULB_HEIGHT = 35.5
GLASS_STROKE = 5.5
TOP_CAP = (16.0, 4.5, 84.0, 14.5)
BOTTOM_CAP = (16.0, 85.5, 84.0, 95.5)

# The file icon shows an hourglass part-way through, so both bulbs hold sand.
ICON_PROGRESS = 0.25

# A perfectly edge-on glass would give the transform a zero row, so keep a sliver of height.
MINIMUM_FLIP_SCALE = 0.04

PLATE_COLOR = (43, 47, 58, 255)
FRAME_COLOR = (238, 240, 244, 255)

SUPERSAMPLE = 8
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
PNG_FROM_SIZE = 128  # smaller entries are stored as BMP, which every shell understands


def sand_color(progress: float) -> tuple[int, int, int, int]:
    """Same green-to-red ramp as SandColor() in the C# renderer."""
    hue = 120.0 * math.pow(1.0 - progress, 1.25)
    value = 0.80 + 0.15 * progress
    saturation = 0.92

    chroma = value * saturation
    sector = max(0.0, min(360.0, hue)) / 60.0
    x = chroma * (1.0 - abs((sector % 2.0) - 1.0))
    m = value - chroma
    table = [(chroma, x, 0.0), (x, chroma, 0.0), (0.0, chroma, x),
             (0.0, x, chroma), (x, 0.0, chroma), (chroma, 0.0, x)]
    r, g, b = table[int(sector) % 6]
    return (round((r + m) * 255), round((g + m) * 255), round((b + m) * 255), 255)


def upper_sand(progress: float) -> list[tuple[float, float]] | None:
    remaining = 1.0 - progress
    if remaining <= 0.0001:
        return None
    height = BULB_HEIGHT * math.sqrt(remaining)
    half_width = HALF_BASE_WIDTH * height / BULB_HEIGHT
    top = NECK_Y - height
    return [(CENTER_X - half_width, top), (CENTER_X + half_width, top), (CENTER_X, NECK_Y)]


def lower_sand_depth(progress: float) -> float:
    return BULB_HEIGHT * (1.0 - math.sqrt(1.0 - max(0.0, min(1.0, progress))))


def lower_sand(progress: float) -> list[tuple[float, float]] | None:
    if progress <= 0.0001:
        return None
    depth = lower_sand_depth(progress)
    half_width = HALF_BASE_WIDTH * (BULB_HEIGHT - depth) / BULB_HEIGHT
    surface = LOWER_BASE_Y - depth
    return [
        (CENTER_X - HALF_BASE_WIDTH, LOWER_BASE_Y),
        (CENTER_X + HALF_BASE_WIDTH, LOWER_BASE_Y),
        (CENTER_X + half_width, surface),
        (CENTER_X - half_width, surface),
    ]


def flip(image: Image.Image, angle_degrees: float, scale: float) -> Image.Image:
    """Squashes the glass vertically about its neck, the way ApplyFlip does in the C# renderer."""
    factor = math.cos(math.radians(angle_degrees))
    if abs(factor) < MINIMUM_FLIP_SCALE:
        factor = MINIMUM_FLIP_SCALE if angle_degrees <= 90.0 else -MINIMUM_FLIP_SCALE

    # PIL's AFFINE matrix maps output pixels back to input pixels, hence the reciprocal.
    # Integer coordinates address pixel centres here while GDI+ rasterises in continuous space,
    # so the axis is nudged half a pixel to line a finished half-turn up with a fresh pour.
    # GDI+ mirrors exactly about y=50 and needs no such nudge.
    neck = NECK_Y * scale + 0.5
    return image.transform(
        image.size,
        Image.AFFINE,
        (1.0, 0.0, 0.0, 0.0, 1.0 / factor, neck - neck / factor),
        resample=Image.BILINEAR,
    )


def render(
    size: int,
    progress: float = ICON_PROGRESS,
    color_progress: float | None = None,
    flip_angle: float = 0.0,
    plate: bool = True,
    frame_color: tuple[int, int, int, int] = FRAME_COLOR,
    finished: bool = False,
) -> Image.Image:
    """Draws the hourglass. With `plate` it becomes the file icon; without, the tray icon.

    `progress` sets the sand levels (one pour) and `color_progress` sets the colour (the whole
    interval); leaving the latter out ties them together, which is what the file icon wants.
    """
    if color_progress is None:
        color_progress = progress

    canvas = size * SUPERSAMPLE
    scale = canvas / 100.0
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def pt(points):
        return [(x * scale, y * scale) for x, y in points]

    def box(rect):
        left, top, right, bottom = rect
        return [left * scale, top * scale, right * scale, bottom * scale]


    upper_bulb = [(CENTER_X - HALF_BASE_WIDTH, UPPER_BASE_Y),
                  (CENTER_X + HALF_BASE_WIDTH, UPPER_BASE_Y),
                  (CENTER_X, NECK_Y)]
    lower_bulb = [(CENTER_X - HALF_BASE_WIDTH, LOWER_BASE_Y),
                  (CENTER_X + HALF_BASE_WIDTH, LOWER_BASE_Y),
                  (CENTER_X, NECK_Y)]

    hollow = (frame_color[0], frame_color[1], frame_color[2], 46)
    draw.polygon(pt(upper_bulb), fill=hollow)
    draw.polygon(pt(lower_bulb), fill=hollow)

    sand = sand_color(color_progress)
    top_sand = upper_sand(progress)
    if top_sand:
        draw.polygon(pt(top_sand), fill=sand)
    bottom_sand = lower_sand(progress)
    if bottom_sand:
        draw.polygon(pt(bottom_sand), fill=sand)

    # The falling stream only exists while sand is actually moving, as in the C# renderer.
    if 0.005 < progress < 0.995:
        stream = (sand[0], sand[1], sand[2], 210)
        draw.line(pt([(CENTER_X, NECK_Y), (CENTER_X, LOWER_BASE_Y - lower_sand_depth(progress))]),
                  fill=stream, width=max(1, round(3.0 * scale)))

    stroke = max(1, round(GLASS_STROKE * scale))
    outline = sand if finished else frame_color
    for bulb in (upper_bulb, lower_bulb):
        draw.line(pt(bulb + [bulb[0]]), fill=outline, width=stroke, joint="curve")

    draw.rounded_rectangle(box(TOP_CAP), radius=3.5 * scale, fill=frame_color)
    draw.rounded_rectangle(box(BOTTOM_CAP), radius=3.5 * scale, fill=frame_color)

    if flip_angle:
        image = flip(image, flip_angle, scale)

    if plate:
        # The plate stays put while the glass turns, so it is laid in underneath afterwards.
        backdrop = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
        ImageDraw.Draw(backdrop).rounded_rectangle(
            [0, 0, canvas - 1, canvas - 1], radius=22.0 * scale, fill=PLATE_COLOR)
        backdrop.alpha_composite(image)
        image = backdrop

    return image.resize((size, size), Image.LANCZOS)


def bmp_entry(image: Image.Image) -> bytes:
    """A 32-bit BMP icon entry: header, bottom-up BGRA pixels, then an empty AND mask."""
    width, height = image.size
    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0)

    pixels = image.load()
    rows = []
    for y in range(height - 1, -1, -1):
        row = bytearray()
        for x in range(width):
            r, g, b, a = pixels[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))

    mask_stride = ((width + 31) // 32) * 4
    mask = bytes(mask_stride * height)
    return header + b"".join(rows) + mask


def png_entry(image: Image.Image) -> bytes:
    from io import BytesIO
    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def build_ico(path: Path) -> None:
    entries = []
    for size in SIZES:
        image = render(size)
        payload = png_entry(image) if size >= PNG_FROM_SIZE else bmp_entry(image)
        entries.append((size, payload))

    offset = 6 + 16 * len(entries)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    for size, payload in entries:
        dimension = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset)
        offset += len(payload)

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(bytes(directory) + b"".join(payload for _, payload in entries))


if __name__ == "__main__":
    target = Path(__file__).resolve().parent.parent / "src" / "Timerlight" / "Assets" / "timerlight.ico"
    build_ico(target)
    print(f"wrote {target} ({target.stat().st_size} bytes)")
