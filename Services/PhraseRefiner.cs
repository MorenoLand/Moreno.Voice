using System.IO;
using SherpaOnnx;

namespace Moreno.Voice.Services;

// Re-reads finished speech with a much larger offline model. Its output is punctuated and capitalized and is far better at short or unusual words.
internal sealed class PhraseRefiner : IDisposable
{
	private readonly OfflineRecognizer _recognizer;
	private readonly object _lock = new();
	private bool _disposed;

	private PhraseRefiner(OfflineRecognizer recognizer) => _recognizer = recognizer;

	public static PhraseRefiner Create(string modelDirectory, int threads)
	{
		var config = new OfflineRecognizerConfig();
		config.FeatConfig.SampleRate = 16000;
		config.FeatConfig.FeatureDim = 80;
		config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
		config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx");
		config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory, "joiner.int8.onnx");
		config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
		config.ModelConfig.ModelType = "nemo_transducer";
		config.ModelConfig.NumThreads = threads;
		config.ModelConfig.Provider = "cpu";
		return new PhraseRefiner(new OfflineRecognizer(config));
	}

	// Blocking; call from a background thread. Returns an empty string if the refiner was disposed meanwhile.
	public string Refine(float[] samples)
	{
		lock (_lock)
		{
			if (_disposed) return string.Empty;
			using var stream = _recognizer.CreateStream();
			stream.AcceptWaveform(16000, samples);
			_recognizer.Decode(stream);
			return stream.Result.Text.Trim();
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;
			_disposed = true;
			_recognizer.Dispose();
		}
	}
}
