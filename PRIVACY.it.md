# Privacy e protezione dei dati (GDPR) — LEXIS

> English version: [PRIVACY.md](PRIVACY.md). Questo documento descrive il comportamento del software e come usarlo in modo
> secondo i principi di privacy by design del GDPR (Regolamento (UE) 2016/679). È una guida tecnica, **non una consulenza legale**
> né una certificazione di conformità: base giuridica,
> tempi di conservazione e DPIA spettano all'organizzazione che usa LEXIS.

## 1. Ruoli

LEXIS è uno **strumento desktop locale**. Gli autori/manutentori di questo repository **non ricevono, non ospitano e non
trattano** dati dei casi e non sono né titolari né responsabili del trattamento. Il **titolare del trattamento è
l'esaminatore o l'organizzazione che usa LEXIS** su un'estrazione (Procura/polizia giudiziaria, CTU o CTP, studio legale,
azienda, investigatore privato), che decide finalità e mezzi. Se l'esaminatore agisce per conto di altri (consulente del
titolare) si applicano il rapporto con il responsabile (art. 28) oppure le norme sull'attività giudiziaria (in Italia,
d.lgs. 51/2018 per il procedimento penale e le regole sui consulenti tecnici).

## 2. Dati trattati

Legge estrazioni di telefoni e computer (messaggi, chiamate, contatti, posizioni, media, account, documenti) già
elaborate da ALEAPP/iLEAPP. Include dati personali del proprietario e di terzi, spesso **categorie particolari**
(art. 9) e **dati relativi a reati** (art. 10). LEXIS li tratta **solo sul computer su cui è in esecuzione**.

## 3. Comportamento predefinito: nulla esce dal dispositivo

| Ambito | Predefinito | Note |
| --- | --- | --- |
| Telemetria, statistiche, crash report, controllo aggiornamenti | **Nessuno** — il codice non lo fa | Verificato con revisione del codice. |
| Accesso di rete durante l'analisi | **Nessuno** | ALEAPP/iLEAPP e il lettore funzionano offline. |
| Funzioni AI (riassunti, traduzione, OCR con modello, descrizione immagini/video, trascrizione con modello, MCP) | **Disattivate.** «Use AI» è deselezionato; MCP e Claude CLI non sono consentiti finché l'esaminatore non li consente | Dettagli nella sezione 4. |
| Mappe online | **Disattivate** (sfondo bianco, i punti sono comunque tracciati). Leaflet è incluso nell'exe, nessuna CDN | Attivare *Online map tiles* solo se si accetta che tile.openstreetmap.org (e la rete) conoscano l'area visualizzata e l'indirizzo IP. |
| Download whisper.cpp / modelli | Solo su clic dell'esaminatore nelle impostazioni AI; file accettati solo se lo SHA-256 è quello pubblicato | Da github.com / huggingface.co; nessun dato del caso viene inviato. |
| Log | `%LOCALAPPDATA%\AleappReader\errors.log`: **solo tipo e stack** dell'eccezione, nessun messaggio, percorso o contenuto del caso; massimo 256 KB | I messaggi sono omessi perché possono citare nomi di file o testo dei record. |
| Copie temporanee delle prove | Create in `%TEMP%\AleappReader`; **cancellate alla chiusura di LEXIS** e ripulite al primo avvio dopo un crash | Le prove originali non sono mai modificate. |
| Impostazioni | `%APPDATA%\AleappReader\settings.json`: preferenze, fuso orario, **percorsi dei casi recenti**, endpoint AI. Le chiavi API sono **cifrate con DPAPI di Windows** per l'utente corrente, mai in chiaro e mai nel repository | Eliminare il file per dimenticare i casi recenti. |
| Stato analisi (`.lexisstate`) | Scritto **solo quando l'esaminatore lo esporta**, dove lo salva | Tag, note dell'esaminatore, metadati (artefatto, riga, percorso, dimensione, una data) e hash SHA-256; nessun contenuto delle prove, ma resta un dato del caso: conservarlo e cancellarlo con il caso. Nessuna funzione lo invia altrove. |

## 4. Funzioni facoltative che possono inviare dati all'esterno (solo se attivate)

1. **AI su motore locale** (Ollama, LM Studio, whisper.cpp su questo PC): nulla esce dalla macchina.
2. **AI su server della rete locale**: i dati vanno a quel server; le impostazioni lo indicano come «on your network».
3. **AI su server remoto** (indirizzo fuori dal PC e dalla LAN): vengono inviati estratti del caso **e la chiave API**.
   LEXIS impone **https://**, lo segnala come «leaves your network» e chiede conferma ogni volta. **L'uso di un servizio AI
   remoto è un trasferimento di dati personali a un responsabile**: servono base giuridica, contratto ex art. 28 e, se il
   fornitore è fuori dallo SEE, uno strumento di trasferimento (decisione di adeguatezza o SCC, artt. 44-49).
   Per le prove si raccomandano **solo modelli locali**.
4. **Claude CLI** (se consentito): ciò che legge è inviato al fornitore (fuori UE per impostazione predefinita). Stessi obblighi del punto 3.
5. **Ricerca web** nelle domande AI: solo parole di ricerca (scartate quelle con cifre o `@`), mostrate e confermate ogni volta e registrate nel log degli invii del caso.
6. **Server MCP** (se consentito): ascolta solo su `127.0.0.1`, con token segreto nel percorso, rifiuta origini esterne.
7. **Mappe online**: vedi sezione 3.

Ogni invio AI è registrato in `_reader_ai.db` del caso (cosa, dove, quando) a fini di responsabilizzazione (art. 5(2)).

## 5. Principi e supporto del software

- **Liceità, limitazione delle finalità (art. 5(1)(a),(b))** — decise dal titolare; LEXIS non aggiunge usi secondari.
- **Minimizzazione (5(1)(c), 25)** — *Portable case* ed export HTML permettono di scegliere artefatti, **solo record taggati**,
  media, file system, e **trascrizioni audio solo se selezionate (predefinito: no)**. Le trascrizioni automatiche non contano come tag
  e sono rimosse dagli export se non approvate.
- **Esattezza (5(1)(d))** — originali mai alterati; manifest SHA-256; l'esaminatore può correggere o escludere il testo AI.
- **Limitazione della conservazione (5(1)(e))** — LEXIS non cancella i casi: stabilire un periodo di conservazione e poi
  eliminare cartella del caso, file `_reader_*.db`, export e portable case. Le copie temporanee sono cancellate in automatico.
- **Integrità e riservatezza (5(1)(f), 32)** — vedi sezione 6.
- **Diritti degli interessati (artt. 12-23)** — gestiti dal titolare; ricerca ed export aiutano a individuare ed estrarre i dati;
  possono operare limitazioni per attività giudiziarie e di sicurezza (art. 23, norme nazionali).
- **Registro, DPIA (artt. 30, 35)** — l'analisi forense di dispositivi personali, spesso su larga scala e con categorie particolari,
  richiede normalmente una **DPIA** (o l'equivalente ai sensi del d.lgs. 51/2018) a cura del titolare; usare le sezioni 3-4 come base.
- **Trasferimenti (artt. 44-49)** — **per impostazione predefinita nessun dato è trasferito fuori da UE/SEE** perché nulla viene inviato.
  Trasferimenti possono nascere solo dalle funzioni della sezione 4 o da come si conservano/condividono gli export.

## 6. Sicurezza del trattamento (art. 32)

Incluse: prove aperte in sola lettura da copie; verifica SHA-256; portable case firmati (chiave dell'esaminatore nel
key store di Windows, non esportabile); escape HTML dei dati nelle mappe e nei report; MCP solo loopback con token e controllo
Origin; password dei backup passate via ambiente e mai da riga di comando; controlli sui percorsi (`..`, percorsi assoluti,
zip-slip) in estrazione ed export; SQL parametrizzato; download verificati con SHA-256; pulizia dei temporanei.

Raccomandate al titolare: cifratura del disco (BitLocker); utente Windows dedicato, aggiornato e con password robusta e blocco schermo;
nessun caso su cartelle condivise o sincronizzate in cloud; supporti cifrati per portable case ed export (lo zip del portable case
**non è cifrato**); controllo accessi e tracciamento di chi apre i casi; cancellazione sicura a fine conservazione; personale vincolato alla riservatezza.

## 7. Uso del repository

**Non inserire mai dati reali di casi in issue, pull request, screenshot o file di test.** Usare solo dati sintetici.
Segnalazione di vulnerabilità: [SECURITY.md](SECURITY.md); altre segnalazioni su https://github.com/Medvjed80/LEXIS/issues.
