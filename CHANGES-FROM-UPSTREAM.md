# Changes from upstream / Modifiche rispetto all'originale

This fork is based on **[G33KF/LEXIS](https://github.com/G33KF/LEXIS) at upstream version 1.2.5** (commit `a0bc77e`, tag `Lexis_V.1.2.5`, MIT).
The git history keeps the upstream commits; this fork's work is the commits on top of it (`git log a0bc77e..HEAD`,
`git diff a0bc77e..HEAD`). The version of this fork is **1.2.7**.

Questo fork si basa su **[G33KF/LEXIS](https://github.com/G33KF/LEXIS) alla versione upstream 1.2.5** (commit `a0bc77e`, tag `Lexis_V.1.2.5`, MIT).
La cronologia git conserva i commit originali; il lavoro del fork è nei commit successivi.

- [English](#english)
- [Italiano](#italiano)

---

## English


### Highlights (features of 1.2.6 and 1.2.7)
- **One self-contained exe with ALEAPP and iLEAPP built in.** No Python, no LEAPP folder and no .NET install are needed on the target PC. ALEAPP ships with 1,344 modules (including the 8 LEXIS modules: Carved, RecoveryApps, Session, Signal, SimInfo, SmsViews, WeChatMultiAccount, WhatsApp). iLEAPP ships with 1,233 modules (including the 2 LEXIS modules: Carved, WhatsApp). On the first extraction the engines unpack to `%LOCALAPPDATA%\AleappReader\engine`.
- **Tagged files you can work with.** You can open a tagged file in its original folder, then edit or remove its tags.
- **Portable Case** includes the tagged files, laid out by the source file system, and can optionally include audio transcripts.
- **Automatic transcription** with whisper.cpp saves each transcript into the file's note.

### Added
- **Tagged Items: file actions.**
  - *Open in original folder* opens Extraction Files on the file's folder on the device, with the file selected. Double-clicking a file does the same.
  - *Open file* opens the file in the built-in viewer. *Show in Explorer* is available when a local copy exists.
  - *Remove tag* works on several selected rows at once. *Change tag* is in the right-click menu.
  - You can also edit the tag and note in the details pane, both from the list and once the file is open in its folder. The list updates immediately, and a file with no tags left drops off it.
- **Portable Case: *Include tagged files*** (on by default).
  - Tagged files go into the zip under `Case/extraction/`, at their original device paths.
  - They are hashed in `SHA256SUMS.txt` and listed in the manifest with their `device_path`.
  - Their tags and notes come along, so *Open in original folder* also works in the portable reader.
  - If a tagged file can't be read, the export stops and names the file.
- ***Include audio transcripts*** (off by default) in the Portable Case, chat HTML export and HTML report dialogs.
  - When it's on, only the transcripts of the exported audio files are included. In HTML each transcript appears next to its audio.
  - When it's off, no transcript text leaves the case, not even inside notes.
  - The export log and the manifest record which choice was made.
- **Transcripts in file notes.** A transcript from whisper.cpp is written into the audio file's note.
  - Your own note is kept, and the transcript is added below it under "Transcript (automatic):".
  - The note shows in the gallery, in Extraction Files, in the details pane and in chats.
  - Transcripts saved by earlier versions are copied into the notes when the case opens.
- **Automatic transcription** (AI settings, on by default). It only runs when whisper.cpp, running locally, is the transcription engine; a cloud engine is never used for this. It runs in the background, with progress and *Cancel transcription* in the status bar.
- **Manual transcription.** *Transcribe selected audio* and *Transcribe all audio* are in the Media Gallery and the chat list. The chat list also has *Transcribe audio* for the selected chats only.
- **AI on the current selection.** In the chat list, the Media Gallery and Extraction Files, the AI works on the selected chats or files (Ctrl+click or Shift+click), not on every checked row.
- **UFADE and UFED advanced logical iOS extractions.** An `Apple_….zip`, with or without its `.ufd`, is recognised.
  - iLEAPP processes it as an iTunes backup. The backup password from the UFD is never written on the command line or in the log.
  - Extraction Files opens the zip in place.
- **Clearer errors in the extraction window.** When the modules can't be listed, the window shows the real error.

### Fixed
- **Portable Case export:** `SQLite Error 19: FOREIGN KEY constraint failed` on *WhatsApp – Messages* is fixed. The original schema is kept.
- **Automatic transcripts no longer appear among Tagged Items.** A file now counts as tagged only when you tag it or write a note yourself. Existing cases are fixed when they are read, and no text is deleted.
- **Large cases no longer freeze on opening.** Transcripts are copied once, in the background, after the case has loaded.
- **Audio preview:** ffmpeg error -22 is fixed. Opus, AMR, Ogg and the other formats Windows can't play are converted to WAV for listening, and the original is only read.
- **iTunes sources recorded as `private/var/...`** are no longer reported as missing.
- **Extraction modules:** a single faulty module no longer empties the whole list. LEXIS modules are always bundled, and the build fails if they are missing.

### Privacy and security hardening (fork only)
- **Map**: Leaflet / Leaflet.markercluster are embedded (no CDN); a Content-Security-Policy limits the page; **online map tiles are off by default** (they reveal the viewed area to the tile server).
- **Logs**: `errors.log` holds exception types and stack frames only, never messages (which can quote case data); capped at 256 KB.
- **Temp files**: copies of evidence in `%TEMP%\AleappReader` (extraction cache, decrypted backup databases, source copies) are deleted when LEXIS exits and swept at start-up after a crash.
- **TLS**: AI servers outside the PC and the LAN must use `https://`.
- **Explorer** is started with an argument list instead of a quoted command string.
- **Secrets**: none were present in the source; API keys are entered in the UI and stored encrypted with Windows DPAPI, never in the repository. A personal build path in the README example was replaced by a placeholder. `.gitleaks.toml`, `.pre-commit-config.yaml` and `settings.example.json` added; `.gitignore` extended.
- **Documentation**: `PRIVACY.md` / `PRIVACY.it.md` (GDPR), `SECURITY.md`, `NOTICE` (fork attribution, Leaflet), this file.
- **Tests**: `PrivacyTests` (no messages in logs, temp sweep).

### Files changed
About 40 source files and 10 new files (`TaggedFileNav.cs`, `TranscriptNotes.cs`, `UfedLogical.cs`, `AudioPlayback.cs`, `ErrorText.cs`,
`HtmlExportWindow`, new tests, embedded map assets). `ALEAPP/` and `ILEAPP/` change only in `scripts/plugin_loader.py` and
`packaging/build.py`; the LEXIS modules in `scripts/lexis_artifacts` are unchanged from upstream. Not included: any exe, build output, case data.

---

## Italiano


### In evidenza (funzioni di 1.2.6 e 1.2.7)
- **Un unico exe autonomo con ALEAPP e iLEAPP inclusi.** Sul PC di destinazione non servono Python, cartelle LEAPP o .NET. ALEAPP ha 1.344 moduli, tra cui gli 8 di LEXIS (Carved, RecoveryApps, Session, Signal, SimInfo, SmsViews, WeChatMultiAccount, WhatsApp). iLEAPP ne ha 1.233, tra cui i 2 di LEXIS (Carved, WhatsApp). Alla prima estrazione i motori vengono scompattati in `%LOCALAPPDATA%\AleappReader\engine`.
- **File taggati gestibili.** Puoi aprire un file taggato nella sua cartella originale e poi modificarne o rimuoverne i tag.
- **Il Portable Case** include i file taggati, ordinati secondo il file system di origine, e se vuoi anche le trascrizioni audio.
- **La trascrizione automatica** con whisper.cpp salva ogni trascrizione nella nota del file.

### Novità
- **Tagged Items: azioni sui file.**
  - *Open in original folder* apre Extraction Files nella cartella del file sul dispositivo, con il file selezionato. Il doppio clic su un file fa la stessa cosa.
  - *Open file* apre il file nel visualizzatore di LEXIS. *Show in Explorer* è disponibile se esiste una copia locale.
  - *Remove tag* funziona anche su più righe selezionate insieme. *Change tag* si trova nel menu del tasto destro.
  - Tag e nota si possono modificare anche nel pannello dei dettagli, sia dalla lista sia dopo aver aperto il file nella sua cartella. La lista si aggiorna subito, e un file senza più tag sparisce dalla lista.
- **Portable Case: *Include tagged files*** (attiva di default).
  - I file taggati vengono messi nello zip sotto `Case/extraction/`, con i loro percorsi originali del dispositivo.
  - Sono inclusi negli hash di `SHA256SUMS.txt` ed elencati nel manifest con il loro `device_path`.
  - Tag e note li seguono, quindi *Open in original folder* funziona anche nel reader portatile.
  - Se un file taggato non è leggibile, l'esportazione si ferma e indica quale file è.
- ***Include audio transcripts*** (disattivata di default) nelle finestre di Portable Case, export HTML delle chat e report HTML.
  - Se è attiva, vengono incluse solo le trascrizioni degli audio esportati. Nell'HTML ogni trascrizione compare accanto al suo audio.
  - Se è disattivata, nessun testo di trascrizione esce dal caso, nemmeno dentro le note.
  - Il registro dell'esportazione e il manifest riportano la scelta fatta.
- **Trascrizioni nelle note dei file.** La trascrizione fatta da whisper.cpp viene scritta nella nota del file audio.
  - La tua nota resta, e la trascrizione viene aggiunta sotto, con l'intestazione "Transcript (automatic):".
  - La nota si vede nella galleria, in Extraction Files, nel pannello dei dettagli e nelle chat.
  - Le trascrizioni salvate con le versioni precedenti vengono copiate nelle note quando apri il caso.
- **Trascrizione automatica** (impostazioni AI, attiva di default). Parte solo se il motore di trascrizione è whisper.cpp in locale: per questo non viene mai usato un servizio cloud. Gira in background, con l'avanzamento e il pulsante *Cancel transcription* nella barra di stato.
- **Trascrizione manuale.** *Transcribe selected audio* e *Transcribe all audio* sono nella Media Gallery e nella lista chat. Nella lista chat c'è anche *Transcribe audio*, che lavora solo sulle chat selezionate.
- **AI sulla selezione corrente.** Nella lista chat, nella Media Gallery e in Extraction Files l'AI lavora sulle chat o sui file selezionati (Ctrl+clic o Maiusc+clic), non su tutte le righe spuntate.
- **Estrazioni iOS logiche avanzate UFADE e UFED.** Un `Apple_….zip`, con o senza il suo `.ufd`, viene riconosciuto.
  - iLEAPP lo elabora come backup iTunes. La password del backup presa dall'UFD non compare mai sulla riga di comando né nel log.
  - Extraction Files apre lo zip direttamente, senza estrarlo.
- **Errori più chiari nella finestra di estrazione.** Se i moduli non si possono elencare, la finestra mostra l'errore vero.

### Correzioni
- **Esportazione Portable Case:** risolto l'errore `SQLite Error 19: FOREIGN KEY constraint failed` su *WhatsApp – Messages*. Lo schema originale resta invariato.
- **Le trascrizioni automatiche non compaiono più tra i Tagged Items.** Un file conta come taggato solo se lo tagghi tu o se ci scrivi una nota. I casi già esistenti vengono corretti in lettura, senza cancellare nessun testo.
- **I casi grandi non si bloccano più all'apertura.** Le trascrizioni vengono copiate una volta sola, in background, dopo che il caso è stato caricato.
- **Anteprima audio:** risolto l'errore -22 di ffmpeg. Opus, AMR, Ogg e gli altri formati che Windows non riproduce vengono convertiti in WAV per l'ascolto, e l'originale viene solo letto.
- **Le sorgenti iTunes registrate come `private/var/...`** non risultano più mancanti.
- **Moduli di estrazione:** un modulo difettoso non svuota più tutta la lista. I moduli LEXIS sono sempre inclusi nella build, che si blocca se mancano.

### Rafforzamento di privacy e sicurezza (solo in questo fork)
- **Mappa**: Leaflet / Leaflet.markercluster sono inclusi nell'exe (nessuna CDN); una Content-Security-Policy limita la pagina; **le mappe online sono disattivate di default** (rivelerebbero al server delle tile l'area visualizzata).
- **Log**: `errors.log` contiene solo tipo ed elenco chiamate delle eccezioni, mai i messaggi (che possono citare dati del caso); massimo 256 KB.
- **File temporanei**: le copie di prove in `%TEMP%\AleappReader` (cache di estrazione, database di backup decifrati, copie dei sorgenti) vengono cancellate alla chiusura di LEXIS e ripulite all'avvio dopo un crash.
- **TLS**: i server AI fuori dal PC e dalla LAN devono usare `https://`.
- **Explorer** viene avviato con un elenco di argomenti invece di una stringa tra virgolette.
- **Segreti**: nel sorgente non ce n'erano; le chiavi API si inseriscono nell'interfaccia e sono cifrate con DPAPI di Windows, mai nel repository. Un percorso personale di build nell'esempio del README è stato sostituito da un segnaposto. Aggiunti `.gitleaks.toml`, `.pre-commit-config.yaml`, `settings.example.json`; esteso `.gitignore`.
- **Documentazione**: `PRIVACY.md` / `PRIVACY.it.md` (GDPR), `SECURITY.md`, `NOTICE` (attribuzione del fork, Leaflet), questo file.
- **Test**: `PrivacyTests` (nessun messaggio nei log, pulizia dei temporanei).

### File modificati
Circa 40 file sorgente modificati e 10 nuovi (`TaggedFileNav.cs`, `TranscriptNotes.cs`, `UfedLogical.cs`, `AudioPlayback.cs`, `ErrorText.cs`,
`HtmlExportWindow`, nuovi test, risorse della mappa). `ALEAPP/` e `ILEAPP/` cambiano solo in `scripts/plugin_loader.py` e
`packaging/build.py`; i moduli LEXIS in `scripts/lexis_artifacts` sono identici all'originale. Non inclusi: exe, output di build, dati di casi.
