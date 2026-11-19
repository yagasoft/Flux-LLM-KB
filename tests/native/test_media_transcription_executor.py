"""Model-free probes of native speech-to-text and its worker protocol."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
import unittest
import weakref
from types import SimpleNamespace
from unittest.mock import patch

source = Path(__file__).resolve().parents[2] / "src/FluxKnowledge.Integrations/Media/media_transcription_executor.py"
spec = importlib.util.spec_from_file_location("flux_transcription_worker", source)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class TranscriptionProfileTests(unittest.TestCase):
    def test_arabic_with_english_selects_the_established_code_switched_profile(self):
        self.assertEqual(("egyptian-ar-en", "ar", "arabic-english"),
                         worker.select_profile([("en", .96), ("ar", .93), ("en", .98)]))

    def test_arabic_audio_can_select_the_profile_without_claiming_a_dialect(self):
        self.assertEqual(("egyptian-ar-en", "ar", "arabic-speech"),
                         worker.select_profile([("ar", .96)]))

    def test_english_or_another_language_keeps_general_decoding(self):
        self.assertEqual(("general", None, "other-language"),
                         worker.select_profile([("en", .96), ("fr", .84)]))

    def test_low_confidence_or_invalid_detection_remains_general(self):
        self.assertEqual(("general", None, "language-unconfirmed"),
                         worker.select_profile([("ar", .53), ("ar", float("nan")), ("ar", 1.1)]))

    def test_arabic_with_another_strong_language_uses_the_multilingual_model(self):
        self.assertEqual(("general", None, "other-language"),
                         worker.select_profile([("ar", .97), ("fr", .92)]))

    def test_startup_gate_refuses_before_opening_private_inputs(self):
        with patch.object(worker, "decode_bounded_audio") as decode, patch(
            "sys.argv", ["worker", "--manifest", "never-read.json", "--output", "never-create.json"]
        ), patch("sys.stdin", SimpleNamespace(readline=lambda _: "")):
            self.assertEqual(73, worker.main())
            decode.assert_not_called()

    def test_decoder_receives_arabic_hint_and_preserves_timed_code_switched_text(self):
        class Audio:
            def __len__(self):
                return 16_000

            def __getitem__(self, _):
                return self

            def __abs__(self):
                return self

            def max(self):
                return .5

        opened = []
        decoding = []

        def run(_, **settings):
            decoding.append(settings)
            return [SimpleNamespace(start=.1, end=.9, text="اختبار with English")], SimpleNamespace(
                language="ar", language_probability=1.0)

        def open_model(profile):
            opened.append(profile)
            return SimpleNamespace(detect_language=lambda **_: ("ar", .93, ()), transcribe=run)

        result = worker.transcribe(Audio(), open_model)
        self.assertEqual(["general", "egyptian-ar-en"], opened)
        self.assertEqual(("ar", "transcribe", True, False),
                         tuple(decoding[0][key] for key in ("language", "task", "word_timestamps",
                                                              "condition_on_previous_text")))
        self.assertEqual(.93, result["languageConfidence"])
        self.assertEqual([dict(startMillis=100, endMillis=900, text="اختبار with English")], result["cues"])


class WarmTranscriptionTests(unittest.TestCase):
    class Audio:
        def __init__(self, language):
            self.language = language

        def __len__(self):
            return 16_000

        def __getitem__(self, _):
            return self

        def __abs__(self):
            return self

        def max(self):
            return .5

    class Model:
        def __init__(self, profile, lifecycle):
            self.profile = profile
            self.lifecycle = lifecycle
            lifecycle.append(("load", profile))

        def __del__(self):
            self.lifecycle.append(("release", self.profile))

        def detect_language(self, *, audio, vad_filter):
            return audio.language, .97, ()

        def transcribe(self, audio, **settings):
            text = "اختبار with English" if settings["language"] == "ar" else "General speech"
            segments = iter([SimpleNamespace(start=.1, end=.9, text=text)])
            return segments, SimpleNamespace(language=audio.language, language_probability=.97)

    def setUp(self):
        self.directory = Path(self.enterContext(TemporaryDirectory(dir=Path(__file__).resolve().parents[2])))
        self.lifecycle = []
        self.enterContext(patch.object(worker, "resolve_cached_configuration", side_effect=lambda config: (config,)))
        self.enterContext(patch.object(worker, "create_cached_model_loader",
                                       return_value=lambda profile: self.Model(profile, self.lifecycle)))
        self.enterContext(patch.object(worker, "decode_bounded_audio",
                                       side_effect=lambda path: self.Audio(path.read_text(encoding="utf-8"))))

    def request(self, number, language="en", configuration=None):
        directory = self.directory / f"recording-س-{number}"
        directory.mkdir()
        manifest = directory / "runtime.json"
        manifest.write_text(json.dumps(configuration or {"pinned": "revision-1"}), encoding="utf-8")
        (directory / "input.media").write_text(language, encoding="utf-8")
        output = directory / "output.json"
        execution_id = f"{number:032x}"
        frame = json.dumps(dict(operation="run", execution_id=execution_id, manifest=str(manifest),
                                output=str(output)), ensure_ascii=False).encode("utf-8") + b"\n"
        return frame, output, execution_id

    def test_worker_reuses_general_and_releases_before_profile_changes(self):
        first, first_output, first_id = self.request(1)
        second, second_output, second_id = self.request(2, "ar")
        third, third_output, third_id = self.request(3)
        replies = io.BytesIO()

        self.assertEqual(0, worker.run_warm_worker(
            io.BytesIO(first + second + third + b'{"operation":"shutdown"}\n'), replies))

        self.assertEqual([
            {"execution_id": first_id, "status": "idle", "model": "general"},
            {"execution_id": second_id, "status": "idle", "model": "egyptian-ar-en"},
            {"execution_id": third_id, "status": "idle", "model": "general"},
        ], [json.loads(line) for line in replies.getvalue().splitlines()])
        self.assertEqual([("load", "general"), ("release", "general"),
                          ("load", "egyptian-ar-en"), ("release", "egyptian-ar-en"),
                          ("load", "general"), ("release", "general")], self.lifecycle)
        self.assertEqual(["General speech", "اختبار with English", "General speech"], [
            json.loads(path.read_text(encoding="utf-8"))["result"]["cues"][0]["text"]
            for path in [first_output, second_output, third_output]
        ])

    def test_worker_rejects_changed_runtime_identity_and_drops_resident_model(self):
        first, _, _ = self.request(1)
        second, output, execution_id = self.request(2, configuration={"pinned": "revision-2"})
        replies = io.BytesIO()

        self.assertEqual(0, worker.run_warm_worker(io.BytesIO(first + second), replies))

        self.assertEqual({"execution_id": execution_id, "status": "idle", "model": None},
                         json.loads(replies.getvalue().splitlines()[-1]))
        self.assertEqual({"succeeded": False, "reasonCode": "transcription-runtime-unavailable"},
                         json.loads(output.read_text(encoding="utf-8")))
        self.assertEqual([("load", "general"), ("release", "general")], self.lifecycle)

    def test_worker_requires_complete_bounded_run_frames_before_private_input_access(self):
        valid, output, _ = self.request(1)
        request = json.loads(valid)
        for frame in [
            valid.rstrip(b"\n"),
            b"\xff\n",
            b" " * (64 * 1024) + valid,
            b'{"operation":"run","operation":"shutdown"}\n',
            json.dumps({**request, "execution_id": "invalid"}).encode("utf-8") + b"\n",
            json.dumps({**request, "manifest": "runtime.json"}).encode("utf-8") + b"\n",
            json.dumps({**request, "output": str(self.directory / "output.json")}).encode("utf-8") + b"\n",
        ]:
            with self.subTest(frame=frame[:160]):
                replies = io.BytesIO()
                self.assertEqual(73, worker.run_warm_worker(io.BytesIO(frame), replies))
                self.assertEqual(b"", replies.getvalue())
                self.assertFalse(output.exists())
        self.assertEqual([], self.lifecycle)

    def test_control_size_limit_applies_to_json_bytes_before_the_line_ending(self):
        valid, _, execution_id = self.request(1)
        raw = valid.rstrip(b"\n")
        full = raw.rjust(16 * 1024, b" ")
        replies = io.BytesIO()

        self.assertEqual(0, worker.run_warm_worker(io.BytesIO(full + b"\r\n"), replies))
        self.assertEqual(execution_id, json.loads(replies.getvalue())["execution_id"])

    def test_worker_never_acknowledges_a_result_that_cannot_be_fsynced(self):
        request, _, _ = self.request(1)
        replies = io.BytesIO()
        with patch("os.fsync", side_effect=OSError("synthetic disk error")):
            self.assertEqual(74, worker.run_warm_worker(io.BytesIO(request), replies))
        self.assertEqual(b"", replies.getvalue())
        self.assertEqual([("load", "general"), ("release", "general")], self.lifecycle)

    def test_worker_converts_oversized_serialized_output_to_bounded_refusal(self):
        request, output, execution_id = self.request(1)
        replies = io.BytesIO()
        oversized_result = {"data": "x" * (8 * 1024 * 1024)}
        with patch.object(worker, "transcribe", return_value=oversized_result):
            self.assertEqual(0, worker.run_warm_worker(io.BytesIO(request), replies))
        self.assertEqual({"execution_id": execution_id, "status": "idle", "model": None},
                         json.loads(replies.getvalue()))
        self.assertEqual({"succeeded": False, "reasonCode": "transcription-output-too-large"},
                         json.loads(output.read_text(encoding="utf-8")))


    def test_worker_releases_transcript_before_acknowledging_idle(self):
        request, _, _ = self.request(1)
        weak_results = []

        class Result(dict):
            pass

        def transcribe(*_):
            result = Result(text="synthetic private transcript")
            weak_results.append(weakref.ref(result))
            return result

        class Replies(io.BytesIO):
            def write(self, reply):
                if weak_results[0]() is not None:
                    raise AssertionError("An idle worker still owns the transcript")
                return super().write(reply)

        with patch.object(worker, "transcribe", side_effect=transcribe):
            self.assertEqual(0, worker.run_warm_worker(io.BytesIO(request), Replies()))

    def test_existing_output_is_preserved_without_running_inference(self):
        request, output, _ = self.request(1)
        output.write_bytes(b"existing result")
        replies = io.BytesIO()

        self.assertEqual(74, worker.run_warm_worker(io.BytesIO(request), replies))
        self.assertEqual(b"existing result", output.read_bytes())
        self.assertEqual(b"", replies.getvalue())
        self.assertEqual([], self.lifecycle)

    def test_warm_cli_keeps_provider_diagnostics_out_of_protocol_stdout(self):
        request, output, execution_id = self.request(1)
        script = """
import os
import runpy
import sys

main = runpy.run_path(sys.argv.pop(1))["main"]

def recognise(*args, **kwargs):
    print("python provider log", flush=True)
    os.write(1, b"native provider log" + bytes([10]))
    return dict(succeeded=False, reasonCode="transcription-no-speech-found")

main.__globals__["run_transcription"] = recognise
raise SystemExit(main())
"""
        process = subprocess.run([sys.executable, "-I", "-S", "-B", "-c", script, str(source), "--warm-worker"],
                                 input=request.rstrip(b"\n") + b"\r\n", capture_output=True, timeout=10)

        self.assertEqual(0, process.returncode, process.stderr)
        self.assertIn(b"python provider log", process.stderr)
        self.assertIn(b"native provider log", process.stderr)
        self.assertEqual({"execution_id": execution_id, "status": "idle", "model": None}, json.loads(process.stdout))
        self.assertEqual({"succeeded": False, "reasonCode": "transcription-no-speech-found"},
                         json.loads(output.read_text(encoding="utf-8")))

    def test_warm_cli_can_shut_down_before_initializing_the_runtime(self):
        process = subprocess.run([sys.executable, str(source), "--warm-worker"],
                                 input=b'{"operation":"shutdown"}\n', capture_output=True, timeout=10)
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual(b"", process.stdout)


if __name__ == "__main__":
    unittest.main()
