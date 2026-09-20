import argparse
import hashlib
import json
import os
import socket
import stat
import time
from html.parser import HTMLParser
from pathlib import Path


RUNTIME_ROOT = Path(r"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312")
VLM_MODEL_ROOT = Path(r"J:\Models\bundles\paddleocr-vl-1.6\c5630abae1d940eafe0697512a0325494b02ab42")
LAYOUT_MODEL_ROOT = Path(r"J:\Models\bundles\pp-doclayoutv3\7b48a7566925fa464281f930c58eee04fe2c862a")
PAGE_ORIENTATION_MODEL_ROOT = Path(r"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\bundles\page_orientation")
ASSESSMENT_ROOT = Path(r"E:\Temp\flux-ocr-assessment-20260919")
SYNTHETIC_TRUTH_SHA256 = "0b1d760c14ae78c784e880c245b0726f8bbcc66619dd5007f60acc4940c6a294"
FROZEN_ASSESSMENT_SHA256 = {
    Path("synthetic-screening-geometric-v2/truth.json"): SYNTHETIC_TRUTH_SHA256,
    Path("public-scan-supplement/diagnostic-truth.json"): "153394ede21b3b4cbaa05c611d83c9d39ca306b519fa0e54553ca05ac8cce1e6",
    Path("public-scan-supplement/input-catalogue.json"): "4c089c32717a32ac5197c6327e220ca49aeaf3c0883a2d3d8648f9bfc60f3efa",
    Path("pubtabnet-examples/provider-inputs.json"): "3a22840da2ced02b8ec18f17dcc77c3a41895222279a0c8b8a8db4cde42f5ea8",
}
FROZEN_ASSESSMENT_INPUT_ROOT = {
    Path("synthetic-screening-geometric-v2/truth.json"): Path("synthetic-screening-geometric-v2"),
    Path("public-scan-supplement/diagnostic-truth.json"): Path("."),
    Path("public-scan-supplement/input-catalogue.json"): Path("public-scan-supplement"),
    Path("pubtabnet-examples/provider-inputs.json"): Path("pubtabnet-examples"),
}
MIN_NONZERO_ORIENTATION_CONFIDENCE = 0.80


def fixed_model_paths():
    return RUNTIME_ROOT, VLM_MODEL_ROOT, LAYOUT_MODEL_ROOT, PAGE_ORIENTATION_MODEL_ROOT


def correct_page_orientation(image, orientation_result, rotate):
    labels = orientation_result.get("label_names")
    scores = orientation_result.get("scores")
    if not isinstance(labels, list) or len(labels) != 1 or isinstance(scores, (str, bytes)):
        raise ValueError("unexpected-page-orientation")
    try:
        if len(scores) != 1:
            raise ValueError
        angle = int(labels[0])
        confidence = float(scores[0])
    except (IndexError, TypeError, ValueError) as exception:
        raise ValueError("unexpected-page-orientation") from exception
    if angle not in {0, 90, 180, 270} or not 0 <= confidence <= 1:
        raise ValueError("unexpected-page-orientation")
    if angle != 0 and confidence < MIN_NONZERO_ORIENTATION_CONFIDENCE:
        return image, 0
    return rotate(image, angle), angle


class _TableRowsParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.rows = []
        self._row = None
        self._cell = None

    def handle_starttag(self, tag, attrs):
        if tag == "tr":
            self._row = []
        elif tag in {"td", "th"} and self._row is not None:
            self._cell = []
        elif tag == "br" and self._cell is not None:
            self._cell.append("\n")

    def handle_data(self, data):
        if self._cell is not None:
            self._cell.append(data)

    def handle_endtag(self, tag):
        if tag in {"td", "th"} and self._cell is not None and self._row is not None:
            self._row.append("".join(self._cell).strip())
            self._cell = None
        elif tag == "tr" and self._row is not None:
            if self._cell is not None:
                self._row.append("".join(self._cell).strip())
                self._cell = None
            self.rows.append(self._row)
            self._row = None


def rows_from_html_table(html):
    parser = _TableRowsParser()
    parser.feed(html)
    parser.close()
    return parser.rows


def _valid_bbox(block):
    bbox = block.get("block_bbox")
    if not isinstance(bbox, (list, tuple)) or len(bbox) != 4:
        return None
    try:
        x0, y0, x1, y1 = (float(value) for value in bbox)
    except (TypeError, ValueError):
        return None
    if x1 <= x0 or y1 <= y0:
        return None
    return x0, y0, x1, y1


def blocks_in_reading_order(blocks):
    annotated = [(index, block, _valid_bbox(block)) for index, block in enumerate(blocks)]
    if not annotated or any(bbox is None for _, _, bbox in annotated):
        return list(blocks)
    x_min = min(bbox[0] for _, _, bbox in annotated)
    x_max = max(bbox[2] for _, _, bbox in annotated)
    horizontal_span = x_max - x_min
    left_edges = sorted({bbox[0] for _, _, bbox in annotated})
    gaps = [(right - left, (left + right) / 2) for left, right in zip(left_edges, left_edges[1:])]
    if horizontal_span <= 0 or not gaps:
        return list(blocks)
    gutter, split = max(gaps)
    if gutter < horizontal_span * 0.25:
        return list(blocks)
    left = [entry for entry in annotated if entry[2][0] < split]
    right = [entry for entry in annotated if entry[2][0] >= split]
    if len(left) < 2 or len(right) < 2:
        return list(blocks)
    sides = ["left" if entry[2][0] < split else "right" for entry in annotated]
    longest_provider_run = 1
    current_provider_run = 1
    for previous, current in zip(sides, sides[1:]):
        if current == previous:
            current_provider_run += 1
            longest_provider_run = max(longest_provider_run, current_provider_run)
        else:
            current_provider_run = 1
    if longest_provider_run > 2:
        return list(blocks)
    key = lambda entry: (entry[2][1], entry[2][0], entry[0])
    return [block for _, block, _ in sorted(left, key=key) + sorted(right, key=key)]


def emitted_block_text(block):
    content = str(block.get("block_content") or "")
    if block.get("block_label") == "table":
        rows = rows_from_html_table(content)
        if rows:
            return "\n".join("\t".join(row) for row in rows)
    return content


def emitted_page_text(blocks):
    return "\n".join(emitted_block_text(block) for block in blocks_in_reading_order(blocks))


def inputs_from_truth(truth):
    return [(sample["id"], sample["imagePath"]) for sample in truth["samples"]]


def run_batch(truth_path, output_directory, selected_ids, predict, input_root=None):
    truth_file = Path(truth_path).resolve()
    images_root = Path(input_root).resolve() if input_root is not None else truth_file.parent
    output = Path(output_directory).resolve()
    truth = json.loads(truth_file.read_text(encoding="utf-8"))
    available = dict(inputs_from_truth(truth))
    selected = list(selected_ids)
    missing = [sample_id for sample_id in selected if sample_id not in available]
    if missing:
        raise ValueError("unknown-sample-id:" + ",".join(missing))
    raw_directory = output / "raw"
    raw_directory.mkdir(parents=True, exist_ok=True)
    raw_paths = [raw_directory / f"{sample_id}.json" for sample_id in selected]
    if next((path for path in raw_paths if path.exists()), None) is not None:
        raise FileExistsError("raw-result-already-exists")
    written = []
    for sample_id in selected:
        image_path = (images_root / available[sample_id]).resolve()
        _assert_under(images_root, image_path, "image-path-outside-input-root")
        raw_result = predict(image_path)
        raw_path = raw_directory / f"{sample_id}.json"
        with raw_path.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(raw_result, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        written.append(sample_id)
    return written


def assemble_candidate(truth_path, output_directory, candidate_name, engine, revision, run_id, candidate_filename="candidate-results.json"):
    truth_file = Path(truth_path).resolve()
    output = Path(output_directory).resolve()
    candidate_leaf = Path(candidate_filename)
    if candidate_leaf.parent != Path(".") or candidate_leaf.name != candidate_filename or candidate_leaf.suffix.lower() != ".json":
        raise ValueError("candidate-filename-must-be-a-json-leaf")
    truth = json.loads(truth_file.read_text(encoding="utf-8"))
    sample_ids = [sample_id for sample_id, _ in inputs_from_truth(truth)]
    raw_directory = output / "raw"
    missing = [sample_id for sample_id in sample_ids if not (raw_directory / f"{sample_id}.json").is_file()]
    if missing:
        raise FileNotFoundError("raw-result-missing:" + ",".join(missing))
    candidate_path = output / candidate_leaf
    if candidate_path.exists():
        raise FileExistsError("candidate-results-already-exists")
    samples = []
    for sample_id in sample_ids:
        raw_path = raw_directory / f"{sample_id}.json"
        samples.append(candidate_sample(sample_id, json.loads(raw_path.read_text(encoding="utf-8"))))
    candidate = {
        "candidateName": candidate_name,
        "provenance": {"engine": engine, "version": revision, "runId": run_id},
        "samples": samples,
    }
    with candidate_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(candidate, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    return candidate_path


def candidate_sample(sample_id, raw_result):
    blocks = blocks_in_reading_order(raw_result.get("parsing_res_list", []))
    tables = []
    candidate_blocks = []
    for index, block in enumerate(blocks):
        bbox = _valid_bbox(block)
        if bbox is not None:
            x0, y0, x1, y1 = bbox
            candidate_blocks.append(
                {
                    "id": f"emitted-{index:03}",
                    "text": emitted_block_text(block),
                    "x": x0,
                    "y": y0,
                    "width": x1 - x0,
                    "height": y1 - y0,
                }
            )
        if block.get("block_label") == "table":
            tables.append(
                {
                    "id": f"emitted-table-{index:03}",
                    "rows": rows_from_html_table(str(block.get("block_content") or "")),
                }
            )
    return {
        "id": sample_id,
        "pageText": "\n".join(emitted_block_text(block) for block in blocks),
        "blockOrder": [f"emitted-{index:03}" for index in range(len(blocks))],
        "tables": tables,
        "blocks": candidate_blocks if len(candidate_blocks) == len(blocks) else [],
    }


def _assert_under(parent, path, reason):
    try:
        path.resolve().relative_to(parent.resolve())
    except ValueError as exception:
        raise ValueError(reason) from exception


def _assert_fixed_models_are_safe():
    model_root = Path(r"J:\Models")
    for path in fixed_model_paths():
        _assert_under(model_root, path, "model-path-outside-j-store")
        if not path.is_dir():
            raise FileNotFoundError("verified-model-path-missing:" + str(path))
        current = model_root
        for part in path.relative_to(model_root).parts:
            current = current / part
            attributes = getattr(os.lstat(current), "st_file_attributes", 0)
            if attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise OSError("model-path-reparse-point:" + str(current))


def _create_local_components():
    _assert_fixed_models_are_safe()
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
    os.environ["PIP_NO_INDEX"] = "1"
    os.environ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1"
    from paddlex.inference.utils.official_models import official_models

    def blocked_download(*args, **kwargs):
        raise RuntimeError("unexpected-model-download")

    def blocked_connect(self, address):
        raise RuntimeError("unexpected-network-connect")

    official_models._download_from_hoster = blocked_download
    socket.socket.connect = blocked_connect
    from paddlex.inference.models import create_predictor
    from paddlex.inference.pipelines import create_pipeline, load_pipeline_config

    config = load_pipeline_config("PaddleOCR-VL-1.6")
    config["batch_size"] = 1
    config["use_doc_preprocessor"] = False
    config["SubModules"]["LayoutDetection"]["model_dir"] = str(LAYOUT_MODEL_ROOT)
    config["SubModules"]["VLRecognition"]["model_dir"] = str(VLM_MODEL_ROOT)
    orientation_predictor = create_predictor(
        "PP-LCNet_x1_0_doc_ori",
        model_dir=PAGE_ORIENTATION_MODEL_ROOT,
        device="cpu",
        engine="onnxruntime",
        batch_size=1,
    )
    return orientation_predictor, create_pipeline(config=config, device="gpu:0", use_hpip=False)


def _assert_frozen_assessment_paths(truth_path, output_directory):
    truth_file = Path(truth_path).resolve()
    assessment_root = ASSESSMENT_ROOT.resolve()
    _assert_under(assessment_root, truth_file, "truth-path-outside-assessment")
    _assert_under(ASSESSMENT_ROOT, Path(output_directory), "output-path-outside-assessment")
    relative_path = truth_file.relative_to(assessment_root)
    expected = FROZEN_ASSESSMENT_SHA256.get(relative_path)
    if expected is None:
        raise ValueError("assessment-input-not-approved")
    actual = hashlib.sha256(truth_file.read_bytes()).hexdigest()
    if actual != expected:
        raise ValueError("assessment-sha256-mismatch")
    input_root_relative = FROZEN_ASSESSMENT_INPUT_ROOT.get(relative_path)
    if input_root_relative is None:
        raise ValueError("assessment-input-root-not-approved")
    input_root = (assessment_root / input_root_relative).resolve()
    _assert_under(assessment_root, input_root, "assessment-input-root-outside-assessment")
    if not input_root.is_dir():
        raise FileNotFoundError("assessment-input-root-missing:" + str(input_root))
    return input_root


def _run_local_batch(truth_path, output_directory, selected_ids):
    input_root = _assert_frozen_assessment_paths(truth_path, output_directory)
    started = time.perf_counter()
    orientation_predictor, pipeline = _create_local_components()
    from paddlex.inference.pipelines.components.common.warp_image import rotate_image
    import cv2

    def predict(image_path):
        image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
        if image is None:
            raise FileNotFoundError("screening-image-read-failed:" + str(image_path))
        orientation_result = next(orientation_predictor.predict(image))
        corrected_image, angle = correct_page_orientation(image, orientation_result, rotate_image)
        result = dict(next(pipeline.predict(corrected_image)).json["res"])
        result["benchmark_page_orientation_degrees"] = angle
        return result

    written = run_batch(truth_path, output_directory, selected_ids, predict, input_root=input_root)
    elapsed = time.perf_counter() - started
    print("processed=" + ",".join(written))
    print("elapsed_seconds=" + format(elapsed, ".3f"))
    return written


def main(argv=None):
    parser = argparse.ArgumentParser(description="Guarded, local-only PaddleOCR-VL frozen-assessment driver")
    commands = parser.add_subparsers(dest="command", required=True)
    run = commands.add_parser("run")
    run.add_argument("--truth", required=True)
    run.add_argument("--output", required=True)
    run.add_argument("--ids", required=True, help="comma-separated frozen sample IDs")
    assemble = commands.add_parser("assemble")
    assemble.add_argument("--truth", required=True)
    assemble.add_argument("--output", required=True)
    assemble.add_argument("--run-id", required=True)
    assemble.add_argument("--candidate-file", default="candidate-results.json")
    args = parser.parse_args(argv)
    if args.command == "run":
        _run_local_batch(args.truth, args.output, [sample_id for sample_id in args.ids.split(",") if sample_id])
        return 0
    _assert_frozen_assessment_paths(args.truth, args.output)
    path = assemble_candidate(
        args.truth,
        args.output,
        "PaddleOCR-VL-1.6 GPU guarded frozen assessment",
        "PaddleOCR-VL-1.6",
        "c5630abae1d940eafe0697512a0325494b02ab42",
        args.run_id,
        args.candidate_file,
    )
    print("candidate_results=" + str(path))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
