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
- [Aspire CLI](https://aspire.dev), for AppHost startup, shutdown, and live checks
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
aspire run --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```

For an Aspire-managed background run:

```bash
aspire start --non-interactive --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
aspire wait web --status healthy --timeout 180 --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
aspire describe --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```

Stop only this AppHost without resetting its persistent data:

```bash
aspire stop --non-interactive --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```

1. Open the Aspire dashboard and wait for the services to become healthy.
2. Open the **web** endpoint.
3. Check in using a demo guest's last name and room number from the table below.

Startup prepares the database, demo stays, and Markdown knowledge base.

#### Demo guest check-in information

These are seeded demo stays. Use any row's last name and room number to verify
the stay and start a concierge chat.

| Guest last name | Room number |
| --- | --- |
| Rivera | 412 |
| Morgan | 812 |
| Phillip | 112 |

## Try the concierge

- Ask a hotel question: “What time is checkout?”
- Check the weather: “What's the weather tomorrow?”
- Ask for nearby cafes, then request a ride to “that coffee shop.”
- Use the voice control for a spoken conversation and allow microphone access when prompted.

### Conversation display

Voice requests appear as pending guest text when the authenticated Think request
is submitted, without waiting for the answer or audio playback. Completed replies
are reconciled from the stay transcript by turn ID; pending text is not durable
history and does not indicate that a requested action succeeded.

### Rides and the Activity panel

**Activity** shows ride updates for the entire stay, including earlier chats.
Booked cards show estimated **destination arrival** in Miami time when available,
not driver arrival. Entries without an arrival estimate omit it.
An explicit new request can create another ride to the same destination, even
at the same pickup time. The offer warns about matching active rides and makes
clear that this is an additional booking, not a change. Nothing is booked until
the guest confirms that offer in a later turn. To change an existing booking,
cancel it first, then request and confirm a replacement.

<details>
<summary>Ride update delivery</summary>

Successful cancellations publish their stored event before the cancellation
activity returns terminal status, so workflow closure cannot discard that update.
Retried publications are deduplicated by ride ID and event sequence.

</details>

### Nearby recommendations

A singular nearest request presents just the nearest place; an options request
can present up to three. Ask for nearby cafes, then request a ride to an
alternative or “that coffee shop.” Duplicate names need a distinguishing street;
ambiguous references are not guessed. Offers end with an explicit booking
question, not a booking claim.

<details>
<summary>Place continuity and routing</summary>

Nearby recommendations retain the provider's name, street, identity, and
coordinates in bounded stay-workflow state, shared by voice and web.
The concierge can reuse that verified destination without another nearby search
or destination geocode. It still obtains a fresh driving estimate and checks
active rides before offering anything.
The model resolves conversational references using recent conversation and
structured places. Code only matches its resolved name/address to a unique
retained place; it does not parse the guest's wording.

Model context keeps every branch's address independently of the spoken reply.
Nearby continuity expires after ten minutes or six subsequent accepted
turns; destination clarifications expire after five minutes or two subsequent
turns. Continue-As-New preserves the original expiry. These are fixed bounds,
not new settings. See [configuration](docs/configuration.md#hotel-and-rides).

</details>

### Verification status

**Activity panel (Step 7): accepted by Cecil on 2026-10-31.** Cecil reported,
“I just ran another sesison. The activity panel updates look fine to me. What's
next?” This owner check, together with the existing automated tests and rendered
panel/database evidence, closes the remaining UI gate. The report does not say
whether a reload was needed; there is no independent no-reload capture.

The continuity/rendering slice has regression and current-code replay checks.
The `ride-conversation` live gate passed recommendation, contextual destination,
offer, confirmation, status, gym location/hours, and end-chat checks, including
exactly one persisted ride and booking event.

A separate headless Chrome check passed six spoken turns through the real
Deepgram relay with synthetic microphone audio. It checked nonzero returned
audio, playback acknowledgements and drain, and the final chat-ended screen.
End-of-input to first returned audio ranged from 2.7 to 4.0 seconds in that run.
This verifies the browser audio pipeline, not physical microphone/speaker
quality or a latency guarantee. The earlier isolated first-answer delay remains
unexplained; no speculative provider change or warmup was added.

The worker and web resources were rebuilt for these checks without a data reset.
Future workflow command-order changes still require explicit approval to reset
Temporal/Postgres data.

**Step 8 audit status (2026-10-31): completed.** The configuration tables below and in the
[configuration guide](docs/configuration.md) cover the implemented Phase 1/2
settings. An isolated solution build succeeded with zero warnings and errors.
Existing compiled test outputs passed the focused AI-configuration tests
(19/19), the Phase 1 retrieval/session/Think/end-chat regression set (137/137),
and the full suite (1,061/1,061). Tests used `--no-build --no-restore` to avoid
overwriting assemblies used by the live app; a separate clean build used an
isolated artifacts directory.

With Cecil's explicit stop/restart approval, the fresh startup gate passed on
2026-10-31 at 8:43–8:48 PM EDT (2026-11-01 UTC), using the documented
`aspire stop --non-interactive --apphost src/Concierge.AppHost/Concierge.AppHost.csproj`
and `aspire start --non-interactive --apphost src/Concierge.AppHost/Concierge.AppHost.csproj`
commands. Aspire 13.6.4 rebuilt and started the current source; `aspire wait`
confirmed Postgres, both databases, Temporal, ride-service, workflow-server,
web, and the Think tunnel healthy. Setup completed with 8 Markdown sources,
17 sections/chunks, no guest/stay overwrites, and no re-embedding or removed
sources. Temporary setup readiness 503s cleared to 200; the existing tunnel
was reconciled successfully after a create conflict.

Headless Chrome verified **Phillip / 112** and rendered the existing Activity
snapshot: 20 events for five rides, including the already-completed cinema ride.
No new guest turn, booking, or cancellation was submitted. All existing row
identities were retained: 7 stays, 67 transcript turns, 9 rides, and 36 ride
events; transcript/event content hashes and both named volume identities were
unchanged. Four browser sign-ins, including verification-script retries, added
four session rows (9 → 13); opening Activity marked the displayed snapshot read.
Redacted commands, startup logs, health, persistence comparisons, browser
results, and a screenshot are saved in the audit session's `files/step8-startup`
directory. No workflow/database reset or volume deletion was performed, and the
Aspire-managed app was left running.

The latest reported cinema turn measured Think
at 9.1 seconds, Deepgram total at 10.2 seconds, and TTS at 41 milliseconds;
this is one turn, not a latency guarantee.

## Configuration

The [Development settings](src/Concierge.AppHost/appsettings.Development.json)
select local Ollama models. The values below are Development defaults, combining
checked-in AppHost settings with service option defaults. Outside Development,
AppHost selects Foundry and requires explicit chat and embedding endpoints and
deployment names. Aspire forwards AppHost AI, hotel, ride-policy, and service-key
settings as shown; the other settings are configured directly on their owning
service. Secrets are empty unless supplied through the AppHost user-secrets store.
This reference covers the app's Phase 1/2 options; framework logging and
ASP.NET Core host settings follow .NET defaults, while Aspire injects service
connection strings, Temporal endpoints, and generated ports.

The tables are the concise reference; the [configuration guide](docs/configuration.md)
explains validation, behavior, and Aspire wiring in more detail.

### AppHost → WorkflowServer

| Key | Default | Units, validation, and purpose |
| --- | --- | --- |
| `Parameters:geoapify-api-key` → `Geoapify:ApiKey` | Empty | Secret key; nonblank and required at WorkflowServer startup for hotel geocoding, places, and routes. Set with the Geoapify command above. |
| `AI:Chat:Provider` | `Ollama` | `Foundry` or `Ollama` (case-insensitive); selects the chat provider. |
| `AI:Chat:Endpoint` | `http://127.0.0.1:11434` | Absolute HTTP(S) URL; Foundry requires HTTPS. Chat accepts a clean resource root or `/v1` Ollama / `/openai/v1` Foundry Responses base, without credentials, query, or fragment. |
| `AI:Chat:Model` | `concierge-chat` | Nonblank model name; Foundry uses the deployment name. |
| `Parameters:foundry-chat-api-key` → `AI:Chat:ApiKey` | Empty | Secret Foundry key; optional when `DefaultAzureCredential` is configured; unused by Ollama. |
| `AI:Chat:RequestTimeoutSeconds` | `18` | Seconds; integer `1–18`, including concurrency wait; bounds one chat request. |
| `AI:Chat:MaxConcurrentRequests` | `2` | Positive integer; per-WorkflowServer chat concurrency. |
| `AI:Chat:ContextWindowTokens` | `32768` | Tokens; integer at least `21000` and must match the selected model; declares available chat context. |
| `AI:Chat:ReasoningEffort` | `low` | `low` or `none` (case-insensitive); selected model must support it. |
| `AI:Embeddings:Provider` | `Ollama` | `Foundry` or `Ollama` (case-insensitive); selects the embedding provider. |
| `AI:Embeddings:Endpoint` | `http://127.0.0.1:11434` | Absolute HTTP(S) URL; Foundry requires HTTPS; embedding service endpoint. |
| `AI:Embeddings:Model` | `embeddinggemma-2:740m` | Nonblank model name; Foundry uses the deployment name. |
| `Parameters:foundry-embeddings-api-key` → `AI:Embeddings:ApiKey` | Empty | Secret Foundry key; optional when `DefaultAzureCredential` is configured; unused by Ollama. |
| `AI:Embeddings:Dimensions` | `768` | Dimensions; exactly `768` to match the stored vector schema and model output. |
| `AI:Embeddings:RequestTimeoutSeconds` | `8` | Seconds; integer `1–8`, including concurrency wait; bounds retrieval embeddings. |
| `AI:Embeddings:IngestionRequestTimeoutSeconds` | `60` | Seconds; integer `1–298`; bounds knowledge-ingestion embeddings and Ollama warm-up. |
| `AI:Embeddings:MaxConcurrentRequests` | `2` | Positive integer; per-WorkflowServer embedding concurrency. |
| `Hotel:Address` | `2217 NW Miami Court, Miami, FL 33127` | Nonblank address that passes startup geocoding; hotel anchor for weather, routes, and rides. |
| `Hotel:TimeZoneId` | `America/New_York` | Nonblank time-zone ID supported by the host; hotel-local dates and ride times. |
| `Rides:MinimumAdvanceNoticeMinutes` | `5` | Positive whole minutes; minimum notice for scheduled pickups, shared by WorkflowServer and RideService. |

### WorkflowServer settings

Set these in WorkflowServer's `appsettings.json`/environment, not in AppHost:

| Key | Default | Units, validation, and purpose |
| --- | --- | --- |
| `Setup:KnowledgeDirectory` | `knowledge/hotel-zgy` | Path to recursively discovered Markdown sources; directory and valid source/section content are required for startup setup and retrieval. |
| `Setup:OverwriteExistingSeedRecords` | `false` | Boolean; when true, startup setup updates existing configured demo guests and stays. |
| `Setup:Guests` | Built-in demo guests | Nonempty list of `GuestId` GUID, nonblank `GivenName`, and nonblank `FamilyName`; each name is limited to 100 characters. Seeds demo guest identities. |
| `Setup:Stays` | Built-in demo stays | Nonempty list of `StayId`/`GuestId` GUIDs, nonblank `ExternalStayKey`, a configured guest, and a valid one-to-four-digit room number not starting with zero. Seeds demo stays. |

Setup reconciles the configured records and knowledge base at startup. Do not
enable overwrite or point it at persistent data just to inspect configuration.

### Web settings

AppHost forwards the Deepgram API key only; configure the other Web settings
through Web's own appsettings or service environment:

| Key | Default | Units, validation, and purpose |
| --- | --- | --- |
| `Parameters:deepgram-api-key` → `Deepgram:ApiKey` | Empty | Secret Voice Agent key; required to use browser voice. Set with the Deepgram command above. |
| `Deepgram:ListenModel` | `nova-3` | Deepgram-supported model identifier; speech recognition. |
| `Deepgram:SpeakModel` | `aura-2-thalia-en` | Deepgram-supported model identifier; speech synthesis. |
| `Deepgram:IdleTimeoutMinutes` | `2` | Minutes; integer `1–60`; ends a voice chat after inactivity. |
| `Concierge:Persistence:FlushIntervalSeconds` | `30` | Seconds; integer `1–600`; maximum interval for batching transcript writes. |
| `Concierge:Persistence:FlushBatchSize` | `5` | Turns; integer `1–100`; transcript batch size. |

Web persists transcript batches under the selected policy when starting a stay;
existing stays retain their original policy. AppHost does not forward
`Concierge:Persistence` or the Deepgram model/idle-timeout settings.

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

The action smoke uses a fresh verified stay for each independent conversation
group, retaining shared state only for dependent turns. It checks ordered tool
calls and decoded outcomes from Temporal history. Missing prerequisites block
dependent turns and fail the gate; independent groups continue. Test-created
guest, stay, and ride rows remain as evidence.

Focused modes use the same scenarios as the full gate:

```bash
dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs -- nearby
dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs -- nearest
dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs -- weather-now
dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs --artifacts-path .artifacts/ride-live-gate -- ride-conversation
dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs -- selftest
```

`nearby` retains the weather/location conversation preceding the movie-theater
lookup. `selftest` checks the harness offline without accessing Aspire or
submitting guest turns.

`ride-conversation` uses one fresh fictional stay for a real-model conversation:
pharmacy recommendation → “Could you book me a ride there right now?” → offer →
confirmation → status → hotel gym question → end chat. The deictic follow-up is
not replaced with a destination name. Temporal turn histories assert tool order,
one confirmation, model/tool budgets, and the recommended endpoint's name,
address, and coordinates. A stay-scoped database read requires exactly one ride
and one `Booked` event. End chat calls `/api/concierge/end` and verifies that the
credential is rejected afterward; this is not a Deepgram end-chat/audio gate.
The harness retains its fictional rows as evidence and cleans up only its own
session/workflow. Run `selftest` to check the validator without live I/O.

Spoken ride offers establish the destination and address. Matching confirmations
and status replies reuse the trusted short venue name; immediate confirmations
say “now,” while scheduled pickups and web receipts retain precise hotel-local
dates and times. No address is guessed by splitting on commas. If the retained
canonical place context has expired or changed, the full endpoint label is kept
rather than risking the wrong branch.

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
aspire run --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```

## Implementation details

- [Stay workflow and conversation memory](src/Concierge.WorkflowServer/Workflows/ConciergeStayWorkflow.cs)
- [Browser voice integration](src/Concierge.Web/Voice/)
- [Knowledge-base Markdown](knowledge/hotel-zgy/)
