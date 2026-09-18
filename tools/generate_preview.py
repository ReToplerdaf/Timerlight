#!/usr/bin/env python3
"""Renders docs/preview.png - what the tray icon does over one hour.

The drawing code is imported from generate_icon.py, so the preview always shows the shape
that HourglassIconRenderer.cs actually paints. The top two panels show the same moments on
the two taskbar themes: the colour marches steadily across the hour while the sand runs its
own ten-minute pour. The bottom panel is the turn-over that separates two pours.

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
SEGMENT_MINUTES = 10

DARK_TASKBAR = (32, 32, 32)
LIGHT_TASKBAR = (243, 243, 243)

# Minutes chosen so the sand level and the colour never coincide - they are separate clocks.
MINUTE_COLUMNS = [0, 5, 14, 27, 43, 60]
FLIP_ANGLES = [0, 30, 60, 90, 120, 150, 180]

CELL = 96
CAPTION_HEIGHT = 26
PANEL_LABEL_HEIGHT = 22
PADDING = 14
COLUMN_COUNT = max(len(MINUTE_COLUMNS) + 1, len(FLIP_ANGLES))


def load_font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    for candidate in (
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
    ):
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    return ImageFont.load_default(size)


def magnify(icon: Image.Image, opacity: float = 1.0) -> Image.Image:
    if opacity < 1.0:
        alpha = icon.getchannel("A").point(lambda value: round(value * opacity))
        icon = icon.copy()
        icon.putalpha(alpha)
    size = TRAY_SIZE * MAGNIFICATION
    return icon.resize((size, size), Image.NEAREST)


def minute_frames(light_background: bool) -> list[tuple[Image.Image, str]]:
    """The icon at each sample minute, plus the dim half of the blink at the end."""
    frames = []
    for minutes in MINUTE_COLUMNS:
        finished = minutes >= TARGET_MINUTES
        sand = 1.0 if finished else (minutes % SEGMENT_MINUTES) / SEGMENT_MINUTES
        color = min(1.0, minutes / TARGET_MINUTES)
        icon = render(TRAY_SIZE, progress=sand, color_progress=color,
                      plate=False, light_background=light_background, finished=finished)
        frames.append((magnify(icon), f"{minutes} мин"))

    blinking = render(TRAY_SIZE, progress=1.0, color_progress=1.0,
                      plate=False, light_background=light_background, finished=True)
    frames.append((magnify(blinking, opacity=0.22), "мигает"))
    return frames


def flip_frames(light_background: bool) -> list[tuple[Image.Image, str]]:
    """The turn-over: drained glass swinging round to become a fresh pour."""
    color = SEGMENT_MINUTES / TARGET_MINUTES
    frames = []
    for index, angle in enumerate(FLIP_ANGLES):
        icon = render(TRAY_SIZE, progress=1.0, color_progress=color,
                      flip_angle=float(angle), plate=False, light_background=light_background)
        if index == 0:
            caption = "песок внизу"
        elif index == len(FLIP_ANGLES) - 1:
            caption = "и снова вверху"
        else:
            caption = ""
        frames.append((magnify(icon), caption))
    return frames


def draw_panel(image: Image.Image, top: int, background: tuple[int, int, int],
               label: str, frames: list[tuple[Image.Image, str]], font, caption_font) -> None:
    draw = ImageDraw.Draw(image)
    height = PANEL_LABEL_HEIGHT + CELL + CAPTION_HEIGHT
    draw.rectangle([0, top, image.width, top + height], fill=background)

    text_color = (240, 240, 240) if sum(background) < 384 else (40, 40, 40)
    muted_color = (170, 170, 170) if sum(background) < 384 else (105, 105, 105)
    draw.text((PADDING, top + 5), label, font=font, fill=text_color)

    for index, (icon, caption) in enumerate(frames):
        cell_left = PADDING + index * CELL
        x = cell_left + (CELL - icon.width) // 2
        y = top + PANEL_LABEL_HEIGHT + (CELL - icon.height) // 2
        image.paste(icon, (x, y), icon)

        if caption:
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
    width = PADDING * 2 + CELL * COLUMN_COUNT
    image = Image.new("RGB", (width, panel_height * 3), DARK_TASKBAR)

    draw_panel(image, 0, DARK_TASKBAR,
               "Цвет — за весь час, песок — за десять минут (тёмная панель задач)",
               minute_frames(light_background=False), font, caption_font)
    draw_panel(image, panel_height, LIGHT_TASKBAR,
               "То же самое на светлой панели задач",
               minute_frames(light_background=True), font, caption_font)
    draw_panel(image, panel_height * 2, DARK_TASKBAR,
               "Переворот: каждые 10 минут, полсекунды",
               flip_frames(light_background=False), font, caption_font)

    target = Path(__file__).resolve().parent.parent / "docs" / "preview.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    image.save(target, optimize=True)
    print(f"wrote {target} ({target.stat().st_size} bytes, {image.width}x{image.height})")


if __name__ == "__main__":
    main()
