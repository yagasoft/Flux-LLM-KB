"""Synthetic tests for Teams nameplates and attribution, with no model loads."""
from pathlib import Path
import sys
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src/FluxKnowledge.Integrations/Media"))
import speaker_attribution as speakers


def badge(frame, x, y):
    frame[y:y + 13, x:x + 65] = (101, 100, 165)
    for offset in range(4, 59, 6):
        frame[y + 3:y + 10, x + offset:x + offset + 3] = (238, 238, 248)


def rail_frame(highlighted=True, two=False):
    frame = np.zeros((540, 960, 3), dtype=np.uint8)
    frame[34:506, :837] = 220
    for y in (74, 172, 270, 368, 466):
        yy, xx = np.ogrid[:540, :960]
        frame[(xx - 898) ** 2 + (yy - y) ** 2 < 38 ** 2] = (180, 150, 124)
    if highlighted:
        badge(frame, 853, 108)
    if two:
        badge(frame, 853, 206)
    badge(frame, 200, 200)
    return frame


class NameplateTests(unittest.TestCase):
    def test_rail_shape_and_overlapping_highlights(self):
        frame = rail_frame(two=True)
        layout, boxes = speakers.detect_nameplates(frame)
        self.assertEqual("sidebar", layout)
        self.assertEqual(2, len(boxes))
        self.assertTrue(all(box[0] == 853 for box in boxes))

    def test_rail_can_be_present_without_highlights(self):
        self.assertEqual(("sidebar", []), speakers.detect_nameplates(rail_frame(highlighted=False)))

    def test_presenter_badge_below_shared_content(self):
        frame = rail_frame(highlighted=False)
        badge(frame, 4, 525)
        self.assertEqual(4, speakers.detect_nameplates(frame)[1][0][0])

    def test_gallery_row_with_highlight(self):
        frame = np.zeros((540, 960, 3), dtype=np.uint8)
        yy, xx = np.ogrid[:540, :960]
        for x in (98, 288, 480, 670, 860):
            frame[(xx - x) ** 2 + (yy - 270) ** 2 < 50 ** 2] = (180, 150, 124)
        badge(frame, 197, 320)
        layout, boxes = speakers.detect_nameplates(frame)
        self.assertEqual("gallery", layout)
        self.assertEqual(197, boxes[0][0])

    def test_generic_video_with_purple_caption_is_unsupported(self):
        frame = np.full((540, 960, 3), 220, dtype=np.uint8)
        badge(frame, 853, 108)
        self.assertEqual((None, []), speakers.detect_nameplates(frame))


class AlignmentTests(unittest.TestCase):
    def test_splits_segment_and_preserves_spoken_words(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "gallery", ("Speaker A",)), (.25, "gallery", ("Speaker A",)),
            (.5, "gallery", ("Speaker B",)), (.75, "gallery", ("Speaker B",))])
        words = [
            dict(start=.1, end=.3, word=" hello"),
            dict(start=.6, end=.9, word=" there")]
        parts = timeline.split(.1, .9, "hello there", words)
        self.assertEqual([("hello", ("Speaker A",)), ("there", ("Speaker B",))],
                         [(part.text, part.candidates) for part in parts])
        self.assertEqual(words, [word for part in parts for word in part.words])

    def test_overlap_and_unknown_are_distinct(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "sidebar", ("Name A", "Name B")), (.25, "sidebar", ("Name A", "Name B")),
            (.5, "sidebar", ()), (.75, "sidebar", ())])
        overlap = timeline.split(.1, .3, "hello", None)[0]
        unknown = timeline.split(.6, .9, "world", None)[0]
        self.assertEqual("Name A or Name B: hello", overlap.labelled_text)
        self.assertEqual("Unknown speaker: world", unknown.labelled_text)

    def test_missing_video_preserves_existing_format(self):
        cue = speakers.SpeakerTimeline("audio-only", []).split(0., 1., "hello there", None)[0]
        self.assertIsNone(cue.candidates)
        self.assertEqual("hello there", cue.labelled_text)

    def test_indivisible_cue_does_not_guess_across_speaker_changes(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "gallery", ("Name A",)), (.2, "gallery", ("Name A",)),
            (.4, "gallery", ("Name A",)), (.6, "gallery", ("Name A",)),
            (.8, "gallery", ("Name B",)), (1., "gallery", ("Name B",))])
        cue = timeline.split(.1, 1.1, "Hello and goodbye", None)[0]
        self.assertEqual((), cue.candidates)

    def test_indivisible_cue_requires_sustained_evidence(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (step / 5, "sidebar", ("Name A",) if 22 <= step <= 27 else ()) for step in range(50)])
        cue = timeline.split(0., 10., "A long cue", None)[0]
        self.assertEqual((), cue.candidates)

    def test_speaker_boundary_does_not_separate_parts_of_one_written_word(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "sidebar", ("Name A",)), (.2, "sidebar", ("Name A",)),
            (.6, "sidebar", ("Name B",)), (.8, "sidebar", ("Name B",))])
        words = [dict(start=0., end=.2, word=" do"), dict(start=.6, end=.9, word="n't")]
        self.assertEqual(["don't"], [cue.text for cue in timeline.split(0., .9, "don't", words)])

    def test_zero_duration_words_keep_text_without_blocking_real_turn_boundaries(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "sidebar", ("Name A",)), (.2, "sidebar", ("Name A",)),
            (.4, "sidebar", ("Name A",)), (.6, "sidebar", ("Name B",)),
            (.8, "sidebar", ("Name B",)), (1., "sidebar", ("Name B",))])
        words = [dict(start=0., end=.2, word=" First"), dict(start=.6, end=.6, word=" then"),
                 dict(start=.6, end=.9, word=" second")]
        cues = timeline.split(0., .9, "First then second", words)
        self.assertEqual([(0., .2, "First", ("Name A",)), (.6, .9, "then second", ("Name B",))],
                         [(cue.start, cue.end, cue.text, cue.candidates) for cue in cues])
        self.assertEqual(words, [word for cue in cues for word in cue.words])

    def test_conflicting_overlapping_word_times_keep_the_cue_unsplit(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "sidebar", ("Name A",)), (.2, "sidebar", ("Name A",)),
            (.4, "sidebar", ("Name A",)), (.6, "sidebar", ("Name B",)),
            (.8, "sidebar", ("Name B",)), (1., "sidebar", ("Name B",))])
        words = [dict(start=0., end=.6, word=" Hello"), dict(start=.5, end=1., word=" there")]
        self.assertEqual([(0., 1., "Hello there", ())], [
            (cue.start, cue.end, cue.text, cue.candidates) for cue in timeline.split(0., 1., "Hello there", words)
        ])

    def test_audio_discontinuities_are_detected_before_attribution(self):
        from fractions import Fraction
        from types import SimpleNamespace

        def packet(tick):
            return SimpleNamespace(pts=tick, duration=1024, time_base=Fraction(1, 48000))

        end = speakers._audio_packet_end(packet(0), 0.)
        self.assertAlmostEqual(1024 / 48000, end)
        self.assertIsNone(speakers._audio_packet_end(packet(8192), end))
        # Small individual gaps must not accumulate past the audio/video tolerance.
        for tick in (1024 + 1920, 2 * (1024 + 1920)):
            end = speakers._audio_packet_end(packet(tick), end)
            self.assertIsNotNone(end)
        self.assertIsNone(speakers._audio_packet_end(packet(3 * (1024 + 1920)), end))

    def test_short_words_use_nearby_steady_highlight(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "sidebar", ("Name A",)), (.20, "sidebar", ("Name A",))])
        cue = timeline.split(.19, .21, "a", [dict(start=.19, end=.21, word="a")])[0]
        self.assertEqual(("Name A",), cue.candidates)

    def test_one_blank_frame_between_same_highlights_does_not_break_a_turn(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "sidebar", ("Name A",)), (.20, "sidebar", ("Name A",)), (.40, "sidebar", ()),
            (.60, "sidebar", ("Name A",)), (.80, "sidebar", ("Name A",))])
        cue = timeline.split(.34, .46, "a", [dict(start=.34, end=.46, word="a")])[0]
        self.assertEqual(("Name A",), cue.candidates)

    def test_word_text_mismatch_preserves_segment(self):
        timeline = speakers.SpeakerTimeline("analysed", [
            (0., "gallery", ("Name A",)), (.25, "gallery", ("Name A",))])
        cue = timeline.split(0., .4, "Hello!", [dict(start=0., end=.4, word="Hello")])[0]
        self.assertEqual("Hello!", cue.text)


if __name__ == "__main__":
    unittest.main()
