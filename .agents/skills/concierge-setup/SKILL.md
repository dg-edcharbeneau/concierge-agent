---
name: concierge-setup
description: Sets up the Concierge Agent repo on a new machine - checks prerequisite tools, walks the user through creating Geoapify, Deepgram, and Microsoft Foundry (or local Ollama) accounts and keys, stores settings in the AppHost user-secrets store without exposing key values, then starts the Aspire AppHost and verifies it is healthy. Use when someone has just cloned the repo, asks how to get it running, is missing API keys, or startup fails on a missing or rejected key or AI provider setting.
compatibility: Requires a POSIX shell (macOS, Linux, or WSL/Git Bash), perl, and network access. Key entry needs the user's own interactive terminal.
---

# Concierge setup

Get the repo from fresh clone to a healthy running AppHost. The repo is the
source of truth: derive setting names, defaults, validation rules, and model
choices from the files listed below at run time. Don't rely on values quoted in
this skill or from memory; they can drift from the code.

## Safety rules

- Never read, print, log, or ask the user to paste a secret value into the
  conversation. That includes `dotnet user-secrets list` output: show setting
  *names* only.
- The user enters each secret themselves with `scripts/set-secret.sh` in their
  own terminal. It needs a TTY, so agents can't run it, and must not try to pipe
  values into it.
- You may set non-secret values yourself (provider, endpoint, deployment/model
  name, timeouts) with `dotnet user-secrets set ... --project <AppHost dir>`
  after the user gives you the values.
- Never put secrets in checked-in `appsettings*.json`.
- Ask before installing software, and before running anything that deletes data
  (`just reset`) or writes demo records (`just live-concierge-actions`).

## Sources of truth

Read these as needed instead of assuming:

| Need | File |
| --- | --- |
| Prerequisite tools, model pull commands, demo guests, troubleshooting | `README.md` |
| AppHost project path | `aspire.config.json` |
| Required parameters and how settings are forwarded | `src/Concierge.AppHost/AppHost.cs` |
| Development defaults (provider, endpoints, models) | `src/Concierge.AppHost/appsettings.Development.json` |
| Every setting's default and validation rule | `docs/configuration.md` |
| Which Foundry deployment and reasoning effort were evaluated | `tests/Concierge.Evaluation/README.md` |
| Foundry request shape (reasoning effort always sent, temperature omitted) | `src/Concierge.WorkflowServer/AI/AIProviderServiceCollectionExtensions.cs` |
| Local chat model definition | `models/ollama/Modelfile.concierge-chat` |
| External account steps (sign-up pages, where keys live) | [references/accounts.md](references/accounts.md) |

## Workflow

### 1. Check current state

Run from the repo root:

```bash
.agents/skills/concierge-setup/scripts/check-setup.sh
```

It reports tools, Docker, the HTTPS dev cert, dev tunnel login, which
user-secrets *names* are set, and each AI capability's effective provider. It
derives the required parameters from `AppHost.cs`. If it says the AppHost shape
changed, read `AppHost.cs` and continue manually. Re-run it after each step
below.

### 2. Install missing tools

Use the README prerequisite links and the platform's package manager. Ask the
user first. Start Docker if it's installed but the daemon isn't running.

### 3. Choose AI providers

Chat and embeddings each use `Ollama` (local) or `Foundry`. Ask the user which
they want; they can mix them.

- **Ollama:** use the model commands in the README. The chat model is large;
  check that the machine has the memory for it before recommending it.
- **Foundry:** read the chat and embedding rows in `docs/configuration.md` and
  the evaluated deployment in `tests/Concierge.Evaluation/README.md`. Tell the
  user which models meet those rules, such as reasoning support, a supported
  reasoning effort, embedding dimensions, and context window, before they
  deploy anything.

Settings for a chosen provider override the Development defaults through
user-secrets. Set each one the configuration reference marks as needed for that
provider.

### 4. Create accounts and store secrets

Go through the parameters `check-setup.sh` reports as missing, one at a time:

1. Walk the user through the matching section of
   [references/accounts.md](references/accounts.md), including the link.
2. Have them run, in their own terminal:
   ```bash
   .agents/skills/concierge-setup/scripts/set-secret.sh Parameters:<parameter-name>
   ```
   The script prints only the stored length. If the length looks wrong for that
   service, have them re-enter it.

Foundry API-key parameters are optional. Without them the app uses
`DefaultAzureCredential`, so the user can run `az login` instead.

### 5. One-time machine setup

Have the user run these themselves (both are interactive):

- `dotnet dev-certs https --trust` if the cert isn't trusted.
- `devtunnel user login` (add `-g` for GitHub) if not logged in. Voice needs it.

### 6. Start and verify

```bash
A=$(sed -n 's/.*"path": *"\([^"]*\)".*/\1/p' aspire.config.json)
aspire start --non-interactive --apphost "$A"
aspire wait web --status healthy --timeout 240 --apphost "$A"
aspire describe --apphost "$A"
```

Everything should be `Running` and `Healthy`. For any resource that is
`Finished` or unhealthy, read its log:
`aspire logs <resource> --tail 80 --apphost "$A"`. Startup errors name the
setting to fix.

After a fix, restart the AppHost (`aspire stop`, then `aspire start`).
Parameter values are read when the AppHost starts, so restarting a single
resource keeps the old value.

Optional checks:

- `just live-place-weather` calls Geoapify and Open-Meteo directly. Read-only
  and quick.
- `just live-concierge-actions` exercises chat, weather, routes, and rides
  through the running app. Ask first: it takes minutes, uses model quota, and
  leaves demo ride and stay rows behind.

### 7. Report

Tell the user:

- Which settings were stored (names only).
- Resource health from `aspire describe`.
- The `web` URL.
- Which demo guest to check in as (from the README table).
- Any check you didn't run.

## Known failures

| Symptom | Cause and fix |
| --- | --- |
| WorkflowServer exits: `Geoapify returned HTTP 401` | Key mistyped, from another project, or restricted by origin/IP in the Geoapify project. Fix it in Geoapify, re-run `set-secret.sh`, restart the AppHost. |
| AppHost throws `AI:<Capability>:<Key> is required` | That provider's endpoint or model isn't set. Set it as a non-secret user-secret. |
| Foundry endpoint rejected | Use the bare resource URL. Paths other than `/openai/v1`, and query strings, are rejected (see `docs/configuration.md`). |
| Foundry chat request rejected for reasoning effort | The deployment doesn't support the configured effort, or isn't a reasoning model. Pick one that the evaluation README shows working. |
| Tunnel authentication requested | `devtunnel user login`, then restart. |
| Anything else | The README troubleshooting table. |
