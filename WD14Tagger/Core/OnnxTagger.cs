using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WD14Tagger.Core;

/// <summary>WD14 ONNX 模型的托管封装：一次加载、串行重复推理。</summary>
public sealed class OnnxTagger : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float[] _buffer;
    private bool _disposed;

    public int InputSize { get; }
    public string ModelPath { get; }
    public int ThreadCount { get; }

    public OnnxTagger(string onnxPath, int threads)
    {
        ModelPath = onnxPath;
        ThreadCount = Math.Max(1, threads);

        var options = new SessionOptions
        {
            IntraOpNumThreads = ThreadCount,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = true
        };

        _session = new InferenceSession(onnxPath, options);

        var inputMeta = _session.InputMetadata.First();
        _inputName = inputMeta.Key;
        var dims = inputMeta.Value.Dimensions;
        InputSize = dims.Length == 4 && dims[1] > 0 ? dims[1] : 448;

        _buffer = new float[InputSize * InputSize * 3];
    }

    /// <summary>把预处理好的 BGR NHWC 数据送进模型，返回 10861 维概率。</summary>
    public float[] Run(ReadOnlySpan<float> data)
    {
        if (data.Length != _buffer.Length)
            throw new ArgumentException($"输入数据长度 {data.Length} 与模型输入 {_buffer.Length} 不一致");

        data.CopyTo(_buffer);
        var tensor = new DenseTensor<float>(_buffer, new[] { 1, InputSize, InputSize, 3 });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = results.First().AsEnumerable<float>();
        return output.ToArray();
    }

    /// <summary>预热：第一次推理会触发算子初始化，避免首张图计时/卡顿。</summary>
    public void Warmup()
    {
        Array.Clear(_buffer);
        Run(_buffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
