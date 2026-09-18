#!/usr/bin/env python3
"""Renders docs/preview.png - the tray icon across one hour, on both taskbar themes.

The drawing code is imported from generate_icon.py, so the preview always shows the
shape that HourglassIconRenderer.cs actually paints.

Usage: python3 tools/generate_preview.py
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate_icon import render  # noqa: E402

TRAY_SIZE = 24          # what Windows 11 asks for at 125% scaling
MAGNIFICATION = 3
TARGET_MINUTES = 60

DARK_TASKBAR = (32, 32, 32)
LIGHT_TASKBAR = (243, 243, 243)
DARK_FRAME = (58, 58, 62, 255)     # glass colour used on a light taskbar
LIGHT_FRAME = (238, 240, 244, 255) # glass colour used on a dark taskbar

# The last column is the dim half of the blink, so both blink frames are visible.
COLUMNS = [
    (0.0, "0 мин", False, 1.0),
    (0.2, "12 мин", False, 1.0),
    (0.4, "24 мин", False, 1.0),
    (0.6, "36 мин", False, 1.0),
    (0.8, "48 мин", False, 1.0),
    (1.0, "60 мин", True, 1.0),
    (1.0, "мигает", True, 0.22),
]

CELL = 96
CAPTION_HEIGHT = 26
PANEL_LABEL_HEIGHT = 22
PADDING = 14


def load_font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    for candidate in (
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
    ):
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    return ImageFont.load_default(size)


def tray_icon(progress: float, finished: bool, opacity: float, frame: tuple[int, int, int, int]) -> Image.Image:
    icon = render(TRAY_SIZE, progress=progress, plate=False, frame_color=frame, finished=finished)
    if opacity < 1.0:
        alpha = icon.getchannel("A").point(lambda value: round(value * opacity))
        icon.putalpha(alpha)
    return icon.resize(
        (TRAY_SIZE * MAGNIFICATION, TRAY_SIZE * MAGNIFICATION), Image.NEAREST
    )


def draw_panel(image: Image.Image, top: int, background: tuple[int, int, int],
               frame: tuple[int, int, int, int], label: str, font, caption_font) -> None:
    draw = ImageDraw.Draw(image)
    height = PANEL_LABEL_HEIGHT + CELL + CAPTION_HEIGHT
    width = image.width
    draw.rectangle([0, top, width, top + height], fill=background)

    text_color = (240, 240, 240) if sum(background) < 384 else (40, 40, 40)
    muted_color = (170, 170, 170) if sum(background) < 384 else (105, 105, 105)
    draw.text((PADDING, top + 5), label, font=font, fill=text_color)

    for index, (progress, caption, finished, opacity) in enumerate(COLUMNS):
        icon = tray_icon(progress, finished, opacity, frame)
        cell_left = PADDING + index * CELL
        x = cell_left + (CELL - icon.width) // 2
        y = top + PANEL_LABEL_HEIGHT + (CELL - icon.height) // 2
        image.paste(icon, (x, y), icon)

        text_width = draw.textlength(caption, font=caption_font)
        draw.text(
            (cell_left + (CELL - text_width) / 2, top + PANEL_LABEL_HEIGHT + CELL + 3),
            caption,
            font=caption_font,
            fill=muted_color,
        )


def main() -> None:
    font = load_font(14)
    caption_font = load_font(13)

    panel_height = PANEL_LABEL_HEIGHT + CELL + CAPTION_HEIGHT
    width = PADDING * 2 + CELL * len(COLUMNS)
    image = Image.new("RGB", (width, panel_height * 2), DARK_TASKBAR)

    draw_panel(image, 0, DARK_TASKBAR, LIGHT_FRAME, "Тёмная панель задач", font, caption_font)
    draw_panel(image, panel_height, LIGHT_TASKBAR, DARK_FRAME, "Светлая панель задач", font, caption_font)

    target = Path(__file__).resolve().parent.parent / "docs" / "preview.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    image.save(target, optimize=True)
    print(f"wrote {target} ({target.stat().st_size} bytes, {image.width}x{image.height})")


if __name__ == "__main__":
    main()
