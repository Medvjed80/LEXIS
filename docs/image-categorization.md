# Categorize images

*Media Gallery › Categorize images ▾* labels each picture with categories from a list the examiner edits, each with a
confidence from 0 to 1. **The result is AI-generated and unverified: a review aid that points to pictures worth a look,
not a finding.** Every categorized picture must be checked by the examiner.

## Running it

| Menu entry | Pictures sent |
|---|---|
| Selected images | the tiles selected in the gallery (click, Ctrl+click, Shift+click) |
| Images the filters keep (all pages) | every picture the current filters keep (type, app, origin, date, AI category, size, text) |
| All images | every picture of the gallery's current source (*Linked to records* or *Whole extraction*) |
| Export categories (CSV)… | every stored result of the case, see below |

Only pictures are sent (videos and audio are left out). Progress shows under the gallery header; **Stop** pauses.
A new run **resumes**: a picture already categorized by the same engine, model and prompt version is skipped. An error on
one picture (unreadable file, timeout, malformed answer) is recorded for that picture and the batch goes on; errors are
tried again on the next run. When the model declines to label a picture, it is recorded as **model refused** (and not
tried again with the same model and prompt version).

Pictures that live only in the extraction are read from it read-only, in memory; nothing is written to the evidence.
A picture the vision model cannot read (HEIC, TIFF, BMP, or larger than 15 MB) is sent as a JPEG copy made in memory
with the codecs Windows has. Images sent to a **remote** server are confirmed first; Ollama and LM Studio on this
computer are local.

## Results in the gallery

* A purple **badge** on the tile lists the categories at or above the threshold with their confidence
  ("AI: Drugs 0.82 · Weapons 0.64"); its tooltip gives every score, the engine, model, prompt version, time and the
  model's one-line reason. "AI: model refused" marks a refusal.
* The **AI category** filter keeps the pictures in one category (at or above the threshold), *Any AI category*,
  *Not categorized* or *Model refused*. It combines with the other filters.
* **Tags** (off by default, *AI settings › Categorize images*): each picture that has **no tag yet** is tagged with its
  best category above the threshold, as `AI: <category>` ("Other/None" is never a tag). The `AI:` prefix marks the tag
  as AI-generated; a tag the examiner set, on the file or on its record, is never replaced.

## Settings (*AI settings › Categorize images (Media Gallery)*)

| Setting | Default | |
|---|---|---|
| Categories | Drugs, Money/Cash, Weapons, Documents/IDs, Vehicles, Screenshots/Chats, Nudity/Sexual, Minors present, Alcohol, Other/None | one per line; *Default list* restores them |
| Confidence threshold | 0.6 | a category counts from this confidence on (badge, filter, tag); every score is stored, so it can be changed afterwards |
| Tag images with their best category | off | see above |
| Categorizer | Vision LLM | *Vision LLM* or *ONNX model* |

Editing the category list makes a new **prompt version** (`cat-v1-<hash of the list>`): the next run categorizes again.

### Vision LLM

The vision role of *AI settings › Models for each job* (for example an Ollama vision model). One picture per request,
temperature 0, the answer held to a JSON schema whose category names are an `enum` of the list:

```json
{"categories":[{"name":"Drugs","confidence":0.82}],"reason":"one sentence on what is visible"}
```

Ollama gets the schema as `format` on `/api/chat`, with `think: false` (a reasoning model answers at once) and
`num_ctx` capped by *Max context (num_ctx)*; an OpenAI-compatible server (LM Studio…) gets it as `response_format`
(asked again without it if the server does not take it). The answer is still read defensively: JSON inside code fences
or after text, names matched to the list regardless of case (others dropped), confidences clamped to 0–1, a percentage
such as 85 read as 0.85. An empty list with a reason starting "refused", or a plain-text refusal, is recorded as refused.
Every send is in the case's send log, as for the other AI jobs.

For an 8 GB graphics card choose a compact vision model that fits entirely in GPU memory. The recommended model is
`qwen3-vl:4b-instruct` (about 3.3 GB; use the `-instruct` tag, not the plain `qwen3-vl:4b` reasoning variant).
Alternatives: `gemma3:4b` (faster, a little weaker on hard OCR) and `qwen3.5:9b` (best quality, but borderline on 8 GB —
keep it only if `ollama ps` shows 100% GPU, otherwise use `qwen3.5:4b`). Larger models spill onto the CPU and become very
slow.

### ONNX model (local classifier)

A picture classifier exported to ONNX (for example a CLIP or ViT fine-tuned on the categories and exported with
`torch.onnx.export` or Hugging Face Optimum), run on this computer with ONNX Runtime: **DirectML** (any DirectX 12 GPU)
when available, else the CPU. Nothing leaves the computer and no AI server is needed.

| Setting | |
|---|---|
| ONNX model | the `.onnx` file: one image input, `1×3×H×W` (or `1×H×W×3`) float; one output with one score per label |
| Labels | `labels.txt`: one label per line, in the order of the model's outputs (`0 Drugs`, `0: Drugs` and `#` comments are accepted) |
| Input size | 224 by default; a fixed size declared by the model wins |
| Scores | *Sigmoid* (multi-label, default), *Softmax* (one label per picture) or *None* (the model outputs probabilities) |
| Mean / std | per-channel R,G,B of 0–1 pixels; empty: ImageNet (0.485,0.456,0.406 / 0.229,0.224,0.225). CLIP: 0.4815,0.4578,0.4082 / 0.2686,0.2613,0.2758 |

The picture is decoded with the Windows codecs, its shorter side scaled to the input size and the centre cropped, then
normalised. The labels of the ONNX model are the categories (the list above is for the vision LLM). The model name stored
is its file name with the start of its SHA-256; the prompt version is a hash of the labels and preprocessing.
The classifier sits behind the `IImageCategorizer` interface (`Engine`, `Model`, `PromptVersion`, `Categorize`), so
another engine can be added the same way. CUDA is not bundled (DirectML covers NVIDIA, AMD and Intel GPUs).

## Where results are kept

In the case's AI base `_reader_ai.db` (beside the tags, or under `%LOCALAPPDATA%\AleappReader\ai` for a read-only case),
table `image_categories`, one row per picture × engine × model × prompt version:

| Column | |
|---|---|
| `ref_id`, `name`, `path` | the gallery's media id, the file name, its path in the device |
| `sha256` | SHA-256 of the picture's bytes as read |
| `engine`, `model`, `prompt_version` | e.g. `Vision LLM (Ollama)`, `qwen3-vl:4b-instruct`, `cat-v1-1a2b3c4d`; or `ONNX`, `clip.onnx#…`, `onnx-…` |
| `at` | when (UTC, milliseconds) |
| `categories` | every score, JSON `[{"name","confidence"}]` |
| `reason`, `status`, `error` | the model's reason; `ok`, `refused` or `error`; the error text |
| `send_id` | the row of the send log for a vision LLM request |

## CSV export

*Export categories (CSV)…* writes every stored result (UTF-8 with BOM, for Excel). The first line says the content is
AI-generated and unverified and gives the threshold; columns: file, device_path, sha256, engine, model, prompt_version,
categorized_utc, status, categories_above_threshold, all_scores, reason, error. Cells that would start a spreadsheet
formula are neutralised.

## Privacy

With a local engine (Ollama/LM Studio on this computer, or ONNX) no picture leaves the machine. A remote server is used
only when chosen in AI settings, and each run that would send pictures to it is confirmed first.
