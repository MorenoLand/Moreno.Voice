using System.IO;
using System.Text.Json;

namespace Moreno.Voice.Services;

// Plain data plus JSON persistence: nothing here depends on a UI toolkit or an operating system.
internal sealed class AppSettings
{
	public const double MinOrbScale = 0.6;
	public const double MaxOrbScale = 2.0;
	public const double DefaultOrbHue = 170;

	// Hue in degrees. Mint is the reference teal; the others are the same gradient with its hue shifted.
	public static readonly (string Name, double Hue)[] OrbColors =
	[
		("Mint", DefaultOrbHue), ("Cyan", 190), ("Ocean", 220), ("Indigo", 245), ("Violet", 268), ("Orchid", 295),
		("Rose", 335), ("Sunset", 22), ("Amber", 42), ("Lime", 85), ("Green", 135), ("Jade", 155),
	];
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moreno.Voice", "settings.json");
	private readonly object _saveLock = new();
	private Timer? _saveTimer;
	private double _orbScale = 1;
	private double _soundVolume = 0.5;
	private double _orbHue = DefaultOrbHue;

	public event Action? Changed;

	public double OrbScale
	{
		get => _orbScale;
		set => _orbScale = Math.Clamp(value, MinOrbScale, MaxOrbScale);
	}

	public double OrbHue
	{
		get => _orbHue;
		set => _orbHue = Math.Clamp(value, 0, 360);
	}

	public bool SoundsEnabled { get; set; } = true;

	// Re-reads each finished sentence with a larger model for punctuation and fewer mistakes. It needs a one-time download and about a gigabyte of memory.
	public bool SecondPassEnabled { get; set; } = true;

	public double SoundVolume
	{
		get => _soundVolume;
		set => _soundVolume = Math.Clamp(value, 0, 1);
	}

	public static AppSettings Load()
	{
		try
		{
			if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
		}
		return new AppSettings();
	}

	public void ResetToDefaults()
	{
		OrbScale = 1;
		OrbHue = DefaultOrbHue;
		SoundsEnabled = true;
		SecondPassEnabled = true;
		SoundVolume = 0.5;
		NotifyChanged();
	}

	// Call after changing a property: listeners update at once, and the file is written once the changes settle.
	public void NotifyChanged()
	{
		Changed?.Invoke();
		lock (_saveLock)
		{
			_saveTimer?.Dispose();
			_saveTimer = new Timer(_ => Save(), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
		}
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
			var temporaryPath = $"{SettingsPath}.tmp";
			File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
			File.Move(temporaryPath, SettingsPath, true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}
}
