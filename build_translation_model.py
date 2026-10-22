"""Build a CPU INT8 ONNX translator from the original Helsinki-NLP weights."""
import json, os, shutil, sys
from pathlib import Path
import hashlib
sys.path.insert(0, str(Path(__file__).parent / ".build-python"))
import numpy as np
import torch
from transformers import MarianMTModel, MarianTokenizer
from huggingface_hub import snapshot_download
from onnxruntime.quantization import quantize_dynamic, QuantType
import onnxruntime as ort
import onnx

torch.set_num_threads(4)
repo = "Helsinki-NLP/opus-mt-de-ar"
revision = "d5d9d874e996ff95c10a58bccaab541e5033022b"
source = Path(snapshot_download(repo, revision=revision, local_dir=Path(__file__).parent / "model-build/original", allow_patterns=["*.json", "*.spm", "pytorch_model.bin", "model.safetensors"]))
print("Original model:", repo, revision, flush=True)
model = MarianMTModel.from_pretrained(source, attn_implementation="eager").eval()
# SentencePiece's Windows filename loader needs an ASCII relative path here.
tokenizer = MarianTokenizer.from_pretrained(os.path.relpath(source, Path.cwd()))
text = ">>ara<< Guten Morgen. Ich möchte einen Termin für morgen vereinbaren."
batch = tokenizer([text], return_tensors="pt")
with torch.no_grad():
    generated = model.generate(**batch, max_new_tokens=80, num_beams=1)
print("PyTorch baseline:", tokenizer.batch_decode(generated, skip_special_tokens=True), flush=True)
build = Path(__file__).parent / "model-build"
build.mkdir(exist_ok=True)
destination = Path(os.environ["LOCALAPPDATA"]) / "SoutCallRecorder/models/translation-de-ar-v1.2"
destination.mkdir(parents=True, exist_ok=True)

class Encoder(torch.nn.Module):
    def __init__(self):
        super().__init__(); self.encoder = model.get_encoder()
    def forward(self, input_ids, attention_mask):
        return self.encoder(input_ids=input_ids, attention_mask=attention_mask, return_dict=False)[0]

class Decoder(torch.nn.Module):
    def __init__(self):
        super().__init__(); self.decoder=model.model.decoder; self.head=model.lm_head
        self.register_buffer("bias", model.final_logits_bias)
    def forward(self, input_ids, encoder_attention_mask, encoder_hidden_states):
        hidden=self.decoder(input_ids=input_ids, encoder_attention_mask=encoder_attention_mask,
            encoder_hidden_states=encoder_hidden_states, use_cache=False, return_dict=False)[0]
        return self.head(hidden)+self.bias

ids = batch["input_ids"]; mask = batch["attention_mask"]
with torch.no_grad():
    hidden = model.get_encoder()(input_ids=ids, attention_mask=mask).last_hidden_state
    torch.onnx.export(Encoder(), (ids, mask), str(build/"encoder.onnx"),
        input_names=["input_ids","attention_mask"], output_names=["last_hidden_state"],
        dynamic_axes={"input_ids":{0:"batch",1:"source"},"attention_mask":{0:"batch",1:"source"},"last_hidden_state":{0:"batch",1:"source"}},
        opset_version=17, dynamo=False)
    decoder_ids=torch.tensor([[model.config.decoder_start_token_id, 42, 100]], dtype=torch.int64)
    torch.onnx.export(Decoder(), (decoder_ids, mask, hidden), str(build/"decoder.onnx"),
        input_names=["input_ids","encoder_attention_mask","encoder_hidden_states"], output_names=["logits"],
        dynamic_axes={"input_ids":{0:"batch",1:"target"},"encoder_attention_mask":{0:"batch",1:"source"},"encoder_hidden_states":{0:"batch",1:"source"},"logits":{0:"batch",1:"target"}},
        opset_version=17, dynamo=False)
print("ONNX export complete", flush=True)
for part in ("encoder", "decoder"):
    quantize_dynamic(onnx.load(str(build/f"{part}.onnx")), str(destination/f"{part}_model_quantized.onnx"),
        weight_type=QuantType.QUInt8, per_channel=False, extra_options={"MatMulConstBOnly":True})
for name in ("source.spm","vocab.json","config.json"):
    shutil.copy2(source/name,destination/name)

opts=ort.SessionOptions(); opts.intra_op_num_threads=4
enc=ort.InferenceSession(str(destination/"encoder_model_quantized.onnx"),opts)
dec=ort.InferenceSession(str(destination/"decoder_model_quantized.onnx"),opts)
encoded=enc.run(None,{"input_ids":ids.numpy(),"attention_mask":mask.numpy()})[0]
output=[model.config.decoder_start_token_id]
for step in range(100):
    scores=dec.run(None,{"input_ids":np.array([output],dtype=np.int64),"encoder_attention_mask":mask.numpy(),"encoder_hidden_states":encoded})[0]
    scores[0,-1,model.config.pad_token_id]=-np.inf
    next_token=int(scores[0,-1].argmax())
    if next_token == model.config.eos_token_id: break
    output.append(next_token)
else: raise RuntimeError("Exported model did not terminate")
print("INT8 ONNX:",tokenizer.decode(output,skip_special_tokens=True),flush=True)
assets=[]
for name in ("encoder_model_quantized.onnx","decoder_model_quantized.onnx","source.spm","vocab.json","config.json"):
    path=destination/name
    assets.append({"Name":name,"Size":path.stat().st_size,"Sha256":hashlib.sha256(path.read_bytes()).hexdigest()})
(Path(__file__).parent/"src/TranslationAssets.json").write_text(json.dumps({"Repository":repo,"Revision":revision,"Assets":assets},indent=2)+"\n",encoding="utf8")
packaged = build / "translation-assets"
packaged.mkdir(exist_ok=True)
for asset in assets:
    shutil.copy2(destination/asset["Name"], packaged/asset["Name"])
print("Verified original-weight model built:", destination, flush=True)
