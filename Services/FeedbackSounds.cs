using System.IO;
using System.Media;

namespace Moreno.Voice.Services;

// Short, quiet, synthesized chimes so there are no audio assets to ship: two soft sine notes, rising to start and falling to stop.
internal static class FeedbackSounds
{
	private const int SampleRate = 44100;
	private static SoundPlayer? _startPlayer;
	private static SoundPlayer? _stopPlayer;
	private static bool _enabled = true;

	// Volume is 0..1 on a curve that keeps the middle of the slider quiet: 0.5 is a peak of 0.1 of full scale.
	public static void Configure(bool enabled, double volume)
	{
		_enabled = enabled;
		var amplitude = 0.4 * volume * volume;
		_startPlayer = Create(659.25, 880.0, amplitude);
		_stopPlayer = Create(880.0, 587.33, amplitude);
	}

	public static void PlayStart() => Play(_startPlayer);

	public static void PlayStop() => Play(_stopPlayer);

	private static void Play(SoundPlayer? player)
	{
		if (!_enabled || player is null) return;
		try { player.Play(); }
		catch (Exception) { }
	}

	private static SoundPlayer Create(double firstHertz, double secondHertz, double amplitude)
	{
		const double noteSeconds = 0.16;
		const double gapSeconds = 0.05;
		var noteSamples = (int)(SampleRate * noteSeconds);
		var gapSamples = (int)(SampleRate * gapSeconds);
		var samples = new short[(noteSamples * 2) + gapSamples + (SampleRate / 10)];
		AddNote(samples, 0, noteSamples, firstHertz, amplitude);
		AddNote(samples, noteSamples + gapSamples, noteSamples, secondHertz, amplitude * 0.85);
		return new SoundPlayer(new MemoryStream(ToWave(samples)));
	}

	private static void AddNote(short[] samples, int start, int length, double hertz, double amplitude)
	{
		for (var index = 0; index < length && start + index < samples.Length; index++)
		{
			var seconds = index / (double)SampleRate;
			var attack = Math.Min(1, seconds / 0.012);
			var decay = Math.Exp(-seconds * 16);
			var tone = Math.Sin(2 * Math.PI * hertz * seconds) + (0.18 * Math.Sin(4 * Math.PI * hertz * seconds));
			samples[start + index] += (short)(tone * attack * decay * amplitude * short.MaxValue / 1.18);
		}
	}

	private static byte[] ToWave(short[] samples)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write("RIFF"u8);
		writer.Write(36 + (samples.Length * 2));
		writer.Write("WAVEfmt "u8);
		writer.Write(16);
		writer.Write((short)1);
		writer.Write((short)1);
		writer.Write(SampleRate);
		writer.Write(SampleRate * 2);
		writer.Write((short)2);
		writer.Write((short)16);
		writer.Write("data"u8);
		writer.Write(samples.Length * 2);
		foreach (var sample in samples) writer.Write(sample);
		writer.Flush();
		return stream.ToArray();
	}
}
