import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import paddleocr_vl_driver as driver
from paddleocr_vl_driver import assemble_candidate, blocks_in_reading_order, candidate_sample, correct_page_orientation, emitted_page_text, fixed_model_paths, inputs_from_truth, rows_from_html_table, run_batch


class PaddleOcrVlAdapterTests(unittest.TestCase):
    def test_emitted_page_text_preserves_provider_order_without_sorting(self):
        blocks = [
            {"block_label": "text", "block_content": "Second emitted"},
            {"block_label": "title", "block_content": "First by position"},
        ]

        self.assertEqual("Second emitted\nFirst by position", emitted_page_text(blocks))

    def test_rows_from_html_table_preserves_cell_text_and_empty_cells(self):
        html = "<table><tr><th>Code</th><th>Amount</th></tr><tr><td>A-01</td><td></td></tr></table>"

        self.assertEqual([["Code", "Amount"], ["A-01", ""]], rows_from_html_table(html))

    def test_candidate_sample_keeps_raw_block_order_and_table_cells(self):
        raw = {
            "parsing_res_list": [
                {"block_label": "title", "block_content": "Heading", "block_bbox": [0, 0, 10, 10]},
                {"block_label": "table", "block_content": "<table><tr><td>A-01</td></tr></table>", "block_bbox": [0, 20, 10, 30]},
            ]
        }

        self.assertEqual(
            {
                "id": "en-01",
                "pageText": "Heading\nA-01",
                "blockOrder": ["emitted-000", "emitted-001"],
                "tables": [{"id": "emitted-table-001", "rows": [["A-01"]]}],
                "blocks": [
                    {"id": "emitted-000", "text": "Heading", "x": 0.0, "y": 0.0, "width": 10.0, "height": 10.0},
                    {"id": "emitted-001", "text": "A-01", "x": 0.0, "y": 20.0, "width": 10.0, "height": 10.0},
                ],
            },
            candidate_sample("en-01", raw),
        )

    def test_blocks_in_reading_order_groups_two_clear_columns_before_emitting_the_right_column(self):
        blocks = [
            {"id": "heading", "block_bbox": [75, 90, 400, 130]},
            {"id": "left-first", "block_bbox": [75, 210, 480, 240]},
            {"id": "right-first", "block_bbox": [645, 210, 1040, 240]},
            {"id": "left-second", "block_bbox": [75, 300, 330, 330]},
            {"id": "right-second", "block_bbox": [645, 320, 1030, 350]},
        ]

        self.assertEqual(
            ["heading", "left-first", "left-second", "right-first", "right-second"],
            [block["id"] for block in blocks_in_reading_order(blocks)],
        )

    def test_blocks_in_reading_order_keeps_provider_order_without_two_clear_columns(self):
        blocks = [
            {"id": "provider-second", "block_bbox": [75, 210, 720, 240]},
            {"id": "provider-first", "block_bbox": [75, 90, 400, 130]},
            {"id": "provider-third", "block_bbox": [76, 300, 720, 330]},
        ]

        self.assertEqual(["provider-second", "provider-first", "provider-third"], [block["id"] for block in blocks_in_reading_order(blocks)])

    def test_blocks_in_reading_order_preserves_a_clear_multi_column_provider_sequence_with_long_runs(self):
        blocks = [
            {"id": "left-heading", "block_bbox": [75, 90, 400, 130]},
            {"id": "left-first", "block_bbox": [75, 210, 480, 240]},
            {"id": "left-second", "block_bbox": [75, 300, 330, 330]},
            {"id": "right-first", "block_bbox": [645, 210, 1040, 240]},
            {"id": "right-second", "block_bbox": [645, 320, 1030, 350]},
            {"id": "left-footer", "block_bbox": [75, 420, 480, 450]},
            {"id": "right-footer", "block_bbox": [645, 420, 1030, 450]},
        ]

        self.assertEqual(
            ["left-heading", "left-first", "left-second", "right-first", "right-second", "left-footer", "right-footer"],
            [block["id"] for block in blocks_in_reading_order(blocks)],
        )

    def test_inputs_from_truth_exposes_only_identity_and_image_path(self):
        truth = {
            "samples": [
                {"id": "en-01", "imagePath": "pages/en-01.png", "pageText": "Expected text must not reach the provider"}
            ]
        }

        self.assertEqual([("en-01", "pages/en-01.png")], inputs_from_truth(truth))

    def test_frozen_assessment_guard_accepts_only_the_pinned_public_diagnostic_input(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            public = root / "public-scan-supplement"
            public.mkdir()
            truth = public / "diagnostic-truth.json"
            truth.write_text('{"samples":[]}', encoding="utf-8")
            output = root / "candidate-output"
            sha256 = __import__("hashlib").sha256(truth.read_bytes()).hexdigest()

            with patch.object(driver, "ASSESSMENT_ROOT", root), patch.object(
                driver,
                "FROZEN_ASSESSMENT_SHA256",
                {Path("public-scan-supplement/diagnostic-truth.json"): sha256},
            ), patch.object(
                driver,
                "FROZEN_ASSESSMENT_INPUT_ROOT",
                {Path("public-scan-supplement/diagnostic-truth.json"): Path(".")},
            ):
                self.assertEqual(root, driver._assert_frozen_assessment_paths(truth, output))

                truth.write_text('{"samples":[{"id":"changed"}]}', encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "assessment-sha256-mismatch"):
                    driver._assert_frozen_assessment_paths(truth, output)

    def test_frozen_assessment_guard_refuses_an_unlisted_input_even_under_the_assessment_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            unlisted = root / "unlisted.json"
            unlisted.write_text('{"samples":[]}', encoding="utf-8")

            with patch.object(driver, "ASSESSMENT_ROOT", root), patch.object(
                driver,
                "FROZEN_ASSESSMENT_SHA256",
                {Path("public-scan-supplement/diagnostic-truth.json"): "0" * 64},
            ), patch.object(
                driver,
                "FROZEN_ASSESSMENT_INPUT_ROOT",
                {Path("public-scan-supplement/diagnostic-truth.json"): Path(".")},
            ):
                with self.assertRaisesRegex(ValueError, "assessment-input-not-approved"):
                    driver._assert_frozen_assessment_paths(unlisted, root / "candidate-output")

    def test_public_scan_provider_catalogue_is_a_pinned_assessment_input_with_its_own_root(self):
        path = Path("public-scan-supplement/input-catalogue.json")

        self.assertEqual(
            "4c089c32717a32ac5197c6327e220ca49aeaf3c0883a2d3d8648f9bfc60f3efa",
            driver.FROZEN_ASSESSMENT_SHA256[path],
        )
        self.assertEqual(Path("public-scan-supplement"), driver.FROZEN_ASSESSMENT_INPUT_ROOT[path])

    def test_public_table_provider_catalogue_is_a_pinned_assessment_input_with_its_own_root(self):
        path = Path("pubtabnet-examples/provider-inputs.json")

        self.assertEqual(
            "3a22840da2ced02b8ec18f17dcc77c3a41895222279a0c8b8a8db4cde42f5ea8",
            driver.FROZEN_ASSESSMENT_SHA256[path],
        )
        self.assertEqual(Path("pubtabnet-examples"), driver.FROZEN_ASSESSMENT_INPUT_ROOT[path])

    def test_run_batch_writes_raw_output_from_image_path_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "pages").mkdir()
            (root / "pages" / "en-01.png").write_bytes(b"fixture")
            truth_path = root / "truth.json"
            truth_path.write_text(
                json.dumps({"samples": [{"id": "en-01", "imagePath": "pages/en-01.png", "pageText": "Expected text stays out of predict"}]}),
                encoding="utf-8",
            )
            output = root / "output"
            paths = []

            def predict(image_path):
                paths.append(Path(image_path).name)
                return {"parsing_res_list": [{"block_label": "text", "block_content": "Provider output", "block_bbox": [0, 0, 1, 1]}]}

            self.assertEqual(["en-01"], run_batch(truth_path, output, ["en-01"], predict))
            self.assertEqual(["en-01.png"], paths)
            self.assertEqual("Provider output", json.loads((output / "raw" / "en-01.json").read_text(encoding="utf-8"))["parsing_res_list"][0]["block_content"])
            with self.assertRaises(FileExistsError):
                run_batch(truth_path, output, ["en-01"], predict)

    def test_run_batch_uses_the_declared_input_root_and_refuses_an_escaping_image_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            public = root / "public-scan-supplement"
            public.mkdir()
            (public / "scan.png").write_bytes(b"fixture")
            truth_path = public / "diagnostic-truth.json"
            truth_path.write_text(
                json.dumps({"samples": [{"id": "scan", "imagePath": "public-scan-supplement/scan.png"}]}),
                encoding="utf-8",
            )
            seen = []

            run_batch(
                truth_path,
                root / "output",
                ["scan"],
                lambda image: seen.append(image) or {"parsing_res_list": []},
                input_root=root,
            )

            self.assertEqual([public / "scan.png"], seen)
            truth_path.write_text(
                json.dumps({"samples": [{"id": "escape", "imagePath": "../outside.png"}]}),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "image-path-outside-input-root"):
                run_batch(truth_path, root / "second-output", ["escape"], lambda _: {"parsing_res_list": []}, input_root=public)

    def test_assemble_candidate_uses_retained_raw_results_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "pages").mkdir()
            (root / "pages" / "en-01.png").write_bytes(b"fixture")
            truth_path = root / "truth.json"
            truth_path.write_text(json.dumps({"samples": [{"id": "en-01", "imagePath": "pages/en-01.png", "pageText": "Expected"}]}), encoding="utf-8")
            output = root / "output"
            run_batch(truth_path, output, ["en-01"], lambda _: {"parsing_res_list": [{"block_label": "text", "block_content": "Observed", "block_bbox": [0, 0, 1, 1]}]})

            candidate_path = assemble_candidate(truth_path, output, "candidate", "engine", "revision", "run-1")

            candidate = json.loads(candidate_path.read_text(encoding="utf-8"))
            self.assertEqual("candidate", candidate["candidateName"])
            self.assertEqual({"engine": "engine", "version": "revision", "runId": "run-1"}, candidate["provenance"])
            self.assertEqual("Observed", candidate["samples"][0]["pageText"])
            with self.assertRaises(FileExistsError):
                assemble_candidate(truth_path, output, "candidate", "engine", "revision", "run-1")
            represented_path = assemble_candidate(
                truth_path,
                output,
                "candidate",
                "engine",
                "revision",
                "run-2",
                "candidate-results-represented.json",
            )
            self.assertEqual("candidate-results-represented.json", represented_path.name)
            with self.assertRaises(ValueError):
                assemble_candidate(
                    truth_path,
                    output,
                    "candidate",
                    "engine",
                    "revision",
                    "run-3",
                    "..\\outside.json",
                )

    def test_fixed_model_paths_stay_under_the_j_drive_model_store(self):
        self.assertTrue(all(str(path).lower().startswith("j:\\models\\") for path in fixed_model_paths()))

    def test_correct_page_orientation_rotates_the_vlm_input_by_the_local_model_label(self):
        observed = []

        def rotate(image, angle):
            observed.append((image, angle))
            return "rotated-image"

        corrected, angle = correct_page_orientation("original-image", {"label_names": ["90"], "scores": [0.90]}, rotate)

        self.assertEqual(("rotated-image", 90), (corrected, angle))
        self.assertEqual([("original-image", 90)], observed)

    def test_correct_page_orientation_keeps_the_original_for_a_low_confidence_nonzero_label(self):
        observed = []

        corrected, angle = correct_page_orientation(
            "original-image",
            {"label_names": ["180"], "scores": [0.49]},
            lambda image, rotation: observed.append((image, rotation)),
        )

        self.assertEqual(("original-image", 0), (corrected, angle))
        self.assertEqual([], observed)

    def test_correct_page_orientation_accepts_the_provider_confidence_sequence(self):
        corrected, angle = correct_page_orientation(
            "original-image",
            {"label_names": ["180"], "scores": (0.90,)},
            lambda image, rotation: f"{image}:{rotation}",
        )

        self.assertEqual(("original-image:180", 180), (corrected, angle))

    def test_correct_page_orientation_refuses_an_unsupported_model_label(self):
        with self.assertRaisesRegex(ValueError, "unexpected-page-orientation"):
            correct_page_orientation("original-image", {"label_names": ["45"], "scores": [0.99]}, lambda image, angle: image)

    def test_correct_page_orientation_refuses_a_result_without_one_confidence_score(self):
        with self.assertRaisesRegex(ValueError, "unexpected-page-orientation"):
            correct_page_orientation("original-image", {"label_names": ["90"], "scores": []}, lambda image, angle: image)


if __name__ == "__main__":
    unittest.main()
