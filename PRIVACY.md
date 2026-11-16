# Privacy and data protection (GDPR) — LEXIS

> Italian version: [PRIVACY.it.md](PRIVACY.it.md). This document describes how the software behaves and how to use it
> following the privacy-by-design principles of the GDPR (Regulation (EU) 2016/679). It is technical guidance, **not legal advice**
> and not a certification of compliance:
> the lawful basis, the retention periods and the DPIA belong to the organisation that uses LEXIS.

## 1. Roles

LEXIS is a **local desktop tool**. The authors/maintainers of this repository do **not** receive, host or process any
case data and are neither controller nor processor of it. The **data controller is the examiner or the organisation
that uses LEXIS** on an extraction (public prosecutor's office / judicial police, a court-appointed expert, a law
firm, a company or a private investigator), who decides purposes and means. Where an examiner acts for another party
(a court-appointed expert, a consultant for a controller), a processor relationship (art. 28) or the national rules for
judicial activity apply (in Italy, d.lgs. 51/2018 for criminal proceedings and the rules on technical consultants).

## 2. What data LEXIS handles

It reads extractions of phones and computers (messages, calls, contacts, locations, media, accounts, documents) that
ALEAPP/iLEAPP report. This includes personal data of the device owner and of third parties, and often **special
categories** (art. 9) and **criminal-offence data** (art. 10). LEXIS handles it **only on the computer where it runs**.

## 3. Default behaviour: nothing leaves the device

| Area | Default | Notes |
| --- | --- | --- |
| Telemetry, analytics, crash reporting, update checks | **None** — no code does this | Verified by code review (no telemetry libraries, no update call). |
| Network access while analysing a case | **None** | ALEAPP/iLEAPP and the reader run offline. |
| AI features (summaries, translation, OCR by model, image/video description, audio transcription by model, MCP) | **Off.** `Use AI` is unchecked; MCP and the Claude CLI are not allowed until the examiner allows them | Details in section 4. |
| Map tiles | **Off** (blank background; points still plotted). Leaflet is embedded, no CDN | Tick *Online map tiles* only if you accept that tile.openstreetmap.org (and your network) learn the area viewed and your IP address. |
| Whisper.cpp / model download | Only when the examiner clicks it in AI settings; files are accepted only if their SHA-256 is the published one | Downloads from github.com / huggingface.co; no case data is sent. |
| Logs | `%LOCALAPPDATA%\AleappReader\errors.log`: exception **types and stack frames only**, no messages, no paths, no case content; capped at 256 KB | Messages are withheld because they can quote file names or record text. |
| Temporary copies of evidence | Created in `%TEMP%\AleappReader`; **deleted when LEXIS closes**, and swept at the next start after a crash | Evidence itself is never modified; databases are opened from copies. |
| Settings | `%APPDATA%\AleappReader\settings.json`: UI choices, time zone, **recent case paths**, AI endpoints. API keys are stored **encrypted with Windows DPAPI** for the current Windows user, never in clear and never in the repository | Delete the file to forget the recent cases. |

## 4. Optional features that can send data out (opt-in only)

1. **AI on a local engine** (Ollama, LM Studio, whisper.cpp on this PC): nothing leaves the machine.
2. **AI on a server on your network**: data goes to that server; the settings window labels it "on your network".
3. **AI on a remote server** (any address outside the PC and the LAN): case excerpts **and the API key** are sent.
   LEXIS requires **https://** for such servers, labels them "leaves your network", and asks for confirmation
   each time. **Using a remote AI service is a transfer of personal data to a processor**: you need a lawful basis,
   an art. 28 contract, and — if the provider is outside the EEA — a transfer tool (adequacy decision or SCCs, art. 44-49).
   For evidence, the recommended configuration is **local models only**.
4. **Claude CLI** (if you allow it): what it reads is sent to its provider (outside the EU by default). Same duties as 3.
5. **Web search** inside AI questions: only search words, with words containing digits or `@` dropped, shown to the
   examiner and confirmed each time; every search is written to the case's send log.
6. **MCP server** (if you allow it): listens on `127.0.0.1` only, behind a secret path token, and refuses foreign origins.
7. **Online map tiles**: see section 3.

Every AI send is recorded in the case's `_reader_ai.db` (what, where, when) so the examiner can account for it
(art. 5(2) accountability). AI output is a review aid, not a finding.

## 5. Principles and how the software supports them

- **Lawfulness, purpose limitation (art. 5(1)(a),(b))** — decided by the controller; LEXIS adds no secondary use.
- **Data minimisation (5(1)(c), 25)** — *Portable case* and *HTML export* let you choose artifacts, **tagged records only**,
  media or not, file system or not, and **audio transcripts only when you tick the box (off by default)**.
  Automatic transcripts never count as a tag and are stripped from exports unless approved.
- **Accuracy (5(1)(d))** — originals are never altered; SHA-256 manifests; the examiner can correct or exclude AI text
  (the model text is kept for the audit trail).
- **Storage limitation (5(1)(e))** — LEXIS does not delete a case for you. Define a retention period, then delete the case
  folder, `_reader_*.db` sidecars, exports and portable cases when it ends. Temporary copies are deleted automatically.
- **Integrity and confidentiality (5(1)(f), 32)** — see section 6.
- **Data-subject rights (arts. 12-23)** — handled by the controller. Search and export features help locate and extract
  a subject's data; judicial and security exemptions may limit rights (art. 23, national law).
- **Records, DPIA (arts. 30, 35)** — forensic analysis of personal devices, often at scale and with special-category
  data, will normally require a **DPIA** (or the equivalent under d.lgs. 51/2018) by the controller. Use sections 3-4 as input.
- **Transfers (arts. 44-49)** — **no transfer outside the EU/EEA happens by default** because nothing is sent anywhere.
  Transfers can only arise from the opt-in features in section 4 or from how you store/share exports.

## 6. Security of processing (art. 32) — measures and recommendations

Built in: evidence opened read-only from copies; SHA-256 verification; signed portable cases (examiner key held in the
Windows key store, not exportable); HTML escaping of case data in the map and reports; loopback-only MCP with token and
Origin check; backup passwords passed through the environment, never on a command line; path checks (`..`, rooted paths,
zip-slip) when unpacking archives and writing exports; parameterised SQL; downloads verified by SHA-256; temp cleanup.

Recommended for the controller: full-disk encryption (BitLocker) on the analysis PC; a dedicated, patched Windows user
with a strong password and screen lock; no case on shared or cloud-synced folders; encrypted media for portable cases
and exports (the portable case zip is **not encrypted** — store it on encrypted media or inside an encrypted container);
access control and logging of who opens cases; verified secure deletion at the end of retention; a trained examiner bound by confidentiality.

## 7. Using this repository and issues

**Never put real case data in issues, pull requests, screenshots or test files.** Use synthetic data only. The test suite
uses synthetic data exclusively. Report vulnerabilities as described in [SECURITY.md](SECURITY.md); other issues at https://github.com/Medvjed80/LEXIS/issues.

## 8. Third-party components

ALEAPP and iLEAPP (MIT, Alexis Brignoni and contributors), Leaflet (BSD-2-Clause) and Leaflet.markercluster (MIT),
ffmpeg (LGPL), the SILK decoder, Tesseract/Leptonica, PdfPig and others are listed in [NOTICE](NOTICE). None of them
is configured to send data out by default. `build.ps1` needs internet **at build time only** (pip, and `git clone` of
`mister-skinnylegs` for ALEAPP).
