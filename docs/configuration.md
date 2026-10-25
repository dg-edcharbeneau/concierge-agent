# Configuration reference

Defaults below are for running the demo in Development. Secrets default to
empty. Outside Development, the base AppHost `appsettings.json` selects Foundry
with no endpoint or model, so AppHost fails at startup until both are configured.

AppHost forwards AI, hotel, ride-policy, and service-key settings to the
services. The [Development settings](../src/Concierge.AppHost/appsettings.Development.json)
use local Ollama models.

## Service keys

Configure these on **AppHost**. It forwards them to the indicated service.

| AppHost setting | Service setting | Default | Valid values / use |
| --- | --- | --- | --- |
| `Parameters:geoapify-api-key` | WorkflowServer: `Geoapify:ApiKey` | Empty | Valid Geoapify key; required at startup. |
| `Parameters:deepgram-api-key` | Web: `Deepgram:ApiKey` | Empty | Valid Deepgram Voice Agent key; required for voice. |

## AI providers

Configure on **AppHost**; forwarded to **WorkflowServer**. Foundry API keys use
the `Parameters:*` settings below. Provider names are case-insensitive.

| Setting | Default | Valid values / constraints |
| --- | --- | --- |
| `AI:Chat:Provider` | `Ollama` | `Foundry`, `Ollama`. |
| `AI:Chat:Endpoint` | `http://127.0.0.1:11434` | Resource root or Responses API base: `/v1` for Ollama, `/openai/v1` for Foundry. HTTP(S) for Ollama; HTTPS for Foundry. No credentials, query, or fragment. |
| `AI:Chat:Model` | `concierge-chat` | Nonblank model name; Azure deployment name for Foundry. |
| `Parameters:foundry-chat-api-key` | Empty | Foundry chat key; forwarded as `AI:Chat:ApiKey`. Empty selects `DefaultAzureCredential`; unused for Ollama. |
| `AI:Chat:RequestTimeoutSeconds` | `18` | Integer `1–18`; includes waiting for a concurrency slot. |
| `AI:Chat:MaxConcurrentRequests` | `2` | Positive integer; per-process chat concurrency. |
| `AI:Chat:ContextWindowTokens` | `32768` | Integer at least `21000`; must match a context size the selected model supports. |
| `AI:Chat:ReasoningEffort` | `low` | `low`, `none` (case-insensitive); selected model must support it. |
| `AI:Embeddings:Provider` | `Ollama` | `Foundry`, `Ollama`. |
| `AI:Embeddings:Endpoint` | `http://127.0.0.1:11434` | Absolute HTTP(S) URL; HTTPS required for Foundry. |
| `AI:Embeddings:Model` | `embeddinggemma-2:740m` | Nonblank model name; Azure deployment name for Foundry. |
| `Parameters:foundry-embeddings-api-key` | Empty | Foundry embeddings key; forwarded as `AI:Embeddings:ApiKey`. Empty selects `DefaultAzureCredential`; unused for Ollama. |
| `AI:Embeddings:Dimensions` | `768` | Exactly `768`, matching the database schema and model output. |
| `AI:Embeddings:RequestTimeoutSeconds` | `8` | Integer `1–8`; retrieval embedding call, including concurrency-slot wait. |
| `AI:Embeddings:IngestionRequestTimeoutSeconds` | `60` | Integer `1–298`; ingestion embedding call and Ollama embedding warm-up. |
| `AI:Embeddings:MaxConcurrentRequests` | `2` | Positive integer; per-process embedding concurrency. |

## Hotel and rides

Configure on **AppHost**; it forwards the address to WorkflowServer, and the
time zone and ride policy to both WorkflowServer and RideService.

| Setting | Default | Valid values / constraints |
| --- | --- | --- |
| `Hotel:Address` | `2217 NW Miami Court, Miami, FL 33127` | Nonblank street address that passes startup geocoding. |
| `Hotel:TimeZoneId` | `America/New_York` | Nonblank time-zone ID supported by the service host. |
| `Rides:MinimumAdvanceNoticeMinutes` | `5` | Positive whole minutes; scheduled pickups only. |

For example, change scheduled pickups to require 12 minutes’ notice:

```bash
dotnet user-secrets set "Rides:MinimumAdvanceNoticeMinutes" "12" --project src/Concierge.AppHost
```

AppHost forwards this value to both WorkflowServer and RideService. The pickup
must still meet the minimum when confirmed; immediate rides are unaffected.

## Browser voice

Configure on **Web**. AppHost forwards the API key only; it does not forward
the other `Deepgram:*` options.

| Setting | Default | Valid values / constraints |
| --- | --- | --- |
| `Deepgram:ApiKey` | Empty | Valid Voice Agent key; supplied through `Parameters:deepgram-api-key` under Aspire. |
| `Deepgram:ListenModel` | `nova-3` | Deepgram Voice Agent speech-to-text model supported by the service. |
| `Deepgram:SpeakModel` | `aura-2-thalia-en` | Deepgram Voice Agent text-to-speech model supported by the service. |
| `Deepgram:IdleTimeoutMinutes` | `2` | Integer `1–60`; ends an idle voice chat. |

## Knowledge base and demo stays

Configure on **WorkflowServer**.

| Setting | Default | Valid values / constraints |
| --- | --- | --- |
| `Setup:KnowledgeDirectory` | `knowledge/hotel-zgy` | Existing directory with valid knowledge Markdown. Relative paths resolve against the service content root, then its app directory; by default that is the build output, which receives only the `*.md` files directly in `knowledge/hotel-zgy/`, so files in subfolders are ignored. |
| `Setup:OverwriteExistingSeedRecords` | `false` | `true`, `false`; update existing demo guest and stay records when true. |
| `Setup:Guests` | Built-in demo guests (Rivera, Morgan, Phillip) | Seeded demo guests: `GuestId`, `GivenName`, `FamilyName`. |
| `Setup:Stays` | Built-in demo stays (rooms 412, 812, 112) | Seeded demo stays: `StayId`, `ExternalStayKey`, `GuestId`, `RoomNumber`. |

## Transcript persistence

Text and voice conversation transcripts are stored in the PostgreSQL
`concierge` database, in the `transcript_turns` table.

Database writes are batched using the settings below; ending a chat also
flushes pending turns.

To change them, add a `Concierge:Persistence` section to
[Web's appsettings.json](../src/Concierge.Web/appsettings.json).
Web uses the defaults below unless overridden and passes the policy when starting
a new stay workflow. Existing stay workflows retain their original policy.
**AppHost does not read or forward these settings.**

| Setting | Default | Valid values / constraints |
| --- | --- | --- |
| `Concierge:Persistence:FlushIntervalSeconds` | `30` | Integer `1–600`. |
| `Concierge:Persistence:FlushBatchSize` | `5` | Integer `1–100` turns. |
