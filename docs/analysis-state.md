# Analysis state (`.lexisstate`)

*Analysis state › Export analysis state… / Import analysis state…* in the toolbar (Italian Windows: *Stato analisi ›
Esporta stato analisi… / Importa stato analisi…*) saves the examiner's tags and notes of the selected device to a file and
loads them onto a **new parsing of the same extraction** — typically one made with a newer LEXIS / ALEAPP / iLEAPP that
finds more. Items are re-aligned by hash, never by position.

## What is in the file

Versioned JSON (`format: "lexis-analysis-state"`, `schemaVersion: 1`), UTF-8:

| Part | Content |
|---|---|
| header | app version, schema version, export time (UTC) |
| `case` | LEAPP name and version, "parsed by", platform, extraction **file name** (never a full path), portable-case root |
| `tagDefinitions` | the tags of the settings with their colour, plus any tag in use that the settings no longer list |
| `inventory` | every artifact (table, name, category, module, record count, source files as device paths with SHA-256) and the file count |
| `items` | every tagged record or file: tag, examiner note (automatic transcripts are left out), creation time, matching keys |

No evidence content is written: no message text, phone number, file bytes. A record carries hashes of its values and the
first date of the row (so an unmatched item can be found by hand); a file carries its path, size and SHA-256. The file is
**case data all the same** (tags, notes, paths, hashes of short values): it is written only where the examiner saves it,
and LEXIS has no code that sends it anywhere.

## Matching keys

**Records** (chat messages, timeline and location entries, any ALEAPP/iLEAPP artifact row; the timeline and the map tag the
artifact row they come from):

- `contentHash` — SHA-256 over every field of the row except media references, as `name␟value␞` sorted by the normalised
  column header (lower case, spaces collapsed). Values are canonical: null = empty; `5`, `"5"`, `5.0` alike; text NFC with
  `\n` line ends, trimmed; blobs by their SHA-256; columns named like *source / path / file* are cut to the device path
  (`C:\Temp\run1\Dump\data\data\x\db` and `/tmp/run2/Dump/data/data/x/db` are both `/data/data/x/db`).
  On import the hash is recomputed **on the old field list**, so a column the newer parser added does not change it.
- `coreHash` — the same over the identity fields only (text, sender, recipient, phone, name, title, id, account, URL,
  coordinates…; no dates, no paths, whose format a newer parser may change).
- `coreFieldHashes` — each identity field's value hash (16 hex digits), `rowId`, and `sourceHash` (SHA-256 of the
  artifact's source files read from the extraction).

**Files** (tagged in Extraction Files or in the gallery): SHA-256 (computed on export), size, path.

## Import tiers

Every tier is an equality; nothing is matched "nearly".

1. **Hash** — records: `contentHash` on the old fields; files: SHA-256 (only files of the same size are hashed).
2. **Secondary key** — records only: `coreHash`; else the same `rowId` with the source database unchanged (same SHA-256)
   and every identity field both parsings still have equal.
3. **Path + size** — files whose SHA-256 could not be read on export. When the extraction is not reachable on import,
   a file tag is kept on its path and reported as *not verified*.

The record's artifact is found by table name, else by artifact name + category.

**Ambiguous**: when several targets match, the one at the same path (files) or the same row id (records) is taken if
there is exactly one; otherwise every candidate is tagged and the item is flagged ambiguous.

**Additive**: an untagged target takes the imported tag and note; a target that already has a tag keeps it, and the
imported note (with the imported tag, when different) is appended to its note. Nothing is deleted; importing twice
changes nothing more. Tags the settings do not list are added to them.

**Summary**: matched by hash / secondary key / path, ambiguous, not found, and the new items of this parsing
(the rise of each artifact's record count, every record of an artifact the old parsing did not have, and the rise of
the file count). It can be saved as CSV (one line per item) or text for the case file; export and import are written to
the session's Exports log.

## Limits

- **Identical rows** (same values in every field, e.g. two identical messages) cannot be told apart: they are tagged
  together and reported as ambiguous, unless one keeps the old row id.
- A newer parser that **reformats an identity value** (not just a date or path), or renames every identity column while
  the source database also changed, leaves the record unmatched (reported as not found).
- Artifacts with **no identity column** have only the content hash.
- **Media columns** are not part of the hashes (their reference ids are made up by each run). A tagged media file is
  matched as a file.
- **New items** are counted from record and file counts, not listed: an artifact whose new parsing drops some rows and
  adds others shows only the net rise.
- A file whose SHA-256 could not be read on either side falls back to path + size.
- Hashes of short values (a phone number) can be confirmed by someone who guesses the value: treat the file as case data.
