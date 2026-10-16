# Hotel ZGY retrieval evaluation package

This directory contains the contract and data for `hotel-zgy-retrieval-eval`.
There is one package. When Cecil approves a change, edit it directly.
The canonical data and schema are at
[`../../.artifacts/evaluation/hotel-zgy/`](../../.artifacts/evaluation/hotel-zgy/README.md).
The local copies are runnable mirrors: only package-relative provenance links
and `corpusSnapshot.repositoryRootFromPackage` differ. Schemas are byte-identical.

- [`hotel-zgy-eval.json`](hotel-zgy-eval.json) — evaluation cases, planned
  sources, gates, provenance, review state, and the recorded corpus snapshot.
- [`hotel-zgy-eval.schema.json`](hotel-zgy-eval.schema.json) — JSON Schema
  Draft 2020-12 for the package data shape.
- [`validate.cs`](validate.cs) — .NET file-based validator for schema and
  cross-field semantics, evidence references, counts, local file links, and
  the corpus snapshot.
- [`validate-negative-tests.cs`](validate-negative-tests.cs) — mutation tests
  that confirm the validator rejects invalid packages.
- `runner/` — evaluation-only comparison runner. `results/<date>[-<run-id>]/`
  holds each run's raw `run.json` (which records the package SHA-256) and its
  `results.md`.

## Review state

- `humanReview`: `approved` by Cecil Phillip on 2026-10-09. The user handoff
  approved the facts, cases, and gates. On 2026-10-14 Cecil approved the
  staff-contact facts in `zgy-services`, the room-service ordering route, and
  case ZGY-PARA-004. On 2026-10-16 Cecil approved pool hours and convenience-store
  location, hours, stock categories, and referral for specific brands and sizes.
- All 8 planned sources are `approved`. All 22 cases are `active`.
- Corpus absence for the 5 no-evidence cases was confirmed against the full
  recorded snapshot. The corpus contains related passages but no evidence for
  front-desk charger lending, parking prices or non-guest parking terms, or a
  pet policy, specific toothpaste brands, or specific mouthwash sizes.
  ZGY-NONE-005 deliberately remains a pet-policy fallback. Guest name, room
  and computed-floor forwarding are covered by `AuthenticatedGuestContextTests`
  and `VerifiedGuestContextTests` in `Concierge.Core.Tests`; this stateless
  corpus package does not evaluate guest identity.

This package records no retrieval or answer results.

## Cases and gates

| Category | Cases | Expected evidence keys |
| --- | --- | --- |
| `exact_term` | 10 | 10 |
| `paraphrase` | 5 | 5 |
| `multi_source` | 2 | 4 |
| `no_evidence` | 5 | 0 |

The package uses these initial demo gates. They are package-specific and are
not universal benchmarks. The count-based minimums are authoritative:

- Recall@5: at least 18 of 19 expected evidence keys (`threshold` 0.90;
  ceiling(0.90 × 19) = 18). Only the combined (production) retrieval arm is
  gated. Lexical-only and vector-only Recall@5 are comparison numbers, not
  pass/fail.
- Answer support: 17 of 17 answerable cases.
- No-evidence behavior: 5 of 5 cases.

Case IDs ZGY-NONE-001 and ZGY-NONE-002 were removed because approved facts now
cover guest Wi-Fi and EV charging. Their IDs are not reused.

For a no-evidence answer to pass, the assistant must not guess. It must say
that the hotel information does not establish the answer and direct the guest
to hotel staff. The retrieval result does not have to be empty. A related
passage does not count as sufficient evidence. For example, phone chargers in
guest rooms and conference rooms do not establish front-desk charger lending.

## Corpus snapshot

The evaluation corpus is every Markdown file under `knowledge/hotel-zgy/`
(8 sources, 16 sections/chunks in the 2026-10-16 setup run).
`corpusSnapshot` records the repository-relative path, `sourceId`, SHA-256 of
the exact file bytes, and byte length of each file. The snapshot was captured
on 2026-10-09 after the approved corpus updates and before any comparison. It
was recaptured on 2026-10-14 after the approved staff-contact update to
`services.md`, and on 2026-10-16 after the approved pool/store additions.
Older runs used smaller snapshots (initially 14 evidence keys / 12 answerable
cases, then 15 / 13) and are not comparable gate-for-gate.
The validator fails in any of these cases:

- A corpus file is missing from the snapshot.
- A snapshot entry has no matching corpus file.
- A hash or byte length does not match.
- A recorded `sourceId` differs from the file front matter.
- A planned source path differs from its snapshot path.
- The package has no snapshot.

| Path | sourceId | SHA-256 | Bytes |
| --- | --- | --- | --- |
| `knowledge/hotel-zgy/amenities.md` | `zgy-amenities` | `923f1df1e4258034312aa16ec5f20e0bbf1707a2679d5aa5efb4c0c53d8e2335` | 389 |
| `knowledge/hotel-zgy/amenity-hours.md` | `zgy-amenity-hours` | `5c4189bb1cd9cb62118408cfc024b90c3c0a60052a678fc868a0f31a8dc5e9be` | 301 |
| `knowledge/hotel-zgy/connectivity.md` | `zgy-connectivity` | `c8816230b0328063dc81fc96bc4afbee1d0e3af6d125e08e652e25187f64f74a` | 244 |
| `knowledge/hotel-zgy/convenience-store.md` | `zgy-convenience-store` | `2842411a4e2779fcf194c76958bdf412bbf8b9c4707b854d996b405734cd5862` | 640 |
| `knowledge/hotel-zgy/parking.md` | `zgy-parking` | `0bad9f1b48beda2f8c30ecd04aecdf2d5e903fac9673a6c3d3b6adf89536d14b` | 251 |
| `knowledge/hotel-zgy/policies.md` | `zgy-policies` | `7a028d3b7eea34057fba7ee0120a505f08c1f57a0904f96ba6ef92719866e54a` | 427 |
| `knowledge/hotel-zgy/property-overview.md` | `zgy-property-overview` | `15b4ce88cc164475d28c7220eb6c6e53d619c56559357c3ba75bc097c683e3c7` | 369 |
| `knowledge/hotel-zgy/services.md` | `zgy-services` | `42f7ffc58428953f167e4268ee3ddaaa3160d39a8b05ad1c8b58c0bcda22f0a8` | 1085 |

## Validate

From the repository root, with the .NET SDK, run:

```text
dotnet run --file tests/Concierge.Evaluation/validate.cs -- \
  tests/Concierge.Evaluation/hotel-zgy-eval.json \
  tests/Concierge.Evaluation/hotel-zgy-eval.schema.json

dotnet run --file tests/Concierge.Evaluation/validate-negative-tests.cs -- \
  tests/Concierge.Evaluation/validate.cs \
  tests/Concierge.Evaluation/hotel-zgy-eval.json \
  tests/Concierge.Evaluation/hotel-zgy-eval.schema.json
```

On 2026-10-16 the expanded mirror package validator passed and all 24
negative-test cases passed.
These checks verify only the package shape, consistency, and corpus snapshot.
They do not produce retrieval or answer-quality results.

## Run the comparison

From the repository root, with Docker running and the configured model services
available:

```bash
dotnet run --project tests/Concierge.Evaluation/runner -- --run-id central-config
```

The runner reads setup defaults from WorkflowServer's settings files and AI
defaults from `src/Concierge.AppHost/appsettings.json` followed by
`appsettings.Development.json`. Models, embedding dimensions, request limits,
and chat context window therefore match Aspire Development without a second
copy of the defaults. Environment variables are loaded last and override them,
for example `AI__Chat__Model` or `AI__Embeddings__Endpoint`. The existing
`--run-id` argument labels output; it is not a configuration override.

The runner does not load AppHost user-secrets or resolve Aspire parameters.
For Foundry, override that capability's `Provider`, HTTPS `Endpoint`, and
`Model` (deployment name) through environment variables. Supply
`AI__Chat__ApiKey` / `AI__Embeddings__ApiKey` only for API-key authentication;
otherwise Foundry uses `DefaultAzureCredential`. Dimensions remain `768` for
the current schema. Ollama provenance is collected separately for each
Ollama capability at its configured endpoint; Foundry receives no Ollama API
requests. API keys are not recorded in results.

The shared Foundry Responses path omits `temperature`: Microsoft lists it as
[unsupported for reasoning models](https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/reasoning#unsupported-parameters),
including `gpt-5-mini`. Historical Ollama fixture runs used temperature `0`;
the production-prompt runner now omits it for both providers, as production does.
The runner records the temperature (`null` means omitted) and configured
reasoning effort in `generation.options`. Use a supported effort for the
deployment; `gpt-5-mini` supports `low`, not `none`.

### Pool and convenience-store evaluation (2026-10-16)

Both completed runs below verified the current 8-file snapshot, ingested
16 sections/chunks, and used the unchanged concise production instructions.
The six added cases pass on both providers. All 44 completed answers were
manually reviewed against expected claims and their actual retrieved passages.
Manual grades are in each `results.md`; the runner's exit code alone does
not grade answer support.

| Run/report | Support | Fallback | Combined Recall@5 | Run elapsed | Answer mean / median / range (seconds) |
| --- | ---: | ---: | ---: | ---: | --- |
| [Ollama `concierge-chat`, low](results/2026-10-16-ollama-low-pool-store-1/results.md) | 16/17 | 5/5 | 19/19 | 114.83 s | 4.91 / 3.89 / 1.62–24.64 |
| [Foundry `gpt-5-mini`, low](results/2026-10-16-foundry-gpt-5-mini-low-pool-store-key-2/results.md) | 17/17 | 5/5 | 19/19 | 107.28 s | 4.59 / 2.85 / 1.58–21.14 |

Foundry passes every gate. Ollama fails the all-answer-support gate on the
existing ZGY-EXACT-001: after giving the correct address it adds that the
address "doesn't correspond to a real property." The evidence establishes a
fictional Hotel ZGY and a no-affiliation disclaimer, not an absence of a real
property at the address. This unsupported addition is conservatively failed.
No prompt or corpus facts were changed to tune away this answer.
These are single full runs, not a repeated-request latency benchmark or
end-to-end voice measurements.

The first Foundry attempt used `DefaultAzureCredential` and stopped on a
tenant mismatch before its first answer. The existing AppHost API key was
then passed in-memory through `AI__Chat__ApiKey`, without printing or recording
it. That first key-authenticated attempt completed three answers, then hit the
configured 180-second request timeout on ZGY-EXACT-004; it produced no saved
run. One full infrastructure retry, under the `key-2` run ID, completed above.
No failed attempt is counted as a completed gate run.

Package SHA-256 values:

- Runnable mirror: `87fc0f3a87cc59412cddadba808f09c5330fe077bd24543c380f656f6be9abb1`
  (recorded in both completed `run.json` files).
- Canonical: `90bc5eb6f6af59e398c501923b1689a76cb29b00db77c9fc2e4e1aad08e98e06`.
  Its hash differs only because local references resolve from a different
  directory; semantic equality and byte-identical schemas were checked.

Both package validators and both sets of 24 existing negative validation
checks passed. Worker output corpus bytes were also checked against every
snapshot file. Canonical artifacts and raw evaluation results are ignored by
the repository's existing ignore rules; the tracked mirrors remain under
this directory.

#### Active corpus refresh

The existing evaluation build refreshed WorkflowServer's linked
`knowledge/hotel-zgy` output files through its existing `CopyToOutputDirectory`
configuration. The live worker reads those files; changing repository Markdown
alone does not refresh the active index.

At 2026-10-16 12:33:06 UTC the existing `Concierge.HotelSetup` workflow completed
against the active Aspire database, with overwrite disabled and the same
previous setup seed input and embedding identity:

- Workflow ID: `concierge-hotel-zgy-setup-v1`.
- Run ID: `01a144b3-8b98-756a-852c-83c2476dd11d`.
- Startup generation: `approved-pool-store-20261016`.
- Elapsed: 6.325 seconds; 8 sources, 16 sections, 16 chunks; 3 newly embedded
  sections; no sources removed; zero guest/stay rows inserted or overwritten.
- Embeddings: Ollama `embeddinggemma-2:740m`, 768 dimensions.

A read-only query of the active database subsequently confirmed the exact
pool-hours, store-location-and-hours, and store-stock passages with non-null
embeddings, 8 sources, and 16/16 embedded chunks. AppHost was not restarted.
The live knowledge index is updated for the existing voice retrieval path;
no microphone/Deepgram end-to-end voice turn was exercised in this change.

### Production grounding prompt (2026-10-14)

The runner now references `ConciergeStayWorkflow.ConciergeInstructions` directly
through the existing friend assembly, rather than copying an evaluation fixture.
The production prompt was revised **after** 2026-10-14 evidence of paraphrase
refusals and invented procedures; it is neither the original fixture nor its
rule-3 revision. It requires current-evidence-only facts, recognizes the same
service in other words without conflating related topics, prohibits invented
facts/procedures/policies, answers supported parts with staff fallback for
unsupported parts, and keeps history non-authoritative.

Generation uses production's system-instructions/system-evidence/user-question
layout, unadorned retrieved passage text, 1,024-token answer cap, omitted
temperature, and the shared provider adapter's configured reasoning effort.
Following the Foundry Wi-Fi answer that invented a "when prompted" interaction
step, the instruction now explicitly says not to infer a login screen, prompt,
or sequence of access steps from evidence that only names a credential or
access method. `ConciergeGroundingInstructionTests.SystemInstructionsProhibitUnstatedOperationalSteps`
guards that prompt contract.
`run.json` records the exact prompt, provenance, message layout, and options;
`answer.prompt` is a readable evidence/question record, not a single user message
sent to the model. Each case is independent, without a continuity summary or
prior turns. This evaluates retrieval plus prompt/model generation, **not** the
entire stay workflow, Deepgram, speech, or end-to-end voice latency.

Three fresh sequential Foundry `gpt-5-mini` / `low` runs against that exact
production prompt were completed on 2026-10-14. All 45 answers were manually
reviewed against expected claims and actual retrieved passages:

| Run/report | Support | Fallback | Combined recall | Mean / median / max answer seconds |
| --- | --- | --- | --- | --- |
| [production-1](results/2026-10-14-foundry-gpt-5-mini-low-production-1/results.md) | 10/12 | 3/3 | 14/14 | 4.0152 / 2.5515 / 14.8863 |
| [production-2](results/2026-10-14-foundry-gpt-5-mini-low-production-2/results.md) | 12/12 | 3/3 | 14/14 | 3.8213 / 3.0039 / 8.3755 |
| [production-3](results/2026-10-14-foundry-gpt-5-mini-low-production-3/results.md) | 11/12 | 3/3 | 14/14 | 2.8110 / 2.2550 / 7.3358 |
| Aggregate (45 answers) | 33/36 | 9/9 | 42/42 | 3.5492 / 2.6225 / 14.8863 |

Only run 2 passes all per-run gates. Failures: run 1 invents a Wi-Fi prompt
step and a front-desk food-delivery request route; run 3 conservatively fails
for broadening a late-checkout request requirement to other checkout changes.
PARA-003 answers the paraphrase in all runs, but passes support in only 2/3.
There were no answer-based retries or extra tuning runs. Historical fixture
latencies are not controlled comparisons because prompts/layout/caps differ;
no cold-load claim is established. These results do not lock Foundry as the
production default. Raw results and reports are local artifacts under the
repository's ignored results directory.

### Staff-contact grounding runs — initial prompt (2026-10-15)

Six sequential production-prompt runs were manually graded against package
SHA-256
`ed607471baa1fe24cfc909defb7cbee4b3b6138bbbf3ca0076d329e6a42672d5`.
All used reasoning effort `low`, verified the same corpus snapshot (7 files,
13 sections/chunks), and retrieved 15/15 expected evidence keys:

| Run/report | Answer support | No-evidence | Recall@5 | Mean / median / max answer seconds |
| --- | ---: | ---: | ---: | ---: |
| [Ollama 1](results/2026-10-15-ollama-low-kb-staff-final-1/results.md) | 13/13 | 3/3 | 15/15 | 5.01 / 4.05 / 21.16 |
| [Ollama 2](results/2026-10-15-ollama-low-kb-staff-final-2/results.md) | 13/13 | 3/3 | 15/15 | 4.23 / 4.43 / 11.67 |
| [Ollama 3](results/2026-10-15-ollama-low-kb-staff-final-3/results.md) | 13/13 | 3/3 | 15/15 | 3.56 / 3.22 / 5.79 |
| **Ollama aggregate (48 calls)** | **39/39** | **9/9** | **45/45** | **4.27 / 4.01 / 21.16** |
| [Foundry `gpt-5-mini` 1](results/2026-10-15-foundry-gpt-5-mini-low-kb-staff-final-1/results.md) | 12/13 | 3/3 | 15/15 | 4.13 / 3.30 / 8.81 |
| [Foundry `gpt-5-mini` 2](results/2026-10-15-foundry-gpt-5-mini-low-kb-staff-final-2/results.md) | 13/13 | 3/3 | 15/15 | 4.06 / 2.60 / 15.18 |
| [Foundry `gpt-5-mini` 3](results/2026-10-15-foundry-gpt-5-mini-low-kb-staff-final-3/results.md) | 13/13 | 3/3 | 15/15 | 2.78 / 2.67 / 4.04 |
| **Foundry aggregate (48 calls)** | **38/39** | **9/9** | **45/45** | **3.66 / 2.75 / 15.18** |

Five of six runs pass all gates. Foundry run 1 fails ZGY-EXACT-004 because
"enter your room number when prompted for access" adds a Wi-Fi interaction step
not present in the retrieved evidence. Generic referrals to the front desk or
hotel staff pass; an unlisted staff role remains an invented fact and fails.
The user-approved omission of not-yet-implemented concierge capabilities does
not fail answer support, but a model may not claim it can perform those
actions. These are model-call measurements, not end-to-end voice latency.

### Wi-Fi procedural-step prompt follow-up (2026-10-15)

After adding the explicit login-screen/prompt/sequence instruction above, three
fresh sequential runs per provider were manually reviewed. All six recorded
package SHA-256
`ed607471baa1fe24cfc909defb7cbee4b3b6138bbbf3ca0076d329e6a42672d5`, used
reasoning effort `low`, verified the 7-file corpus snapshot and retrieved all
15 expected evidence keys:

| Run/report | Answer support | No-evidence | Recall@5 | Mean / median / max answer seconds |
| --- | ---: | ---: | ---: | ---: |
| [Ollama 1](results/2026-10-15-ollama-low-access-guard-1/results.md) | 13/13 | 3/3 | 15/15 | 4.71 / 3.79 / 20.52 |
| [Ollama 2](results/2026-10-15-ollama-low-access-guard-2/results.md) | 13/13 | 3/3 | 15/15 | 4.53 / 3.90 / 11.25 |
| [Ollama 3](results/2026-10-15-ollama-low-access-guard-3/results.md) | 13/13 | 3/3 | 15/15 | 5.66 / 5.40 / 10.29 |
| **Ollama aggregate (48 calls)** | **39/39** | **9/9** | **45/45** | **4.97 / 4.18 / 20.52** |
| [Foundry `gpt-5-mini` 1](results/2026-10-15-foundry-gpt-5-mini-low-access-guard-1/results.md) | 13/13 | 3/3 | 15/15 | 3.33 / 2.74 / 11.33 |
| [Foundry `gpt-5-mini` 2 retry](results/2026-10-15-foundry-gpt-5-mini-low-access-guard-2-retry/results.md) | 13/13 | 3/3 | 15/15 | 2.25 / 2.10 / 3.25 |
| [Foundry `gpt-5-mini` 3](results/2026-10-15-foundry-gpt-5-mini-low-access-guard-3/results.md) | 13/13 | 3/3 | 15/15 | 10.58 / 3.48 / 68.64 |
| **Foundry aggregate (48 calls)** | **39/39** | **9/9** | **45/45** | **5.39 / 2.55 / 68.64** |

All six follow-up runs pass every gate. None adds the unsupported "when
prompted" Wi-Fi step; every answer either states only that guests use their
room number to access Wi-Fi or refers them to hotel staff for other details.
The first Foundry run-2 attempt exited 134 during its third answer after a
canceled TLS request. It did not save a result; the full run was repeated once
under a new run ID and completed successfully. Foundry run 3 had a 68.64-second
maximum answer call. Latency is model-call latency, not voice latency.

## Contract summary

- The package evaluates retrieval evidence and grounded-answer behavior. It
  does not evaluate provider configuration or represent measured results.
- Retrieval uses the final deduplicated source-section evidence-key list.
  Exact lookup results keep their source and section identity.
- An answerable case passes only when the response answers every part of the
  question and covers the expected claims, every hotel fact it states is
  entailed by a passage actually retrieved for that case (any retrieved
  passage, not only the expected evidence keys), and it adds no invented or
  contradicting hotel fact. A generic referral such as "please contact the
  front desk" is conversation, not a hotel fact, and does not fail support
  unless it states or contradicts a specific unretrieved fact (hours, phone
  number, cost, procedure). Naming a staff role or department that no
  retrieved passage establishes (for example, "parking attendants") is an
  invented fact; generic "hotel staff" is not. Expected evidence keys measure Recall@5 only.
- No-evidence cases require a staff fallback rather than an unsupported
  answer. The same staff-role rule applies to the fallback referral.
- The corpus snapshot hashes gate every run, and each `run.json` records the
  package SHA-256.
