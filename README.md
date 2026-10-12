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

AI provider and setup options belong to `Concierge.WorkflowServer`. The
transcript persistence interval is configured on `Concierge.Web`. App secrets
are stored in the AppHost user-secrets store and passed only to the service
that needs them.

```bash
dotnet user-secrets --project src/Concierge.AppHost set "Parameters:foundry-chat-api-key" "<key>"
dotnet user-secrets --project src/Concierge.AppHost set "Parameters:foundry-embeddings-api-key" "<key>"
dotnet user-secrets --project src/Concierge.AppHost set "Parameters:deepgram-api-key" "<key>"
```

Keys under `AI:Chat:*` and `AI:Embeddings:*`:

| Key | Configuration owner | Development value | Notes |
| --- | --- | --- | --- |
| `Provider` | `Concierge.WorkflowServer` | `Ollama` | `Ollama` or `Foundry` (Azure AI Foundry / Azure OpenAI). |
| `Endpoint` | `Concierge.WorkflowServer` | `http://127.0.0.1:11434` | Foundry requires HTTPS. |
| `Model` | `Concierge.WorkflowServer` | `qwen3.8:27b-mlx` / `embeddinggemma-2:740m` | For Foundry, use the deployment name. |
| `AI:Chat:ApiKey` | AppHost parameter → `Concierge.WorkflowServer` | empty | Set `Parameters:foundry-chat-api-key` only when using a Foundry chat deployment. If unset, Foundry uses `DefaultAzureCredential`; Ollama starts without prompting and ignores this setting. |
| `AI:Embeddings:ApiKey` | AppHost parameter → `Concierge.WorkflowServer` | empty | Set `Parameters:foundry-embeddings-api-key` only when using Foundry embeddings. If unset, Foundry uses `DefaultAzureCredential`; Ollama starts without prompting and ignores this setting. |
| `RequestTimeoutSeconds` | `Concierge.WorkflowServer` | `180` | Must be positive. Default `60`. |
| `MaxConcurrentRequests` | `Concierge.WorkflowServer` | `2` | Must be positive. Default `4`. |
| `AI:Chat:ContextWindowTokens` | `Concierge.WorkflowServer` | `32768` (default) | Sent to Ollama as `num_ctx`. |
| `AI:Embeddings:Dimensions` | `Concierge.WorkflowServer` | `768` | Must be `768`. The schema uses `vector(768)`. |

Ollama does not use `ApiKey` or Azure credentials; only Foundry uses the
optional `ApiKey` / `DefaultAzureCredential` selection described above.

The Deepgram credential belongs to Web:

| AppHost user-secrets key | Target | Notes |
| --- | --- | --- |
| `Parameters:deepgram-api-key` | `Deepgram:ApiKey` in `Concierge.Web` | Required secret parameter for the Web voice integration. |

`appsettings.json` leaves `Endpoint` and `Model` empty and sets `Dimensions` to
`0`. Outside `Development`, you must set them. Settings are validated at
startup.

Under Aspire, the WorkflowServer launch profile sets `Development`, which loads
`appsettings.Development.json` (the Ollama defaults). Secrets are loaded by the
AppHost and injected into the relevant service; WorkflowServer does not have a
separate user-secrets store.

Other settings:

| Key | Owner | Default | Notes |
| --- | --- | --- | --- |
| `Concierge:Persistence:FlushIntervalSeconds` | `Concierge.Web` | `30` | How often transcript turns are written to PostgreSQL. 1–600. It applies to stays started after the change. |
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
