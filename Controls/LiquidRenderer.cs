namespace Moreno.Voice.Controls;

// A flat disc with a pale liquid whose bright front tilts and sloshes. The gradient profile was sampled from a capture of the reference orb:
// deep teal that slowly lightens toward the front, a sharp lift to a near-white crest, then a pale plateau. Output is premultiplied BGRA.
internal sealed class LiquidRenderer
{
	// Distance from the crest in disc diameters (negative is the dark side) and the color there.
	private static readonly (float Distance, int Rgb)[] Profile =
	[
		(-0.60f, 0x00AD90), (-0.50f, 0x00AD90), (-0.41f, 0x02AF94), (-0.35f, 0x02B498), (-0.30f, 0x03B69C), (-0.24f, 0x05BEA4), (-0.19f, 0x06C1AB), (-0.17f, 0x0AC8B1),
		(-0.15f, 0x18CCB8), (-0.13f, 0x3CD5C5), (-0.10f, 0x67E0D4), (-0.08f, 0x97EAE2), (-0.06f, 0xBDF2EC), (-0.04f, 0xD5F4EF), (-0.01f, 0xE1F6F4),
		(0.01f, 0xE1F6F4), (0.03f, 0xDCF6F2), (0.07f, 0xD0F2EE), (0.10f, 0xC9F0E9), (0.14f, 0xC1EFE8), (0.19f, 0xB9ECE4), (0.23f, 0xB1EBE1),
		(0.27f, 0xA9E8DE), (0.34f, 0xA5E6DC), (0.60f, 0xA5E6DC),
	];
	private const float ReferenceHue = 170f;
	private const float AlertHue = 6f;
	private const float AlertLightnessLift = 0.14f;
	private const int LookupSize = 256;
	private const float LookupStart = -0.65f;
	private const float LookupSpan = 1.3f;
	private const int WarpGrid = 13;
	private readonly float[] _x;
	private readonly float[] _y;
	private readonly float[] _edge;
	private readonly float[] _normalLookup = new float[LookupSize * 3];
	private readonly float[] _alertLookup = new float[LookupSize * 3];
	private readonly float[] _warp = new float[WarpGrid * WarpGrid];

	public LiquidRenderer(int size, float hue = ReferenceHue)
	{
		Size = size;
		_x = new float[size * size];
		_y = new float[size * size];
		_edge = new float[size * size];
		for (var row = 0; row < size; row++)
		{
			for (var column = 0; column < size; column++)
			{
				var index = row * size + column;
				var nx = ((column + 0.5f) / size * 2) - 1;
				var ny = ((row + 0.5f) / size * 2) - 1;
				_x[index] = nx;
				_y[index] = ny;
				_edge[index] = Math.Clamp((1 - MathF.Sqrt((nx * nx) + (ny * ny))) * size / 2f + 0.5f, 0, 1);
			}
		}
		SetHue(hue);
		BuildLookup(_alertLookup, AlertHue, AlertLightnessLift);
	}

	public int Size { get; }

	// Hue in degrees; the reference orb is 170 (teal). Lightness and saturation of every stop are kept.
	public void SetHue(float hue) => BuildLookup(_normalLookup, hue, 0);

	// fill 0..1 raises the liquid from nothing to the whole disc; tilt is the front's angle in radians (0 is level). The front is bent by a very smooth,
	// slowly moving field (lobeTime is its clock, lobeAmount how far it bends the front, in disc diameters), the way the reference front domes and swirls.
	// alert 0..1 blends toward the error color; brightness 1 is full color and lower fades the disc toward a dim, translucent version.
	public void Render(byte[] pixels, float lobeTime, float fill, float tilt, float lobeAmount, float alert, float brightness)
	{
		BuildWarp(lobeTime, lobeAmount);
		var sinTilt = MathF.Sin(tilt);
		var cosTilt = MathF.Cos(tilt);
		// The crest travels from beyond the bottom edge to beyond the top edge, so fill 0 shows no liquid at all and fill 1 shows only the pale plateau.
		var crest = 1.6f - (3.2f * fill);
		var dim = (1 - brightness) * 0.78f;
		var opacity = 0.6f + (0.4f * brightness);
		var grainTime = lobeTime * 0.4f;
		for (var index = 0; index < Size * Size; index++)
		{
			var edge = _edge[index];
			var offset = index * 4;
			if (edge <= 0)
			{
				pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 0;
				continue;
			}
			var x = _x[index];
			var y = _y[index];
			var down = (x * sinTilt) + (y * cosTilt);
			var distance = ((down - crest) * 0.5f) + SampleWarp(x, y);
			var lookup = (int)Math.Clamp((distance - LookupStart) * ((LookupSize - 1) / LookupSpan), 0, LookupSize - 1) * 3;
			// Faint cloudy grain that is strongest around the front, as in the reference.
			var nearFront = Math.Max(0, 1 - (((distance + 0.05f) * (distance + 0.05f)) / 0.09f));
			var grain = 1 + (0.05f * nearFront * Noise(x * 6.5f, (y * 6.5f) + 2.2f, grainTime + 11f));
			var r = Mix(_normalLookup[lookup], _alertLookup[lookup], alert) * grain;
			var g = Mix(_normalLookup[lookup + 1], _alertLookup[lookup + 1], alert) * grain;
			var b = Mix(_normalLookup[lookup + 2], _alertLookup[lookup + 2], alert) * grain;
			r = Mix(r, Mix(_normalLookup[0], _alertLookup[0], alert) * 0.28f, dim);
			g = Mix(g, Mix(_normalLookup[1], _alertLookup[1], alert) * 0.28f, dim);
			b = Mix(b, Mix(_normalLookup[2], _alertLookup[2], alert) * 0.28f, dim);
			var alpha = opacity * edge;
			pixels[offset] = (byte)(Math.Clamp(b, 0, 255) * alpha);
			pixels[offset + 1] = (byte)(Math.Clamp(g, 0, 255) * alpha);
			pixels[offset + 2] = (byte)(Math.Clamp(r, 0, 255) * alpha);
			pixels[offset + 3] = (byte)(255 * alpha);
		}
	}

	// The bend is smooth enough to be computed on a coarse grid and interpolated, which keeps a frame cheap.
	private void BuildWarp(float time, float amount)
	{
		for (var row = 0; row < WarpGrid; row++)
		{
			for (var column = 0; column < WarpGrid; column++)
			{
				var x = (((column / (WarpGrid - 1f)) * 2) - 1) * 1.1f;
				var y = (((row / (WarpGrid - 1f)) * 2) - 1) * 1.1f;
				var swirl = Noise((x * 0.75f) + 1.3f, (y * 0.75f) + 4.1f, time * 0.8f);
				_warp[(row * WarpGrid) + column] = amount * Noise((x * 0.85f) + (1.2f * swirl) + 5.2f, (y * 0.85f) - (1.2f * swirl) + 2.7f, time);
			}
		}
	}

	private float SampleWarp(float x, float y)
	{
		var gx = Math.Clamp(((x / 1.1f) + 1) * 0.5f * (WarpGrid - 1), 0, WarpGrid - 1.001f);
		var gy = Math.Clamp(((y / 1.1f) + 1) * 0.5f * (WarpGrid - 1), 0, WarpGrid - 1.001f);
		var column = (int)gx;
		var row = (int)gy;
		var fx = gx - column;
		var fy = gy - row;
		var top = Mix(_warp[(row * WarpGrid) + column], _warp[(row * WarpGrid) + column + 1], fx);
		var bottom = Mix(_warp[((row + 1) * WarpGrid) + column], _warp[((row + 1) * WarpGrid) + column + 1], fx);
		return Mix(top, bottom, fy);
	}

	private static void BuildLookup(float[] lookup, float hue, float lightnessLift)
	{
		var shift = hue - ReferenceHue;
		var stop = 1;
		for (var step = 0; step < LookupSize; step++)
		{
			var distance = LookupStart + (LookupSpan * step / (LookupSize - 1));
			while (stop < Profile.Length - 1 && Profile[stop].Distance < distance) stop++;
			var from = Profile[stop - 1];
			var to = Profile[stop];
			var blend = Math.Clamp((distance - from.Distance) / (to.Distance - from.Distance), 0, 1);
			var (r, g, b) = ShiftHue(Mix(Channel(from.Rgb, 16), Channel(to.Rgb, 16), blend), Mix(Channel(from.Rgb, 8), Channel(to.Rgb, 8), blend), Mix(Channel(from.Rgb, 0), Channel(to.Rgb, 0), blend), shift, lightnessLift);
			lookup[step * 3] = r;
			lookup[(step * 3) + 1] = g;
			lookup[(step * 3) + 2] = b;
		}
	}

	private static float Channel(int rgb, int shift) => (rgb >> shift) & 0xFF;

	// Rotates the hue of a stop. A plain rotation keeps HSL lightness, which makes yellows muddy and blues very dark, so for themes (not the alert color)
	// lightness is moved back toward the original perceived brightness.
	private static (float R, float G, float B) ShiftHue(float r, float g, float b, float degrees, float lightnessLift)
	{
		if (Math.Abs(degrees) < 0.01f && lightnessLift == 0) return (r, g, b);
		var (hue, saturation, lightness) = ToHsl(r / 255, g / 255, b / 255);
		lightness += lightnessLift * (1 - lightness);
		var newHue = ((hue + degrees) % 360 + 360) % 360;
		var shifted = FromHsl(newHue, saturation, lightness);
		if (lightnessLift != 0) return (shifted.R * 255, shifted.G * 255, shifted.B * 255);
		var target = Mix(Luminance(shifted.R, shifted.G, shifted.B), Luminance(r / 255, g / 255, b / 255), 0.55f);
		float low = 0, high = 1;
		for (var step = 0; step < 12; step++)
		{
			var middle = (low + high) / 2;
			var candidate = FromHsl(newHue, saturation, middle);
			if (Luminance(candidate.R, candidate.G, candidate.B) < target) low = middle;
			else high = middle;
		}
		var result = FromHsl(newHue, saturation, (low + high) / 2);
		return (result.R * 255, result.G * 255, result.B * 255);
	}

	private static float Luminance(float r, float g, float b) => (0.2126f * MathF.Pow(r, 2.2f)) + (0.7152f * MathF.Pow(g, 2.2f)) + (0.0722f * MathF.Pow(b, 2.2f));

	private static (float Hue, float Saturation, float Lightness) ToHsl(float r, float g, float b)
	{
		var max = MathF.Max(r, MathF.Max(g, b));
		var min = MathF.Min(r, MathF.Min(g, b));
		var lightness = (max + min) / 2;
		var chroma = max - min;
		if (chroma < 0.0001f) return (0, 0, lightness);
		var saturation = chroma / (1 - MathF.Abs((2 * lightness) - 1));
		var hue = max == r ? ((g - b) / chroma) % 6 : (max == g ? ((b - r) / chroma) + 2 : ((r - g) / chroma) + 4);
		hue *= 60;
		return (hue < 0 ? hue + 360 : hue, saturation, lightness);
	}

	private static (float R, float G, float B) FromHsl(float hue, float saturation, float lightness)
	{
		var c = (1 - MathF.Abs((2 * lightness) - 1)) * saturation;
		var x = c * (1 - MathF.Abs(((hue / 60) % 2) - 1));
		var m = lightness - (c / 2);
		var (r, g, b) = (int)(hue / 60) switch
		{
			0 => (c, x, 0f),
			1 => (x, c, 0f),
			2 => (0f, c, x),
			3 => (0f, x, c),
			4 => (x, 0f, c),
			_ => (c, 0f, x),
		};
		return (r + m, g + m, b + m);
	}

	// The body color of the orb (the lit teal just above the front) for a given hue, 0..255 per channel; used for the color swatches in settings.
	public static (byte R, byte G, byte B) SwatchColor(float hue)
	{
		var lookup = new float[LookupSize * 3];
		BuildLookup(lookup, hue, 0);
		var index = (int)((-0.2f - LookupStart) * ((LookupSize - 1) / LookupSpan)) * 3;
		return ((byte)Math.Clamp(lookup[index], 0, 255), (byte)Math.Clamp(lookup[index + 1], 0, 255), (byte)Math.Clamp(lookup[index + 2], 0, 255));
	}

	private static float Mix(float from, float to, float amount) => from + ((to - from) * amount);

	private static readonly byte[] Permutation = BuildPermutation();

	private static float Noise(float x, float y, float z)
	{
		var xi = (int)MathF.Floor(x);
		var yi = (int)MathF.Floor(y);
		var zi = (int)MathF.Floor(z);
		x -= xi;
		y -= yi;
		z -= zi;
		xi &= 255;
		yi &= 255;
		zi &= 255;
		var u = Fade(x);
		var v = Fade(y);
		var w = Fade(z);
		var a = Permutation[xi] + yi;
		var aa = Permutation[a] + zi;
		var ab = Permutation[a + 1] + zi;
		var b = Permutation[xi + 1] + yi;
		var ba = Permutation[b] + zi;
		var bb = Permutation[b + 1] + zi;
		return Mix(
			Mix(Mix(Gradient(Permutation[aa], x, y, z), Gradient(Permutation[ba], x - 1, y, z), u), Mix(Gradient(Permutation[ab], x, y - 1, z), Gradient(Permutation[bb], x - 1, y - 1, z), u), v),
			Mix(Mix(Gradient(Permutation[aa + 1], x, y, z - 1), Gradient(Permutation[ba + 1], x - 1, y, z - 1), u), Mix(Gradient(Permutation[ab + 1], x, y - 1, z - 1), Gradient(Permutation[bb + 1], x - 1, y - 1, z - 1), u), v),
			w);
	}

	private static float Fade(float t) => t * t * t * ((t * ((t * 6) - 15)) + 10);

	private static float Gradient(int hash, float x, float y, float z)
	{
		var h = hash & 15;
		var u = h < 8 ? x : y;
		var v = h < 4 ? y : (h is 12 or 14 ? x : z);
		return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
	}

	private static byte[] BuildPermutation()
	{
		var values = Enumerable.Range(0, 256).ToArray();
		var random = new Random(7319);
		for (var index = values.Length - 1; index > 0; index--)
		{
			var swap = random.Next(index + 1);
			(values[index], values[swap]) = (values[swap], values[index]);
		}
		var table = new byte[512];
		for (var index = 0; index < 512; index++) table[index] = (byte)values[index & 255];
		return table;
	}
}
