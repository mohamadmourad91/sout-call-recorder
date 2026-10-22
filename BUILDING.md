# Building on Windows

Install the .NET 8 SDK. Run commands from the repository root.

The translation model is deliberately kept outside Git history. Download the `Sout-Translation-Model-DE-AR-v1.2.zip` asset from the v1.2.0 GitHub release, then extract its contents:

```powershell
Expand-Archive -LiteralPath .\Sout-Translation-Model-DE-AR-v1.2.zip -DestinationPath .\model-build\translation-assets -Force
dotnet build .\src\CallRecorder.csproj -c Release
dotnet run --project .\checks\Checks.csproj -c Release
```

The model archive contains five files directly at its root. Their SHA-256 values must match `src/TranslationAssets.json`. The application also verifies the extracted model assets before loading them.

Publish a self-contained x64 EXE:

```powershell
dotnet publish .\src\CallRecorder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\release-v1.2
```

Copy the contents of `licenses/`, `THIRD_PARTY_NOTICES.md`, and the user documentation alongside the EXE when distributing it. Translation weights are embedded in the EXE. The Whisper speech-recognition model downloads separately on first use.

## Rebuilding the translation model

Alternatively, run `build_translation_model.py` to download the pinned original Helsinki-NLP model and regenerate the quantized ONNX assets and manifest. This requires Python, PyTorch, Transformers, SentencePiece, NumPy, ONNX, ONNX Runtime, ml_dtypes, and huggingface_hub. The Windows end-user EXE does not require Python.

The script records asset hashes and compares a sample translation before finishing. Different library versions can change generated model hashes; retain the manifest produced by the same build.

## Additional integration checks

These commands require local audio fixtures, which are not shipped in Git:

```powershell
dotnet run --project .\checks\Checks.csproj -c Release -- --live-lifecycle
dotnet run --project .\checks\Checks.csproj -c Release -- --live-file .\sample-german.wav
```

The following checks play audio on the selected Windows output endpoint:

```powershell
dotnet run --project .\checks\Checks.csproj -c Release -- --loopback
dotnet run --project .\checks\Checks.csproj -c Release -- --live-loopback .\sample-german.wav
```

The recorder, real-time audio replay, actual WASAPI loopback, simultaneous recording and translation, silence segmentation, device failure, and cancellation were exercised during development. This does not establish transcription or translation accuracy for every conversation.
