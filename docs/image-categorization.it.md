# Categorizza immagini

*Media Gallery › Categorize images ▾* assegna a ogni immagine le categorie di un elenco modificabile dall'esaminatore,
ciascuna con una confidenza da 0 a 1. **Il risultato è generato dall'IA e non verificato: un aiuto alla revisione che
segnala le immagini da guardare, non un accertamento.** Ogni immagine categorizzata va controllata dall'esaminatore.

L'interfaccia di LEXIS è in inglese: qui sotto i nomi dei comandi sono riportati come appaiono nel programma.

## Avvio

| Voce di menu | Immagini inviate |
|---|---|
| Selected images | le miniature selezionate nella galleria (clic, Ctrl+clic, Maiusc+clic) |
| Images the filters keep (all pages) | tutte le immagini che i filtri correnti mantengono (tipo, app, origine, data, categoria IA, dimensione, testo) |
| All images | tutte le immagini della sorgente corrente della galleria (*Linked to records* o *Whole extraction*) |
| Export categories (CSV)… | tutti i risultati salvati del caso, vedi sotto |

Vengono inviate solo immagini (video e audio esclusi). L'avanzamento compare sotto l'intestazione della galleria;
**Stop** mette in pausa. Una nuova esecuzione **riprende**: un'immagine già categorizzata con lo stesso motore, modello e
versione del prompt viene saltata. Un errore su un'immagine (file illeggibile, timeout, risposta malformata) viene
registrato per quell'immagine e il lotto prosegue; gli errori vengono ritentati all'esecuzione successiva. Quando il
modello rifiuta di etichettare un'immagine, viene registrato come **model refused** (e non ritentato con lo stesso modello
e la stessa versione del prompt).

Le immagini presenti solo nell'estrazione vengono lette in sola lettura, in memoria; nulla viene scritto sulla prova.
Un'immagine che il modello di visione non legge (HEIC, TIFF, BMP, oppure oltre 15 MB) viene inviata come copia JPEG creata
in memoria con i codec di Windows. L'invio a un server **remoto** viene confermato prima; Ollama e LM Studio su questo
computer sono locali.

## Risultati nella galleria

* Un **badge** viola sulla miniatura elenca le categorie pari o superiori alla soglia con la loro confidenza
  ("AI: Drugs 0.82 · Weapons 0.64"); il tooltip mostra tutti i punteggi, motore, modello, versione del prompt, ora e la
  motivazione di una riga del modello. "AI: model refused" indica un rifiuto.
* Il filtro **AI category** mantiene le immagini di una categoria (pari o sopra la soglia), *Any AI category*,
  *Not categorized* o *Model refused*. Si combina con gli altri filtri.
* **Tag** (disattivato di default, *AI settings › Categorize images*): ogni immagine **ancora senza tag** riceve la sua
  categoria migliore sopra la soglia come `AI: <categoria>` ("Other/None" non diventa mai un tag). Il prefisso `AI:` indica
  che il tag è generato dall'IA; un tag messo dall'esaminatore, sul file o sul suo record, non viene mai sostituito.

## Impostazioni (*AI settings › Categorize images (Media Gallery)*)

| Impostazione | Predefinito | |
|---|---|---|
| Categories | Drugs, Money/Cash, Weapons, Documents/IDs, Vehicles, Screenshots/Chats, Nudity/Sexual, Minors present, Alcohol, Other/None | una per riga; *Default list* le ripristina |
| Confidence threshold | 0.6 | una categoria conta da questa confidenza in su (badge, filtro, tag); tutti i punteggi sono salvati, quindi si può cambiare dopo |
| Tag images with their best category | disattivato | vedi sopra |
| Categorizer | Vision LLM | *Vision LLM* oppure *ONNX model* |

Modificare l'elenco delle categorie crea una nuova **versione del prompt** (`cat-v1-<hash dell'elenco>`): l'esecuzione
successiva categorizza di nuovo.

### Vision LLM

Il ruolo Vision di *AI settings › Models for each job* (per esempio un modello di visione di Ollama). Un'immagine per
richiesta, temperatura 0, risposta vincolata a uno schema JSON in cui i nomi delle categorie sono un `enum` dell'elenco:

```json
{"categories":[{"name":"Drugs","confidence":0.82}],"reason":"una frase su ciò che si vede"}
```

Ollama riceve lo schema come `format` su `/api/chat`, con `think: false` (un modello "ragionante" risponde subito) e
`num_ctx` limitato da *Max context (num_ctx)*; un server compatibile OpenAI (LM Studio…) lo riceve come `response_format`
(con un secondo tentativo senza, se il server non lo accetta). La risposta viene comunque letta in modo difensivo: JSON
dentro blocchi di codice o dopo del testo, nomi confrontati con l'elenco senza distinguere maiuscole (gli altri scartati),
confidenze limitate a 0–1, una percentuale come 85 letta come 0.85. Un elenco vuoto con motivazione che inizia per
"refused", oppure un rifiuto in testo libero, viene registrato come rifiuto. Ogni invio è nel registro invii del caso,
come per le altre funzioni IA.

Con una scheda video da 8 GB scegli un modello di visione compatto che stia tutto nella memoria della GPU. Il modello
consigliato è `qwen3-vl:4b-instruct` (circa 3,3 GB; usa il tag `-instruct`, non la variante `qwen3-vl:4b` che ragiona).
Alternative: `gemma3:4b` (più veloce, un po' più debole sull'OCR difficile) e `qwen3.5:9b` (la migliore qualità, ma al
limite sugli 8 GB — tienilo solo se `ollama ps` mostra 100% GPU, altrimenti usa `qwen3.5:4b`). I modelli più grandi
finiscono sulla CPU e diventano molto lenti.

### Modello ONNX (classificatore locale)

Un classificatore di immagini esportato in ONNX (per esempio un CLIP o un ViT addestrato sulle categorie ed esportato con
`torch.onnx.export` o Hugging Face Optimum), eseguito su questo computer con ONNX Runtime: **DirectML** (qualsiasi GPU
DirectX 12) se disponibile, altrimenti la CPU. Nulla esce dal computer e non serve un server IA.

| Impostazione | |
|---|---|
| ONNX model | il file `.onnx`: un ingresso immagine float `1×3×H×W` (o `1×H×W×3`); un'uscita con un punteggio per etichetta |
| Labels | `labels.txt`: un'etichetta per riga, nell'ordine delle uscite del modello (accettati `0 Drugs`, `0: Drugs` e commenti `#`) |
| Input size | 224 di default; prevale una dimensione fissa dichiarata dal modello |
| Scores | *Sigmoid* (multi-etichetta, default), *Softmax* (un'etichetta per immagine) o *None* (il modello restituisce già probabilità) |
| Mean / std | R,G,B per canale su pixel 0–1; vuoto: ImageNet (0.485,0.456,0.406 / 0.229,0.224,0.225). CLIP: 0.4815,0.4578,0.4082 / 0.2686,0.2613,0.2758 |

L'immagine viene decodificata con i codec di Windows, il lato corto scalato alla dimensione d'ingresso e ritagliato il
centro, poi normalizzata. Le categorie sono le etichette del modello ONNX (l'elenco sopra vale per il Vision LLM). Il
nome del modello salvato è il nome del file con l'inizio del suo SHA-256; la versione del prompt è un hash delle etichette
e della pre-elaborazione. Il classificatore sta dietro l'interfaccia `IImageCategorizer` (`Engine`, `Model`,
`PromptVersion`, `Categorize`), così un altro motore si aggiunge allo stesso modo. CUDA non è incluso (DirectML copre GPU
NVIDIA, AMD e Intel).

## Dove sono salvati i risultati

Nella base IA del caso `_reader_ai.db` (accanto ai tag, oppure in `%LOCALAPPDATA%\AleappReader\ai` per un caso in sola
lettura), tabella `image_categories`, una riga per immagine × motore × modello × versione del prompt:

| Colonna | |
|---|---|
| `ref_id`, `name`, `path` | l'id del file nella galleria, il nome del file, il percorso nel dispositivo |
| `sha256` | SHA-256 dei byte dell'immagine così come letti |
| `engine`, `model`, `prompt_version` | es. `Vision LLM (Ollama)`, `qwen3-vl:4b-instruct`, `cat-v1-1a2b3c4d`; oppure `ONNX`, `clip.onnx#…`, `onnx-…` |
| `at` | quando (UTC, millisecondi) |
| `categories` | tutti i punteggi, JSON `[{"name","confidence"}]` |
| `reason`, `status`, `error` | la motivazione del modello; `ok`, `refused` o `error`; il testo dell'errore |
| `send_id` | la riga del registro invii per una richiesta al Vision LLM |

## Esportazione CSV

*Export categories (CSV)…* scrive tutti i risultati salvati (UTF-8 con BOM, per Excel). La prima riga dichiara che il
contenuto è generato dall'IA e non verificato e riporta la soglia; colonne: file, device_path, sha256, engine, model,
prompt_version, categorized_utc, status, categories_above_threshold, all_scores, reason, error. Le celle che
avvierebbero una formula del foglio di calcolo vengono neutralizzate.

## Privacy

Con un motore locale (Ollama/LM Studio su questo computer, oppure ONNX) nessuna immagine esce dalla macchina. Un server
remoto viene usato solo se scelto nelle impostazioni IA, e ogni esecuzione che gli invierebbe immagini viene confermata prima.
