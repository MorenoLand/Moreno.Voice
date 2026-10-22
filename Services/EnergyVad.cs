namespace Moreno.Voice.Services;

internal sealed class EnergyVad
{
	private const double MinFloor = 0.0006;
	private const double MaxFloor = 0.04;
	private const double HangoverSeconds = 0.2;
	private double _floor = 0.004;
	private double _hangover;

	public double NoiseFloor => _floor;
	public bool IsSpeech { get; private set; }
	public double Level { get; private set; }

	public bool Process(double rms, double seconds)
	{
		// The floor drops to quiet moments quickly and rises slowly, and never while speaking, so steady fan or hiss becomes the floor but speech does not.
		if (rms < _floor) _floor += (rms - _floor) * Math.Min(1, seconds / 0.25);
		else if (!IsSpeech) _floor += (rms - _floor) * Math.Min(1, seconds / 6);
		_floor = Math.Clamp(_floor, MinFloor, MaxFloor);
		var onThreshold = Math.Max(0.008, _floor * 3.5);
		var offThreshold = Math.Max(0.005, _floor * 2.2);
		if (rms >= onThreshold)
		{
			IsSpeech = true;
			_hangover = HangoverSeconds;
		}
		else if (IsSpeech)
		{
			if (rms >= offThreshold) _hangover = HangoverSeconds;
			else if ((_hangover -= seconds) <= 0) IsSpeech = false;
		}
		var decibelsAboveFloor = 20 * Math.Log10(Math.Max(rms, 0.00001) / _floor);
		Level = Math.Clamp((decibelsAboveFloor - 3) / 27, 0, 1);
		return IsSpeech;
	}
}
