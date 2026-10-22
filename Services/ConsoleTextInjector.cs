using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Moreno.Voice.Services;

internal sealed class ConsoleTextInjector
{
	private const uint InputKeyboard = 1;
	private const uint KeyEventUnicode = 0x0004;
	private const uint KeyEventKeyUp = 0x0002;
	private const ushort VkBack = 0x08;
	private const ushort VkControl = 0x11;
	private const ushort VkShift = 0x10;
	private const ushort VkV = 0x56;
	private const ushort VkInsert = 0x2D;
	// Pasting goes through the clipboard and the target reads it some time after the keystroke, so the next paste must wait or it would paste the newer text twice.
	private static readonly TimeSpan PasteInterval = TimeSpan.FromMilliseconds(160);
	private static readonly TimeSpan KeystrokeInterval = TimeSpan.FromMilliseconds(12);
	// Text spoken while the target was not focused is typed when it comes back, for this long after dictation ends.
	private static readonly TimeSpan DeliveryWait = TimeSpan.FromMinutes(2);
	// A new dictation in the same window shortly after the last one continues its text (with a separating space) instead of starting fresh.
	private static readonly TimeSpan ContinuationWindow = TimeSpan.FromMinutes(5);
	private readonly PhraseAssembler _assembler = new();
	private readonly DispatcherTimer _pumpTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
	private IntPtr _targetWindow;
	private IntPtr _lastTarget;
	private PasteShortcut _pasteShortcut;
	private IDataObject? _clipboardBackup;
	private DispatcherTimer? _clipboardRestoreTimer;
	private DateTime _lastSendUtc = DateTime.MinValue;
	private DateTime _lastTypedUtc = DateTime.MinValue;
	private DateTime _deliverUntilUtc = DateTime.MaxValue;
	private bool _finishing;
	private bool _focused = true;

	public ConsoleTextInjector() => _pumpTimer.Tick += (_, _) => Pump();

	public void Begin(IntPtr targetWindow)
	{
		_clipboardRestoreTimer?.Stop();
		// Still attached to this window because text from the last dictation is waiting for it: keep that text and carry on.
		var stillAttached = _targetWindow == targetWindow;
		if (!stillAttached)
		{
			RestoreClipboard();
			_assembler.Reset(keepContext: targetWindow == _lastTarget && DateTime.UtcNow - _lastTypedUtc < ContinuationWindow);
			_pasteShortcut = DetectPasteShortcut(targetWindow);
			if (_pasteShortcut != PasteShortcut.None)
			{
				try { _clipboardBackup = Clipboard.GetDataObject(); }
				catch (ExternalException) { _clipboardBackup = null; }
			}
		}
		_targetWindow = targetWindow;
		_finishing = false;
		_focused = true;
		_pumpTimer.Start();
	}

	// Returns false while the text cannot be typed because the target is not focused; it is kept and typed once the target is focused again.
	public bool Update(RecognitionUpdate update)
	{
		_assembler.Apply(update);
		Pump();
		return _focused;
	}

	// Dictation ended. Anything still waiting for the target (or for a correction) is delivered first, then the injector lets go of the window.
	public void Finish()
	{
		if (_targetWindow == IntPtr.Zero) return;
		_assembler.EndSession();
		_finishing = true;
		_deliverUntilUtc = DateTime.UtcNow + DeliveryWait;
		Pump();
	}

	// The app is closing: nothing more will be typed.
	public void Abandon() => Detach();

	private void Pump()
	{
		if (_targetWindow == IntPtr.Zero) return;
		if (!IsWindow(_targetWindow))
		{
			Detach();
			return;
		}
		if (GetForegroundWindow() != _targetWindow)
		{
			// What is typed so far is frozen, so nothing typed later can edit text or a caret position the user may have changed.
			if (_focused) _assembler.FocusLost();
			_focused = false;
		}
		else
		{
			_focused = true;
			var now = DateTime.UtcNow;
			if (now - _lastSendUtc >= (_pasteShortcut == PasteShortcut.None ? KeystrokeInterval : PasteInterval))
			{
				var edit = _assembler.NextEdit();
				if (edit.IsEmpty) _assembler.Complete(0, string.Empty);
				else
				{
					var backspaces = SendBackspaces(edit.Backspaces);
					var text = backspaces == edit.Backspaces ? SendText(edit.Text) : string.Empty;
					_assembler.Complete(backspaces, text);
					_lastSendUtc = DateTime.UtcNow;
					if (backspaces > 0 || text.Length > 0) _lastTypedUtc = _lastSendUtc;
				}
			}
		}
		if (_finishing && (_assembler.IsIdle || DateTime.UtcNow >= _deliverUntilUtc)) Detach();
	}

	private void Detach()
	{
		_pumpTimer.Stop();
		_assembler.Abandon();
		if (_targetWindow != IntPtr.Zero) _lastTarget = _targetWindow;
		_targetWindow = IntPtr.Zero;
		_finishing = false;
		if (_clipboardBackup is null) return;
		_clipboardRestoreTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
		_clipboardRestoreTimer.Tick -= RestoreClipboardAfterPaste;
		_clipboardRestoreTimer.Tick += RestoreClipboardAfterPaste;
		_clipboardRestoreTimer.Stop();
		_clipboardRestoreTimer.Start();
	}

	private void RestoreClipboardAfterPaste(object? sender, EventArgs e)
	{
		_clipboardRestoreTimer?.Stop();
		RestoreClipboard();
	}

	private void RestoreClipboard()
	{
		if (_clipboardBackup is null) return;
		try { Clipboard.SetDataObject(_clipboardBackup, true); }
		catch (ExternalException) { }
		_clipboardBackup = null;
	}

	// Returns how many backspaces were sent.
	private static int SendBackspaces(int count)
	{
		if (count <= 0) return 0;
		var input = new InputRecord[count * 2];
		for (var index = 0; index < count; index++)
		{
			input[index * 2] = Key(VkBack, 0);
			input[index * 2 + 1] = Key(VkBack, KeyEventKeyUp);
		}
		return PressesSent(SendInput((uint)input.Length, input, Marshal.SizeOf<InputRecord>()), input);
	}

	// Returns the text that was actually sent: all of it, or (for keystrokes that were only partly accepted) the part that went out.
	private string SendText(string text)
	{
		if (text.Length == 0) return string.Empty;
		if (_pasteShortcut != PasteShortcut.None)
		{
			if (!TrySetClipboard(text)) return string.Empty;
			var pasteKeys = _pasteShortcut == PasteShortcut.ControlShiftV
				? new[] { Key(VkControl, 0), Key(VkShift, 0), Key(VkV, 0), Key(VkV, KeyEventKeyUp), Key(VkShift, KeyEventKeyUp), Key(VkControl, KeyEventKeyUp) }
				: new[] { Key(VkShift, 0), Key(VkInsert, 0), Key(VkInsert, KeyEventKeyUp), Key(VkShift, KeyEventKeyUp) };
			return SendInput((uint)pasteKeys.Length, pasteKeys, Marshal.SizeOf<InputRecord>()) == (uint)pasteKeys.Length ? text : string.Empty;
		}
		var input = new InputRecord[text.Length * 2];
		for (var index = 0; index < text.Length; index++)
		{
			input[index * 2] = Unicode(text[index], KeyEventUnicode);
			input[index * 2 + 1] = Unicode(text[index], KeyEventUnicode | KeyEventKeyUp);
		}
		return text[..PressesSent(SendInput((uint)input.Length, input, Marshal.SizeOf<InputRecord>()), input)];
	}

	// Key events come in down/up pairs; if only the down half was accepted the key would stay held, so the up half is sent too.
	private static int PressesSent(uint eventsAccepted, InputRecord[] input)
	{
		if (eventsAccepted % 2 == 1)
		{
			var release = new[] { input[eventsAccepted] };
			SendInput(1, release, Marshal.SizeOf<InputRecord>());
		}
		return (int)((eventsAccepted + 1) / 2);
	}

	// Another program holding the clipboard open makes SetText fail for a moment, which is common right after switching windows.
	private static bool TrySetClipboard(string text)
	{
		for (var attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				Clipboard.SetText(text, TextDataFormat.UnicodeText);
				return true;
			}
			catch (ExternalException)
			{
				Thread.Sleep(15);
			}
		}
		return false;
	}

	private static PasteShortcut DetectPasteShortcut(IntPtr targetWindow)
	{
		var className = new StringBuilder(256);
		GetClassName(targetWindow, className, className.Capacity);
		GetWindowThreadProcessId(targetWindow, out var processId);
		var processName = string.Empty;
		try { processName = Process.GetProcessById((int)processId).ProcessName; }
		catch (ArgumentException) { }
		if (className.ToString().Contains("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase) || processName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)) return PasteShortcut.ControlShiftV;
		if (className.ToString().Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase) || processName.Equals("conhost", StringComparison.OrdinalIgnoreCase) || processName.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)) return PasteShortcut.ShiftInsert;
		return PasteShortcut.None;
	}

	private static InputRecord Key(ushort virtualKey, uint flags) => new() { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } } };
	private static InputRecord Unicode(char character, uint flags) => new() { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = flags } } };
	private enum PasteShortcut { None, ControlShiftV, ShiftInsert }

	[StructLayout(LayoutKind.Sequential)]
	private struct InputRecord
	{
		public uint Type;
		public InputUnion Union;
	}

	[StructLayout(LayoutKind.Explicit, Size = 32)]
	private struct InputUnion
	{
		[FieldOffset(0)] public KeyboardInput Keyboard;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KeyboardInput
	{
		public ushort VirtualKey;
		public ushort ScanCode;
		public uint Flags;
		public uint Time;
		public IntPtr ExtraInfo;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint inputCount, InputRecord[] inputs, int inputSize);
	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindow(IntPtr hWnd);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
