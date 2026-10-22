using System.Text.RegularExpressions;

namespace Moreno.Voice.Services;

internal sealed class TranscriptComposer
{
	private const double SentencePauseSeconds = 1.0;
	private static readonly Dictionary<string, string> TermCorrections = new(StringComparer.OrdinalIgnoreCase)
	{
		["moreno dot voice"] = "Moreno.Voice",
		["moreno voice"] = "Moreno Voice",
		["visual studio code"] = "Visual Studio Code",
		["windows terminal"] = "Windows Terminal",
		["sherpa onnx"] = "Sherpa-ONNX",
		["onnx runtime"] = "ONNX Runtime",
		["power shell"] = "PowerShell",
		["chat g p t"] = "ChatGPT",
		["chat gpt"] = "ChatGPT",
		["open ai"] = "OpenAI",
		["c plus plus"] = "C++",
		["c sharp"] = "C#",
		["dot net"] = ".NET",
		["nu get"] = "NuGet",
		["get hub"] = "GitHub",
		["getthub"] = "GitHub",
		["gethub"] = "GitHub",
		["c-sharp"] = "C#",
		["codex"] = "Codex",
		["github"] = "GitHub",
		["powershell"] = "PowerShell",
		["sherpa"] = "Sherpa",
		["whisper"] = "Whisper",
		["api"] = "API",
		["cli"] = "CLI",
		["json"] = "JSON",
		["yaml"] = "YAML",
		["xaml"] = "XAML",
		["xml"] = "XML",
		["onnx"] = "ONNX",
		["url"] = "URL",
		["http"] = "HTTP",
		["https"] = "HTTPS",
		["cpu"] = "CPU",
		["gpu"] = "GPU",
	};
	private static readonly Regex TermPattern = new(
		@"(?<![\p{L}\p{N}])(?:" + string.Join("|", TermCorrections.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex SentenceStart = new(@"([.!?]\s+)(\p{L})", RegexOptions.CultureInvariant);
	// The large model sometimes attaches a currency symbol to the word before it ("about$12").
	private static readonly Regex GluedCurrency = new(@"(?<=[\p{L}\p{N}])(?=[$€£]\d)", RegexOptions.CultureInvariant);
	private static readonly Regex LoneI = new(@"(?<![\p{L}\p{N}])i(?![\p{L}\p{N}])", RegexOptions.CultureInvariant);
	// The model sometimes emits one of these at the end of a pause and again when the speaker actually says it.
	private static readonly HashSet<string> RepeatableFunctionWords = new(StringComparer.OrdinalIgnoreCase) { "and", "the", "a", "to", "of", "so", "but", "that", "is", "it", "in", "i" };
	private bool _hasCommitted;
	private string _lastCommitted = string.Empty;

	public TranscriptComposer Clone() => (TranscriptComposer)MemberwiseClone();

	public void Reset()
	{
		_hasCommitted = false;
		_lastCommitted = string.Empty;
	}

	// The separator and capitalization depend only on what was committed before this phrase, so they stay the same on every update of the phrase.
	public string Compose(string recognized, double pauseBeforeSeconds)
	{
		var text = Normalize(recognized);
		if (_hasCommitted && pauseBeforeSeconds < 2) text = DropRepeatedBoundaryWord(text);
		if (text.Length == 0) return string.Empty;
		if (!_hasCommitted || pauseBeforeSeconds >= SentencePauseSeconds || EndsSentence(_lastCommitted)) text = char.ToUpperInvariant(text[0]) + text[1..];
		return _hasCommitted ? $" {text}" : text;
	}

	// Text from the large model already has its own punctuation and capitalization, so it is kept as is apart from known term spellings.
	public string ComposeRefined(string refined)
	{
		var text = CleanControlCharacters(GluedCurrency.Replace(TermPattern.Replace(refined.Trim(), match => TermCorrections[match.Value]), " "));
		if (text.Length == 0) return string.Empty;
		return _hasCommitted ? $" {text}" : text;
	}

	public void Commit(string typed)
	{
		if (typed.Length == 0) return;
		_hasCommitted = true;
		_lastCommitted = typed;
	}

	private string DropRepeatedBoundaryWord(string text)
	{
		var previousWord = _lastCommitted.TrimEnd().Split(' ')[^1];
		var firstSpace = text.IndexOf(' ');
		var firstWord = firstSpace < 0 ? text : text[..firstSpace];
		if (!RepeatableFunctionWords.Contains(firstWord) || !firstWord.Equals(previousWord, StringComparison.OrdinalIgnoreCase)) return text;
		return firstSpace < 0 ? string.Empty : text[(firstSpace + 1)..];
	}

	private static bool EndsSentence(string text) => text.Length > 0 && text[^1] is '.' or '!' or '?';

	private static string Normalize(string text)
	{
		var normalized = SentenceStart.Replace(text.Trim().ToLowerInvariant(), match => match.Groups[1].Value + match.Groups[2].Value.ToUpperInvariant());
		normalized = LoneI.Replace(normalized, "I");
		normalized = TermPattern.Replace(normalized, match => TermCorrections[match.Value]);
		return CleanControlCharacters(normalized);
	}

	private static string CleanControlCharacters(string text) => new(text.Where(character => !char.IsControl(character) || char.IsWhiteSpace(character)).ToArray());
}
