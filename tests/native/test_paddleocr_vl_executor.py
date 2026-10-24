"""Model-free tests of the real worker protocol at the provider boundary."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import subprocess
import time
import venv
from types import SimpleNamespace
import unittest
from unittest.mock import patch

source = Path(__file__).resolve().parents[2] / "src/FluxKnowledge.Integrations/Documents/paddleocr_vl_executor.py"
spec = importlib.util.spec_from_file_location("flux_ocr_worker", source)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class WorkerFailureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="FluxOcrProtocol_")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        stdin = patch.object(worker.sys, "stdin", SimpleNamespace(readline=lambda limit: "fluxknowledge-ocr-go-v1\n"))
        stdin.start()
        self.addCleanup(stdin.stop)
        site = patch("site.main")
        site.start()
        self.addCleanup(site.stop)
        (self.root / "page.png").write_bytes(b"synthetic provider-boundary fixture")
        (self.root / "input.json").write_text(json.dumps({"pages": [{"pageIndex": 0, "image": "page.png"}]}))
        self.args = ["--startup-gate", "--input", str(self.root / "input.json"), "--output", str(self.root / "output.json")]

    def run_failure(self, error, expected):
        with patch.object(worker, "_create_components", side_effect=error):
            self.assertEqual(1, worker.main(self.args))
        raw = (self.root / "output.json").read_text()
        result = json.loads(raw)
        self.assertFalse(result["succeeded"])
        self.assertEqual(expected, result["reasonCode"])
        self.assertEqual([], result["pages"])
        self.assertNotIn("private path", raw)

    def test_model_initialization_failure_is_bounded_and_contains_no_exception_detail(self):
        self.run_failure(RuntimeError("private path and provider content"), "document-ocr-model-initialization-failed")

    def test_dependency_failure_is_distinct_from_model_initialization(self):
        self.run_failure(ImportError("private path"), "document-ocr-dependency-unavailable")

    def test_offline_guard_failure_is_not_relabelled_as_corrupt_content(self):
        self.run_failure(RuntimeError("unexpected-model-download"), "document-ocr-offline-operation-refused")

    def test_recognition_failure_is_distinct_and_success_keeps_the_existing_protocol(self):
        orientation = SimpleNamespace(predict=lambda image: iter([{"label_names": ["0"], "scores": [1.0]}]))
        def fail(image):
            raise RuntimeError("private path")
        pipeline = SimpleNamespace(predict=fail)
        modules = {
            "cv2": SimpleNamespace(IMREAD_COLOR=1, imread=lambda path, mode: object()),
            "paddlex.inference.pipelines.components.common.warp_image": SimpleNamespace(rotate_image=lambda image, angle: image),
        }
        with patch.dict(sys.modules, modules), patch.object(worker, "_create_components", return_value=(orientation, pipeline)):
            self.assertEqual(1, worker.main(self.args))
            result = json.loads((self.root / "output.json").read_text())
            self.assertEqual("document-ocr-recognition-failed", result["reasonCode"])
            (self.root / "output.json").unlink()
            pipeline.predict = lambda image: iter([SimpleNamespace(json={"res": {"parsing_res_list": [
                {"block_bbox": [0, 0, 10, 10], "block_label": "text", "block_content": "Synthetic OCR"}]}})])
            self.assertEqual(0, worker.main(self.args))
        result = json.loads((self.root / "output.json").read_text())
        self.assertTrue(result["succeeded"])
        self.assertEqual("document-ocr-complete", result["reasonCode"])
        self.assertEqual("Synthetic OCR", result["pages"][0]["blocks"][0]["text"])


class StartupGateTests(unittest.TestCase):
    def test_site_hooks_cannot_execute_before_go_and_packages_are_initialised_after_go(self):
        with tempfile.TemporaryDirectory(prefix="FluxOcrGate_") as directory:
            root = Path(directory)
            runtime = root / "runtime"
            venv.EnvBuilder(with_pip=False).create(runtime)
            python = runtime / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
            package_root = runtime / ("Lib/site-packages" if sys.platform == "win32" else
                f"lib/python{sys.version_info.major}.{sys.version_info.minor}/site-packages")
            sentinel = root / "hook-ran"
            (package_root / "flux_gate_sentinel.pth").write_text(
                f"import pathlib; pathlib.Path({str(sentinel)!r}).write_text('initialised')\n")
            process = subprocess.Popen([str(python), "-I", "-S", str(source), "--startup-gate",
                "--input", str(root / "missing.json"), "--output", str(root / "output.json")],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                # A startup hook would run before the stdin wait, even without Paddle or models.
                time.sleep(1)
                self.assertIsNone(process.poll())
                self.assertFalse(sentinel.exists())
                process.communicate("fluxknowledge-ocr-go-v1\n", timeout=10)
                self.assertEqual("initialised", sentinel.read_text())
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate(timeout=10)


if __name__ == "__main__":
    unittest.main()
