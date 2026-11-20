"""Offline attribution from visible Teams recording nameplates and ASR word times."""
from bisect import bisect_left, bisect_right
from collections import Counter
from pathlib import Path
import math
import os
import re
import subprocess
import time
from typing import NamedTuple


VERSION = "teams-nameplates-v1"
SAMPLE_INTERVAL = .20
MAX_DURATION = 7200
WIDTH, HEIGHT = 960, 540
CACHE = Path(r"J:\Models")
OCR_EXE = CACHE / "runtimes/tesseract/5.5.3.20260724/tesseract.exe"
OCR_DATA = CACHE / "artifacts/sha256/7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"
NAME = re.compile(r"[^\W\d_]+(?:[ .'’\-][^\W\d_]+)*")


def _runs(mask):
    import numpy as np

    boundaries = np.flatnonzero(np.diff(np.r_[False, mask, False]))
    return zip(boundaries[::2], boundaries[1::2])


def _badges(region, left, top):
    import numpy as np

    rgb = region.astype(np.int16)
    red, green, blue = rgb.transpose(2, 0, 1)
    purple = ((red >= 72) & (red <= 133) & (abs(red - green) <= 15) &
              (blue >= 139) & (blue <= 199) & (blue - red >= 45) & (blue - red <= 86))
    white = (rgb.min(axis=2) >= 190) & (rgb.max(axis=2) - rgb.min(axis=2) <= 36)
    seen = []
    for y in np.flatnonzero((purple.sum(axis=1) >= 18) & (purple.sum(axis=1) <= 450)):
        for x, right in _runs(purple[y]):
            width = right - x
            if not 20 <= width <= 118 or any(a <= x < c and b <= y < d for a, b, c, d in seen):
                continue
            affected = (purple[max(0, y - 15):y + 16, x:right].sum(axis=1) >= width * .32)
            offset = max(0, y - 15)
            bounds = next(((a + offset, b + offset) for a, b in _runs(affected)
                           if a + offset <= y < b + offset), None)
            if bounds is None:
                continue
            y0, y1 = bounds
            if not 10 <= y1 - y0 <= 17:
                continue
            fill = purple[y0:y1, max(0, x - 4):right + 4]
            columns = np.flatnonzero(fill.sum(axis=0) >= 4)
            if not len(columns):
                continue
            x0, x1 = columns[[0, -1]] + max(0, x - 4)
            x1 += 1
            if not 23 <= x1 - x0 <= 123:
                continue
            if white[y0 + 2:y1 - 2, x0 + 2:x1 - 2].mean() < .045:
                continue
            seen.append((x0, y0, x1, y1))
    return [(int(a + left), int(b + top), int(c + left), int(d + top)) for a, b, c, d in seen]


def detect_nameplates(frame):
    """Return layout and nameplate bounds in canonical 960×540 coordinates."""
    import numpy as np

    height, width = frame.shape[:2]
    if height % HEIGHT == 0 and width % WIDTH == 0:
        frame = frame[::height // HEIGHT, ::width // WIDTH]
    else:
        frame = frame[(np.arange(HEIGHT) * height // HEIGHT)[:, None], np.arange(WIDTH) * width // WIDTH]
    # The sidebar is black between its circle tiles. Shared content ends before
    # the rail, and the presenter tag lives below the shared content.
    darkness = frame[::2, ::2].max(axis=2) <= 24
    if (darkness[:, 419:426].mean() >= .91 and darkness[:, 469:480].mean() >= .89
            and darkness[:, 426:469].mean() < .94 and darkness[18:249, :414].mean() < .90):
        boxes = _badges(frame[:, 841:944], 841, 0)
        if darkness[-8:, 68:112].mean() >= .96:
            boxes.extend(_badges(frame[521:, :219], 0, 521))
        return "sidebar", boxes
    # Gallery has a black surround with multiple similarly tall, spaced tiles.
    if (darkness[:75].mean() >= .97 and darkness[-75:].mean() >= .97
            and darkness[:, :14].mean() >= .92 and darkness[:, -14:].mean() >= .92):
        populated = (1 - darkness[91:179].mean(axis=0)) > .19
        tiles = [b - a for a, b in _runs(populated) if b - a >= 22]
        if 2 <= len(tiles) <= 9 and max(tiles) <= min(tiles) * 1.8:
            return "gallery", _badges(frame[165:386], 0, 165)
    return None, []


def _cache_file(path, bytes_expected):
    try:
        return path.resolve(strict=True) == path and path.stat().st_size == bytes_expected and path.is_file()
    except OSError:
        return False


class _NameReader:
    def __init__(self):
        self.results = {}
        self.calls = 0
        self.limited = False

    def read(self, image, deadline=math.inf):
        import numpy as np

        # Use high-contrast white glyphs, isolated from the badge background.
        grey = image.min(axis=2)
        mask = grey >= 185
        rows, cols = np.where(mask)
        if len(rows) == 0:
            return None
        mask = mask[rows.min():rows.max() + 1, cols.min():cols.max() + 1]
        if mask.shape[0] < 5 or mask.shape[1] < 10:
            return None
        # Remove compression noise while keeping the full recognisable glyphs
        # for OCR. This cache is local to one recording.
        key = (mask.shape, np.packbits(mask).tobytes())
        if key not in self.results:
            if self.calls >= 320:
                self.limited = True
                return None
            self.calls += 1
            pixels = np.pad(np.repeat(np.repeat(255 - mask.astype(np.uint8) * 255, 2, axis=0),
                                      2, axis=1), 10, constant_values=255)
            height, width = pixels.shape
            source = f"P5\n{width} {height}\n255\n".encode("ascii") + pixels.tobytes()
            result = subprocess.run([str(OCR_EXE), "stdin", "stdout", "--tessdata-dir", str(OCR_DATA),
                                     "-l", "eng", "--psm", "7", "-c", "tessedit_create_tsv=1"],
                                    input=source, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                    timeout=min(4, max(.01, deadline - time.monotonic())), check=True,
                                    cwd=OCR_EXE.parent,
                                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                                    env={**os.environ, "OMP_THREAD_LIMIT": "1"})
            words = []
            confidence = []
            for row in result.stdout.decode("utf-8").splitlines()[1:]:
                fields = row.split("\t", 11)
                if len(fields) == 12 and fields[0] == "5" and fields[11].strip():
                    words.append(fields[11].strip())
                    confidence.append(float(fields[10]))
            name = " ".join(words)
            self.results[key] = (name if confidence and min(confidence) >= 50 and sum(confidence) / len(confidence) >= 70 and
                                 2 <= len(name) <= 80 and NAME.fullmatch(name) else None)
        return self.results[key]


def labelled_text(text, candidates):
    if candidates is None:
        return text
    label = " or ".join(candidates) if candidates else "Unknown speaker"
    return f"{label}: {text}"


class AttributedCue(NamedTuple):
    start: float
    end: float
    text: str
    words: list | None
    candidates: tuple | None

    @property
    def labelled_text(self):
        return labelled_text(self.text, self.candidates)


class SpeakerTimeline:
    def __init__(self, status, samples):
        self.status = status
        self.samples = samples
        self.times = [sample[0] for sample in samples]

    def _candidates(self, start, end):
        if not self.samples:
            return None
        mid = (start + end) / 2
        # Use the nearest observation, avoiding extrapolation on media gaps.
        index = bisect_left(self.times, mid)
        indexes = range(max(0, index - 1), min(index + 1, len(self.times)))
        index = min(indexes, key=lambda i: abs(self.times[i] - mid))
        timestamp, layout, names = self.samples[index]
        if abs(timestamp - mid) > .30 or layout is None:
            return None
        if not names and 0 < index < len(self.samples) - 1:
            before, after = self.samples[index - 1], self.samples[index + 1]
            if before[1] == after[1] == layout and before[2] == after[2] and after[0] - before[0] <= .45:
                names = before[2]
        begin = bisect_left(self.times, mid - .33)
        finish = bisect_right(self.times, mid + .33)
        counts = Counter(name for _, sample_layout, observed in self.samples[begin:finish]
                         if sample_layout == layout for name in observed)
        present = [name for name in names if counts[name] >= 2]
        if len(names) > 1 and not all(name in present for name in names):
            return ()
        return tuple(sorted(present, key=str.casefold))

    def _compatible_points(self, points, neighbour, candidates):
        for point in points:
            timestamp = point["start"]
            observed = self._candidates(timestamp, timestamp)
            if abs(timestamp - neighbour) > .33 or observed and observed != candidates:
                return False
        return True

    def split(self, start, end, text, words):
        def whole():
            candidates = self._candidates(start, end)
            first = bisect_left(self.times, start)
            last = bisect_right(self.times, end)
            if candidates is not None:
                evidence = self.samples[first:last]
                if evidence and (any(layout is None or names not in ((), candidates) for _, layout, names in evidence) or
                                 (end - start > .45 and
                                  sum(names == candidates for _, _, names in evidence) * SAMPLE_INTERVAL < (end - start) * .70)):
                    candidates = ()
            return [AttributedCue(start, end, text, words, candidates)]

        if not words or not self.samples:
            return whole()
        if "".join(word["word"] for word in words).strip() != text:
            return whole()
        previous = start
        for word in words:
            low, high = word["start"], word["end"]
            if not (math.isfinite(low) and math.isfinite(high)
                    and previous <= low <= high <= end):
                return whole()
            previous = high

        # Whisper also emits tokens with a time point but no duration. Attach
        # these to a timed word so they cannot create a zero-length cue.
        groups = []
        timing_points = []
        for word in words:
            if word["start"] == word["end"]:
                timing_points.append(word)
                continue
            candidates = self._candidates(word["start"], word["end"])
            if timing_points and not self._compatible_points(timing_points, word["start"], candidates):
                candidates = ()
            if not groups or groups[-1][0] != candidates:
                groups.append((candidates, []))
            groups[-1][1].extend(timing_points)
            groups[-1][1].append(word)
            timing_points.clear()
        if not groups:
            return whole()
        if timing_points:
            candidates, group = groups[-1]
            if not self._compatible_points(timing_points, group[-1]["end"], candidates):
                candidates = ()
            group.extend(timing_points)
            groups[-1] = (candidates, group)
        if (any(not "".join(word["word"] for word in group).strip() for _, group in groups)
                or any(not group[0]["word"][:1].isspace() for _, group in groups[1:])):
            return whole()
        return [AttributedCue(start if ix == 0 else group[0]["start"],
                              end if ix == len(groups) - 1 else group[-1]["end"],
                              "".join(word["word"] for word in group).strip(), group, candidates)
                for ix, (candidates, group) in enumerate(groups)]


def _audio_packet_end(packet, expected):
    # The ASR clock is continuous decoded audio. A packet-level gap invalidates
    # video-to-word mapping when the decoder has concatenated the waveform.
    if packet.pts is None or packet.time_base is None or not packet.duration:
        return None
    start = float(packet.pts * packet.time_base)
    end = start + float(packet.duration * packet.time_base)
    if not math.isfinite(end) or end <= start or abs(start - expected) > .08:
        return None
    return expected + float(packet.duration * packet.time_base)


def inspect_video(path, duration, *, time_limit=600):
    """Return bounded local visual evidence. Audio and unsupported video pass through."""
    try:
        import av
    except ImportError:
        return SpeakerTimeline("attribution-unavailable", [])

    def refuse_auxiliary_input(*_):
        raise OSError("External video input refused")

    if not math.isfinite(duration) or duration <= 0 or duration > MAX_DURATION:
        return SpeakerTimeline("duration-unavailable", [])
    if not math.isfinite(time_limit) or time_limit <= 0:
        return SpeakerTimeline("analysis-time-limit", [])
    deadline = time.monotonic() + min(600, time_limit)
    try:
        with path.open("rb") as source, av.open(source, mode="r", options={"protocol_whitelist": "pipe"},
                                                 io_open=refuse_auxiliary_input) as container:
            if not container.streams.video:
                return SpeakerTimeline("audio-only", [])
            stream = container.streams.video[0]
            if (not container.streams.audio or stream.width < 640 or stream.height < 360 or
                    stream.width * stream.height > 9_000_000):
                return SpeakerTimeline("unsupported-video", [])
            audio = container.streams.audio[0]
            if audio.start_time is None or audio.time_base is None:
                return SpeakerTimeline("video-timing-unavailable", [])
            origin = float(audio.start_time * audio.time_base)
            if not _cache_file(OCR_EXE, 1867784) or not _cache_file(OCR_DATA / "eng.traineddata", 4113088):
                return SpeakerTimeline("ocr-unavailable", [])
            reader = _NameReader()
            samples = []
            next_sample = 0.
            previous = -math.inf
            audio_end = origin
            for packet in container.demux(stream, audio):
                if time.monotonic() > deadline:
                    return SpeakerTimeline("analysis-time-limit", [])
                if packet.stream.index == audio.index:
                    if packet.size:
                        audio_end = _audio_packet_end(packet, audio_end)
                        if audio_end is None:
                            return SpeakerTimeline("video-timing-unavailable", [])
                    continue
                for frame in packet.decode():
                    if frame.time is None:
                        continue
                    timestamp = float(frame.time) - origin
                    if not math.isfinite(timestamp) or timestamp < previous:
                        return SpeakerTimeline("video-timing-unavailable", [])
                    previous = timestamp
                    if timestamp > duration + .3:
                        continue
                    if timestamp + .0001 < next_sample:
                        continue
                    next_sample = (math.floor((timestamp + .0001) / SAMPLE_INTERVAL) + 1) * SAMPLE_INTERVAL
                    if frame.width * frame.height > 9_000_000:
                        return SpeakerTimeline("unsupported-video", [])
                    original = frame.to_ndarray(format="rgb24")
                    layout, boxes = detect_nameplates(original)
                    names = []
                    if boxes:
                        for x0, y0, x1, y1 in boxes:
                            name = reader.read(original[math.ceil((y0 + 1) * frame.height / HEIGHT):
                                                        math.floor((y1 - 1) * frame.height / HEIGHT),
                                                        math.ceil((x0 + 1) * frame.width / WIDTH):
                                                        math.floor((x1 - 1) * frame.width / WIDTH)], deadline)
                            if name:
                                names.append(name)
                    if len(names) != len(boxes):
                        names.clear()
                    samples.append((timestamp, layout, tuple(sorted(set(names), key=str.casefold))))
            if abs(audio_end - origin - duration) > .5:
                return SpeakerTimeline("video-timing-unavailable", [])
            if not any(layout is not None for _, layout, _ in samples):
                return SpeakerTimeline("unsupported-layout", [])
            return SpeakerTimeline("ocr-budget-reached" if reader.limited else "analysed", samples)
    except (av.FFmpegError, OSError, subprocess.SubprocessError, UnicodeError, ValueError):
        status = "analysis-time-limit" if time.monotonic() >= deadline else "attribution-unavailable"
        return SpeakerTimeline(status, [])
