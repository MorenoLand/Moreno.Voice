using System.Text;

namespace Moreno.Voice.Services;

internal readonly record struct TextEdit(int Backspaces, string Text)
{
	public bool IsEmpty => Backspaces == 0 && Text.Length == 0;
}

// Keeps a model of what the target app should contain and of what it really contains, and plans the smallest edit between them; the caller does the typing.
//
// Three rules keep it from repeating or damaging text:
// - Complete() records what actually reached the target (even a partial send), so the next edit is always planned from reality and nothing is typed twice.
// - When the target loses focus the typed text is frozen. Anything typed afterwards is only ever appended, so a late correction can never backspace into
//   text the user typed or a caret they moved in the meantime.
// - Text that arrives while the target is unfocused is kept and delivered, in order, when it is focused again.
internal sealed class PhraseAssembler
{
	private readonly TranscriptComposer _composer = new();
	private readonly List<Block> _open = [];
	private string _sealed = string.Empty;
	private string _live = string.Empty;
	private string _wanted = string.Empty;

	// Nothing is waiting to be typed or corrected.
	public bool IsIdle => _open.Count == 0;

	// keepContext continues after what was typed before (a new dictation in the same place), so the first words get a separating space.
	public void Reset(bool keepContext = false)
	{
		if (!keepContext) _composer.Reset();
		Abandon();
	}

	public void Abandon()
	{
		_open.Clear();
		_sealed = string.Empty;
		_live = string.Empty;
		_wanted = string.Empty;
	}

	public void Apply(RecognitionUpdate update)
	{
		var block = _open.Find(candidate => candidate.Id == update.UtteranceId);
		if (block is null)
		{
			// A correction for an utterance that was already committed (or belongs to an earlier dictation) has nothing left to change.
			if (update.IsRefinement) return;
			block = new Block { Id = update.UtteranceId, Refinable = update.Refinable };
			_open.Add(block);
		}
		if (update.IsRefinement)
		{
			block.RefinementDone = true;
			block.Refined = update.Text.Length > 0 ? update.Text : null;
			return;
		}
		var phrase = block.Phrases.Find(candidate => candidate.Id == update.PhraseId);
		if (phrase is null)
		{
			phrase = new Phrase { Id = update.PhraseId };
			block.Phrases.Add(phrase);
		}
		phrase.Raw = update.Text;
		phrase.PauseBeforeSeconds = update.PauseBeforeSeconds;
		phrase.IsFinal |= update.IsFinal;
	}

	// Dictation ended, so no further corrections are coming for whatever is still open.
	public void EndSession()
	{
		foreach (var block in _open) block.Closed = true;
	}

	// The target lost focus: what is typed so far is frozen from now on.
	public void FocusLost()
	{
		_sealed += _live;
		_live = string.Empty;
	}

	public TextEdit NextEdit()
	{
		var context = _composer.Clone();
		var desired = new StringBuilder();
		foreach (var block in _open)
		{
			block.Composed = ComposeBlock(block, context);
			desired.Append(block.Composed);
		}
		_wanted = _sealed.Length == 0 ? desired.ToString() : Align(_sealed, desired.ToString());
		var common = 0;
		while (common < _live.Length && common < _wanted.Length && _live[common] == _wanted[common]) common++;
		return new TextEdit(_live.Length - common, _wanted[common..]);
	}

	// Report what really happened to the edit: how many backspaces and which characters reached the target (possibly none).
	public void Complete(int backspacesSent, string textTyped)
	{
		_live = _live[..Math.Max(0, _live.Length - backspacesSent)] + textTyped;
		if (_sealed.Length == 0)
		{
			while (_open.Count > 0 && _open[0].IsDone && _live.StartsWith(_open[0].Composed, StringComparison.Ordinal))
			{
				var block = _open[0];
				_composer.Commit(block.Composed);
				_live = _live[block.Composed.Length..];
				_open.RemoveAt(0);
			}
		}
		else if (_open.TrueForAll(block => block.IsDone) && _live == _wanted)
		{
			_composer.Commit(_sealed + _live);
			_open.Clear();
			_sealed = string.Empty;
			_live = string.Empty;
		}
	}

	// The part of the intended text that follows the frozen text, always starting at a word boundary. Normally the frozen text is a prefix; when a correction
	// reworded it, the words already delivered are skipped instead. A word that was frozen only partly is left as typed: finishing it later could splice
	// the rest onto whatever the user typed in the meantime.
	private static string Align(string frozen, string desired)
	{
		if (desired.StartsWith(frozen, StringComparison.Ordinal))
		{
			var rest = desired[frozen.Length..];
			if (frozen.Length == 0 || char.IsWhiteSpace(frozen[^1]) || rest.Length == 0 || char.IsWhiteSpace(rest[0])) return rest;
			var boundary = 0;
			while (boundary < rest.Length && !char.IsWhiteSpace(rest[boundary])) boundary++;
			return rest[boundary..];
		}
		var delivered = SplitWords(frozen);
		var intended = SplitWords(desired);
		if (delivered.Count == 0) return desired;
		var remainder = new StringBuilder();
		for (var index = delivered.Count; index < intended.Count; index++) remainder.Append(' ').Append(intended[index]);
		return remainder.ToString();
	}

	private static List<string> SplitWords(string text) => [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

	private static string WithoutLastWord(string text)
	{
		var trimmed = text.TrimEnd();
		var space = trimmed.LastIndexOf(' ');
		return space < 0 ? string.Empty : trimmed[..space];
	}

	private static string ComposeBlock(Block block, TranscriptComposer context)
	{
		if (block.Refined is not null)
		{
			var refined = context.ComposeRefined(block.Refined);
			context.Commit(refined);
			return refined;
		}
		var text = new StringBuilder();
		foreach (var phrase in block.Phrases)
		{
			// The last word of a phrase that is still being recognized is the part the model keeps changing, so it is not typed until it is settled.
			var composed = context.Compose(phrase.IsFinal ? phrase.Raw : WithoutLastWord(phrase.Raw), phrase.PauseBeforeSeconds);
			context.Commit(composed);
			text.Append(composed);
		}
		return text.ToString();
	}

	private sealed class Phrase
	{
		public int Id;
		public string Raw = string.Empty;
		public double PauseBeforeSeconds;
		public bool IsFinal;
	}

	private sealed class Block
	{
		public int Id;
		public bool Refinable;
		public bool Closed;
		public List<Phrase> Phrases = [];
		public string? Refined;
		public bool RefinementDone;
		public string Composed = string.Empty;

		public bool IsDone => Closed || (Refinable ? RefinementDone : Phrases.TrueForAll(phrase => phrase.IsFinal));
	}
}
