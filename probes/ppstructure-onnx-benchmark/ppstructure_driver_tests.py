import importlib.util
import inspect
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import onnx
from onnx import TensorProto, helper


DRIVER_PATH = Path(__file__).with_name("ppstructure_driver.py")
SPEC = importlib.util.spec_from_file_location("ppstructure_driver", DRIVER_PATH)
driver = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(driver)


def inline_model() -> bytes:
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [1])
    node = helper.make_node("Identity", ["x"], ["y"])
    return helper.make_model(helper.make_graph([node], "inline", [x], [y]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def external_tensor(name: str) -> onnx.TensorProto:
    tensor = helper.make_tensor(name, TensorProto.FLOAT, [1], [1.0])
    tensor.data_location = TensorProto.EXTERNAL
    tensor.ClearField("float_data")
    tensor.ClearField("raw_data")
    entry = tensor.external_data.add()
    entry.key = "location"
    entry.value = "outside.bin"
    return tensor


def external_tensor_without_metadata(name: str) -> onnx.TensorProto:
    tensor = helper.make_tensor(name, TensorProto.FLOAT, [1], [1.0])
    tensor.data_location = TensorProto.EXTERNAL
    tensor.ClearField("float_data")
    tensor.ClearField("raw_data")
    return tensor


def external_initializer_model() -> bytes:
    initializer = external_tensor("weight")
    output = helper.make_tensor_value_info("weight", TensorProto.FLOAT, [1])
    return helper.make_model(helper.make_graph([], "external-initializer", [], [output], [initializer]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def external_metadata_without_external_location_model() -> bytes:
    initializer = helper.make_tensor("weight", TensorProto.FLOAT, [1], [1.0])
    entry = initializer.external_data.add()
    entry.key = "location"
    entry.value = "outside.bin"
    output = helper.make_tensor_value_info("weight", TensorProto.FLOAT, [1])
    return helper.make_model(helper.make_graph([], "external-metadata", [], [output], [initializer]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def nested_attribute_model() -> bytes:
    output = helper.make_tensor_value_info("value", TensorProto.FLOAT, [1])
    then_graph = helper.make_graph([], "then", [], [output], [external_tensor("value")])
    else_initializer = helper.make_tensor("value", TensorProto.FLOAT, [1], [0.0])
    else_graph = helper.make_graph([], "else", [], [output], [else_initializer])
    condition = helper.make_tensor_value_info("condition", TensorProto.BOOL, [])
    node = helper.make_node("If", ["condition"], ["result"], then_branch=then_graph, else_branch=else_graph)
    result = helper.make_tensor_value_info("result", TensorProto.FLOAT, [1])
    return helper.make_model(helper.make_graph([node], "nested", [condition], [result]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def sparse_external_model() -> bytes:
    values = external_tensor("sparse-values")
    indices = helper.make_tensor("sparse-indices", TensorProto.INT64, [1], [0])
    sparse = helper.make_sparse_tensor(values, indices, [1])
    output = helper.make_tensor_value_info("out", TensorProto.FLOAT, [1])
    model = helper.make_model(helper.make_graph([], "sparse", [], [output], sparse_initializer=[sparse]), opset_imports=[helper.make_opsetid("", 18)])
    return model.SerializeToString()


def sparse_attribute_external_model() -> bytes:
    values = external_tensor("sparse-attribute-values")
    indices = helper.make_tensor("sparse-attribute-indices", TensorProto.INT64, [1], [0])
    sparse = helper.make_sparse_tensor(values, indices, [1])
    node = helper.make_node("Constant", [], ["out"], sparse_value=sparse)
    output = helper.make_tensor_value_info("out", TensorProto.FLOAT, [1])
    return helper.make_model(helper.make_graph([node], "sparse-attribute", [], [output]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def valid_sparse_initializer_model() -> onnx.ModelProto:
    values = helper.make_tensor("sparse-values", TensorProto.FLOAT, [1], [1.0])
    indices = helper.make_tensor("sparse-indices", TensorProto.INT64, [1], [0])
    sparse = helper.make_sparse_tensor(values, indices, [1])
    output = helper.make_tensor_value_info("out", TensorProto.FLOAT, [1])
    node = helper.make_node("Identity", ["sparse-values"], ["out"])
    return helper.make_model(helper.make_graph([node], "valid-sparse", [], [output], sparse_initializer=[sparse]), opset_imports=[helper.make_opsetid("", 18)])


def model_with_attribute(attribute: onnx.AttributeProto) -> bytes:
    input_value = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1])
    output = helper.make_tensor_value_info("out", TensorProto.FLOAT, [1])
    node = helper.make_node("Identity", ["input"], ["out"])
    node.attribute.add().CopyFrom(attribute)
    return helper.make_model(helper.make_graph([node], "attribute", [input_value], [output]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def undefined_tensor_attribute_model() -> bytes:
    attribute = onnx.AttributeProto()
    attribute.name = "untyped-tensor"
    attribute.t.CopyFrom(external_tensor("untyped-tensor-value"))
    return model_with_attribute(attribute)


def repeated_tensor_attribute_model() -> bytes:
    attribute = onnx.AttributeProto()
    attribute.name = "untyped-tensors"
    attribute.tensors.add().CopyFrom(external_tensor("repeated-tensor-value"))
    return model_with_attribute(attribute)


def repeated_sparse_attribute_model() -> bytes:
    values = external_tensor("repeated-sparse-values")
    indices = helper.make_tensor("repeated-sparse-indices", TensorProto.INT64, [1], [0])
    attribute = onnx.AttributeProto()
    attribute.name = "untyped-sparse-tensors"
    attribute.sparse_tensors.add().CopyFrom(helper.make_sparse_tensor(values, indices, [1]))
    return model_with_attribute(attribute)


def repeated_graph_attribute_model() -> bytes:
    output = helper.make_tensor_value_info("external-output", TensorProto.FLOAT, [1])
    attribute = onnx.AttributeProto()
    attribute.name = "untyped-graphs"
    attribute.graphs.add().CopyFrom(helper.make_graph([], "external-graph", [], [output], [external_tensor("external-output")]))
    return model_with_attribute(attribute)


def sparse_indices_external_model() -> bytes:
    values = helper.make_tensor("sparse-values", TensorProto.FLOAT, [1], [1.0])
    sparse = helper.make_sparse_tensor(values, external_tensor("sparse-indices"), [1])
    output = helper.make_tensor_value_info("out", TensorProto.FLOAT, [1])
    node = helper.make_node("Identity", ["sparse-values"], ["out"])
    return helper.make_model(helper.make_graph([node], "sparse-indices-external", [], [output], sparse_initializer=[sparse]), opset_imports=[helper.make_opsetid("", 18)]).SerializeToString()


def model_function_external_tensor_model() -> bytes:
    function = helper.make_function(
        "test.preflight",
        "ExternalConstant",
        [],
        ["value"],
        [helper.make_node("Constant", [], ["value"], value=external_tensor("function-value"))],
        [helper.make_opsetid("", 18)],
    )
    model = helper.make_model(
        helper.make_graph([], "inline-root", [], []),
        opset_imports=[helper.make_opsetid("", 18)],
    )
    model.functions.append(function)
    return model.SerializeToString()


def model_function_attribute_proto_external_model() -> bytes:
    attribute = onnx.AttributeProto()
    attribute.name = "function-default"
    attribute.t.CopyFrom(external_tensor("function-default-value"))
    function = helper.make_function(
        "test.preflight",
        "ExternalDefault",
        [],
        ["value"],
        [helper.make_node("Constant", [], ["value"], value=helper.make_tensor("value", TensorProto.FLOAT, [1], [1.0]))],
        [helper.make_opsetid("", 18)],
        attributes=["function-default"],
        attribute_protos=[attribute],
    )
    model = helper.make_model(helper.make_graph([], "inline-root", [], []), opset_imports=[helper.make_opsetid("", 18)])
    model.functions.append(function)
    return model.SerializeToString()


def training_graph_with_external_tensor(graph_name: str) -> onnx.GraphProto:
    output = helper.make_tensor_value_info("trained-weight", TensorProto.FLOAT, [1])
    return helper.make_graph([], graph_name, [], [output], [external_tensor("trained-weight")])


def training_initialization_external_model() -> bytes:
    model = helper.make_model(helper.make_graph([], "inline-root", [], []), opset_imports=[helper.make_opsetid("", 18)])
    model.training_info.append(helper.make_training_info(
        algorithm=helper.make_graph([], "algorithm-inline", [], []),
        algorithm_bindings=[],
        initialization=training_graph_with_external_tensor("initialization-external"),
        initialization_bindings=[],
    ))
    return model.SerializeToString()


def training_algorithm_external_model() -> bytes:
    model = helper.make_model(helper.make_graph([], "inline-root", [], []), opset_imports=[helper.make_opsetid("", 18)])
    model.training_info.append(helper.make_training_info(
        algorithm=training_graph_with_external_tensor("algorithm-external"),
        algorithm_bindings=[],
        initialization=helper.make_graph([], "initialization-inline", [], []),
        initialization_bindings=[],
    ))
    return model.SerializeToString()


class OnnxSchemaPreflightTests(unittest.TestCase):
    def test_valid_inline_model_is_accepted(self):
        self.assertIsNone(driver.preflight_model_bytes(inline_model()))

    def test_external_initializer_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(external_initializer_model())

    def test_external_metadata_is_refused_even_without_external_location(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(external_metadata_without_external_location_model())

    def test_external_location_without_metadata_is_refused(self):
        output = helper.make_tensor_value_info("weight", TensorProto.FLOAT, [1])
        model = helper.make_model(helper.make_graph([], "external-location", [], [output], [external_tensor_without_metadata("weight")]), opset_imports=[helper.make_opsetid("", 18)])
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(model.SerializeToString())

    def test_undefined_attribute_tag_holding_external_tensor_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(undefined_tensor_attribute_model())

    def test_repeated_tensor_sparse_and_graph_attributes_are_refused(self):
        for model in (repeated_tensor_attribute_model(), repeated_sparse_attribute_model(), repeated_graph_attribute_model()):
            with self.subTest(model=model):
                with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
                    driver.preflight_model_bytes(model)

    def test_external_tensor_in_nested_graph_attribute_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(nested_attribute_model())

    def test_external_sparse_tensor_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(sparse_external_model())

    def test_external_sparse_indices_are_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(sparse_indices_external_model())

    def test_valid_sparse_initializer_is_checker_valid_and_accepted(self):
        model = valid_sparse_initializer_model()
        onnx.checker.check_model(model)
        self.assertIsNone(driver.preflight_model_bytes(model.SerializeToString()))

    def test_external_sparse_tensor_in_attribute_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(sparse_attribute_external_model())

    def test_external_tensor_in_model_function_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(model_function_external_tensor_model())

    def test_external_tensor_in_model_function_attribute_default_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(model_function_attribute_proto_external_model())

    def test_external_tensor_in_training_initialization_graph_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(training_initialization_external_model())

    def test_external_tensor_in_training_algorithm_graph_is_refused(self):
        with self.assertRaisesRegex(driver.OnnxSchemaPreflightError, "external-data"):
            driver.preflight_model_bytes(training_algorithm_external_model())

    def test_refusal_does_not_import_provider_or_onnxruntime(self):
        import sys
        sys.modules.pop("paddleocr", None)
        sys.modules.pop("paddlex", None)
        sys.modules.pop("onnxruntime", None)
        with self.assertRaises(driver.OnnxSchemaPreflightError):
            driver.preflight_model_bytes(external_initializer_model())
        self.assertNotIn("paddleocr", sys.modules)
        self.assertNotIn("paddlex", sys.modules)
        self.assertNotIn("onnxruntime", sys.modules)


class ProbeRoleContractTests(unittest.TestCase):
    def test_guard_self_test_source_contains_no_ort_session_construction(self):
        source = inspect.getsource(driver.guard_self_test)
        self.assertNotIn("onnxruntime", source)
        self.assertNotIn("InferenceSession", source)

    def test_main_refuses_missing_role_before_provider_import(self):
        class ProviderImportAttempted(RuntimeError):
            pass

        original_import = __import__

        def refuse_provider_import(name, *args, **kwargs):
            if name.startswith(("paddleocr", "paddlex", "onnxruntime")):
                raise ProviderImportAttempted(name)
            return original_import(name, *args, **kwargs)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            bundle.mkdir()
            config_path = root / "config.json"
            config_path.write_text(json.dumps({
                "bundleRoot": str(bundle),
                "runtimeRoot": str(root / "runtime"),
                "modelDirs": {},
                "samples": [],
                "outputPath": str(root / "output.json"),
            }), encoding="utf-8")
            with mock.patch.object(driver, "install_guard"), mock.patch("builtins.__import__", new=refuse_provider_import):
                with self.assertRaisesRegex(RuntimeError, "missing-required-model-roles"):
                    driver.main(str(config_path))

    def test_main_refuses_unknown_role_before_provider_import(self):
        class ProviderImportAttempted(RuntimeError):
            pass

        original_import = __import__

        def refuse_provider_import(name, *args, **kwargs):
            if name.startswith(("paddleocr", "paddlex", "onnxruntime")):
                raise ProviderImportAttempted(name)
            return original_import(name, *args, **kwargs)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            bundle.mkdir()
            config_path = root / "config.json"
            config_path.write_text(json.dumps({
                "bundleRoot": str(bundle),
                "runtimeRoot": str(root / "runtime"),
                "modelDirs": {"unexpected": str(bundle)},
                "samples": [],
                "outputPath": str(root / "output.json"),
            }), encoding="utf-8")
            with mock.patch.object(driver, "install_guard"), mock.patch("builtins.__import__", new=refuse_provider_import):
                with self.assertRaisesRegex(RuntimeError, "missing-required-model-roles.*unknown-model-roles"):
                    driver.main(str(config_path))

    def test_cli_main_preflight_refusal_uses_complete_temporary_role_map(self):
        result = subprocess.run(
            [sys.executable, str(DRIVER_PATH), "--preflight-refusal-test"],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("main-onnx-schema-preflight-refusal: provider/ORT imports not called", result.stdout)


if __name__ == "__main__":
    unittest.main()
