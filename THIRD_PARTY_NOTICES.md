# Third-party components

Sout includes the following third-party software and model assets. Their license texts and notices are available in `licenses/` and accompany the Windows release.

| Component | Source | License |
| --- | --- | --- |
| NAudio 2.2.1 | https://github.com/naudio/NAudio | MIT |
| .NET 8 runtime | https://github.com/dotnet/runtime | MIT and third-party notices |
| Whisper.net 1.9.1 | https://github.com/sandrohanea/whisper.net | MIT |
| whisper.cpp | https://github.com/ggml-org/whisper.cpp | MIT |
| Whisper large-v3-turbo model, GGML Q5 conversion | https://huggingface.co/ggerganov/whisper.cpp | MIT |
| ONNX Runtime 1.30.0 | https://github.com/microsoft/onnxruntime | MIT and third-party notices |
| Microsoft.ML.Tokenizers 2.0.1 | https://github.com/dotnet/machinelearning | MIT and third-party notices |
| Helsinki-NLP/opus-mt-de-ar model | https://huggingface.co/Helsinki-NLP/opus-mt-de-ar | Apache 2.0 |

The German-to-Arabic model was exported from original revision `d5d9d874e996ff95c10a58bccaab541e5033022b` to ONNX opset 17, then dynamically quantized to unsigned INT8. The conversion is implemented by `build_translation_model.py`. Asset hashes are listed in `src/TranslationAssets.json`. See `licenses/Translation-model-NOTICE.txt` for attribution and modification details.

Google FLEURS samples used during development are licensed CC BY 4.0. Audio samples, recordings, generated transcripts, and machine-specific verification reports are not included in this repository or the application download.
