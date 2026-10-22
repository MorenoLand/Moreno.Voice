using System.Text;

namespace Moreno.Voice.Services;

internal readonly record struct TextEdit(int Backspaces, string Text)
{
	public bool IsEmpty => Backspaces == 0 && Text.Length == 0;
}

internal enum TypingResult { NotAttempted, Succeeded, Failed }

// Decides what the target app should contain and the smallest edit that gets it there; the caller does the typing.
// An utterance stays editable while it waits for the large model to re-read it, then its text is swapped for the corrected version and it is committed.
// Committed text is never edited again, so a late or failed correction can never damage what was typed before it.
internal sealed class PhraseAssembler
{
	private readonly TranscriptComposer _composer = new();
	private readonly List<Block> _open = [];
	private string _typed = string.Empty;
	private string _desired = string.Empty;

	public void Reset()
	{
		_composer.Reset();
		_open.Clear();
		_typed = string.Empty;
		_desired = string.Empty;
	}

	public TextEdit Plan(RecognitionUpdate update)
	{
		var block = _open.Find(candidate => candidate.Id == update.UtteranceId);
		if (block is null)
		{
			// A correction for an utterance that was already committed (for example because dictation ended first) has nothing left to change.
			if (update.IsRefinement) return default;
			block = new Block { Id = update.UtteranceId, Refinable = update.Refinable };
			_open.Add(block);
		}
		if (update.IsRefinement)
		{
			block.RefinementDone = true;
			block.Refined = update.Text.Length > 0 ? update.Text : null;
		}
		else
		{
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
		return PlanEdit();
	}

	// Report what happened to the edit from Plan. Nothing is committed until a block is complete, and then only what is really in the target.
	public void Complete(TypingResult result)
	{
		if (result == TypingResult.Succeeded)
		{
			_typed = _desired;
			foreach (var block in _open) block.Typed = block.Composed;
		}
		else if (result == TypingResult.Failed)
		{
			// The target is in an unknown state, so stop touching anything typed so far.
			_open.Clear();
			_typed = string.Empty;
			return;
		}
		while (_open.Count > 0 && _open[0].IsDone)
		{
			var block = _open[0];
			_composer.Commit(block.Typed);
			_typed = _typed.Length >= block.Typed.Length ? _typed[block.Typed.Length..] : string.Empty;
			_open.RemoveAt(0);
		}
	}

	// Dictation ended: whatever is waiting stays as typed.
	public void Finish()
	{
		foreach (var block in _open) _composer.Commit(block.Typed);
		_open.Clear();
		_typed = string.Empty;
		_desired = string.Empty;
	}

	private TextEdit PlanEdit()
	{
		var context = _composer.Clone();
		var desired = new StringBuilder();
		foreach (var block in _open)
		{
			block.Composed = ComposeBlock(block, context);
			desired.Append(block.Composed);
		}
		_desired = desired.ToString();
		var common = 0;
		while (common < _typed.Length && common < _desired.Length && _typed[common] == _desired[common]) common++;
		return new TextEdit(_typed.Length - common, _desired[common..]);
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
			var composed = context.Compose(phrase.Raw, phrase.PauseBeforeSeconds);
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
		public List<Phrase> Phrases = [];
		public string? Refined;
		public bool RefinementDone;
		public string Composed = string.Empty;
		public string Typed = string.Empty;

		public bool IsDone => Refinable ? RefinementDone : Phrases.All(phrase => phrase.IsFinal);
	}
}
