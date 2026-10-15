using Microsoft.ML.OnnxRuntime;

namespace Calcpad.Core.Ai;

/// <summary>
/// The only type in Calcpad that touches <c>Microsoft.ML.OnnxRuntime</c> directly.
/// <para>
/// Everything ONNX specific lives here on purpose: the type is never referenced by
/// <see cref="AiCore"/>, <see cref="AiService"/> or any other AI type at module load, the session is
/// created lazily on the first inference, and every failure (missing native library on Android,
/// wrong architecture, corrupt model) is turned into an error string instead of an exception.
/// </para>
/// </summary>
internal sealed class OnnxSessionHolder : IDisposable
{
    private readonly object _gate = new();
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
    private int _height;
    private int _width;
    private int _channels = 1;
    private bool _channelsLast;

    /// <summary>True once a session has been created successfully.</summary>
    public bool IsLoaded => _session is not null;

    /// <summary>Input height/width the model expects.</summary>
    public (int Height, int Width) InputSize => (_height, _width);

    /// <summary>
    /// Creates the inference session. Safe to call repeatedly; the session is built once. Returns
    /// false and an explanation instead of throwing.
    /// </summary>
    public bool TryLoad(string modelPath, out string? error)
    {
        error = null;
        if (_session is not null)
        {
            return true;
        }

        lock (_gate)
        {
            if (_session is not null)
            {
                return true;
            }

            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
                };

                var session = new InferenceSession(modelPath, options);
                var input = session.InputMetadata.First();
                var dimensions = input.Value.Dimensions;

                // Accept NCHW, NHWC and NHW layouts; dynamic axes are reported as -1 or 0.
                var size = dimensions.Where(d => d > 0).ToArray();
                switch (dimensions.Length)
                {
                    case 4:
                        var channelFirst = dimensions[1] <= 4 && dimensions[1] > 0;
                        _channelsLast = !channelFirst;
                        _channels = channelFirst ? Math.Max(1, dimensions[1]) : Math.Max(1, dimensions[3]);
                        _height = channelFirst ? size[^2] : size[^3];
                        _width = channelFirst ? size[^1] : size[^2];
                        break;
                    case 3:
                        _channels = 1;
                        _channelsLast = false;
                        _height = size[^2];
                        _width = size[^1];
                        break;
                    case 2:
                        _channels = 1;
                        _height = size[^2];
                        _width = size[^1];
                        break;
                    default:
                        error = $"不支持的模型输入维度（{dimensions.Length} 维）";
                        session.Dispose();
                        return false;
                }

                if (_height <= 0 || _width <= 0)
                {
                    error = "模型没有声明固定的输入尺寸";
                    session.Dispose();
                    return false;
                }

                _inputName = input.Key;
                _outputName = session.OutputMetadata.Keys.First();
                _session = session;
                return true;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                _session = null;
                return false;
            }
        }
    }

    /// <summary>Runs inference on one normalised grayscale glyph. Returns null with a message on failure.</summary>
    public float[]? Run(double[] glyph, out string? error)
    {
        error = null;
        var session = _session;
        if (session is null || _inputName is null || _outputName is null)
        {
            error = "模型尚未加载";
            return null;
        }

        try
        {
            var count = _height * _width * _channels;
            var tensor = new float[count];
            for (var i = 0; i < _height * _width; i++)
            {
                var value = i < glyph.Length ? glyph[i] : 0;
                // MNIST style models want zero mean unit variance, everything else [-1, 1].
                var normalized = _height == 28 && _width == 28 ? (value - 0.1307) / 0.3081 : (value * 2) - 1;
                if (_channels == 1)
                {
                    tensor[i] = (float)normalized;
                }
                else
                {
                    var pixel = i / _channels;
                    var channel = i % _channels;
                    tensor[_channelsLast ? (pixel * _channels) + channel : (channel * _height * _width) + pixel] = (float)normalized;
                }
            }

            var dimensions = _channelsLast
                ? new[] { 1, _height, _width, _channels }
                : new[] { 1, _channels, _height, _width };
            var dense = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(tensor, dimensions);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, dense) };
            using var results = session.Run(inputs);
            var output = results.First();
            return output.AsEnumerable<float>().ToArray();
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return null;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        DllNotFoundException => "缺少 ONNX Runtime 原生库（该平台可能未打包）",
        TypeInitializationException => $"ONNX Runtime 初始化失败：{ex.InnerException?.Message ?? ex.Message}",
        BadImageFormatException => "ONNX Runtime 原生库架构不匹配",
        EntryPointNotFoundException => "ONNX Runtime 原生库版本不匹配",
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
