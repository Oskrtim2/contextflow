using System.Reflection;
using ContextFlow.Infrastructure;
using ContextFlow.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ContextFlow.Services;

/// <summary>
/// Fully offline policy engine. A tiny, embedded logistic policy model is
/// evaluated through ONNX Runtime. If the native runtime or model is missing,
/// the same deterministic coefficients are used by a managed fallback so
/// optimization never depends on the network or a cloud service.
/// </summary>
public sealed class LocalAiPolicyEngine : IDisposable
{
    public const string ModelResourceName = "ContextFlow.Assets.policy.onnx";
    public const string InputName = "features";
    public const string OutputName = "optimization_score";

    // Must mirror tools/generate_policy_model.py exactly. Keeping the fallback
    // coefficients in source makes the policy explainable and testable.
    private static readonly float[] Weights =
    [
        2.00f,  // idle pressure
        2.40f,  // window group lives on an inactive page
        1.40f,  // system memory pressure
        1.60f,  // family working-set pressure
        0.50f,  // system CPU pressure
        2.20f,  // family CPU burned while in background
        0.60f,  // logical viewport distance
        0.80f,  // all windows minimized
        -5.00f, // foreground / active
        -1.20f, // learned affinity (apps the user lives in)
        -2.60f, // learned regret (user came back soon after optimization)
        -1.40f  // learned daily rhythm: likely to be used this hour
    ];

    private const float Bias = -3.00f;
    public const string ModelVersion = "2";

    private readonly AppLog _log;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private bool _initializationAttempted;
    private bool _disposed;

    public LocalAiPolicyEngine(AppLog log)
    {
        _log = log;
    }

    public bool IsOnnxAvailable { get; private set; }

    public string EngineLabel => IsOnnxAvailable ? "ONNX v2 · local" : "Heuristic v2 · local";

    /// <summary>Loads the embedded ONNX graph eagerly so the UI can display the active engine.</summary>
    public void WarmUp()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                EnsureInitialized();
            }
        }
    }

    public double Evaluate(PolicyFeatures features)
    {
        var values = features.ToArray();
        if (TryEvaluateOnnx(values, out var onnxScore))
        {
            return Math.Clamp(onnxScore, 0, 1);
        }

        return EvaluateFallback(values);
    }

    private bool TryEvaluateOnnx(float[] values, out double score)
    {
        score = 0;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            EnsureInitialized();
            if (_session is null)
            {
                return false;
            }

            try
            {
                var tensor = new DenseTensor<float>(values, new[] { 1, values.Length });
                using var results = _session.Run(
                [
                    NamedOnnxValue.CreateFromTensor(InputName, tensor)
                ]);

                var output = results.FirstOrDefault(r => r.Name == OutputName) ?? results.First();
                var outputTensor = output.AsTensor<float>();
                score = outputTensor[0, 0];
                return double.IsFinite(score);
            }
            catch (Exception ex)
            {
                _log.Error("ONNX policy inference failed; switching to managed policy.", ex);
                _session?.Dispose();
                _session = null;
                IsOnnxAvailable = false;
                return false;
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_initializationAttempted)
        {
            return;
        }

        _initializationAttempted = true;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ModelResourceName);
            if (stream is null)
            {
                _log.Warn("Embedded ONNX policy model was not found; managed fallback active.");
                return;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            _session = new InferenceSession(memory.ToArray(), options);
            var width = _session.InputMetadata.TryGetValue(InputName, out var input) && input.Dimensions.Length == 2
                ? input.Dimensions[1]
                : -1;
            if (width != PolicyFeatures.Count)
            {
                _log.Warn($"Embedded ONNX policy expects {width} features, engine provides {PolicyFeatures.Count}; managed fallback active.");
                _session.Dispose();
                _session = null;
                return;
            }

            IsOnnxAvailable = true;
            _log.Info("Embedded ONNX policy model loaded.");
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            IsOnnxAvailable = false;
            _log.Error("ONNX Runtime could not be initialized; using managed policy fallback.", ex);
        }
    }

    private static double EvaluateFallback(float[] values)
    {
        var logit = Bias;
        for (var i = 0; i < Math.Min(values.Length, Weights.Length); i++)
        {
            logit += Weights[i] * values[i];
        }

        return 1.0 / (1.0 + Math.Exp(-logit));
    }

    /// <summary>Used by the built-in --self-test switch for field diagnostics.</summary>
    public static double EvaluateFallbackForTest(PolicyFeatures features) => EvaluateFallback(features.ToArray());

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }
}
