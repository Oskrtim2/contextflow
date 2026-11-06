#!/usr/bin/env python3
"""Generate the embedded, fully offline ContextFlow policy model.

The model is deliberately tiny: a logistic policy (9 features -> 1 score).
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
WEIGHTS = np.array(
    [
        1.90,   # idle pressure
        2.80,   # inactive capsule
        1.10,   # system memory pressure
        1.60,   # process working-set pressure
        0.50,   # CPU pressure
        0.80,   # logical viewport distance
        0.30,   # window count pressure
        0.40,   # minimized
        -4.00,  # foreground / active
    ],
    dtype=np.float32,
).reshape(9, 1)

BIAS = np.array([-3.20], dtype=np.float32)

features = helper.make_tensor_value_info("features", TensorProto.FLOAT, [1, 9])
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
    producer_version="1.0.0",
    opset_imports=[helper.make_opsetid("", 17)],
    doc_string="Fully local ContextFlow hardware optimization policy. No network required.",
)
model.ir_version = 8
model.metadata_props.add(key="features", value="idle,inactive_capsule,memory_pressure,working_set,cpu,viewport_distance,window_count,minimized,foreground")
model.metadata_props.add(key="license", value="Proprietary - ContextFlow")
model.metadata_props.add(key="output", value="optimization_score")

onnx.checker.check_model(model)
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
onnx.save(model, OUTPUT)
print(f"Wrote {OUTPUT} ({OUTPUT.stat().st_size} bytes)")
