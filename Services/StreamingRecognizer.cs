using System.IO;
using System.Threading.Tasks;
using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;

namespace Moreno.Voice.Services;

internal sealed class StreamingRecognizer : IDisposable
{
	private const int SampleRate = 16000;
	private const double EndOfSpeechSilence = 0.55;
	private const double ModelEndpointConfirmSilence = 0.3;
	private const double UtteranceEndSilence = 0.9;
	private const double MaxPhraseSeconds = 25;
	private const double MaxUtteranceSeconds = 30;
	private const double PreRollSeconds = 0.3;
	private const double TailPaddingSeconds = 0.4;
	private readonly OnlineRecognizer _recognizer;
	private readonly Channel<RefineJob> _refineChannel = Channel.CreateUnbounded<RefineJob>(new UnboundedChannelOptions { SingleReader = true });
	private readonly Task _refineTask;
	private EnergyVad _vad = new();
	private volatile float _noiseFloor = 0.004f;
	private volatile PhraseRefiner? _refiner;
	private OnlineStream? _stream;
	private WaveIn? _microphone;
	private Channel<AudioChunk>? _audioChannel;
	private Task? _decodeTask;
	private volatile float _level;
	private volatile bool _speaking;
	private int _pendingRefinements;
	private bool _disposed;

	public event Action<RecognitionUpdate>? Recognized;
	public event Action<string>? Failed;

	public float Level => _level;
	public bool IsSpeech => _speaking;

	public StreamingRecognizer(string modelDirectory)
	{
		var config = new OnlineRecognizerConfig();
		config.FeatConfig.SampleRate = SampleRate;
		config.FeatConfig.FeatureDim = 80;
		config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory, "encoder-epoch-99-avg-1.int8.onnx");
		config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory, "decoder-epoch-99-avg-1.onnx");
		config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory, "joiner-epoch-99-avg-1.int8.onnx");
		config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
		config.ModelConfig.ModelingUnit = "bpe";
		config.ModelConfig.BpeVocab = config.ModelConfig.Tokens;
		config.ModelConfig.Provider = "cpu";
		config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
		config.DecodingMethod = "modified_beam_search";
		config.MaxActivePaths = 4;
		config.HotwordsFile = EnsureHotwordsFile(modelDirectory);
		config.HotwordsScore = 2.0f;
		config.EnableEndpoint = 1;
		// The model's own endpoint counts decoder blanks, which also happen mid-speech; phrases are finished by real silence below and these only back that up.
		config.Rule2MinTrailingSilence = 1.2f;
		config.Rule3MinUtteranceLength = 60f;
		_recognizer = new OnlineRecognizer(config);
		_refineTask = Task.Run(RefineLoopAsync);
	}

	// The refiner can arrive after startup (it is large) or be switched off; utterances that began without one are simply not refined.
	public void SetRefiner(PhraseRefiner? refiner) => _refiner = refiner;

	public bool HasRefiner => _refiner is not null;

	public async Task StartAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_microphone is not null) return;
		BeginSession();
		try
		{
			_microphone = new WaveIn { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 40, NumberOfBuffers = 4 };
			_microphone.DataAvailable += OnDataAvailable;
			_microphone.StartRecording();
		}
		catch
		{
			_microphone?.Dispose();
			_microphone = null;
			await EndSessionAsync().ConfigureAwait(false);
			throw;
		}
	}

	public async Task StopAsync()
	{
		var microphone = _microphone;
		_microphone = null;
		if (microphone is not null)
		{
			microphone.DataAvailable -= OnDataAvailable;
			try
			{
				microphone.StopRecording();
			}
			finally
			{
				microphone.Dispose();
			}
		}
		await EndSessionAsync().ConfigureAwait(false);
	}

	// Waits until every queued correction has been published, or the timeout passes.
	public async Task FlushRefinementsAsync(TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (Volatile.Read(ref _pendingRefinements) > 0 && DateTime.UtcNow < deadline) await Task.Delay(40).ConfigureAwait(false);
	}

	internal void BeginSession()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_stream is not null) return;
		_vad = new EnergyVad();
		_stream = _recognizer.CreateStream();
		_audioChannel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
		var reader = _audioChannel.Reader;
		var stream = _stream;
		_decodeTask = Task.Run(() => DecodeAudioAsync(reader, stream));
	}

	internal async Task EndSessionAsync()
	{
		var stream = _stream;
		if (stream is null) return;
		var channel = _audioChannel;
		var decodeTask = _decodeTask;
		_stream = null;
		_audioChannel = null;
		_decodeTask = null;
		channel?.Writer.TryComplete();
		if (decodeTask is not null) await decodeTask.ConfigureAwait(false);
		stream.Dispose();
		_level = 0;
		_speaking = false;
	}

	internal void FeedAudio(float[] samples)
	{
		if (samples.Length == 0) return;
		double squaredSum = 0;
		foreach (var sample in samples) squaredSum += sample * sample;
		var speech = _vad.Process(Math.Sqrt(squaredSum / samples.Length), samples.Length / (double)SampleRate);
		_level = (float)_vad.Level;
		_noiseFloor = (float)_vad.NoiseFloor;
		_speaking = speech;
		_audioChannel?.Writer.TryWrite(new AudioChunk(samples, speech));
	}

	private void OnDataAvailable(object? sender, WaveInEventArgs e)
	{
		var samples = new float[e.BytesRecorded / 2];
		for (var index = 0; index < samples.Length; index++) samples[index] = BitConverter.ToInt16(e.Buffer, index * 2) / 32768f;
		FeedAudio(samples);
	}

	private static string EnsureHotwordsFile(string modelDirectory)
	{
		var path = Path.Combine(modelDirectory, "hotwords.txt");
		if (!File.Exists(path)) File.WriteAllLines(path, ["MORENO VOICE", "CODEX CLI", "SHERPA ONNX", "WINDOWS TERMINAL", "POWERSHELL", "VISUAL STUDIO CODE", "C SHARP", "C PLUS PLUS", "DOT NET", "NUGET", "GITHUB", "OPEN AI", "CHAT GPT", "API KEY", "JSON", "YAML", "XAML"]);
		return path;
	}

	// Digital silence is unlike anything a microphone produces and makes the model invent words, so the padding is noise at the measured floor.
	private float[] QuietPadding()
	{
		var padding = new float[(int)(SampleRate * TailPaddingSeconds)];
		var amplitude = _noiseFloor * 1.7f;
		for (var index = 0; index < padding.Length; index++) padding[index] = (Random.Shared.NextSingle() * 2 - 1) * amplitude;
		return padding;
	}

	private string Decode(OnlineStream stream)
	{
		while (_recognizer.IsReady(stream)) _recognizer.Decode(stream);
		return _recognizer.GetResult(stream).Text;
	}

	private async Task DecodeAudioAsync(ChannelReader<AudioChunk> reader, OnlineStream stream)
	{
		var lastText = string.Empty;
		var quiet = double.PositiveInfinity;
		var pauseBefore = double.PositiveInfinity;
		var phraseStarted = false;
		var phraseHasSpeech = false;
		var phraseSeconds = 0.0;
		var phraseId = 0;
		var utterance = new Utterance();
		try
		{
			await foreach (var chunk in reader.ReadAllAsync().ConfigureAwait(false))
			{
				var seconds = chunk.Samples.Length / (double)SampleRate;
				if (chunk.Speech)
				{
					if (!phraseHasSpeech) pauseBefore = quiet;
					phraseHasSpeech = true;
					quiet = 0;
				}
				else quiet += seconds;
				if (phraseHasSpeech) phraseSeconds += seconds;
				utterance.Samples.AddRange(chunk.Samples);
				if (!utterance.IsActive && !chunk.Speech && utterance.Samples.Count > PreRollSeconds * SampleRate) utterance.Samples.RemoveRange(0, utterance.Samples.Count - (int)(PreRollSeconds * SampleRate));
				stream.AcceptWaveform(SampleRate, chunk.Samples);
				var text = Decode(stream);
				var modelEndpoint = _recognizer.IsEndpoint(stream);
				if (!phraseStarted && (chunk.Speech || text.Length > 0)) StartPhrase();
				var finish = text.Length > 0 && ((phraseHasSpeech && quiet >= EndOfSpeechSilence) || (modelEndpoint && quiet >= ModelEndpointConfirmSilence) || (phraseSeconds >= MaxPhraseSeconds && quiet >= 0.15));
				if (finish)
				{
					// Silence has been heard, so let the model see its trailing context before taking the final text.
					stream.AcceptWaveform(SampleRate, QuietPadding());
					text = Decode(stream);
				}
				if (finish || text != lastText) Publish(text, finish);
				lastText = text;
				if (finish) utterance.PhraseCount++;
				if (finish || (modelEndpoint && text.Length == 0))
				{
					_recognizer.Reset(stream);
					lastText = string.Empty;
					phraseStarted = false;
					phraseHasSpeech = false;
					phraseSeconds = 0;
					pauseBefore = 0;
				}
				if (utterance.PhraseCount > 0 && !phraseStarted && (quiet >= UtteranceEndSilence || utterance.Samples.Count > MaxUtteranceSeconds * SampleRate)) CloseUtterance(quiet);
			}
			stream.AcceptWaveform(SampleRate, QuietPadding());
			stream.InputFinished();
			var tail = Decode(stream);
			if (tail.Length == 0) tail = lastText;
			if (tail.Length > 0)
			{
				if (!phraseStarted) StartPhrase();
				Publish(tail, true);
				utterance.PhraseCount++;
			}
			if (utterance.PhraseCount > 0) CloseUtterance(quiet);
		}
		catch (Exception ex)
		{
			Failed?.Invoke(ex.Message);
		}

		void StartPhrase()
		{
			phraseStarted = true;
			phraseId++;
			if (!utterance.IsActive) utterance.Refinable = _refiner is not null;
			utterance.IsActive = true;
		}

		void Publish(string text, bool isFinal) => Recognized?.Invoke(new RecognitionUpdate(text, isFinal, phraseHasSpeech ? pauseBefore : 0, utterance.Refinable ? utterance.Id : 1_000_000 + phraseId, phraseId, utterance.Refinable));

		void CloseUtterance(double trailingQuiet)
		{
			if (utterance.Refinable)
			{
				var refiner = _refiner;
				if (refiner is null) Recognized?.Invoke(new RecognitionUpdate(string.Empty, true, 0, utterance.Id, 0, true, true));
				else
				{
					// Keep a little of the trailing silence, which the model uses to judge where the sentence ends.
					var trim = (int)(Math.Max(0, Math.Min(trailingQuiet, 30) - 0.3) * SampleRate);
					var samples = utterance.Samples.GetRange(0, Math.Max(0, utterance.Samples.Count - trim)).ToArray();
					Interlocked.Increment(ref _pendingRefinements);
					if (!_refineChannel.Writer.TryWrite(new RefineJob(utterance.Id, samples, refiner))) Interlocked.Decrement(ref _pendingRefinements);
				}
			}
			utterance.Id++;
			utterance.PhraseCount = 0;
			utterance.IsActive = false;
			utterance.Refinable = false;
			utterance.Samples.Clear();
		}
	}

	private async Task RefineLoopAsync()
	{
		await foreach (var job in _refineChannel.Reader.ReadAllAsync().ConfigureAwait(false))
		{
			var text = string.Empty;
			try
			{
				text = job.Refiner.Refine(job.Samples);
			}
			catch (Exception)
			{
				// A failed correction just leaves the streaming text as it was.
			}
			Recognized?.Invoke(new RecognitionUpdate(text, true, 0, job.UtteranceId, 0, true, true));
			Interlocked.Decrement(ref _pendingRefinements);
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		StopAsync().GetAwaiter().GetResult();
		_refineChannel.Writer.TryComplete();
		_refineTask.Wait(TimeSpan.FromSeconds(5));
		_recognizer.Dispose();
		_disposed = true;
	}

	private readonly record struct AudioChunk(float[] Samples, bool Speech);

	private readonly record struct RefineJob(int UtteranceId, float[] Samples, PhraseRefiner Refiner);

	private sealed class Utterance
	{
		public int Id = 1;
		public int PhraseCount;
		public bool IsActive;
		public bool Refinable;
		public List<float> Samples = [];
	}
}
