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
import generate_sprites as sprites


class TransitionSpriteTests(unittest.TestCase):
    def test_transition_frames_keep_pixel_palette_and_canvas_margins(self):
        palette = {value for key, value in vars(sprites).items() if key.startswith("C_")}
        for name in ("sleep_enter", "wake_idle", "wake_work", "pet_end_idle", "pet_end_work"):
            frames = [sprites.STATE_FUNCTIONS[name](i) for i in range(4)]
            self.assertGreaterEqual(len({f.tobytes() for f in frames}), 3, name)
            for frame in frames:
                self.assertEqual(frame.size, (32, 32))
                self.assertTrue({value for _, value in frame.getchannel("A").getcolors(1024)}.issubset({0, 255}))
                self.assertTrue({color for _, color in frame.getcolors(1024) if color[3]}.issubset(palette))
                left, top, right, bottom = frame.getchannel("A").getbbox()
                self.assertGreaterEqual(min(left, top), 1, name)
                self.assertLessEqual(max(right, bottom), 31, name)

    def test_dedicated_transition_endings_equal_canonical_poses(self):
        for name, target in [("sleep_enter", "sleep"), ("wake_idle", "idle"), ("wake_work", "work"),
                             ("pet_end_idle", "idle"), ("pet_end_work", "work")]:
            self.assertEqual(sprites.STATE_FUNCTIONS[name](3).tobytes(), sprites.resting_frame(target).tobytes(), name)


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

    def test_preview_moves_the_eye_layer_with_the_first_head_anchor(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            Image.new("RGBA", (32, 32), (220, 140, 60, 255)).save(root / "neko_idle.png")
            eyes = Image.new("RGBA", (32, 32))
            eyes.putpixel((10, 10), (20, 20, 20, 255))
            eyes.save(root / "neko_eyes_open.png")
            frame = previews.pick_idle_frame(root, {"frameWidth": 32, "frameHeight": 32,
                "states": [{"name": "idle", "eyeLayer": "eyes_open", "eyeOffsets": [{"x": 3, "y": 2}]}]})
            self.assertEqual(frame.getpixel((13, 12)), (20, 20, 20, 255))
            self.assertEqual(frame.getpixel((10, 10)), (220, 140, 60, 255))


class PixelAssetTests(unittest.TestCase):
    def test_native_pixel_has_safe_frames_and_aligned_independent_eyes(self):
        root = Path(__file__).resolve().parents[1] / "neko-assets" / "pixel"
        meta = json.loads((root / "skin.json").read_text(encoding="utf-8"))
        self.assertEqual((meta["frameWidth"], meta["frameHeight"], meta["frames"]), (32, 32, 4))
        colors = set()
        for state in meta["states"]:
            with self.subTest(state=state["name"]), Image.open(root / f"neko_{state['name']}.png") as source:
                sheet = source.convert("RGBA")
                self.assertEqual(sheet.size, (128, 32))
                frames = [sheet.crop((i * 32, 0, (i + 1) * 32, 32)) for i in range(4)]
                if state["fps"] > 0:
                    self.assertGreater(len({frame.tobytes() for frame in frames}), 1)
                for index, frame in enumerate(frames):
                    bounds = frame.getchannel("A").getbbox()
                    self.assertIsNotNone(bounds)
                    self.assertGreaterEqual(min(bounds[:2]), 1)
                    self.assertLessEqual(max(bounds[2:]), 31)
                    data = frame.tobytes()
                    pixels = [tuple(data[i:i + 4]) for i in range(0, len(data), 4)]
                    self.assertLessEqual({p[3] for p in pixels}, {0, 255})
                    colors.update(p[:3] for p in pixels if p[3])
                    if state.get("eyeLayer"):
                        with Image.open(root / f"neko_{state['eyeLayer']}.png") as eyes:
                            self.assertEqual(eyes.size, (32, 32))
                            aligned = Image.new("RGBA", (32, 32))
                            offsets = state.get("eyeOffsets", [])
                            anchor = offsets[index] if offsets else {"x": 0, "y": 0}
                            aligned.alpha_composite(eyes.convert("RGBA"), (anchor["x"], anchor["y"]))
                            self.assertTrue(all(not eye or body for eye, body in
                                                zip(aligned.tobytes()[3::4], data[3::4])))
        self.assertLessEqual(len(colors), 12)
        with Image.open(root / "neko_land.png") as landing, Image.open(root / "neko_idle.png") as idle:
            self.assertEqual(landing.convert("RGBA").crop((96, 0, 128, 32)).tobytes(),
                             idle.convert("RGBA").crop((0, 0, 32, 32)).tobytes())

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
