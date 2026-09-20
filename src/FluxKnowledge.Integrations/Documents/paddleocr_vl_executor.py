"""Fixed, offline PaddleOCR-VL worker for selected private PDF page images.

The .NET parent verifies and holds every model file before this process starts.  This script has
no model, runtime, or source-path arguments: it accepts only a private temporary-page manifest
and writes a bounded result next to it.  It never prints document content.
"""

import argparse
import json
import math
import os
import socket
import stat
import sys
from html.parser import HTMLParser
from pathlib import Path


MODEL_ROOT = Path(r"J:\Models")
VLM_MODEL_ROOT = Path(r"J:\Models\bundles\paddleocr-vl-1.6\c5630abae1d940eafe0697512a0325494b02ab42")
LAYOUT_MODEL_ROOT = Path(r"J:\Models\bundles\pp-doclayoutv3\7b48a7566925fa464281f930c58eee04fe2c862a")
PAGE_ORIENTATION_MODEL_ROOT = Path(
    r"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\bundles\page_orientation"
)
MAXIMUM_RESULT_BYTES = 4 * 1024 * 1024
MAXIMUM_PAGES = 2_000
MAXIMUM_BLOCKS_PER_PAGE = 3_000
MIN_NONZERO_ORIENTATION_CONFIDENCE = 0.80


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


def _refuse(reason):
    raise RuntimeError(reason)


def _is_plain_child(name):
    return (
        isinstance(name, str)
        and name
        and name not in {".", ".."}
        and Path(name).name == name
        and "\x00" not in name
    )


def _assert_fixed_model_paths_are_safe():
    try:
        model_parts = MODEL_ROOT.parts
        if not model_parts or MODEL_ROOT.drive.lower() != "j:":
            _refuse("model-root-invalid")
        for path in (MODEL_ROOT, VLM_MODEL_ROOT, LAYOUT_MODEL_ROOT, PAGE_ORIENTATION_MODEL_ROOT):
            if path.drive.lower() != "j:" or path.parts[: len(model_parts)] != model_parts:
                _refuse("model-path-outside-store")
            current = Path(model_parts[0])
            for part in path.parts[1:]:
                current = current / part
                attributes = getattr(os.lstat(current), "st_file_attributes", 0)
                if attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    _refuse("model-path-reparse-point")
            if not path.is_dir():
                _refuse("model-path-missing")
    except OSError as exception:
        raise RuntimeError("model-path-unavailable") from exception


def _configure_offline_environment():
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
    os.environ["PIP_NO_INDEX"] = "1"
    os.environ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1"

    def blocked_download(*args, **kwargs):
        raise RuntimeError("unexpected-model-download")

    def blocked_connect(self, address):
        raise RuntimeError("unexpected-network-connect")

    socket.socket.connect = blocked_connect
    from paddlex.inference.utils.official_models import official_models

    official_models._download_from_hoster = blocked_download


def _create_components():
    _assert_fixed_model_paths_are_safe()
    _configure_offline_environment()
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


def _normalised_bbox(block):
    bbox = block.get("block_bbox")
    if not isinstance(bbox, (list, tuple)) or len(bbox) != 4:
        return None
    try:
        x0, y0, x1, y1 = (float(value) for value in bbox)
    except (TypeError, ValueError):
        return None
    if not all(math.isfinite(value) for value in (x0, y0, x1, y1)) or x1 <= x0 or y1 <= y0:
        return None
    left = max(0, math.floor(x0))
    top = max(0, math.floor(y0))
    width = max(1, math.ceil(x1) - left)
    height = max(1, math.ceil(y1) - top)
    return left, top, width, height


def _blocks_in_reading_order(blocks):
    annotated = [(index, block, _normalised_bbox(block)) for index, block in enumerate(blocks)]
    if not annotated or any(bbox is None for _, _, bbox in annotated):
        return list(blocks)
    x_min = min(bbox[0] for _, _, bbox in annotated)
    x_max = max(bbox[0] + bbox[2] for _, _, bbox in annotated)
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


def _table_text(content):
    parser = _TableRowsParser()
    parser.feed(content)
    parser.close()
    return "\n".join("\t".join(cell for cell in row) for row in parser.rows)


def _output_blocks(raw_result):
    raw_blocks = raw_result.get("parsing_res_list")
    if not isinstance(raw_blocks, list) or len(raw_blocks) > MAXIMUM_BLOCKS_PER_PAGE:
        _refuse("provider-blocks-invalid")
    output = []
    for block in _blocks_in_reading_order(raw_blocks):
        if not isinstance(block, dict):
            _refuse("provider-block-invalid")
        bbox = _normalised_bbox(block)
        if bbox is None:
            _refuse("provider-block-invalid")
        label = block.get("block_label")
        kind = label if label in {"table", "title", "header", "footer", "figure"} else "text"
        content = block.get("block_content") or ""
        if not isinstance(content, str):
            _refuse("provider-block-invalid")
        if kind == "table":
            content = _table_text(content) or content
        left, top, width, height = bbox
        output.append(
            {
                "kind": kind,
                "left": left,
                "top": top,
                "width": width,
                "height": height,
                "text": content,
            }
        )
    return output


def _correct_orientation(image, orientation_result, rotate):
    labels = orientation_result.get("label_names")
    scores = orientation_result.get("scores")
    if not isinstance(labels, list) or len(labels) != 1 or isinstance(scores, (str, bytes)):
        _refuse("provider-orientation-invalid")
    try:
        if len(scores) != 1:
            _refuse("provider-orientation-invalid")
        angle = int(labels[0])
        confidence = float(scores[0])
    except (IndexError, TypeError, ValueError) as exception:
        raise RuntimeError("provider-orientation-invalid") from exception
    if angle not in {0, 90, 180, 270} or not 0 <= confidence <= 1:
        _refuse("provider-orientation-invalid")
    if angle != 0 and confidence < MIN_NONZERO_ORIENTATION_CONFIDENCE:
        return image, 0
    return rotate(image, angle), angle


def _read_input(input_path, output_path):
    source = Path(input_path).resolve(strict=True)
    destination = Path(output_path).resolve(strict=False)
    work_directory = source.parent.resolve(strict=True)
    if destination.parent != work_directory or destination.name != "output.json":
        _refuse("io-path-invalid")
    document = json.loads(source.read_text(encoding="utf-8"))
    pages = document.get("pages") if isinstance(document, dict) else None
    if not isinstance(pages, list) or not pages or len(pages) > MAXIMUM_PAGES:
        _refuse("input-pages-invalid")
    seen = set()
    inputs = []
    for page in pages:
        if not isinstance(page, dict):
            _refuse("input-page-invalid")
        page_index = page.get("pageIndex")
        image_name = page.get("image")
        if not isinstance(page_index, int) or page_index < 0 or page_index in seen or not _is_plain_child(image_name):
            _refuse("input-page-invalid")
        image_path = (work_directory / image_name).resolve(strict=True)
        if image_path.parent != work_directory or image_path.suffix.lower() != ".png":
            _refuse("input-page-invalid")
        seen.add(page_index)
        inputs.append((page_index, image_path))
    return destination, inputs


def _write_result(destination, result):
    encoded = json.dumps(result, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(encoded) > MAXIMUM_RESULT_BYTES:
        _refuse("provider-result-too-large")
    with destination.open("xb") as stream:
        stream.write(encoded)


def main(argv=None):
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    destination, inputs = _read_input(args.input, args.output)
    orientation_predictor, pipeline = _create_components()
    from paddlex.inference.pipelines.components.common.warp_image import rotate_image
    import cv2

    pages = []
    for page_index, image_path in inputs:
        image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
        if image is None:
            _refuse("input-image-invalid")
        orientation_result = next(orientation_predictor.predict(image))
        corrected, orientation_degrees = _correct_orientation(image, orientation_result, rotate_image)
        prediction = next(pipeline.predict(corrected))
        raw_result = dict(prediction.json["res"])
        pages.append(
            {
                "pageIndex": page_index,
                "orientationDegrees": orientation_degrees,
                "blocks": _output_blocks(raw_result),
            }
        )
    _write_result(destination, {"succeeded": True, "reasonCode": "document-ocr-complete", "pages": pages})
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception:
        # Do not expose paths, document content, model identifiers, or provider diagnostics via IIS.
        raise SystemExit(1)
