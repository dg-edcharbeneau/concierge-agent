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
`POST /v1/chat/completions` (no trailing slash) returns **501 JSON** with an
OpenAI-shaped `error` indicating that the Think handler is not implemented.
Other Think paths/methods, and that exact path on normal listeners, return empty
**404** responses. A bounded live test on 2026-10-13 verified identical
local/public 501 bodies, empty 404s, and preserved UI/API/health behavior with
normal certificate validation and no redirects. This verifies HTTPS forwarding
and pending-handler isolation, not the Think protocol or Deepgram integration.
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
| `Model` | AppHost → `Concierge.WorkflowServer` | `qwen3.8:27b-mlx` / `embeddinggemma-2:740m` | For Foundry, use the deployment name. |
| `AI:Chat:ApiKey` | AppHost parameter → `Concierge.WorkflowServer` | not created | Created only when `AI:Chat:Provider` is `Foundry`, from `Parameters:foundry-chat-api-key`. If unset, Foundry uses `DefaultAzureCredential`; Aspire does not prompt. |
| `AI:Embeddings:ApiKey` | AppHost parameter → `Concierge.WorkflowServer` | not created | Created only when `AI:Embeddings:Provider` is `Foundry`, from `Parameters:foundry-embeddings-api-key`. If unset, Foundry uses `DefaultAzureCredential`; Aspire does not prompt. |
| `RequestTimeoutSeconds` | AppHost → `Concierge.WorkflowServer` | `180` | Must be positive. Options default `60`. |
| `MaxConcurrentRequests` | AppHost → `Concierge.WorkflowServer` | `2` | Must be positive. Options default `4`. |
| `AI:Chat:ContextWindowTokens` | AppHost → `Concierge.WorkflowServer` | `32768` | Sent to Ollama as `num_ctx`. |
| `AI:Embeddings:Dimensions` | AppHost → `Concierge.WorkflowServer` | `768` | Must be `768`. The schema uses `vector(768)`. |

The AppHost's Ollama defaults are in `src/Concierge.AppHost/appsettings.Development.json`.
To use Foundry for a capability, override its keys in the AppHost
user-secrets store. For example, for chat:

```bash
aspire secret set "AI:Chat:Provider" "Foundry" --apphost src/Concierge.AppHost
aspire secret set "AI:Chat:Endpoint" "https://<resource>.openai.azure.com/" --apphost src/Concierge.AppHost
aspire secret set "AI:Chat:Model" "<deployment>" --apphost src/Concierge.AppHost
```

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
dimensions, a `32768` chat context window, `60`-second timeouts, and concurrency
`4`. Its endpoints and models are deliberately empty: outside `Development`,
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
URL and model names from the table above. Timeout, concurrency, and context
window are optional: the options defaults are `60`, `4`, and `32768`.
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
