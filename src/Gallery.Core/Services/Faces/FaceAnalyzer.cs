using Microsoft.ML.OnnxRuntime;

namespace Gallery.Core.Services.Faces;

/// <summary>Finds faces and fingerprints them. Safe to call from several threads at once.</summary>
public interface IFaceAnalyzer : IDisposable
{
    /// <summary>The model pack that makes the fingerprints. Fingerprints from different packs can't be compared.</summary>
    string Pack { get; }

    /// <summary>The backend each model runs on, e.g. "detector on CoreML, fingerprints on CoreML".</summary>
    string Backends { get; }

    /// <summary>Finds faces in the picture, in its pixel coordinates.</summary>
    List<DetectedFace> Detect(FacePixels picture);

    /// <summary>The fingerprint of an aligned 112 × 112 face (see <see cref="FaceAligner"/>), scaled to length 1.</summary>
    float[] Embed(FacePixels alignedFace);
}

/// <summary>
/// Runs a pack's two models in ONNX Runtime, one session each, shared by all workers. Each call uses one thread, because
/// several one-thread workers are faster than one worker with many threads. The provider is chosen per model:
/// <list type="bullet">
/// <item><c>auto</c>: CoreML on macOS, otherwise the CPU.</item>
/// <item><c>coreml</c>: CoreML for fingerprints. For the detector, only at size 640 with a square input, because the
/// model files declare output shapes for a 640 × 640 input and CoreML depends on them.</item>
/// <item><c>cuda</c>, <c>openvino</c>: only in builds that include them.</item>
/// </list>
/// If a model's backend fails to start or fails while running, that model switches to the CPU for the rest of the run
/// and writes one log line.
/// </summary>
public sealed class OnnxFaceAnalyzer : IFaceAnalyzer
{
    private readonly ModelRunner _detector;
    private readonly ModelRunner _recognizer;
    private readonly int _detectorSize;

    public OnnxFaceAnalyzer(string pack, string detectorPath, string recognizerPath, string provider, int detectorSize, ILogger log)
    {
        Pack = pack;
        _detectorSize = detectorSize;
        provider = (provider ?? "auto").Trim().ToLowerInvariant();
        if (provider == "auto") provider = OperatingSystem.IsMacOS() ? "coreml" : "cpu";
        var detectorProvider = provider == "coreml" && detectorSize != 640 ? "cpu" : provider;
        _detector = new ModelRunner("detector", detectorPath, detectorProvider, log);
        _recognizer = new ModelRunner("fingerprints", recognizerPath, provider, log);
    }

    public string Pack { get; }

    public string Backends => $"detector on {_detector.Backend}, fingerprints on {_recognizer.Backend}";

    public List<DetectedFace> Detect(FacePixels picture) =>
        _detector.Run(session => FaceDetector.Run(session, FaceDetector.Prepare(picture, _detectorSize, square: _detector.Provider == "coreml")));

    public float[] Embed(FacePixels alignedFace) => _recognizer.Run(session => FaceRecognizer.Run(session, alignedFace));

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
    }

    /// <summary>One model's session. Switches to the CPU when its backend fails.</summary>
    private sealed class ModelRunner : IDisposable
    {
        private readonly string _what;
        private readonly string _path;
        private readonly ILogger _log;
        private readonly object _sync = new();
        private InferenceSession _session;
        private readonly List<InferenceSession> _retired = new();

        public ModelRunner(string what, string path, string provider, ILogger log)
        {
            _what = what;
            _path = path;
            _log = log;
            Provider = provider;
            try { _session = new InferenceSession(path, Options(provider)); }
            catch (Exception ex) when (provider != "cpu")
            {
                _log.LogWarning("Faces: the {What} model can't use {Provider} ({Error}); using the CPU", what, provider, FirstLine(ex.Message));
                Provider = "cpu";
                _session = new InferenceSession(path, Options("cpu"));
            }
        }

        public string Provider { get; private set; }

        public string Backend => Provider switch { "cpu" => "the CPU", "coreml" => "CoreML", "cuda" => "CUDA", "openvino" => "OpenVINO", var p => p };

        public T Run<T>(Func<InferenceSession, T> run)
        {
            InferenceSession session;
            lock (_sync) session = _session;
            try { return run(session); }
            catch (Exception ex) when (ex is OnnxRuntimeException && Provider != "cpu")
            {
                lock (_sync)
                {
                    if (Provider != "cpu")
                    {
                        _log.LogWarning("Faces: the {What} model failed on {Provider} ({Error}); using the CPU from now on", _what, Backend, FirstLine(ex.Message));
                        // Other workers may still use the old session, so dispose it only with this runner.
                        _retired.Add(_session);
                        _session = new InferenceSession(_path, Options("cpu"));
                        Provider = "cpu";
                    }
                    session = _session;
                }
                return run(session);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _session.Dispose();
                foreach (var s in _retired) s.Dispose();
            }
        }

        private static SessionOptions Options(string provider)
        {
            var o = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
                // The models declare fixed output shapes (for a 640 × 640 input). Other sizes work but warn on every run.
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            };
            o.AddSessionConfigEntry("session.intra_op.allow_spinning", "0"); // idle threads sleep instead of spinning
            switch (provider)
            {
                case "cpu": break;
                case "coreml":
                    o.AppendExecutionProvider("CoreML", new Dictionary<string, string> { ["ModelFormat"] = "MLProgram", ["MLComputeUnits"] = "ALL" });
                    break;
                case "cuda": o.AppendExecutionProvider_CUDA(0); break;
                case "openvino": o.AppendExecutionProvider_OpenVINO(); break;
                default: throw new ArgumentException($"unknown face provider \"{provider}\"; use auto, cpu, coreml, cuda or openvino");
            }
            return o;
        }

        private static string FirstLine(string s) => s.Split('\n')[0].Trim();
    }
}
