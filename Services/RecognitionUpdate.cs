namespace Moreno.Voice.Services;

// UtteranceId groups the phrases of one spoken sentence or thought; Refinable says a corrected version of the whole utterance will follow as an IsRefinement update
// (empty text means the correction was skipped). Phrases of an utterance that cannot be refined get a unique id each, so they are complete as soon as they are final.
internal readonly record struct RecognitionUpdate(string Text, bool IsFinal, double PauseBeforeSeconds, int UtteranceId = 0, int PhraseId = 0, bool Refinable = false, bool IsRefinement = false);
