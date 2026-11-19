"""Model-free tests of the real worker protocol at the provider boundary."""
import importlib.util
import io
import json
import os
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


class WarmWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="FluxOcrWarm_")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def command(self, execution_id, page_index):
        directory = self.root / execution_id
        directory.mkdir()
        (directory / "page.png").write_bytes(b"synthetic provider-boundary fixture")
        manifest = directory / "input.json"
        manifest.write_text(json.dumps({"pages": [{"pageIndex": page_index, "image": "page.png"}]}))
        return {
            "operation": "run",
            "execution_id": execution_id,
            "manifest": str(manifest),
            "output": str(directory / "output.json"),
        }

    def run_worker(self, commands, responses):
        return worker._run_warm_worker(
            io.BytesIO(b"".join(json.dumps(command).encode("utf-8") + b"\n" for command in commands)), responses)

    def test_sequential_requests_reuse_fixed_components_and_acknowledge_durable_results(self):
        first = self.command("a" * 32, 3)
        second = self.command("b" * 32, 7)
        events = []
        original_fsync = os.fsync

        def sync_result(fd):
            original_fsync(fd)
            events.append("fsync")

        root = self.root

        class Responses(io.BytesIO):
            def write(self, raw):
                response = json.loads(raw)
                document = json.loads((root / response["execution_id"] / "output.json").read_bytes())
                events.append(("ack", document["pages"][0]["pageIndex"]))
                return super().write(raw)

        responses = Responses()
        orientation = SimpleNamespace(predict=lambda image: iter([{"label_names": ["0"], "scores": [1.0]}]))
        pipeline = SimpleNamespace(predict=lambda image: iter([SimpleNamespace(json={"res": {
            "parsing_res_list": [{"block_bbox": [0, 0, 10, 10], "block_label": "text", "block_content": "OCR"}]
        }})]))
        modules = {
            "cv2": SimpleNamespace(IMREAD_COLOR=1, imread=lambda path, mode: object()),
            "paddle.device": SimpleNamespace(synchronize=lambda device: events.append(("gpu-idle", device))),
            "paddlex.inference.pipelines.components.common.warp_image": SimpleNamespace(rotate_image=lambda image, angle: image),
        }
        with (
            patch.dict(sys.modules, modules),
            patch("site.main") as initialise_packages,
            patch.object(worker, "_assert_fixed_model_paths_are_safe"),
            patch.object(worker, "_create_components", return_value=(orientation, pipeline)) as create_components,
            patch.object(worker.os, "fsync", side_effect=sync_result),
        ):
            self.assertEqual(0, self.run_worker([first, second, {"operation": "shutdown"}], responses))

        initialise_packages.assert_called_once()
        create_components.assert_called_once()
        self.assertEqual(
            [("gpu-idle", "gpu:0"), "fsync", ("ack", 3), ("gpu-idle", "gpu:0"), "fsync", ("ack", 7)], events
        )
        self.assertEqual(
            [{"execution_id": command["execution_id"], "status": "idle", "model": "ocr"} for command in [first, second]],
            [json.loads(line) for line in responses.getvalue().splitlines()],
        )

    def test_invalid_command_stream_refuses_before_reading_pages_or_loading_provider(self):
        valid = self.command("a" * 32, 3)
        rejected = [
            b" " * (64 * 1024) + b"\n",
            b'{"operation":"shutdown"}',
            b'{"operation":"run","execution_id":"a","manifest":"\xff","output":"output.json"}\n',
            b'{"operation":"run","operation":"shutdown"}\n',
            json.dumps(dict(valid, execution_id="A" * 32)).encode("utf-8") + b"\n",
            json.dumps(dict(valid, manifest="input.json")).encode("utf-8") + b"\n",
            json.dumps(dict(valid, output=str(self.root / "output.json"))).encode("utf-8") + b"\n",
            json.dumps(dict(valid, model="unexpected")).encode("utf-8") + b"\n",
        ]
        for frame in rejected:
            with self.subTest(frame=frame[:96]), patch("site.main") as initialise_packages:
                responses = io.BytesIO()
                self.assertEqual(1, worker._run_warm_worker(io.BytesIO(frame), responses))
                initialise_packages.assert_not_called()
                self.assertEqual(b"", responses.getvalue())
                self.assertFalse(Path(valid["output"]).exists())

    def test_control_limit_counts_json_bytes_before_platform_line_ending(self):
        command = b'{"operation":"shutdown"}'.ljust(16 * 1024)
        for newline in (b"\n", b"\r\n"):
            with self.subTest(newline=newline):
                self.assertEqual(0, worker._run_warm_worker(io.BytesIO(command + newline), io.BytesIO()))
                self.assertEqual(1, worker._run_warm_worker(io.BytesIO(command + b" " + newline), io.BytesIO()))


    def test_manifest_over_byte_limit_refuses_without_loading_provider(self):
        command = self.command("a" * 32, 3)
        manifest = Path(command["manifest"])
        manifest.write_bytes(manifest.read_bytes() + b" " * (512 * 1024))
        responses = io.BytesIO()

        with patch("site.main") as initialise_packages:
            self.assertEqual(1, self.run_worker([command], responses))

        initialise_packages.assert_not_called()
        self.assertEqual(b"", responses.getvalue())
        self.assertFalse(Path(command["output"]).exists())


    def test_empty_stream_and_shutdown_never_load_model(self):
        for frame in (b"", b'{"operation":"shutdown"}\n'):
            with self.subTest(frame=frame), patch("site.main") as initialise_packages:
                responses = io.BytesIO()
                self.assertEqual(0, worker._run_warm_worker(io.BytesIO(frame), responses))
                initialise_packages.assert_not_called()
                self.assertEqual(b"", responses.getvalue())

    def test_provider_failure_writes_existing_failure_and_exits_without_idle(self):
        request = self.command("a" * 32, 3)
        responses = io.BytesIO()
        with (
            patch("site.main"),
            patch.object(worker, "_assert_fixed_model_paths_are_safe"),
            patch.object(worker, "_create_components", side_effect=RuntimeError("private path")),
        ):
            self.assertEqual(1, self.run_worker([request], responses))

        self.assertEqual(b"", responses.getvalue())
        self.assertEqual(
            {"succeeded": False, "reasonCode": "document-ocr-model-initialization-failed", "pages": []},
            json.loads(Path(request["output"]).read_bytes()),
        )


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


    def test_warm_process_accepts_shutdown_and_eof_without_site_initialisation(self):
        with tempfile.TemporaryDirectory(prefix="FluxOcrWarmGate_") as directory:
            root = Path(directory)
            runtime = root / "runtime"
            venv.EnvBuilder(with_pip=False).create(runtime)
            python = runtime / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
            package_root = runtime / ("Lib/site-packages" if sys.platform == "win32" else
                f"lib/python{sys.version_info.major}.{sys.version_info.minor}/site-packages")
            sentinel = root / "hook-ran"
            (package_root / "flux_gate_sentinel.pth").write_text(
                f"import pathlib; pathlib.Path({str(sentinel)!r}).write_text('initialised')\n")
            for command in (b'{"operation":"shutdown"}\n', b""):
                with self.subTest(command=command):
                    result = subprocess.run(
                        [str(python), "-I", "-S", str(source), "--warm-worker"],
                        input=command, capture_output=True, timeout=10, check=False)
                    self.assertEqual(0, result.returncode, result.stderr.decode())
                    self.assertEqual(b"", result.stdout)
                    self.assertFalse(sentinel.exists())


    def test_warm_stdout_contains_only_idle_acknowledgement(self):
        with tempfile.TemporaryDirectory(prefix="FluxOcrWarmPipe_") as directory:
            root = Path(directory)
            runtime = root / "runtime"
            venv.EnvBuilder(with_pip=False).create(runtime)
            python = runtime / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
            package_root = runtime / ("Lib/site-packages" if sys.platform == "win32" else
                f"lib/python{sys.version_info.major}.{sys.version_info.minor}/site-packages")
            (package_root / "flux_ocr_fixture.pth").write_text("import flux_ocr_fixture\n")
            (package_root / "flux_ocr_fixture.py").write_text(
                """
import os
import sys
from types import SimpleNamespace as Stub

print("site diagnostics", flush=True)
os.write(1, b"provider descriptor diagnostics\\n")
if sys.platform == "win32":
    import ctypes
    from ctypes import wintypes

    kernel = ctypes.WinDLL("kernel32")
    kernel.GetStdHandle.argtypes = [wintypes.DWORD]
    kernel.GetStdHandle.restype = wintypes.HANDLE
    kernel.WriteFile.argtypes = [
        wintypes.HANDLE, ctypes.c_char_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID
    ]
    diagnostic = b"native standard handle diagnostics\\n"
    kernel.WriteFile(kernel.GetStdHandle(-11), diagnostic, len(diagnostic), ctypes.byref(wintypes.DWORD()), None)

sys.modules["__main__"]._create_components = lambda: (
    Stub(predict=lambda image: iter([{"label_names": ["0"], "scores": [1.0]}])),
    Stub(predict=lambda image: iter([Stub(json={"res": {"parsing_res_list": []}})])),
)
sys.modules["cv2"] = Stub(IMREAD_COLOR=1, imread=lambda path, mode: object())
sys.modules["paddle.device"] = Stub(synchronize=lambda device: None)
sys.modules["paddlex.inference.pipelines.components.common.warp_image"] = Stub(rotate_image=lambda image, angle: image)
"""
            )
            (root / "page.png").write_bytes(b"synthetic")
            manifest = root / "input.json"
            manifest.write_text(json.dumps({"pages": [{"pageIndex": 0, "image": "page.png"}]}))
            request = {
                "operation": "run", "execution_id": "a" * 32,
                "manifest": str(manifest), "output": str(root / "output.json"),
            }
            result = subprocess.run(
                [str(python), "-I", "-S", str(source), "--warm-worker"],
                input=json.dumps(request).encode("utf-8") + b'\n{"operation":"shutdown"}\n',
                capture_output=True, timeout=10, check=False)
            self.assertEqual(0, result.returncode, result.stderr.decode())
            self.assertEqual(
                [b'{"execution_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","status":"idle","model":"ocr"}'],
                result.stdout.splitlines(),
            )


if __name__ == "__main__":
    unittest.main()
