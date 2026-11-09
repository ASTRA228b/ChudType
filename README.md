# ChudType 0.1.4

ChudType is a Windows dictation app from CHUDAI. Its small floating bar uses the logo from the official ChudGPT landing page. Click the logo to start dictation, or drag it to move the bar and save its position. You can also hold **Ctrl + Shift + Space** and release it when you finish speaking. The bar shows the live microphone level, elapsed time, transcription progress, and stop and cancel controls. Use **Dictation → Floating bar** to change its visibility, choose a dock edge, or reset the saved position.

[Product page](https://chudgpt-landing.vercel.app/ChudType) · [Windows downloads](https://github.com/ASTRA228b/ChudType/releases/latest)

## Get started

1. Install the x64 MSI, or extract the portable ZIP to a writable folder and run **ChudType.exe**.
2. In first-run setup, allow microphone use, choose an input, and approve the download of a Whisper model. The model’s size and SHA-256 digest are checked before use.
3. Run a test in ChudType. Then focus an editable field in another app and use the shortcut or the floating bar. When you finish speaking, press **Enter** or click **Enter ↵** on the bar to insert the transcript at the cursor. If review is enabled, press **Enter** again when the preview appears.
4. Choose **AI Processing → ChudGPT Dictation** to try the dedicated local cleanup model.

To change the shortcut, open **Dictation**, click the current shortcut, and press the new key combination. Release its main key to save it. The dashboard header shows the selected Whisper model and whether it is recording, transcribing, or idle. **Stop model** cancels active dictation and unloads the speech model. The next dictation loads it again as needed.

The release targets Windows 11 x64. It includes .NET 10 and supports CPU inference with and without AVX. Packages created with the Visual C++ redistribution path include the app-local CRT. Otherwise install the Microsoft Visual C++ x64 Redistributable if required by the system. The packages are unsigned.

The portable build keeps its data in the **data** folder beside the executable. The installed app uses **%LOCALAPPDATA%\CHUDAI\ChudType**. Keep that folder if you want to retain downloaded models, settings, and saved transcripts. Audio is processed in memory. Transcript history, statistics, and online text processing require opt-in.

## Components

| Component | Responsibility |
| --- | --- |
| UI | WPF dashboard, first-run setup, tray, and nonactivating overlay |
| Core | Dictation state machine, preferences, snippet expansion, cleanup model, and provider contracts |
| Audio | WASAPI microphone capture, level measurements, and energy-based voice activity detection |
| Speech | Verified model download and native Whisper.net inference |
| Storage | SQLite via the Windows SQLite library, JSON exports, and local structured diagnostics |
| Windows | Global keyboard hook, monitor placement, accessibility focus verification, and Unicode input |
| model | Training script for the bundled ChudGPT Dictation checkpoint |
| tests | xUnit tests, mock HTTP providers, and a recorded-speech integration fixture |
| packaging | WiX installer and self-contained portable build |

Whisper Tiny, Base, and Small support automatic language detection and manual selection for English, Spanish, French, Portuguese, German, Russian, Italian, Japanese, Korean, Chinese, Polish, and Ukrainian. The English-only ChudGPT Dictation v1 checkpoint is a 133-parameter neural classifier trained on 164 synthetic examples. It makes a narrow edit to some spoken “um” and “uh” fillers. The training script reports 14/14 held-out synthetic cases, which covers those examples only. Raw transcripts are available in the app. The checkpoint ships inside ChudType and has no hosted endpoint.

Online ChudGPT cleanup follows the official Plato stateless chat and Main/Public one-shot generation contracts. Custom HTTPS providers accept JSON with **text** and **style** and return **reply**. Opt-in is required to send transcript text. Successful rewrites require review before insertion, with a choice to use the raw text. Provider failures fall back to local text. [ChudGPT API guide](https://chudgpt-landing.vercel.app/api-guide)

## Build and test

Use the .NET 10 SDK on Windows. Install WiX CLI 5.0.2 to build the MSI.

    dotnet test ChudType.sln
    pwsh -File packaging/Build.ps1 -VCRedistPath "<Visual Studio x64 Microsoft.VC143.CRT directory>"

For the recorded-speech test, set **CHUDTYPE_TEST_ASSETS** to a directory containing **jfk.wav** from the whisper.cpp sample folder and the verified multilingual **ggml-tiny.bin**. The test prints cold and warm latency for the 11-second WAV. The benchmark uses CPU inference and includes segmentation and recognition. It excludes live microphone capture and insertion.

## Compatibility and follow-up

- Automatic insertion requires a stable, writable Windows accessibility target. Protected fields are blocked when Windows exposes their password status. Some browser, terminal, editor, and elevated surfaces require the explicit Copy action. Multiline transcripts are offered for copying to avoid sending submission keystrokes.
- Hands-free ends after a pause and recordings have a five-minute cap. The activity detector uses audio energy. Background noise may require threshold adjustment.
- This release uses CPU inference. Validate Windows 10, GPU backends, noisy and multilingual recordings, hardware disconnection, mixed-DPI displays, and more Windows text targets before widening the support claims.
- The editor provides developer vocabulary and opt-in spoken punctuation. Full language-aware code formatting, microphone-side pronunciation hints, acrylic, and remote speech recognition remain future work.
