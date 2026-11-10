#!/usr/bin/env python3
"""Generate the embedded, fully offline ContextFlow policy model.

The model is deliberately tiny: a logistic policy (12 features -> 1 score).
Version 2 adds background CPU share and three on-device learned signals.
It runs through ONNX Runtime on CPU and adds no network or cloud dependency.
The coefficients mirror the managed fallback in LocalAiPolicyEngine.cs.

Usage:
    python tools/generate_policy_model.py
"""
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src" / "ContextFlow" / "Assets" / "policy.onnx"

# Keep this ordering identical to PolicyFeatures.ToArray().
FEATURES = [
    ("idle", 2.00),
    ("inactive_page", 2.40),
    ("memory_pressure", 1.40),
    ("working_set", 1.60),
    ("system_cpu", 0.50),
    ("background_cpu", 2.20),
    ("viewport_distance", 0.60),
    ("minimized", 0.80),
    ("foreground", -5.00),
    ("learned_affinity", -1.20),
    ("learned_regret", -2.60),
    ("predicted_use_soon", -1.40),
]
N = len(FEATURES)
WEIGHTS = np.array([w for _, w in FEATURES], dtype=np.float32).reshape(N, 1)

BIAS = np.array([-3.00], dtype=np.float32)

features = helper.make_tensor_value_info("features", TensorProto.FLOAT, [1, N])
output = helper.make_tensor_value_info("optimization_score", TensorProto.FLOAT, [1, 1])

weight_initializer = numpy_helper.from_array(WEIGHTS, name="policy_weights")
bias_initializer = numpy_helper.from_array(BIAS, name="policy_bias")

matmul = helper.make_node("MatMul", ["features", "policy_weights"], ["logit"], name="policy_matmul")
add = helper.make_node("Add", ["logit", "policy_bias"], ["policy_logit"], name="policy_bias_add")
sigmoid = helper.make_node("Sigmoid", ["policy_logit"], ["optimization_score"], name="policy_sigmoid")

graph = helper.make_graph(
    [matmul, add, sigmoid],
    "ContextFlowLocalPolicy",
    [features],
    [output],
    [weight_initializer, bias_initializer],
    doc_string=(
        "Offline logistic policy used by ContextFlow to score whether an inactive "
        "window group is a safe candidate for priority throttling, working-set "
        "trimming, or DWM composition cloaking."
    ),
)

model = helper.make_model(
    graph,
    producer_name="ContextFlow",
    producer_version="2.0.0",
    opset_imports=[helper.make_opsetid("", 17)],
    doc_string="Fully local ContextFlow hardware optimization policy. No network required.",
)
model.ir_version = 8
model.metadata_props.add(key="features", value=",".join(n for n, _ in FEATURES))
model.metadata_props.add(key="license", value="Proprietary - ContextFlow")
model.metadata_props.add(key="output", value="optimization_score")

onnx.checker.check_model(model)
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
onnx.save(model, OUTPUT)
print(f"Wrote {OUTPUT} ({OUTPUT.stat().st_size} bytes)")
