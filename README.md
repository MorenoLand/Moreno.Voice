Types what you say into whatever app you're using. Speech recognition runs on your machine, and your audio never leaves it.

Click the floating orb or press `Ctrl+Alt+Space` to start and stop. Words appear as you speak, and each finished sentence is then corrected and punctuated by a larger model. Terminal windows get text through their paste shortcut; other apps get simulated keystrokes.

It only runs on Windows right now. The recognition, text handling and orb code is plain .NET and portable. The window, microphone capture, global hotkey and keystroke injection are what would need porting; see [Portability](#portability).

## Run

Install the .NET 9 SDK, then run:

```powershell
dotnet run --project Moreno.Voice.csproj
```

The speech model is stored under the current user's local application data folder. For now that means Windows x64 with the .NET 9 Desktop Runtime.

## Publish

Create a single-file Windows x64 app with:

```powershell
dotnet publish .\Moreno.Voice.csproj -c Release -r win-x64 --self-contained false
```

The publish folder contains one executable and Sherpa's two required native runtime DLLs. The speech model downloads on first launch without unpacking an archive.

## Speech engine

Dictation runs fully on your machine in two passes.

**Live typing.** [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) with an int8 streaming English Zipformer model, modified beam search and contextual hotwords for developer terms types words as you speak. An adaptive energy detector tracks the microphone noise floor, and phrases end on real silence rather than the model's blank-token endpoint, which also fires mid-speech. Stopping flushes the model with floor-level noise so the last words are typed (digital silence makes the model invent words).

**Sentence correction.** Phrases that follow each other with short pauses are grouped into an utterance. When it ends, a much larger offline model ([NVIDIA Parakeet TDT 0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2), int8) re-reads its audio and the typed text is replaced with the corrected, punctuated, capitalized version, usually within a second or two. This fixes the streaming model's weak spots, such as short or unusual words and words it runs together ("alt space crap" instead of "altspacecrap"). It can be switched off in Settings, and if the model is missing or fails, dictation simply continues with the live text. Typing is careful about focus. What reached the window is tracked exactly, so a failed or partly accepted keystroke batch is never typed twice. If you switch to another window, whatever is typed so far is frozen and only appended to afterwards, so a late correction can never edit text or a caret position you changed, and what you say meanwhile is kept and typed when you switch back (for a couple of minutes after you stop). Terminals get text through the clipboard, with a pause between pastes so one paste can't overwrite the next. The last word of a phrase that is still being recognized is held back until it settles.

Developer terms and a lone `i` are restored before typing, phrases are joined with a space, and a function word repeated across a phrase boundary is dropped.

### Models

No model files are stored in this repository. On first launch the app downloads them to `%LOCALAPPDATA%\Moreno.Voice\Models` (the equivalent local application data folder on other systems):

| Model | Size | Source |
| --- | --- | --- |
| Streaming Zipformer (live typing) | about 190 MB | [csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-21](https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-21) |
| Parakeet TDT 0.6B v2 (sentence correction) | about 660 MB | [csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8) |

Each download is pinned to an exact repository revision, and the size and SHA-256 of every file are checked as it downloads (see `Services/ModelInstaller.cs`), so a changed or corrupted file is rejected. The sentence-correction model is downloaded in the background after the app is usable and needs roughly 1 GB of memory once loaded. Microphone audio is never sent anywhere.

### Attribution

The Parakeet TDT 0.6B v2 model is by NVIDIA and licensed [CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/); the int8 sherpa-onnx conversion is by [csukuangfj](https://huggingface.co/csukuangfj). The app downloads it at runtime and does not redistribute it. sherpa-onnx is Apache-2.0.

## Orb

The orb is a flat disc with a pale liquid whose bright front tilts and sloshes, modeled on a capture of the reference orb (gradient profile and colors sampled from it). It is drawn by a small pure-C# renderer into a bitmap, with no shaders or bitmap effects.

- **Inactive:** an empty, dim, still disc. Nothing renders, so it uses no CPU.
- **Listening:** the liquid fills in and rests low; your voice raises it and kicks the tilt through a spring simulation, so it overshoots and settles like liquid.
- **Error:** turns coral for a few seconds, then drains.

Right-click the orb for **Settings**: orb size, color (Mint is the reference; Ocean, Violet and Rose shift its hue), and the start/stop chime and its volume. Settings are saved as JSON in the local application data folder.

## Portability

Voice detection, text composition and edit planning, settings, the model installer, the sentence-correction wrapper and the orb renderer (`Services/EnergyVad.cs`, `TranscriptComposer.cs`, `PhraseAssembler.cs`, `AppSettings.cs`, `ModelInstaller.cs`, `PhraseRefiner.cs`, `Controls/LiquidRenderer.cs`) use no Windows or WPF APIs and compile as plain `net9.0`. Still Windows-only: the WPF windows and styles, `ConsoleTextInjector` (`SendInput`, clipboard paste, foreground window), the global hotkey (`RegisterHotKey`), microphone capture inside `StreamingRecognizer` (NAudio/WinMM) and the chimes (`System.Media.SoundPlayer`). A port would replace those behind small interfaces and move the XAML to a cross-platform toolkit such as Avalonia.
