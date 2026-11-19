"""Model-free probes of native speech-to-text profile selection."""
import importlib.util
from pathlib import Path
import unittest
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


if __name__ == "__main__":
    unittest.main()
