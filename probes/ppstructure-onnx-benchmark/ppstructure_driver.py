"""One-shot, offline PP-StructureV3 consumer. This is a benchmark probe, not a provider adapter."""
import json
import os
import socket
import sys
import builtins
import tempfile
from pathlib import Path


class OnnxSchemaPreflightError(RuntimeError):
    pass


EXPECTED_MODEL_ROLES = frozenset({
    "layout",
    "block_layout",
    "table_classification",
    "wired_table_structure",
    "wired_table_cell_detection",
    "wireless_table_cell_detection",
    "wireless_table_structure",
    "text_detection",
    "english_recognition",
    "page_orientation",
    "line_orientation",
})


class RefusingSocket(socket.socket):
    def connect(self, *_args, **_kwargs):
        raise RuntimeError("network-refused-by-benchmark-guard")

    def connect_ex(self, *_args, **_kwargs):
        raise RuntimeError("network-refused-by-benchmark-guard")

    def sendto(self, *_args, **_kwargs):
        raise RuntimeError("network-refused-by-benchmark-guard")


def refuse_dns(*_args, **_kwargs):
    raise RuntimeError("dns-refused-by-benchmark-guard")


def install_guard(runtime_root: Path) -> None:
    for key in ("PADDLE_PDX_CACHE_HOME", "HF_HOME", "HF_HUB_CACHE", "TEMP", "TMP"):
        value = runtime_root / "offline-cache" / key.lower()
        value.mkdir(parents=True, exist_ok=True)
        os.environ[key] = str(value)
    font = Path(os.environ.get("WINDIR", r"C:\\Windows")) / "Fonts" / "arial.ttf"
    if not font.is_file():
        raise RuntimeError("required-local-font-unavailable")
    os.environ.update({
        "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1", "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK": "True",
        "PADDLE_PDX_EAGER_INIT": "False", "PADDLE_PDX_DISABLE_DEVICE_FALLBACK": "True",
        "PADDLE_PDX_LOCAL_FONT_FILE_PATH": str(font),
    })
    socket.getaddrinfo = refuse_dns
    socket.socket = RefusingSocket


def preflight_model_bytes(model_bytes: bytes) -> None:
    """Parse only with the official ONNX schema and refuse all external tensor declarations."""
    import onnx
    from onnx import TensorProto

    try:
        model = onnx.load_model_from_string(model_bytes)
    except Exception as error:
        raise OnnxSchemaPreflightError("onnx-schema-invalid") from error

    def inspect_message(message) -> None:
        if isinstance(message, TensorProto):
            if message.data_location == TensorProto.EXTERNAL or message.external_data:
                raise OnnxSchemaPreflightError("onnx-schema-external-data-refused")
        for field, value in message.ListFields():
            if field.type != field.TYPE_MESSAGE:
                continue
            if field.is_repeated:
                for nested_message in value:
                    inspect_message(nested_message)
            else:
                inspect_message(value)

    inspect_message(model)


def external_data_fixture() -> bytes:
    from onnx import TensorProto, helper

    tensor = helper.make_tensor("weight", TensorProto.FLOAT, [1], [1.0])
    tensor.data_location = TensorProto.EXTERNAL
    tensor.ClearField("float_data")
    tensor.ClearField("raw_data")
    location = tensor.external_data.add()
    location.key = "location"
    location.value = "outside.bin"
    output = helper.make_tensor_value_info("weight", TensorProto.FLOAT, [1])
    return helper.make_model(helper.make_graph([], "external", [], [output], [tensor]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def guard_self_test(runtime_root: Path) -> int:
    install_guard(runtime_root)
    import ssl  # Regression: the guard must not break SSL subclass construction.
    import requests
    _ = ssl, requests
    for action, expected in ((lambda: socket.getaddrinfo("example.invalid", 443), "dns-refused"), (lambda: socket.socket().connect(("127.0.0.1", 9)), "network-refused")):
        try:
            action()
        except RuntimeError as error:
            if expected not in str(error):
                raise
        else:
            raise RuntimeError("guard-permitted-network-operation")
    try:
        preflight_model_bytes(external_data_fixture())
    except OnnxSchemaPreflightError as error:
        if str(error) != "onnx-schema-external-data-refused":
            raise
    else:
        raise RuntimeError("schema-preflight-refusal-missing")
    print("guard-self-test: socket/dns guarded; schema refusal passed; no provider session created")
    return 0


def validate_model_roles(raw_model_dirs) -> None:
    if not isinstance(raw_model_dirs, dict):
        raise RuntimeError("invalid-model-role-map")
    actual_roles = set(raw_model_dirs)
    missing_roles = EXPECTED_MODEL_ROLES - actual_roles
    unknown_roles = actual_roles - EXPECTED_MODEL_ROLES
    if missing_roles or unknown_roles:
        details = []
        if missing_roles:
            details.append("missing-required-model-roles:" + ",".join(sorted(missing_roles)))
        if unknown_roles:
            details.append("unknown-model-roles:" + ",".join(sorted(unknown_roles)))
        raise RuntimeError(";".join(details))


def main(config_path: str) -> int:
    config = json.loads(Path(config_path).read_text(encoding="utf-8"))
    bundle_root = Path(config["bundleRoot"]).resolve(strict=True)
    install_guard(Path(config["runtimeRoot"]))
    validate_model_roles(config.get("modelDirs"))
    model_dirs = {}
    for role, raw in config["modelDirs"].items():
        path = Path(raw).resolve(strict=True)
        if bundle_root not in path.parents or not (path / "inference.onnx").is_file() or not (path / "inference.yml").is_file():
            raise RuntimeError("unlisted-or-unsafe-model-directory:" + role)
        model_dirs[role] = str(path)
    for path in model_dirs.values():
        preflight_model_bytes((Path(path) / "inference.onnx").read_bytes())

    # Guard is installed before importing third-party provider code. It is an application guard, not an OS sandbox.
    from paddleocr import PPStructureV3
    import paddlex
    import yaml
    pipeline_config_path = Path(paddlex.__file__).resolve().parent / "configs" / "pipelines" / "PP-StructureV3.yaml"
    pipeline_config = yaml.safe_load(pipeline_config_path.read_text(encoding="utf-8"))
    pipeline_config["batch_size"] = 1
    pipeline_config["SubModules"]["LayoutDetection"]["batch_size"] = 1
    pipeline_config["SubPipelines"]["DocPreprocessor"]["batch_size"] = 1
    pipeline_config["SubPipelines"]["DocPreprocessor"]["SubModules"]["DocOrientationClassify"]["batch_size"] = 1
    for ocr in (pipeline_config["SubPipelines"]["GeneralOCR"], pipeline_config["SubPipelines"]["TableRecognition"]["SubPipelines"]["GeneralOCR"]):
        ocr["batch_size"] = 1
        ocr["SubModules"]["TextLineOrientation"]["batch_size"] = 1
        ocr["SubModules"]["TextRecognition"]["batch_size"] = 1
    pipeline = PPStructureV3(
        paddlex_config=pipeline_config,
        engine="onnxruntime", device="cpu", engine_config={"providers": ["CPUExecutionProvider"]},
        layout_detection_model_name="PP-DocLayout_plus-L", layout_detection_model_dir=model_dirs["layout"],
        region_detection_model_name="PP-DocBlockLayout", region_detection_model_dir=model_dirs["block_layout"],
        doc_orientation_classify_model_name="PP-LCNet_x1_0_doc_ori", doc_orientation_classify_model_dir=model_dirs["page_orientation"],
        text_detection_model_name="PP-OCRv6_medium_det", text_detection_model_dir=model_dirs["text_detection"],
        textline_orientation_model_name="PP-LCNet_x1_0_textline_ori", textline_orientation_model_dir=model_dirs["line_orientation"],
        text_recognition_model_name="PP-OCRv6_medium_rec", text_recognition_model_dir=model_dirs["english_recognition"],
        table_classification_model_name="PP-LCNet_x1_0_table_cls", table_classification_model_dir=model_dirs["table_classification"],
        wired_table_structure_recognition_model_name="SLANeXt_wired", wired_table_structure_recognition_model_dir=model_dirs["wired_table_structure"],
        wireless_table_structure_recognition_model_name="SLANet_plus", wireless_table_structure_recognition_model_dir=model_dirs["wireless_table_structure"],
        wired_table_cells_detection_model_name="RT-DETR-L_wired_table_cell_det", wired_table_cells_detection_model_dir=model_dirs["wired_table_cell_detection"],
        wireless_table_cells_detection_model_name="RT-DETR-L_wireless_table_cell_det", wireless_table_cells_detection_model_dir=model_dirs["wireless_table_cell_detection"],
        table_orientation_classify_model_name="PP-LCNet_x1_0_doc_ori", table_orientation_classify_model_dir=model_dirs["page_orientation"],
        use_doc_orientation_classify=True, use_doc_unwarping=False, use_textline_orientation=True,
        use_table_recognition=True, use_formula_recognition=False, use_chart_recognition=False,
        use_seal_recognition=False, use_region_detection=True, markdown_ignore_labels=[],
    )
    output = []
    for sample in config["samples"]:
        try:
            result = list(pipeline.predict(sample["imagePath"], batch_size=1))
            output.append({"id": sample["id"], "raw": [item.json for item in result], "failureReason": None})
        except Exception as error:
            output.append({"id": sample["id"], "raw": [], "failureReason": str(error)})
    Path(config["outputPath"]).write_text(json.dumps({"engine": "PPStructureV3", "effectiveBatchSize": 1, "samples": output}, indent=2), encoding="utf-8")
    return 0 if not any(item["failureReason"] for item in output) else 1


def preflight_refusal_test() -> int:
    imported = []
    original_import = builtins.__import__

    def observe_import(name, *args, **kwargs):
        if name.startswith(("paddleocr", "paddlex", "onnxruntime")):
            imported.append(name)
        return original_import(name, *args, **kwargs)

    try:
        builtins.__import__ = observe_import
        try:
            with tempfile.TemporaryDirectory() as temporary_directory:
                temporary_root = Path(temporary_directory)
                bundle_root = temporary_root / "bundle"
                model_directory = bundle_root / "external-model"
                model_directory.mkdir(parents=True)
                (model_directory / "inference.onnx").write_bytes(external_data_fixture())
                (model_directory / "inference.yml").write_text("synthetic: true\n", encoding="utf-8")
                config_path = temporary_root / "config.json"
                config_path.write_text(json.dumps({
                    "bundleRoot": str(bundle_root),
                    "runtimeRoot": str(temporary_root / "runtime"),
                    "modelDirs": {role: str(model_directory) for role in EXPECTED_MODEL_ROLES},
                    "samples": [],
                    "outputPath": str(temporary_root / "output.json"),
                }), encoding="utf-8")
                main(str(config_path))
        except RuntimeError as error:
            if str(error) != "onnx-schema-external-data-refused":
                raise
        else:
            raise RuntimeError("schema-preflight-refusal-missing")
    finally:
        builtins.__import__ = original_import
    if imported:
        raise RuntimeError("provider-constructor-imported-before-preflight:" + ",".join(imported))
    print("main-onnx-schema-preflight-refusal: provider/ORT imports not called")
    return 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--guard-self-test"]:
        raise SystemExit(guard_self_test(Path(r"J:\\Models\\runtimes\\ppstructurev3-3.7.0-ort-1.30.0-cp312")))
    if sys.argv[1:] == ["--preflight-refusal-test"]:
        raise SystemExit(preflight_refusal_test())
    raise SystemExit(main(sys.argv[1]))
