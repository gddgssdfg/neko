"""Regression checks for the offline sprite/preview compilers (no image generation)."""
from pathlib import Path
import sys
import tempfile
import unittest
import json

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import gen_skin_previews as previews
from ai_sheet_to_skin import pixelize


class PreviewTests(unittest.TestCase):
    def test_file_layout_keeps_the_full_high_resolution_frame(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = Image.new("RGBA", (128, 128))
            ImageDraw.Draw(image).rectangle((40, 40, 110, 110), fill=(255, 128, 0, 255))
            image.save(root / "neko_idle_0.png")
            frame = previews.pick_idle_frame(root, {"layout": "files", "frameWidth": 32, "frameHeight": 32})
            self.assertEqual(frame.size, (128, 128))
            self.assertIsNotNone(previews.build_preview(root, {"layout": "files", "frameWidth": 32, "frameHeight": 32}))

    def test_small_pixel_sprite_fills_thumbnail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sprite = Image.new("RGBA", (32, 32))
            ImageDraw.Draw(sprite).rectangle((8, 6, 23, 29), fill=(240, 150, 60, 255))
            sprite.save(root / "neko_idle.png")
            preview = previews.build_preview(root, {"frameWidth": 32, "frameHeight": 32})
            bounds = preview.getchannel("A").getbbox()
            self.assertGreaterEqual(bounds[3] - bounds[1], 72)

    def test_preview_includes_the_idle_eye_layer(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            Image.new("RGBA", (32, 32), (220, 140, 60, 255)).save(root / "neko_idle.png")
            eyes = Image.new("RGBA", (32, 32))
            eyes.putpixel((10, 10), (20, 20, 20, 255))
            eyes.save(root / "neko_eyes_open.png")
            frame = previews.pick_idle_frame(root, {"frameWidth": 32, "frameHeight": 32,
                "states": [{"name": "idle", "eyeLayer": "eyes_open"}]})
            self.assertEqual(frame.getpixel((10, 10)), (20, 20, 20, 255))

    def test_individual_frame_layout_uses_idle_zero(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            Image.new("RGBA", (16, 16), (255, 0, 0, 255)).save(root / "neko_climb_0.png")
            Image.new("RGBA", (16, 16), (0, 255, 0, 255)).save(root / "neko_idle_0.png")
            frame = previews.pick_idle_frame(root, {"layout": "files", "frameWidth": 16, "frameHeight": 16})
            self.assertEqual(frame.getpixel((0, 0)), (0, 255, 0, 255))


class PixelAssetTests(unittest.TestCase):
    def test_invisible_colors_do_not_consume_the_palette(self):
        image = Image.new("RGBA", (64, 64), (0, 0, 255, 0))
        image.putpixel((32, 32), (255, 128, 0, 255))
        output = pixelize(image, 1)
        self.assertEqual(output.getpixel((32, 32)), (255, 128, 0, 255))
        self.assertEqual(output.getpixel((0, 0))[3], 0)

    def test_pixel_redraw_has_a_shared_16_color_palette_and_safe_animation_margins(self):
        root = Path(__file__).resolve().parents[1] / "neko-assets" / "pixel_redraw"
        meta = json.loads((root / "skin.json").read_text(encoding="utf-8"))
        self.assertTrue(meta["pixelArt"])
        self.assertEqual((meta["frameWidth"], meta["frameHeight"], meta["frames"]), (64, 64, 4))
        self.assertEqual({s["name"] for s in meta["states"]},
                         {"idle", "walk", "sit", "sleep", "pet", "drag", "fall", "climb", "work"})
        colors = set()
        for state in meta["states"]:
            with self.subTest(state=state["name"]), Image.open(root / f"neko_{state['name']}.png") as source:
                sheet = source.convert("RGBA")
                self.assertEqual(sheet.size, (256, 64))
                data = sheet.tobytes()
                self.assertLessEqual(set(data[3::4]), {0, 255})
                colors.update(tuple(data[i:i + 3]) for i in range(0, len(data), 4) if data[i + 3])
                frames = [sheet.crop((i * 64, 0, (i + 1) * 64, 64)) for i in range(4)]
                for frame in frames:
                    bounds = frame.getchannel("A").getbbox()
                    self.assertIsNotNone(bounds)
                    self.assertGreater(min(bounds[:2]), 0, "sprite touches top/left boundary")
                    self.assertLess(max(bounds[2:]), 64, "sprite touches right/bottom boundary")
                if state["fps"] > 0:
                    self.assertGreater(len({frame.tobytes() for frame in frames}), 1)
        self.assertLessEqual(len(colors), 16)


if __name__ == "__main__":
    unittest.main()
