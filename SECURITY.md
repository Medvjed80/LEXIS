# Security policy

## Supported versions

Only the latest release of this fork receives fixes.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem, and **never attach real case data** (chats, images, names,
phone numbers, OCR text, extractions): use synthetic data that reproduces the problem.

Use GitHub's **private vulnerability reporting** ("Security" tab → "Report a vulnerability") on this repository.
*(Maintainer: enable it under Settings → Code security, and add a contact address here if you prefer e-mail.)*
For problems in the upstream project, report to https://github.com/G33KF/LEXIS; for ALEAPP/iLEAPP, to their own repositories.

We aim to acknowledge within 7 days. Please give us reasonable time to fix before public disclosure.

## Scope and threat model

LEXIS opens **untrusted evidence** (extractions, archives, databases, documents). In scope: path traversal or zip-slip on
extraction/export/portable case, SQL injection, script injection in the map/HTML reports, unsafe deserialization,
code execution from opening an evidence file, leaks of case data to logs, temp files or the network, weakened integrity
verification (SHA-256 manifests, signed portable cases), and exposure of API keys.
Out of scope: attacks needing an already compromised Windows account, and AI models' own behaviour.

## Secrets

The repository must contain **no secrets**. API keys are typed in *AI settings* and stored encrypted (Windows DPAPI) in the
user's profile — never in the source tree. See `settings.example.json` for the settings layout (keys are never in clear).
A `.gitleaks.toml` and a `.pre-commit-config.yaml` are provided; run `gitleaks dir .` and `gitleaks git .` before publishing.
If a secret is ever committed, **revoke it first**, then remove it from history.

## Hardening already in place

Read-only handling of evidence; archive-member path checks; parameterised SQL; HTML escaping; WebView2 map page with a
Content-Security-Policy and embedded scripts (no CDN); loopback-only MCP server with secret path and Origin check; https required for remote AI
endpoints; backup passwords passed via environment variables; errors.log without messages; temp copies of evidence deleted on exit;
downloads (whisper.cpp) accepted only with the published SHA-256. See [PRIVACY.md](PRIVACY.md) for the data-protection view.
