# Concierge Agent

A virtual concierge for Hotel ZGY, a fictional hotel in Wynwood, Miami. Built
with .NET 10, ASP.NET Core, Aspire, Temporal, and Deepgram’s Voice Agent API.
Guests can use text or browser voice to ask about the hotel, check weather,
find nearby places and their opening hours, get driving routes, and book, check,
list, and cancel rides. An in-app **Activity** panel shows ride updates.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker, running
- [just](https://just.systems)
- [Aspire CLI](https://aspire.dev), for `just reset` and `just live-concierge-actions`
  (`just run` uses `dotnet run`)
- Dev Tunnels CLI (`devtunnel`)
- [Ollama](https://ollama.com) for local models, or configured Foundry deployments
- Geoapify account and API key (required at startup for location and mapping features)
- Deepgram account and API key with Voice Agent API access (required for browser voice)

Run all commands below from the repository root.

## Run locally

### 1. Prepare the models

Development defaults to Ollama for both chat and embeddings.

```bash
ollama pull qwen3.8:27b-mlx
ollama pull embeddinggemma-2:740m
ollama create concierge-chat -f models/ollama/Modelfile.concierge-chat
```

The [Modelfile](models/ollama/Modelfile.concierge-chat) sets a 32,768-token
context. Load the chat model before trying voice to avoid its initial load delay.

To use Foundry instead, configure it [below](#use-foundry) and skip the local models
for whichever capability you switch.

### 2. Set the service keys

Geoapify is used for mapping features and is **required at startup**:

```bash
dotnet user-secrets set "Parameters:geoapify-api-key" "<your-key>" --project src/Concierge.AppHost
```

Deepgram key is required for voice features:

```bash
dotnet user-secrets set "Parameters:deepgram-api-key" "<your-key>" --project src/Concierge.AppHost
```

Keep keys in the AppHost user-secrets store, not checked-in settings.
`aspire secret set "<key>" "<value>" --apphost src/Concierge.AppHost` writes to
the same store, so either command works.

### 3. Prepare HTTPS and the tunnel

Once per machine:

```bash
dotnet dev-certs https --trust
devtunnel user login
```

Aspire creates the public voice callback tunnel and supplies its URL automatically.
The web UI uses a separate local HTTPS endpoint.

### 4. Start and sign in

```bash
just run
```

Alternatively, run `dotnet run --project src/Concierge.AppHost`. In the Aspire
dashboard, wait for the services to become healthy, then open the **web** endpoint.
Startup prepares the database, demo stays, and Markdown knowledge base.

| Last name | Room |
| --- | --- |
| Rivera | 412 |
| Morgan | 812 |
| Phillip | 112 |

Try “What time is checkout?” or “What’s the weather tomorrow?” Use the voice
control for a spoken conversation and allow microphone access when prompted.

## Configuration

AppHost forwards AI, hotel, ride-policy, and service-key settings to the
services. The [Development settings](src/Concierge.AppHost/appsettings.Development.json)
use local Ollama models.

See the [configuration reference](docs/configuration.md) for settings, defaults,
validation limits, and service ownership.

### Use Foundry

For chat, set the service endpoint and **deployment name**:

```bash
dotnet user-secrets set "AI:Chat:Provider" "Foundry" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Chat:Endpoint" "https://<resource>.openai.azure.com/" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Chat:Model" "<chat-deployment>" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Chat:RequestTimeoutSeconds" "15" --project src/Concierge.AppHost
dotnet user-secrets set "Parameters:foundry-chat-api-key" "<your-key>" --project src/Concierge.AppHost
```

To switch embeddings too:

```bash
dotnet user-secrets set "AI:Embeddings:Provider" "Foundry" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Embeddings:Endpoint" "https://<resource>.openai.azure.com/" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Embeddings:Model" "<embedding-deployment>" --project src/Concierge.AppHost
dotnet user-secrets set "Parameters:foundry-embeddings-api-key" "<your-key>" --project src/Concierge.AppHost
```

The embedding deployment must support **768 dimensions**. If a Foundry API key
is omitted, the application uses `DefaultAzureCredential` and requires an
appropriately authorized Azure identity.

## Tests and evaluation

Docker must be running for the integration tests. Run `just` to list all recipes.

```bash
just test
just live-place-weather      # Geoapify/Open-Meteo check; uses the AppHost user-secret key
just live-concierge-actions  # action checks against the running app
just eval-validate
just eval-run                # saves a new result; optional run ID: just eval-run my-run
```

Browser audio worklet tests use
`node --test tests/voice-play-worklet.test.mjs` (requires Node.js).

## Troubleshooting and reset

| Problem | Action |
| --- | --- |
| Tunnel authentication requested | Run `devtunnel user login`, then retry startup. |
| HTTPS certificate not trusted | Run `dotnet dev-certs https --trust`. |
| Hotel location fails at startup | Check the Geoapify key and `Hotel:Address`; inspect WorkflowServer logs in Aspire. |
| Missing AI endpoint/model | Set both values for the selected provider; Foundry uses deployment names. |
| Timeout setting rejected | Keep chat at 18 seconds or less and retrieval embeddings at 8 or less. |
| Demo stays expired or a schema/workflow change requires fresh data | Reset local data using the command below. |

**Reset deletes local Postgres data and Temporal history.** Use it when you want
a fresh demo, then start the app again:

```bash
just reset
just run
```

## Implementation details

- [Stay workflow and conversation memory](src/Concierge.WorkflowServer/Workflows/ConciergeStayWorkflow.cs)
- [Browser voice integration](src/Concierge.Web/Voice/)
- [Knowledge-base Markdown](knowledge/hotel-zgy/)
