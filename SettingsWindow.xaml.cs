using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using Moreno.Voice.Services;

namespace Moreno.Voice;

public partial class SettingsWindow : Window
{
	private readonly AppSettings _settings;
	private bool _loading = true;

	internal SettingsWindow(AppSettings settings)
	{
		_settings = settings;
		InitializeComponent();
		foreach (var (name, hue) in AppSettings.OrbColors)
		{
			var swatch = new ToggleButton { Style = (Style)FindResource("SwatchToggle"), Background = new SolidColorBrush(SwatchColor(hue)), ToolTip = name, Tag = hue };
			swatch.Click += Swatch_Click;
			Swatches.Children.Add(swatch);
		}
		KeyDown += (_, e) =>
		{
			if (e.Key == Key.Escape) Close();
		};
		ShowCurrentValues();
	}

	private void ShowCurrentValues()
	{
		_loading = true;
		SizeSlider.Value = _settings.OrbScale * 100;
		SoundsToggle.IsChecked = _settings.SoundsEnabled;
		SecondPassToggle.IsChecked = _settings.SecondPassEnabled;
		foreach (ToggleButton swatch in Swatches.Children) swatch.IsChecked = Math.Abs((double)swatch.Tag - _settings.OrbHue) < 0.5;
		VolumeSlider.Value = _settings.SoundVolume * 100;
		VolumeSlider.IsEnabled = _settings.SoundsEnabled;
		_loading = false;
		SizeValue.Text = $"{SizeSlider.Value:0}%";
		VolumeValue.Text = $"{VolumeSlider.Value:0}%";
	}

	private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (SizeValue is null) return;
		SizeValue.Text = $"{e.NewValue:0}%";
		if (_loading) return;
		_settings.OrbScale = e.NewValue / 100;
		_settings.NotifyChanged();
	}

	private void Swatch_Click(object sender, RoutedEventArgs e)
	{
		_settings.OrbHue = (double)((ToggleButton)sender).Tag;
		foreach (ToggleButton swatch in Swatches.Children) swatch.IsChecked = ReferenceEquals(swatch, sender);
		_settings.NotifyChanged();
	}

	private static Color SwatchColor(double hue)
	{
		var (r, g, b) = Controls.LiquidRenderer.SwatchColor((float)hue);
		return Color.FromRgb(r, g, b);
	}

	private void SecondPassToggle_Click(object sender, RoutedEventArgs e)
	{
		_settings.SecondPassEnabled = SecondPassToggle.IsChecked == true;
		_settings.NotifyChanged();
	}

	private void SoundsToggle_Click(object sender, RoutedEventArgs e)
	{
		_settings.SoundsEnabled = SoundsToggle.IsChecked == true;
		VolumeSlider.IsEnabled = _settings.SoundsEnabled;
		_settings.NotifyChanged();
		FeedbackSounds.PlayStart();
	}

	private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (VolumeValue is null) return;
		VolumeValue.Text = $"{e.NewValue:0}%";
		if (_loading) return;
		_settings.SoundVolume = e.NewValue / 100;
		_settings.NotifyChanged();
	}

	// Playing on every tick would stutter, so the new level is previewed once the slider is let go.
	private void VolumeSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => FeedbackSounds.PlayStart();

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		_settings.ResetToDefaults();
		ShowCurrentValues();
	}

	private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState == MouseButtonState.Pressed) DragMove();
	}

	private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
