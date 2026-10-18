# Concierge Agent

A virtual concierge demo for Hotel ZGY, a fictional hotel in Wynwood, Miami,
built with .NET 10, Temporal, and .NET Aspire.


## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker
- [just](https://just.systems). Run `just` to list the recipes. Each recipe
  wraps a single `dotnet` command from the root `justfile`.
- [Aspire CLI](https://aspire.dev) (optional). You need it only for `aspire run`.
- [Ollama](https://ollama.com)

  ```bash
  ollama pull qwen3.8:27b-mlx
  ollama pull embeddinggemma-2:740m
  ollama create concierge-chat -f models/ollama/Modelfile.concierge-chat
  ```

To use Azure AI Foundry / Azure OpenAI instead of Ollama, see
[Configuration](#configuration).

## Run locally

From the repository root:

```bash
just run      # or: aspire run
```

The AppHost creates an anonymous Dev Tunnel for Web's dedicated Think endpoint
(`POST /v1/chat/completions`); the normal Web endpoint and UI are not exposed by
that tunnel. The dedicated Think listener and tunnel's local forwarding use
HTTPS with the existing ASP.NET Core development certificate; all Web listeners
are HTTPS-only. A terminal `MapWhen` branch isolates Think from Razor, UI error
and status pages, HSTS, and antiforgery. The exact, case-insensitive
`POST /v1/chat/completions` (no trailing slash) requires a short-lived,
stay-bound voice bearer and returns a completed Temporal answer as SSE
(assistant role, content, stop, `[DONE]`). Deepgram's supplied history is
ignored; the stay workflow owns shared text/voice context.
Other Think paths/methods, and that exact path on normal listeners, return empty
**404** responses. A bounded live test on 2026-10-13 verified identical
local/public 501 bodies, empty 404s, and preserved UI/API/health behavior with
normal certificate validation and no redirects. That historical probe verified
the then-pending handler's isolation, not the subsequently implemented voice protocol.
Aspire injects the assigned public Think URL into Web as
`THINK_PUBLIC_URL`; no tunnel URL argument is needed. Before starting Aspire
the first time on a machine, sign in once with the Dev Tunnels CLI:

```bash
devtunnel user login
```

Alternatively, run `devtunnel user login` to use a Microsoft account. This
authenticates the tunnel host; the tunnel itself allows anonymous visitors.
If the Aspire dashboard shows “Dev tunnels requires authentication to
continue”, complete this sign-in and retry.


Verify a demo stay with a last name and room number:

| Last name | Room |
| --- | --- |
| Rivera | 412 |
| Morgan | 812 |

Room numbers are 1–4 digits and can't start with `0`.

### Stay memory and answer context

A turn makes no summary model call. Each answer gets an estimated 8,000-token
working context: instructions, verified guest answer context, current hotel
evidence, a bounded stay memory, and the newest complete recent turns that fit.

Sign-in and subsequent authentication forward the database guest's given name,
family name and room number, bound to the authorized stay. Web and browser voice
send that profile with each admitted turn, including turns on already-running
stay workflows. It is a separate answer-context message, not hotel evidence or
guest-reported memory; the main concierge instructions are unchanged. This
deliberately sends the verified name and room to the configured model provider.
Client-supplied last names and conversation claims cannot replace this identity.

The server computes the room floor from the authoritative room number: `112`
is floor 1, `412` floor 4, `812` floor 8, and `1212` floor 12. One- and two-digit
rooms remain valid but have no inferred floor. The complete JSON-escaped profile
and its message overhead count against the working budget, rather than being
hidden in the fixed instruction allowance. If the required context cannot fit,
the turn fails safely instead of truncating identity or exceeding the budget.
Raw profile strings are data, not instructions. No extra guest lookup Activity,
tool, or model call is added to an answer.

Stay memory is extracted deterministically, without a model, from explicit
guest statements: allergies, preferences, decisions, and requests. It is scoped
to one stay, and a later statement on the same subject supersedes the earlier
one. Questions are not promoted. Guest statements are continuity, not hotel
authority. Memory, recent turns, and verified guest context carry across
Continue-As-New. Every new web or voice turn requires the verified guest: the
Web returns `401 unauthorized` (sign in again) for a session without a profile,
and the stay workflow's update validator rejects a guestless turn before it
enters history. The profile fields stay nullable only so older workflow
histories and session payloads still replay; they never invent a name or room.

The full transcript is persisted in PostgreSQL. Older turns outside the working
window are not recalled into the prompt; older-turn recall is deferred. The
legacy summary path remains only to replay workflow histories from before the
`stay-memory-v1-*` patch.

Voice turn IDs include a hash of the exact message, so resumed or changed
speech is a distinct turn.

### Voice latency and interruption measurements

The relay logs receipt-to-worklet-silence-acknowledgment time. A browser-rendered
barge-in requires nonzero audio in the playback worklet's last rendered block
before the flush. Queued-only audio, drained playback, and normal speech while
idle are not barge-in acceptance samples; filter on `BargeIn = true`.
The timing includes acknowledgment return transit, excludes Deepgram detection
before relay receipt, and is **not** a hardware/audible-silence measurement.
Chrome real-microphone/speaker acceptance still requires a new live run.

Run playback-boundary tests with `node --test tests/voice-play-worklet.test.mjs`.



## Tests

```bash
just test
```

Docker must be running for the integration tests.

## Configuration

The AppHost owns all AI configuration defaults, separately for chat and
embeddings, and passes configured values to `Concierge.WorkflowServer`. There
are no AI defaults in the workflow server's appsettings files. The workflow
server owns setup options. The transcript persistence interval is
configured on `Concierge.Web`. App secrets are stored in the AppHost
user-secrets store and passed only to the service that needs them.

```bash
aspire secret set "Parameters:foundry-chat-api-key" "<key>" --apphost src/Concierge.AppHost
aspire secret set "Parameters:foundry-embeddings-api-key" "<key>" --apphost src/Concierge.AppHost
aspire secret set "Parameters:deepgram-api-key" "<key>" --apphost src/Concierge.AppHost
aspire secret list --apphost src/Concierge.AppHost
```

`aspire secret` writes to the same store as `dotnet user-secrets --project
src/Concierge.AppHost`; either works.

Keys under `AI:Chat:*` and `AI:Embeddings:*`:

| Key | Configuration owner | Development value | Notes |
| --- | --- | --- | --- |
| `Provider` | AppHost → `Concierge.WorkflowServer` | `Ollama` | `Ollama` or `Foundry` (Azure AI Foundry / Azure OpenAI), case-insensitive. The AppHost stops with an error for any other value. |
| `Endpoint` | AppHost → `Concierge.WorkflowServer` | `http://127.0.0.1:11434` | Foundry requires HTTPS. |
| `Model` | AppHost → `Concierge.WorkflowServer` | `concierge-chat` / `embeddinggemma-2:740m` | The Ollama chat model is created from the checked-in Modelfile below. For Foundry, use the deployment name. |
| `AI:Chat:ApiKey` | AppHost parameter → `Concierge.WorkflowServer` | not created | Created only when `AI:Chat:Provider` is `Foundry`, from `Parameters:foundry-chat-api-key`. If unset, Foundry uses `DefaultAzureCredential`; Aspire does not prompt. |
| `AI:Embeddings:ApiKey` | AppHost parameter → `Concierge.WorkflowServer` | not created | Created only when `AI:Embeddings:Provider` is `Foundry`, from `Parameters:foundry-embeddings-api-key`. If unset, Foundry uses `DefaultAzureCredential`; Aspire does not prompt. |
| `RequestTimeoutSeconds` | AppHost → `Concierge.WorkflowServer` | `180` | Must be positive. Options default `60`. |
| `MaxConcurrentRequests` | AppHost → `Concierge.WorkflowServer` | `2` | Must be positive. Options default `4`. |
| `AI:Chat:ContextWindowTokens` | AppHost → `Concierge.WorkflowServer` | `32768` | Validates that the configured chat context covers the worst-case prompt budget. Ollama's actual `num_ctx` is set by the derived model's Modelfile. |
| `AI:Chat:ReasoningEffort` | AppHost → `Concierge.WorkflowServer` | `low` | Responses API effort; accepts `low` or `none`. Both Ollama and Foundry use `low` by default; `none` may be selected explicitly for supported models. |
| `AI:Embeddings:Dimensions` | AppHost → `Concierge.WorkflowServer` | `768` | Must be `768`. The schema uses `vector(768)`. |

The AppHost's Ollama defaults are in `src/Concierge.AppHost/appsettings.Development.json`.
The Ollama chat model is a derived model so its context window does not depend on
per-request options sent through the shared Responses API client. Create it after
pulling the base model:

```bash
ollama create concierge-chat -f models/ollama/Modelfile.concierge-chat
```

The checked-in Modelfile starts from `qwen3.8:27b-mlx` and fixes
`PARAMETER num_ctx 32768`. Re-run the command if the Modelfile changes. Starting
the application does not create or modify models in the local Ollama installation.
To use Foundry for a capability, override its keys in the AppHost
user-secrets store. For example, for chat:

```bash
aspire secret set "AI:Chat:Provider" "Foundry" --apphost src/Concierge.AppHost
aspire secret set "AI:Chat:Endpoint" "https://<resource>.openai.azure.com/" --apphost src/Concierge.AppHost
aspire secret set "AI:Chat:Model" "<deployment>" --apphost src/Concierge.AppHost
aspire secret set "AI:Chat:RequestTimeoutSeconds" "15" --apphost src/Concierge.AppHost
```

The Development timeout (`180`) is sized for local Ollama. Foundry chat calls
normally finish in 2–4 seconds but occasionally stall for about 30 seconds. A
15-second timeout fails the stalled attempt so Temporal retries the model
activity (up to 5 attempts) instead of holding the voice turn.

With Ollama, the AppHost creates no Foundry parameters, so none appear on the
dashboard. Ollama does not use `ApiKey` or Azure credentials.

Under Aspire, all configured AI settings reach the workflow server as
environment variables. Change defaults only in the AppHost settings files;
AppHost user-secrets and environment variables can override them. The
[retrieval evaluation runner](tests/Concierge.Evaluation/README.md#run-the-comparison)
loads those same central Development defaults, with environment overrides.

The Deepgram credential belongs to Web. It is optional for the current text-only
app and defaults to empty so startup does not prompt for it. Browser voice is not
implemented yet; set the key when that integration is implemented. A configured
AppHost user-secret overrides the empty default.

| AppHost user-secrets key | Target | Notes |
| --- | --- | --- |
| `Parameters:deepgram-api-key` | `Deepgram:ApiKey` in `Concierge.Web` | Optional for text-only use; required when browser voice is implemented. |

The AppHost's base `appsettings.json` selects Foundry, uses `768` embedding
dimensions, a `32768` chat context window, a `15`-second chat timeout, a
`60`-second embeddings timeout, and concurrency `4`. Its endpoints and models are deliberately empty: outside `Development`,
configure `AI:Chat:Endpoint`, `AI:Chat:Model`, `AI:Embeddings:Endpoint`, and
`AI:Embeddings:Model`. "Missing endpoint or model" means the service URL or
deployment name has not been supplied, not that the deployed model is missing.
AppHost stops with an error naming the required key and central settings file;
it never silently falls back to Ollama. Switching a Development capability to
Foundry also requires overriding both its endpoint and model. The worker's
existing validators enforce HTTPS for Foundry, dimensions, and request limits.

### Standalone WorkflowServer

Without Aspire, supply AI settings explicitly through environment variables
(`AI__Chat__Endpoint`, etc.), command-line configuration, or local configuration.
The worker does not automatically load AppHost files or AppHost user-secrets.
For Foundry, set each capability's `Endpoint` and `Model`, plus
`AI__Embeddings__Dimensions=768`; `Provider` defaults to `Foundry`. Set
`AI__Chat__ApiKey` / `AI__Embeddings__ApiKey` only for API-key authentication;
omitting them uses `DefaultAzureCredential`.

For Ollama, also set both `Provider` values to `Ollama`, with the local service
URL and model names from the table above. Create `concierge-chat` using the
command above before running the chat workflow. Timeout, concurrency, and
context window are optional: the options defaults are `60`, `4`, and `32768`.
To reproduce Aspire Development's request limits, explicitly set each
capability's `RequestTimeoutSeconds=180` and `MaxConcurrentRequests=2`.
Standalone execution also requires the normal database and Temporal connection
configuration. WorkflowServer has no separate user-secrets store.

Other settings:

| Key | Owner | Default | Notes |
| --- | --- | --- | --- |
| `Concierge:Persistence:FlushIntervalSeconds` | `Concierge.Web` | `30` | How often transcript turns are written to PostgreSQL. 1–600. It applies to stays started after the change. |
| `Concierge:Persistence:FlushBatchSize` | `Concierge.Web` | `5` | Completed turns that trigger an early transcript write; whichever of batch size or interval comes first wins. 1–100. It applies to stays started after the change. |
| `Setup:KnowledgeDirectory` | `Concierge.WorkflowServer` | `knowledge/hotel-zgy` | Markdown folder to ingest at startup. |
| `Setup:OverwriteExistingSeedRecords` | `Concierge.WorkflowServer` | `false` | Updates existing demo guest and stay records on startup. |

Aspire supplies `ConnectionStrings:concierge` to `Concierge.WorkflowServer` and
Temporal settings to both `Concierge.WorkflowServer` and `Concierge.Web`.
Temporal defaults to `localhost:7233` for the address. Set these connection
settings yourself only when running a service outside the AppHost.

## Retrieval evaluation

The Hotel ZGY evaluation package is in
[tests/Concierge.Evaluation](tests/Concierge.Evaluation).

```bash
just eval-validate          # validate the package against its schema
just eval-run               # run the comparison (run id: local-<timestamp>)
just eval-run my-run-id     # or choose a run id
```

`eval-run` requires Docker and the Ollama models above. Results go to
`tests/Concierge.Evaluation/results/`. The runner won't overwrite an existing
run, so each run needs a new run id.
