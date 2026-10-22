using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Moreno.Voice.Controls;
using Moreno.Voice.Services;

namespace Moreno.Voice;

public partial class MainWindow : Window
{
	private const int HotkeyId = 0x4D56;
	private const int WmHotkey = 0x0312;
	private const int GwlExStyle = -20;
	private const int WsExToolWindow = 0x00000080;
	private const int WsExNoActivate = 0x08000000;
	private const uint ModAlt = 0x0001;
	private const uint ModControl = 0x0002;
	private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
	private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(3) };
	private HwndSource? _source;
	private StreamingRecognizer? _recognizer;
	private IntPtr _windowHandle;
	private bool _recording;
	private bool _injecting;
	private bool _busy;
	private bool _hotkeyRegistered;
	private bool _dragging;
	private int _dragStartX;
	private int _dragStartY;
	private double _dragStartLeft;
	private double _dragStartTop;
	private string? _startupError;
	private readonly ConsoleTextInjector _textInjector = new();
	private readonly AppSettings _settings = AppSettings.Load();
	private SettingsWindow? _settingsWindow;
	private PhraseRefiner? _refiner;
	private bool _refinerLoading;

	public MainWindow()
	{
		InitializeComponent();
		ApplySettings(keepCenter: false);
		Left = (SystemParameters.WorkArea.Width - Width) / 2;
		Top = SystemParameters.WorkArea.Bottom - Height - 36;
		_settings.Changed += () => Dispatcher.BeginInvoke(() => ApplySettings(keepCenter: true));
		SourceInitialized += OnSourceInitialized;
		Loaded += OnLoaded;
		Closed += OnClosed;
		_levelTimer.Tick += (_, _) => Orb.Level = _recognizer?.Level ?? 0;
		_errorTimer.Tick += (_, _) =>
		{
			_errorTimer.Stop();
			Orb.State = _recording ? OrbState.Listening : OrbState.Idle;
		};
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		try
		{
			var progress = new Progress<double>(value => ToolTip = $"Preparing offline speech model ({value:P0})");
			var modelDirectory = await ModelInstaller.EnsureInstalledAsync(ModelInstaller.Streaming, progress);
			_recognizer = new StreamingRecognizer(modelDirectory);
			_recognizer.Recognized += OnRecognized;
			_recognizer.Failed += OnRecognitionFailed;
			ToolTip = IdleToolTip();
			_ = EnsureRefinerAsync();
		}
		catch (Exception ex)
		{
			_startupError = ex.Message;
			ToolTip = $"Speech model setup failed: {_startupError}";
		}
	}

	private string IdleToolTip() => _hotkeyRegistered ? "Click or press Ctrl+Alt+Space to dictate" : "Click to dictate; Ctrl+Alt+Space is unavailable";

	// The large model is downloaded and loaded in the background after the app is usable; until it is ready (or if it is switched off) dictation works as usual.
	private async Task EnsureRefinerAsync()
	{
		if (!_settings.SecondPassEnabled)
		{
			var unloaded = _refiner;
			_refiner = null;
			_recognizer?.SetRefiner(null);
			unloaded?.Dispose();
			return;
		}
		if (_refiner is not null || _refinerLoading || _recognizer is null) return;
		_refinerLoading = true;
		try
		{
			var progress = new Progress<double>(value => ToolTip = $"Downloading the accuracy model ({value:P0})");
			var directory = await ModelInstaller.EnsureInstalledAsync(ModelInstaller.Accuracy, progress);
			if (!_recording) ToolTip = "Loading the accuracy model";
			var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
			var refiner = await Task.Run(() => PhraseRefiner.Create(directory, threads));
			if (!_settings.SecondPassEnabled)
			{
				refiner.Dispose();
				return;
			}
			_refiner = refiner;
			_recognizer?.SetRefiner(refiner);
			if (!_recording) ToolTip = IdleToolTip();
		}
		catch (Exception ex)
		{
			ToolTip = $"The accuracy model is unavailable ({ex.Message}); dictation still works without it";
		}
		finally
		{
			_refinerLoading = false;
		}
	}

	private void OnSourceInitialized(object? sender, EventArgs e)
	{
		_windowHandle = new WindowInteropHelper(this).Handle;
		var style = GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();
		SetWindowLongPtr(_windowHandle, GwlExStyle, new IntPtr(style | WsExToolWindow | WsExNoActivate));
		_source = HwndSource.FromHwnd(_windowHandle);
		_source.AddHook(WindowProcedure);
		_hotkeyRegistered = RegisterHotKey(_windowHandle, HotkeyId, ModControl | ModAlt, 0x20);
	}

	private void Orb_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		GetCursorPos(out var cursor);
		_dragStartX = cursor.X;
		_dragStartY = cursor.Y;
		_dragStartLeft = Left;
		_dragStartTop = Top;
		_dragging = false;
		OrbSurface.CaptureMouse();
	}

	private void Orb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (!OrbSurface.IsMouseCaptured || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || !GetCursorPos(out var cursor)) return;
		var deltaX = cursor.X - _dragStartX;
		var deltaY = cursor.Y - _dragStartY;
		if (!_dragging && Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)) >= 4)
		{
			_dragging = true;
			OrbSurface.Cursor = System.Windows.Input.Cursors.SizeAll;
		}
		if (!_dragging) return;
		Left = Math.Clamp(_dragStartLeft + deltaX, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
		Top = Math.Clamp(_dragStartTop + deltaY, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
	}

	private async void Orb_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		OrbSurface.ReleaseMouseCapture();
		OrbSurface.Cursor = System.Windows.Input.Cursors.Hand;
		if (!_dragging) await ToggleRecordingAsync();
		_dragging = false;
	}

	private async Task ToggleRecordingAsync()
	{
		if (_busy) return;
		if (_recognizer is null)
		{
			ToolTip = _startupError is null ? "The local speech model is still preparing" : $"Speech model setup failed: {_startupError}";
			return;
		}

		_busy = true;
		try
		{
			if (_recording) await StopDictationAsync(_recognizer);
			else await StartDictationAsync(_recognizer);
		}
		finally
		{
			_busy = false;
		}
	}

	private async Task StartDictationAsync(StreamingRecognizer recognizer)
	{
		var targetWindow = GetForegroundWindow();
		if (targetWindow == IntPtr.Zero || targetWindow == _windowHandle)
		{
			ToolTip = "Focus a text field, then start dictation";
			return;
		}

		_textInjector.Begin(targetWindow);
		_injecting = true;
		try
		{
			FeedbackSounds.PlayStart();
			await recognizer.StartAsync();
			_recording = true;
			_errorTimer.Stop();
			Orb.State = OrbState.Listening;
			_levelTimer.Start();
			ToolTip = "Listening; recognized words are typed into the focused app";
		}
		catch (Exception ex)
		{
			_injecting = false;
			_textInjector.Finish();
			ShowError($"Could not start dictation: {ex.Message}");
		}
	}

	private async Task StopDictationAsync(StreamingRecognizer recognizer)
	{
		_recording = false;
		_levelTimer.Stop();
		Orb.Level = 0;
		Orb.State = OrbState.Idle;
		ToolTip = IdleToolTip();
		await recognizer.StopAsync();
		FeedbackSounds.PlayStop();
		// The corrections for the last sentences may still be computing; the target stays attached until they have been typed.
		await recognizer.FlushRefinementsAsync(TimeSpan.FromSeconds(12));
		// The last phrase and the corrections are published from other threads and reach this one queued behind this call, so let them type before the target is released.
		await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
		_injecting = false;
		_textInjector.Finish();
	}

	private void OnRecognized(RecognitionUpdate update) => Dispatcher.BeginInvoke(() =>
	{
		if (_injecting && !_textInjector.Update(update))
		{
			ToolTip = "Waiting for the original app; what you say is kept and typed when you switch back";
		}
	});

	private void OnRecognitionFailed(string message) => Dispatcher.BeginInvoke(() =>
	{
		_recording = false;
		_injecting = false;
		_levelTimer.Stop();
		Orb.Level = 0;
		_textInjector.Finish();
		ShowError($"Speech recognition stopped: {message}");
		_ = _recognizer?.StopAsync();
	});

	private void ShowError(string message)
	{
		ToolTip = message;
		Orb.State = OrbState.Error;
		_errorTimer.Stop();
		_errorTimer.Start();
	}

	private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
		{
			handled = true;
			_ = ToggleRecordingAsync();
		}
		return IntPtr.Zero;
	}

	private const double BaseWindowSize = 124;

	// The orb grows around its own center so changing the size in settings does not make it jump.
	private void ApplySettings(bool keepCenter)
	{
		var size = BaseWindowSize * _settings.OrbScale;
		var centerX = Left + (Width / 2);
		var centerY = Top + (Height / 2);
		Width = size;
		Height = size;
		if (keepCenter)
		{
			Left = Math.Clamp(centerX - (size / 2), SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - size);
			Top = Math.Clamp(centerY - (size / 2), SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - size);
		}
		Orb.SetBitmapSize(Math.Clamp((int)Math.Round(72 * _settings.OrbScale / 4) * 4, 56, 96));
		Orb.SetHue((float)_settings.OrbHue);
		_ = EnsureRefinerAsync();
		FeedbackSounds.Configure(_settings.SoundsEnabled, _settings.SoundVolume);
	}

	private void Settings_Click(object sender, RoutedEventArgs e)
	{
		if (_settingsWindow is not null)
		{
			_settingsWindow.Activate();
			return;
		}
		_settingsWindow = new SettingsWindow(_settings);
		_settingsWindow.Closed += (_, _) =>
		{
			_settingsWindow = null;
			_settings.Save();
		};
		_settingsWindow.Show();
	}

	private void Exit_Click(object sender, RoutedEventArgs e) => Close();

	[StructLayout(LayoutKind.Sequential)]
	private struct CursorPosition { public int X; public int Y; }

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorPos(out CursorPosition cursor);

	private async void OnClosed(object? sender, EventArgs e)
	{
		_recording = false;
		_injecting = false;
		_textInjector.Abandon();
		_levelTimer.Stop();
		_errorTimer.Stop();
		_source?.RemoveHook(WindowProcedure);
		_source?.Dispose();
		if (_hotkeyRegistered) UnregisterHotKey(_windowHandle, HotkeyId);
		if (_recognizer is not null)
		{
			await _recognizer.StopAsync();
			_recognizer.SetRefiner(null);
			_recognizer.Dispose();
		}
		_refiner?.Dispose();
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
	private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();
}
