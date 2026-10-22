using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Moreno.Voice.Controls;

internal enum OrbState { Idle, Listening, Error }

public partial class OrbControl : UserControl
{
	private const double FrameSeconds = 1.0 / 30;
	private const double MaxTilt = 1.0;
	private const double ActiveBrightness = 1.0;
	private const double IdleBrightness = 0.42;
	private const double RestFill = 0.3;
	private const double VoiceFill = 0.34;
	private int _bitmapSize = 72;
	private float _hue = 170;
	private LiquidRenderer _renderer;
	private byte[] _pixels;
	private WriteableBitmap _bitmap;
	private readonly Random _random = new(5);
	private DateTime _lastTick = DateTime.UtcNow;
	private OrbState _state = OrbState.Idle;
	private bool _loaded;
	private bool _animating;
	private bool _speaking;
	private double _sinceFrame;
	private double _quietFor;
	private double _lastQuiet = 99;
	private double _sinceOnset = 99;
	private double _lobeTime = 7;
	private double _driftPhase;
	private double _kickSign = -1;
	private double _fill;
	private double _fillVelocity;
	private double _tilt;
	private double _tiltVelocity;
	private double _tiltTarget;
	private double _level;
	private double _envelope;
	private double _activity;
	private double _alert;
	private double _brightness = IdleBrightness;

	public OrbControl()
	{
		InitializeComponent();
		_renderer = new LiquidRenderer(_bitmapSize, _hue);
		_pixels = new byte[_bitmapSize * _bitmapSize * 4];
		_bitmap = new WriteableBitmap(_bitmapSize, _bitmapSize, 96, 96, PixelFormats.Pbgra32, null);
		Body.Source = _bitmap;
		Loaded += (_, _) =>
		{
			_loaded = true;
			UpdateAnimation();
		};
		Unloaded += (_, _) =>
		{
			_loaded = false;
			UpdateAnimation();
		};
		Advance(0, force: true);
	}

	internal OrbState State
	{
		get => _state;
		set
		{
			_state = value;
			UpdateAnimation();
		}
	}

	// Raw microphone level, 0..1; ignored unless listening.
	public double Level { get; set; }

	// A larger orb needs a finer bitmap, but each step costs frame time, so the size is chosen by the caller within a budget.
	internal void SetBitmapSize(int size)
	{
		if (size == _bitmapSize) return;
		_bitmapSize = size;
		_renderer = new LiquidRenderer(size, _hue);
		_pixels = new byte[size * size * 4];
		_bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
		Body.Source = _bitmap;
		Advance(0, force: true);
	}

	// Hue in degrees: 170 is the reference teal.
	internal void SetHue(float hue)
	{
		_hue = hue;
		_renderer.SetHue(hue);
		Advance(0, force: true);
	}

	internal void Advance(double seconds, bool force = false)
	{
		var active = State != OrbState.Idle;
		var target = State == OrbState.Listening ? Math.Clamp(Math.Pow(Level, 0.8) * 1.15, 0, 1) : 0;
		_level += (target - _level) * (1 - Math.Exp(-seconds / (target > _level ? 0.04 : 0.22)));
		// The liquid follows the voice through a slow envelope, so it rises with a phrase and relaxes over a second or two instead of twitching with every syllable.
		_envelope += (target - _envelope) * (1 - Math.Exp(-seconds / (target > _envelope ? 0.1 : 1.0)));
		_activity += (target - _activity) * (1 - Math.Exp(-seconds / (target > _activity ? 0.35 : 1.6)));
		var follow = 1 - Math.Exp(-seconds / 0.35);
		_alert += ((State == OrbState.Error ? 1.0 : 0.0) - _alert) * follow;
		_brightness += ((active ? ActiveBrightness : IdleBrightness) - _brightness) * follow;
		DetectOnset(seconds, active);
		var remaining = seconds;
		while (remaining > 0)
		{
			var step = Math.Min(remaining, 1.0 / 120);
			Integrate(step, active);
			remaining -= step;
		}
		_lobeTime += seconds * (active ? 0.035 + (0.3 * _activity) : 0.02);
		if (!active && IsSettledIdle())
		{
			_fill = _fillVelocity = _tilt = _tiltVelocity = _tiltTarget = 0;
		}
		_sinceFrame += seconds;
		if (!force && _sinceFrame < FrameSeconds) return;
		_sinceFrame = 0;
		_renderer.Render(_pixels, (float)_lobeTime, (float)_fill, (float)_tilt, (float)(0.16 + (0.07 * _activity)), (float)_alert, (float)_brightness);
		_bitmap.WritePixels(new Int32Rect(0, 0, _bitmapSize, _bitmapSize), _pixels, _bitmapSize * 4, 0);
		if (!active && IsSettledIdle()) UpdateAnimation();
	}

	// Starting to speak after a pause tips the liquid once; it then relaxes back toward level over a few seconds. Continuous talking does not keep re-tipping it.
	private void DetectOnset(double seconds, bool active)
	{
		_sinceOnset += seconds;
		if (_level >= 0.15)
		{
			if (_quietFor > 0) _lastQuiet = _quietFor;
			_quietFor = 0;
		}
		else _quietFor += seconds;
		if (!active)
		{
			_speaking = false;
			return;
		}
		if (_speaking)
		{
			if (_level < 0.15) _speaking = false;
			return;
		}
		if (_level < 0.32 || _lastQuiet < 0.3 || _sinceOnset < 1.5) return;
		_speaking = true;
		_sinceOnset = 0;
		if (_random.NextDouble() < 0.75) _kickSign = -_kickSign;
		_tiltTarget = _kickSign * Math.Clamp(0.4 + (0.45 * _level) + (0.15 * _random.NextDouble()), 0, MaxTilt);
	}

	// Both the height and the tilt are critically damped springs: they move smoothly toward where the voice and the last tip put them and never ring back and forth.
	private void Integrate(double seconds, bool active)
	{
		var targetFill = !active ? 0 : (State == OrbState.Error ? 0.4 : RestFill + (VoiceFill * _envelope));
		var omega = active ? 4.2 : 3.2;
		_fillVelocity += ((omega * omega * (targetFill - _fill)) - (2 * omega * _fillVelocity)) * seconds;
		_fill = Math.Clamp(_fill + (_fillVelocity * seconds), 0, 0.85);
		if (_fill is <= 0 or >= 0.85) _fillVelocity = 0;
		_driftPhase += seconds * 0.5;
		_tiltTarget *= Math.Exp(-seconds / (active ? 2.4 : 0.8));
		var desiredTilt = active ? _tiltTarget + (0.06 * Math.Sin(_driftPhase)) : 0;
		const double tiltOmega = 2.6;
		_tiltVelocity += ((tiltOmega * tiltOmega * (desiredTilt - _tilt)) - (2 * tiltOmega * _tiltVelocity)) * seconds;
		_tilt = Math.Clamp(_tilt + (_tiltVelocity * seconds), -MaxTilt, MaxTilt);
	}

	// An idle orb is a still, empty disc: once the liquid has drained and everything has settled, nothing renders until the state changes.
	private bool IsSettledIdle() => _fill < 0.002 && Math.Abs(_fillVelocity) < 0.02 && Math.Abs(_tilt) < 0.004 && Math.Abs(_tiltVelocity) < 0.02
		&& _level < 0.004 && _envelope < 0.004 && _activity < 0.004 && _alert < 0.004 && Math.Abs(_brightness - IdleBrightness) < 0.004;

	private void UpdateAnimation()
	{
		var shouldAnimate = _loaded && (State != OrbState.Idle || !IsSettledIdle());
		if (shouldAnimate == _animating) return;
		_animating = shouldAnimate;
		if (shouldAnimate)
		{
			_lastTick = DateTime.UtcNow;
			CompositionTarget.Rendering += OnRendering;
		}
		else CompositionTarget.Rendering -= OnRendering;
	}

	private void OnRendering(object? sender, EventArgs e)
	{
		var now = DateTime.UtcNow;
		var seconds = Math.Min((now - _lastTick).TotalSeconds, 0.1);
		_lastTick = now;
		Advance(seconds);
	}
}
