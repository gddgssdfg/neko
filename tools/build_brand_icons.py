"""Export the approved ImageGen mark to native Windows icon sizes. No artwork generation."""
from pathlib import Path
from io import BytesIO
import struct
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'neko-assets'
BRAND = ASSETS / 'branding'
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

def build():
    source = Image.open(BRAND / 'neko-icon-source.png').convert('RGBA')
    assert source.getextrema()[3][0] == 0, 'Source must retain transparent pixels'
    # Deliberate native-size sampling; larger icons retain the same 64px pixel grid.
    grid = source.resize((64, 64), Image.Resampling.NEAREST)
    frames = {}
    for size in SIZES:
        frame = (source if size < 64 else grid).resize((size, size), Image.Resampling.NEAREST)
        frame.save(BRAND / f'neko-{size}.png')
        frames[size] = frame
    blobs = []
    for frame in frames.values():
        stream = BytesIO(); frame.save(stream, format='PNG'); blobs.append(stream.getvalue())
    offset = 6 + 16 * len(SIZES)
    entries = []
    for size, blob in zip(SIZES, blobs):
        entries.append(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    ico = struct.pack('<HHH', 0, 1, len(SIZES)) + b''.join(entries) + b''.join(blobs)
    (ASSETS / 'app.ico').write_bytes(ico)
    (ASSETS / 'tray.ico').write_bytes(ico)
    # QA sheet contains the exact exported pixels against both shell themes.
    sheet = Image.new('RGB', (1000, 360), '#20242c')
    draw = ImageDraw.Draw(sheet)
    draw.rectangle((0, 180, 1000, 360), fill='#f3f1eb')
    for y, label, color in [(0, 'Dark shell / native icon sizes', '#ffffff'), (180, 'Light shell / native icon sizes', '#27221e')]:
        draw.text((16, y + 10), label, fill=color)
        x = 18
        for size in SIZES[:-1]:
            sheet.paste(frames[size], (x, y+40), frames[size])
            draw.text((x, y+154), str(size), fill=color)
            x += max(size + 25, 70)
    output = ROOT / '_eval' / 'brand-icons-preview.png'
    output.parent.mkdir(exist_ok=True)
    sheet.save(output)
    print('Exported app.ico and tray.ico:', ', '.join(map(str, SIZES)))

if __name__ == '__main__':
    build()
