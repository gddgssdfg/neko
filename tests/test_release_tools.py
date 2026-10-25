import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("release_packaging", ROOT / "tools/package_release.py")
packaging = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packaging)


class ReleasePackagingTests(unittest.TestCase):
    def test_staging_excludes_runtime_data_and_credentials_but_keeps_required_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            source = base / "input"
            source.mkdir()
            for relative in ["Neko.exe", "Neko.runtimeconfig.json", "plugins/tool/plugin.toml", "LICENSE",
                             "config.json", "data/notes.json", "logs/session.log", ".env.local", "signing.pfx",
                             "neko-assets/reference/source.png", "Neko.pdb"]:
                path = source / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("fixture")
            target = base / "runtime"
            packaging.stage_runtime(source, target)
            self.assertTrue((target / "Neko.exe").is_file())
            self.assertTrue((target / "Neko.runtimeconfig.json").is_file())
            self.assertTrue((target / "plugins/tool/plugin.toml").is_file())
            self.assertIn("MIT", (target / "LICENSE").read_text(encoding="utf-8"))
            for relative in ["config.json", "data/notes.json", "logs/session.log", ".env.local", "signing.pfx",
                             "neko-assets/reference/source.png", "Neko.pdb"]:
                self.assertFalse((target / relative).exists(), relative)

    def test_staging_refuses_recursive_output_and_overwriting_existing_artifacts(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "input"
            source.mkdir()
            (source / "Neko.exe").write_text("fixture")
            with self.assertRaises(ValueError):
                packaging.stage_runtime(source, source / "nested")
            existing = Path(temporary) / "existing"
            existing.mkdir()
            (existing / "keep.txt").write_text("keep")
            with self.assertRaises(FileExistsError):
                packaging.stage_runtime(source, existing)
            self.assertEqual((existing / "keep.txt").read_text(), "keep")


if __name__ == "__main__":
    unittest.main()
