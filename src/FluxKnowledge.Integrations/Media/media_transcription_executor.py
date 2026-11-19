"""Native FluxKnowledge speech recognition. The supervisor owns its start and exit."""
import gc
import json
import math
import os
from pathlib import Path
import re
import sys

PROFILE_GENERAL = "general"
PROFILE_EGYPTIAN = "egyptian-ar-en"
PROFILE_THRESHOLD = 0.80
MAXIMUM_CONTROL_BYTES = 16 * 1024
MAXIMUM_MANIFEST_BYTES = 64 * 1024
MAXIMUM_RESULT_BYTES = 8 * 1024 * 1024


def select_profile(probes):
    """Select a decoder from language evidence. Whisper does not identify dialect."""
    confident = {language for language, probability in probes
                 if math.isfinite(probability) and PROFILE_THRESHOLD <= probability <= 1}
    if "ar" in confident and confident <= {"ar", "en"}:
        return PROFILE_EGYPTIAN, "ar", "arabic-english" if "en" in confident else "arabic-speech"
    return PROFILE_GENERAL, None, "other-language" if confident else "language-unconfirmed"

def decode_bounded_audio(path):
    import av
    import io
    import numpy as np

    sample_rate = 16_000
    maximum_samples = sample_rate * 60 * 120
    buffer = io.BytesIO()
    sample_count = 0
    resampler = av.audio.resampler.AudioResampler(format="s16", layout="mono", rate=sample_rate)

    def append(frames):
        nonlocal sample_count
        for frame in frames:
            sample_count += frame.samples
            if sample_count > maximum_samples:
                raise WorkRefused("transcription-recording-too-long")
            buffer.write(frame.to_ndarray().tobytes())

    def refuse_auxiliary_input(*_):
        raise WorkRefused("transcription-external-media-refused")

    with path.open("rb") as source, av.open(source, mode="r", options={"protocol_whitelist": "pipe"},
                                             io_open=refuse_auxiliary_input) as container:
        if not container.streams.audio:
            raise WorkRefused("transcription-no-audio-track")
        for frame in container.decode(audio=0):
            append(resampler.resample(frame))
        append(resampler.resample(None))
    if sample_count == 0:
        raise WorkRefused("transcription-no-audio-track")
    return np.frombuffer(buffer.getbuffer(), dtype=np.int16).astype(np.float32) / 32768


class WorkRefused(Exception):
    """Stable outcome code safe to return without input details."""


def transcribe(audio, open_model):
    sample_rate = 16_000
    sample_length = 30 * sample_rate
    end = max(0, len(audio) - sample_length)
    offsets = sorted({0, end // 4, end // 2, 3 * end // 4, end})
    detector = open_model(PROFILE_GENERAL)
    evidence = []
    for offset in offsets:
        window = audio[offset:offset + sample_length]
        # This avoids choosing a profile from silent padding. VAD guards speech-like noise.
        if abs(window).max() < .002:
            continue
        language, probability, _ = detector.detect_language(audio=window, vad_filter=True)
        if math.isfinite(probability) and 0 <= probability <= 1:
            evidence.append(dict(language=language, confidence=round(probability, 4)))
    profile, hint, rationale = select_profile((item["language"], item["confidence"]) for item in evidence)
    if profile == PROFILE_GENERAL:
        model = detector
    else:
        del detector
        gc.collect()
        model = open_model(profile)

    segments, info = model.transcribe(
        audio, language=hint, task="transcribe", beam_size=5, best_of=5, chunk_length=30,
        vad_filter=True, word_timestamps=True, condition_on_previous_text=False,
        vad_parameters=dict(min_silence_duration_ms=500, speech_pad_ms=300),
        temperature=(0.0, 0.2, 0.4, 0.6, 0.8, 1.0))
    cues = []
    total_characters = 0
    duration_millis = round(len(audio) * 1000 / sample_rate)
    for segment in segments:
        text = segment.text.strip()
        start = round(segment.start * 1000)
        end = round(segment.end * 1000)
        if not text or end <= start:
            continue
        total_characters += len(text)
        if len(cues) >= 10_000 or total_characters > 1_000_000:
            raise WorkRefused("transcription-output-too-large")
        if start < 0 or end > duration_millis + 100 or start > duration_millis:
            raise WorkRefused("transcription-output-invalid")
        cues.append(dict(startMillis=start, endMillis=min(end, duration_millis), text=text))
    if not cues:
        raise WorkRefused("transcription-no-speech-found")
    confidence = info.language_probability if hint is None else max(
        item["confidence"] for item in evidence if item["language"] == hint)
    return dict(profile=profile, selectionReason=rationale, language=info.language,
                languageConfidence=round(confidence, 4), languageEvidence=evidence,
                durationMillis=duration_millis, cues=cues)


def resolve_cached_configuration(configuration):
    declared_cache = Path(r"J:\Models")
    cache = declared_cache.resolve(strict=True)
    if cache != declared_cache:
        raise WorkRefused("transcription-cache-outside-approved-root")

    def inside_cache(value):
        path = Path(value).resolve(strict=True)
        if not path.is_relative_to(cache):
            raise WorkRefused("transcription-cache-outside-approved-root")
        return path

    packages = inside_cache(configuration["packages"])
    models = {
        PROFILE_GENERAL: inside_cache(configuration["generalModel"]),
        PROFILE_EGYPTIAN: inside_cache(configuration["egyptianModel"]),
    }
    for model in models.values():
        # faster-whisper falls back to tokenizer acquisition when this file is missing.
        for name in ("model.bin", "config.json", "preprocessor_config.json", "tokenizer.json", "vocabulary.json"):
            if not inside_cache(model / name).is_file():
                raise WorkRefused("transcription-model-assets-unavailable")

    dll_directories = [str(inside_cache(directory)) for directory in configuration["dllDirectories"]]
    return cache, packages, models, dll_directories


def create_cached_model_loader(cache, packages, models, dll_directories):
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HOME"] = str(cache / "provider-cache" / "huggingface")
    dll_handles = [os.add_dll_directory(directory) for directory in dll_directories]
    windows = Path(os.environ["SystemRoot"])
    os.environ["PATH"] = os.pathsep.join(dll_directories + [str(windows / "System32"), str(windows)])
    sys.path.insert(0, str(packages))
    from faster_whisper import WhisperModel

    def open_model(profile):
        # Retain DLL search handles until both detector and selected recogniser are finished.
        if not dll_handles:
            raise WorkRefused("transcription-runtime-unavailable")
        directory = models[profile]
        if not directory.is_dir() or not (directory / "tokenizer.json").is_file():
            raise WorkRefused("transcription-model-assets-unavailable")
        return WhisperModel(str(directory), device="cuda", compute_type="int8_float16",
                            local_files_only=True, cpu_threads=2)

    return open_model


def open_cached_models(configuration):
    return create_cached_model_loader(*resolve_cached_configuration(configuration))


class ResidentModel:
    """Keep one decoder from the exact runtime pinned for this process."""

    def __init__(self):
        self.configuration = None
        self.open_model = None
        self.profile = None
        self.model = None

    def bind(self, configuration):
        # Recheck the local assets before each run. Python native imports cannot be rebound.
        resolved = resolve_cached_configuration(configuration)
        if self.configuration is None:
            self.configuration = resolved
        elif resolved != self.configuration:
            raise WorkRefused("transcription-runtime-unavailable")
        if self.open_model is None:
            self.open_model = create_cached_model_loader(*resolved)
        return self

    def __call__(self, profile):
        if self.profile != profile:
            self.release()
            self.model = self.open_model(profile)
            self.profile = profile
        return self.model

    def release(self):
        if self.model is not None:
            self.model = None
            self.profile = None
            gc.collect()


def run_transcription(manifest, open_models, warm=False):
    try:
        if warm:
            with manifest.open("rb") as stream:
                data = stream.read(MAXIMUM_MANIFEST_BYTES + 1)
            if len(data) > MAXIMUM_MANIFEST_BYTES:
                raise WorkRefused("transcription-runtime-unavailable")
            configuration = json.loads(data.decode("utf-8"))
        else:
            with manifest.open(encoding="utf-8") as stream:
                configuration = json.load(stream)
        source = manifest.parent / "input.media"
        open_model = open_models(configuration)
        audio = decode_bounded_audio(source)
        result = transcribe(audio, open_model)
        return dict(succeeded=True, result=result)
    except WorkRefused as error:
        return dict(succeeded=False, reasonCode=str(error))
    except (ImportError, OSError, KeyError, TypeError):
        return dict(succeeded=False, reasonCode="transcription-runtime-unavailable")
    except Exception:
        return dict(succeeded=False, reasonCode="transcription-recognition-failed")


def unique_object(pairs):
    result = dict(pairs)
    if len(result) != len(pairs):
        raise ValueError("Duplicate control field")
    return result


def control_path(value, name):
    if not isinstance(value, str) or "\x00" in value:
        raise ValueError("Invalid control path")
    path = Path(value)
    if (not path.is_absolute() or path.name != name or ".." in path.parts or
            (os.name == "nt" and len(path.drive) != 2)):
        raise ValueError("Invalid control path")
    return path


def parse_control_frame(line):
    if not line.endswith(b"\n"):
        raise ValueError("Incomplete control frame")
    payload = line[:-2] if line.endswith(b"\r\n") else line[:-1]
    if len(payload) > MAXIMUM_CONTROL_BYTES:
        raise ValueError("Oversized control frame")
    request = json.loads(payload.decode("utf-8"), object_pairs_hook=unique_object)
    if request == {"operation": "shutdown"}:
        return None
    if (not isinstance(request, dict) or request.keys() != {"operation", "execution_id", "manifest", "output"}
            or request["operation"] != "run"):
        raise ValueError("Invalid control request")
    execution_id = request["execution_id"]
    if not isinstance(execution_id, str) or not re.fullmatch(r"[0-9a-f]{32}", execution_id):
        raise ValueError("Invalid execution identity")
    manifest = control_path(request["manifest"], "runtime.json")
    output = control_path(request["output"], "output.json")
    if manifest.parent != output.parent:
        raise ValueError("Mismatched control paths")
    return execution_id, manifest, output


def encode_result(result):
    return json.dumps(result, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")


def write_warm_result(manifest, output, resident):
    with output.open("xb") as stream:
        result = run_transcription(manifest, resident.bind, warm=True)
        try:
            payload = encode_result(result)
        except (ValueError, TypeError):
            result = dict(succeeded=False, reasonCode="transcription-output-invalid")
        else:
            if len(payload) > MAXIMUM_RESULT_BYTES:
                result = dict(succeeded=False, reasonCode="transcription-output-too-large")
        if not result["succeeded"]:
            resident.release()
            payload = encode_result(result)
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())


def run_warm_worker(control, replies):
    # The supervisor establishes native containment before sending the first request.
    resident = ResidentModel()
    try:
        while line := control.readline(MAXIMUM_CONTROL_BYTES + len(b"\r\n")):
            try:
                request = parse_control_frame(line)
            except (ValueError, RecursionError):
                return 73
            if request is None:
                break
            execution_id, manifest, output = request
            write_warm_result(manifest, output, resident)
            replies.write(encode_result(dict(execution_id=execution_id, status="idle", model=resident.profile)) + b"\n")
            replies.flush()
        return 0
    except OSError:
        return 74
    finally:
        resident.release()


def main():
    import argparse

    if sys.argv[1:] == ["--warm-worker"]:
        # Keep the control channel separate from Python and native provider diagnostics.
        with os.fdopen(os.dup(sys.stdout.fileno()), "wb") as replies:
            os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
            return run_warm_worker(sys.stdin.buffer, replies)

    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if sys.stdin.readline(64) != "fluxknowledge-transcription-go-v1\n":
        return 73
    output = run_transcription(args.manifest, open_cached_models)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(output, stream, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
        stream.flush()
        os.fsync(stream.fileno())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
