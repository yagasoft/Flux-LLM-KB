"""Model-free tests of the real worker protocol at the provider boundary."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
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
        (self.root / "page.png").write_bytes(b"synthetic provider-boundary fixture")
        (self.root / "input.json").write_text(json.dumps({"pages": [{"pageIndex": 0, "image": "page.png"}]}))
        self.args = ["--input", str(self.root / "input.json"), "--output", str(self.root / "output.json")]

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


if __name__ == "__main__":
    unittest.main()
