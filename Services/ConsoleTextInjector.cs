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
	private IntPtr _targetWindow;
	private readonly PhraseAssembler _assembler = new();
	private PasteShortcut _pasteShortcut;
	private IDataObject? _clipboardBackup;
	private DispatcherTimer? _clipboardRestoreTimer;

	public void Begin(IntPtr targetWindow)
	{
		_clipboardRestoreTimer?.Stop();
		RestoreClipboard();
		_targetWindow = targetWindow;
		_assembler.Reset();
		_pasteShortcut = DetectPasteShortcut(targetWindow);
		if (_pasteShortcut != PasteShortcut.None)
		{
			try { _clipboardBackup = Clipboard.GetDataObject(); }
			catch (ExternalException) { _clipboardBackup = null; }
		}
	}

	public bool Update(RecognitionUpdate update)
	{
		var edit = _assembler.Plan(update);
		if (_targetWindow == IntPtr.Zero || GetForegroundWindow() != _targetWindow)
		{
			_assembler.Complete(TypingResult.NotAttempted);
			return false;
		}
		var typed = edit.IsEmpty || (SendBackspaces(edit.Backspaces) && SendText(edit.Text));
		_assembler.Complete(typed ? TypingResult.Succeeded : TypingResult.Failed);
		return typed;
	}

	public void Finish()
	{
		_targetWindow = IntPtr.Zero;
		_assembler.Finish();
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

	private static bool SendBackspaces(int count)
	{
		if (count <= 0) return true;
		var input = new InputRecord[count * 2];
		for (var index = 0; index < count; index++)
		{
			input[index * 2] = Key(VkBack, 0);
			input[index * 2 + 1] = Key(VkBack, KeyEventKeyUp);
		}
		return SendInput((uint)input.Length, input, Marshal.SizeOf<InputRecord>()) == (uint)input.Length;
	}

	private bool SendText(string text)
	{
		if (text.Length == 0) return true;
		if (_pasteShortcut != PasteShortcut.None)
		{
			try { Clipboard.SetText(text, TextDataFormat.UnicodeText); }
			catch (ExternalException) { return false; }
			var pasteKeys = _pasteShortcut == PasteShortcut.ControlShiftV
				? new[] { Key(VkControl, 0), Key(VkShift, 0), Key(VkV, 0), Key(VkV, KeyEventKeyUp), Key(VkShift, KeyEventKeyUp), Key(VkControl, KeyEventKeyUp) }
				: new[] { Key(VkShift, 0), Key(VkInsert, 0), Key(VkInsert, KeyEventKeyUp), Key(VkShift, KeyEventKeyUp) };
			return SendInput((uint)pasteKeys.Length, pasteKeys, Marshal.SizeOf<InputRecord>()) == (uint)pasteKeys.Length;
		}
		var input = new InputRecord[text.Length * 2];
		for (var index = 0; index < text.Length; index++)
		{
			input[index * 2] = Unicode(text[index], KeyEventUnicode);
			input[index * 2 + 1] = Unicode(text[index], KeyEventUnicode | KeyEventKeyUp);
		}
		return SendInput((uint)input.Length, input, Marshal.SizeOf<InputRecord>()) == (uint)input.Length;
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
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
