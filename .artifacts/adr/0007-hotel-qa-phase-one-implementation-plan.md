# ADR 0007: Hotel Q&A Phase One Implementation Plan

- **Status:** ACCEPTED; Phase 1 is complete for the current demo scope (accepted by the project owner on 2026-10-18). Steps 1–8 are complete; their implementation, evaluation evidence, and remaining scope boundaries are recorded below. The live 2026-10-19 UTC session verified that a browser-rendered barge-in marks the corresponding Temporal transcript turn. Earlier real-microphone prototype results passed the interruption-latency target; the full 20-interruption Chrome acceptance run was not repeated for transcript marking and is not required for this demo milestone. The latest 2026-10-15 Step 5 follow-up passed combined Recall@5 15/15, answer support 13/13, and no-evidence 3/3 in all three runs for both providers; see §6. The original 11/12 review finding and dissent remain documented, and historical run metadata/hash limitations are noted below. Step 8's production Think handler, dedicated HTTPS listener, live tunnel forwarding, terminal pending-handler isolation, and public URL injection are implemented and covered by Development/Production regressions. Foundry `gpt-5-mini` chat and Foundry `text-embedding-3-large` embeddings passed the evaluation runner (2026-10-18; see §3), and Development now uses Foundry embeddings. Per the project owner (2026-10-18), full-stay evaluation is out of scope for this demo.
- **Date:** 2026-10-08
- **Builds on:** [ADR 0004](0004-concierge-platform-direction.md), [ADR 0005](0005-stay-memory-and-transcript-persistence.md), and [ADR 0006](0006-workflow-server-scaffold.md)
- **Scope:** Hotel ZGY knowledge-base Q&A through web chat and browser voice. ACS telephone voice, live weather/place/travel information, and simulated reservations, ticketing, and rides follow this phase.

## Goal and sequencing

Deliver one locally runnable .NET demo in which a fictional, eligible guest verifies a stay, asks Hotel ZGY questions by text and then browser voice, receives answers grounded in editable Markdown knowledge, and resumes the same durable stay conversation. Each step below is a reviewable vertical increment. “Completion” means its checks have been observed against the running dependencies, not merely that code compiles.

The accepted architecture is in ADRs 0004–0006. This plan records implementation decisions and evidence as each step completes; validation boundaries remain explicit. Steps 3–4 selected local models and a 768-dimensional vector schema and added a candidate evaluation package. Section 3 records the subsequent Foundry evaluation-runner results; full-stay Foundry evaluation remains out of scope for this demo.

**Decision precedence.** The active first-phase choices are MEAI/`TemporalCommunity.Extensions.AI`, with MAF deferred by ADR 0005; Web chat through the concierge API, without an ACS Chat SDK requirement; browser voice through Deepgram Voice Agent, with ACS Call Automation reserved for later telephone voice; and no `Temporalio.Contrib.WorkflowStreams` dependency. The [active project laws](../../.clans/laws.md) were reconciled with these decisions on 2026-10-08. Superseded proposals for MAF, ACS Chat, ACS-native speech, and WorkflowStreams have been removed from the ADR set. The Deepgram/MAF experiments are protocol evidence, not a decision to use MAF in the concierge.

| Step | Gate to the next step | Current state |
| --- | --- | --- |
| 1. WorkflowServer scaffold | Host builds; Aspire startup, both health routes, and sample workflow are checked live | Complete; runtime gate passed |
| 2. Temporal/PostgreSQL and baseline schema | Real connections, dependency readiness, and repeatable baseline schema setup | Complete; independently validated runtime, schema, and recovery gate passed. Current startup uses one repeatable schema script and a schema-version guard |
| 3. Model providers | Independently configurable chat/embedding providers; validated local chat and embedding calls; compatible vector schema | Implementation and local Ollama gate complete. Foundry chat and `text-embedding-3-large` embeddings passed the evaluation runner (2026-10-18). Hotel ZGY evaluation package approved 2026-10-09 with a hashed corpus snapshot |
| 4. Startup setup and ingestion | Repeatable seed and Markdown ingestion produce searchable data | Complete; setup integration and live idempotent re-run independently verified. Clean-ingest behavior is covered by tests, not claimed from the persisted-state run |
| 5. Retrieval | Exact and hybrid paths retrieve grounded chunks with internal provenance | Implementation present in `HotelKnowledgeActivities`, invoked as a Temporal Activity and registered in WorkflowServer. Current `results.md` reports the corrected answer-support claim as 12/12 and gates passing. The original 11/12 finding and dissent remain recorded; see §5 for run metadata and package-hash caveats |
| 6. Verification and durable Q&A | Eligible stays alone can run and resume bounded Temporal conversations | Complete; implementation passes 95/95 tests and the solution builds cleanly. The context/token budget qualifications are in §6 |
| 7. Web chat | A guest can verify, converse, inspect full history, end, and resume | Accepted 2026-10-11 |
| 8. Browser voice | The same stay workflow serves Deepgram voice with interruption behavior | Complete for current demo scope (2026-10-18); implementation/tests pass and live transcript marking verified 2026-10-19 |

## 1. WorkflowServer scaffold, Aspire registration, health endpoints

**Deliverable.** Keep `Concierge.WorkflowServer` as a separate ASP.NET Core Temporal worker/API host. It references ServiceDefaults, maps `/health` and `/health/ready`, retains the template sample workflow/activity, and appears as `workflow-server` with an HTTP endpoint and health probe in AppHost. AppHost supplies the Temporal reference and waits for Temporal so the sample can run from Aspire; step 2 subsequently added PostgreSQL and dependency-aware readiness. Preserve the connection resolver's environment/profile precedence. Remove or replace the sample workflow/activity when the first application workflow makes them unnecessary.

**Completion criteria.** Complete. In an independently verified isolated Aspire run, AppHost injected a randomized `TEMPORAL_ADDRESS` and namespace into WorkflowServer and waited for Temporal; the worker did not use the `localhost:7233` fallback. `/health` and `/health/ready` both returned HTTP 200. A fresh `SampleWorkflow` executed through `Concierge.WorkflowServer-tq`, ran the `Greet` Activity, and returned `Hello, Aspire!`. Aspire was then stopped cleanly. At this gate, readiness proved only the registered self check; step 2 subsequently added the PostgreSQL wiring and real Temporal/PostgreSQL readiness probes.

**Outstanding decisions.** Decide when to retire the template sample and whether the default `Concierge.WorkflowServer-tq` task-queue name remains the demo name.

## 2. Connect Temporal/PostgreSQL, add readiness checks, define baseline schema

**Deliverable.** Extend the step 1 Aspire connection wiring with the `concierge` PostgreSQL connection and pass only required API configuration to Web. Keep connection settings and secrets out of source. Register Temporal and PostgreSQL clients in their owning processes. Add bounded dependency checks to WorkflowServer readiness that make a real Temporal request and a PostgreSQL query; keep `/health` independent of transient dependency failures. WorkflowServer is the sole schema owner and runs the single repeatable `Data/schema.sql` script before setup. A schema-version guard (current version 1) stops startup with reset guidance if an existing application schema is unversioned or has a version mismatch; there is no numbered-migration adoption path. Define the relational schema for fictional guests, unique stays, exact hotel facts, knowledge sources/chunks, ingestion versions, and per-stay transcript turns, with UTC instants, stable identifiers, uniqueness constraints, source metadata, and lookup/order/full-text indexes. Keep the Temporal development server's storage separate from the application database.

**Completion criteria.** Complete and independently validated. AppHost injects the randomized Temporal configuration and the `concierge` PostgreSQL connection into WorkflowServer; Web receives only WorkflowServer service discovery. Runtime and integration tests share the compile-time `pgvector/pgvector:0.8.1-pg18` definition, and the validation run observed PostgreSQL 18.2 with vector 0.8.1. WorkflowServer uses direct Npgsql 10.0.3 and is the sole schema owner. The current lightweight Npgsql schema runner executes the one repeatable script in a transaction while holding a PostgreSQL advisory lock, then checks the schema-version guard before setup; it does not use numbered migrations or migration checksums.

The step 2 baseline schema enabled pgvector and defined guests, stays, hotel facts, knowledge sources and chunks, ingestion versions, and transcript turns. It included UTC timestamps, relational and domain constraints, a generated full-text search vector with a GIN index, and the required lookup/order indexes. At that time, the baseline had no vector column and step 3 used a separate 768-dimensional vector migration. A fresh Aspire run completed a sample Temporal workflow and application-database query. Independently induced Temporal and PostgreSQL outages each made `/health/ready` return HTTP 503 while `/health` remained HTTP 200; readiness recovered after each dependency returned. Real PostgreSQL integration tests at that gate covered first and repeated migration, concurrent startup, and checksum mismatch. The step 2 gate had a clean build and all 19 tests passed, including eight MEAI tests and six real migration tests. This is historical validation of the then-current migration implementation, not a description of today's schema startup mechanism. Seed and ingestion idempotency were not step 2 deliverables; step 4 evidence is recorded below.

**Selected and outstanding decisions.** Direct Npgsql and WorkflowServer as sole schema owner remain selected. The current runner is a lightweight Npgsql schema-script runner with transaction and advisory lock plus a schema-version guard, not a checksummed numbered-migration framework. The implemented dependency probes are bounded to five seconds. Authentication configuration outside local Aspire remains open. Setup readiness gating and provider selection were subsequently implemented and validated in steps 3–4 below. If an old unversioned application schema is found, startup now stops and provides reset guidance rather than adopting it. Reset was approved for the `concierge-postgres-data` volume, but that volume did not exist in Docker, so no volume or data was deleted. The next Aspire launch is expected to create a fresh database and setup should seed it. The schema runner remains independent of setup readiness so the worker can start and run setup after schema initialization completes.

## 3. Configure Foundry/Ollama independently for chat and embeddings

**Decision and implementation.** Chat and embeddings have independent provider options and MEAI registrations through `TemporalCommunity.Extensions.AI`; Foundry is the provider default and Ollama the alternative for each role. `appsettings.Development.json` selects Ollama chat `qwen3.6:latest` and embeddings `embeddinggemma:latest` at 768 dimensions. AppHost passes its selected environment name to both `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` for WorkflowServer and Web; the handoff is environment-specific and is not hard-coded to Development. The verified local run used the Development profile. The selected vector dimension is 768 and the current single schema script defines the schema, including the vector column. Provider/model identity is persisted and checked so setup refuses incompatible indexed vectors rather than silently mixing embedding configurations. Foundry provider configuration and validation exist. Live Foundry `gpt-5-mini` chat requests were verified on 2026-10-14 through the evaluation runner using API-key auth; the shared Responses client omits `temperature` for Foundry because `gpt-5-mini` rejects it. Development defaults to reasoning effort `low` for both providers.

**Completion evidence and gate.** The Ollama chat and embedding calls were validated against local Ollama. Direct `dotnet test Concierge.slnx --no-restore`, after the final code and AppHost edits, passed 50/50 tests. The package is `tests/Concierge.Evaluation/`: approved on 2026-10-09 with 7 approved sources, 12 active answerable cases (14 expected evidence keys), 3 active no-evidence cases, and a hashed corpus snapshot. On 2026-10-14 it was extended to 13 answerable cases (15 evidence keys); see §6. Its .NET validator and all 24 negative tests pass. Comparative results are recorded under step 5.

**Foundry embeddings (2026-10-18).** One evaluation run with Foundry `gpt-5-mini` / `low` chat and Foundry `text-embedding-3-large` embeddings, reduced to 768 dimensions through the API `dimensions` parameter (no schema change), passed every gate: combined Recall@5 19/19 (vector-only 19/19, lexical 8/19), answer support 17/17, and no-evidence 5/5. All 22 answers were manually reviewed against the corpus. These results match the earlier Ollama `embeddinggemma-2:740m` baseline. Report: `tests/Concierge.Evaluation/results/2026-10-18-foundry-gpt-5-mini-low-te3-large-1/results.md`. AppHost Development user-secrets now select Foundry `text-embedding-3-large` for embeddings. The `concierge-postgres-data` volume was reset; startup setup re-embedded all 16 chunks (8 sources) at 768 dimensions with the new model identity.

**Scope decision (2026-10-18, project owner).** This is a demo; Foundry will not be evaluated through a full guest stay, which could span days. Evaluation-runner results plus the live web/voice sessions already recorded under steps 7–8 are sufficient. Other provider/model combinations remain unvalidated.

## 4. Startup Temporal workflow for setup, fictional stays, Markdown ingestion

**Decision and implementation.** WorkflowServer starts the stable-ID `concierge-hotel-zgy-setup-v1` workflow. Activities own Markdown discovery/parsing, chunking, embedding, and database upserts as well as fictional guest/stay setup. The editable Hotel ZGY sources are in `knowledge/hotel-zgy/`; PostgreSQL stores their indexed representation. Setup uses a unique per-process generation marker to distinguish a completed prior run from the current startup, skips unchanged sections, re-embeds changed content, deletes removed source registry rows and their indexed data, records embedding-model identity, and refuses incompatible model identity. Seed overwrite is disabled by default. Overwrite updates only mutable seed fields: a persisted stay's `stay_id`/`external_stay_key` pair is immutable, and any mismatch fails setup with a non-retryable `StayIdentityMismatch` error. The setup readiness check keeps Q&A unavailable until setup has completed. Schema definition and initialization belong to the single repeatable `Data/schema.sql` runner described in §2; there is no separate numbered vector migration.

The step 4 source content is in `knowledge/hotel-zgy/`: `property-overview.md`,
`amenities.md`, `amenity-hours.md`, `connectivity.md`, `parking.md`,
`policies.md`, and `services.md`, carrying the facts approved on 2026-10-09.
The `tests/Concierge.Evaluation/` package records expected
claims for that content, a SHA-256 snapshot of every file, and the confirmed
absence of answers for its 3 no-evidence cases.

**Completion evidence and gate.** Integration tests use PostgreSQL and Temporal; the final direct `dotnet test Concierge.slnx --no-restore` run passed 50/50 tests. Setup tests cover initial embedding and retry behavior. After the final code/AppHost edits, an Aspire run with the exact AppHost and its `https` launch profile (using the Development environment) reached healthy WorkflowServer and Web. WorkflowServer logs recorded setup workflow completion and the setup readiness check reported healthy. The reported outcome was 5 Markdown sources, 7 sections, and 7 chunks, using Ollama `embeddinggemma:latest` at 768 dimensions. This was an idempotent re-run against persisted state and embedded **0 sections on that run**; it is evidence of the persisted-state path, not a clean initial ingest. The tests, not that live re-run, cover initial embedding and retries. Runtime AppHost and local Ollama were stopped after verification.

**Implementation details and remaining boundary.** Ares review found and prompted fixes for stale-joined workflow-generation handling and moved-source fact-locator handling; regression tests cover both corrections. The workflow now uses activity-owned Markdown discovery/parsing, deletes removed source registry rows and their indexed data, skips unchanged sections, re-embeds changed content, gates on model identity, and keeps seed overwrite disabled by default. Setup completion is surfaced through the Temporal workflow result and the `/health/ready` setup check, which Aspire's `WaitFor(workflowServer)` uses to gate Web. These results verify setup and ingestion only; they do not verify retrieval quality, comparative retrieval, or all live model combinations. Step 5 retrieval implementation and evaluation record are recorded below.

## 5. Hotel retrieval: exact facts, full-text/vector search, RRF

**Deliverable.** Implement an exact structured-fact lookup for deterministic hotel facts, then a fallback over eligible knowledge chunks using PostgreSQL full-text search and pgvector. Rank each arm, fuse by stable chunk ID with one-based Reciprocal Rank Fusion (initial `k = 60` per the existing research), and bound the passages passed to chat. Keep source IDs, titles, section anchors, and version/review metadata in internal diagnostics and evaluation records; guest replies need no source titles. Start with exact vector search; add HNSW or reranking only if Hotel ZGY measurements warrant it. Hotel facts come from current knowledge retrieval, not from a stay summary or an unsupported model guess.

**Implementation progress and acceptance gate.** `HotelKnowledgeActivities` implements exact section-keyed fact lookup and fallback PostgreSQL full-text plus exact pgvector retrieval, fused with one-based RRF (`k = 60`) and bounded to five results; internal provenance is retained. The Temporal Activity is registered in WorkflowServer. No user-facing endpoint was added. On 2026-10-09 the evaluation package was approved with a SHA-256 snapshot of all seven `knowledge/hotel-zgy/` files before comparison. The evaluation-only runner (`tests/Concierge.Evaluation/runner/`; the only production change is `internal` accessibility on the two single-arm search methods plus `InternalsVisibleTo`) ingested that corpus with `embeddinggemma` through the product setup workflow and recorded historical results under `tests/Concierge.Evaluation/results/`. Recall@5 over 14 expected evidence keys: combined 14/14 (gate 13/14); lexical-only 6/14 and vector-only 14/14 are comparison numbers. Answers were generated from the combined result with `qwen3.6` and an evaluation-only grounding prompt (not the Step 6 contract). No-evidence passed 3/3. The original strict rule (every claim entailed by *expected* evidence) yielded the reviewer's 11/12 finding in the later run; the earlier run recorded 10/12. The checkout answers included a true late-checkout claim from a retrieved passage outside their expected keys. The 2026-10-09 answer-support rule instead allows entailment from any passage actually retrieved for the case, uses expected keys for Recall@5 only, and gates only the combined arm. The expected claim for EXACT-001 was corrected under this review; the current report at `tests/Concierge.Evaluation/results/2026-10-09-checkout-fix-rerun-1722/results.md` records answer support **12/12** and all gates passing. The correction was a review/regrade, not a new runner execution: the saved `run.json` retains the old claim, and a SHA-256 mismatch means the checked-in package bytes cannot be proven identical to the bytes used at run time. The original review finding and dissent remain documented; human confirmation is not listed as a pending gate. The concierge need not tell a guest that the address is fictional; that narrative belongs in the README/live demo. Historical run limitations (before the lookup correction below): the 12-chunk corpus makes Recall@5 a weak discriminator; `websearch_to_tsquery` requires all query terms, so the lexical arm returned no candidates for 9 of 15 cases and fusion showed no measured gain over vector-only; one tied ranking is ordered by chunk GUID; the exact-fact path fired for 4 cases, and "What time is checkout?" fell through to hybrid search because checkout and late-checkout facts both matched.

**Checkout lookup correction (2026-10-09).** The exact Activity now distinguishes case-insensitive, word-boundary `late checkout` / `late-checkout` phrases from `checkout` occurrences outside those phrases. When both occur, it falls back to hybrid rather than treating the late-checkout record as the whole request. Otherwise it excludes only the unrequested sibling section identity (`checkout` or `late-checkout`) before counting full-text fact matches; unrelated matching facts remain eligible and multiple matches still force hybrid. This is a narrow section/query disambiguation, not an expected-evidence-key rule, score threshold, or synonym router. PostgreSQL/Temporal regression fixtures now ingest both policy sections. Focused `HotelRetrievalTests` with `--no-restore` passed 15/15, covering plain checkout and explicit late checkout without query embedding, combined requests retaining both sections, broad checkout/gym requests, and the existing ambiguous gym and paraphrase paths. The correction did not change the evaluation package or knowledge corpus. The original run's raw output is preserved in its results folder.

**Checkout-correction run and review record (2026-10-09).** The checkout lookup correction was exercised in the historical run recorded at `tests/Concierge.Evaluation/results/2026-10-09-checkout-fix-rerun-1722/`; it returned only `zgy-policies#checkout` for "What time is checkout?" and retained the late-checkout passage for ZGY-PARA-001. The saved run records the then-applied strict-rule score of 11/12. No runner was executed again for the later answer-support correction. The current `results.md` reports 12/12 and passing gates under the rule described above, while `run.json` retains the old score/claim. Its package hash does not match the current package bytes, so those bytes cannot be established as identical to the run-time package. Preserve the original 11/12 reviewer finding and dissent alongside the corrected result. **Step 5's evaluation gate is reported as met under the current rule**; this is a corrected evaluation record, not a new run or proof that the saved run used identical package bytes.

**Completion criteria.** The versioned exact-term, paraphrase, multi-source, and no-evidence questions exercise the intended path. Compare lexical-only, vector-only, and fused results; report Recall@K with the chosen K and answer support/no-evidence pass rate against the thresholds selected in step 3. No-evidence questions lead to a clear “I don't know; contact hotel staff” response. Diagnostics identify the chunks used without exposing them in the guest UI. Changing source content is reflected after ingestion; mismatched vector models are rejected. Query plans and latency are checked against the actual demo corpus before adding an approximate index.

**Outstanding decisions.** The final passage limit is five; candidate-pool size and any latency target remain to be selected from observed needs. The answer-support rule is set in the evaluation package (2026-10-09). Evaluation questions, labels, and thresholds are prerequisites from step 3, not decisions deferred until retrieval acceptance. RRF ranks are not calibrated probabilities, so do not use an arbitrary fused-score threshold as evidence of answerability. Decide whether a later reranker is warranted from measured errors.

## 6. Guest verification and durable Q&A

**Decisions and implementation.** Stay eligibility spans the local Eastern check-in date through the end of the local checkout day, with no grace period. This eligibility decision supersedes ADR 0005's prior configurable checkout-grace rule; it does not supersede ADR 0005's transcript batching policy. Verification binds a per-session authorization token to its stay; the token is hashed in the database and remains bound server-side. Each stay has one Temporal workflow. Turns are ordered and idempotent, and text and voice share stay context. Transcript turns flush when a configurable batch size is reached (5 completed turns by default) or a configurable interval expires (30 seconds by default), whichever comes first, and at session end. The batch-size trigger was added 2026-10-12 to meet ADR 0005 §5; it is patch-gated so stays started earlier replay unchanged. The new patch-gated memory path retains complete raw exchanges by estimated size rather than a six-turn cap, with up to 3,904 tokens carried for raw history. Answer-specific context selection includes only complete newest-first pairs; when pressure prunes history, older complete pairs are removed before state is carried forward. Guest-reported continuity has transcript-sequence provenance; routine model-summary calls are removed. Native extraction is intentionally conservative and limited to explicit first-person allergy, preference, decision, and direct-request patterns. A correction supersedes only a matching normalized topic; distinct or ambiguous preferences remain active. A request remains unresolved because no hotel-action confirmation flow exists. Questions and assistant answers are not memory sources. The old summary path remains solely for replay compatibility; legacy summary content is not trusted as memory.

The answer working-context budget is 8,000 estimated tokens including the 1,024-token output cap and 512-token headroom. Message text is estimated using UTF-8 byte counts plus a 1,536-token fixed allowance; retrieved evidence has a 2,048-token soft cap and is admitted as complete passages within the remaining budget, followed by newest complete recent exchanges. Active continuity is never trimmed to make room; a memory-bearing turn fails closed if its 1,024-token/32-item memory cap would be exceeded. This is not an exact or tokenizer-independent guarantee. `AI:Chat:ContextWindowTokens` remains a model-window validation setting (default 32,768) and is not the answer working budget. Ollama requests continue to use the configured `num_ctx`; Foundry has no per-request `num_ctx` setting. Guest-facing failures are generic; diagnostic details stay in server-side logs.

**Deliverable and completion evidence.** The verification, transcript, and Q&A behavior remains as above. The stay-memory update is patch-gated, carries state through Continue-As-New, adds no model call, and is covered by focused persistence, correction, question/assistant isolation, unresolved-request, budget, and replay tests. Exact recall of older transcript turns in answer generation remains deferred: PostgreSQL stores and displays the full transcript but no answer-path keyword search is implemented. Legacy runs that evicted an important source turn before adopting structured memory cannot recover it from a prior summary.

**Grounding revision (2026-10-14).** After measured paraphrase refusals and
invented procedures, the production instruction now explicitly recognizes the
same evidenced service/facility in other words, excludes merely related topics,
prohibits invented facts/procedures/policies, and answers supported parts while
referring unsupported parts to staff without inventing a service-access route.
At that time, only current evidence was hotel authority; the then-existing
summary and prior conversational turns provided continuity, not hotel facts.
The 2026-10-15 stay-memory update replaced routine summaries in the new path
with explicit guest-reported stay continuity; current hotel facts still come
from current evidence. Replies remain concise (at most three sentences).
The evaluation runner directly reuses the internal production constant through
the existing friend assembly, with production message layout and answer cap;
it no longer uses the independent fixture. This is prompt/model evaluation
without stay history or an end-to-end voice claim; it does not select a new
production model default.

Three fresh sequential Foundry `gpt-5-mini` / `low` production-prompt runs
completed: support **10/12, 12/12, 11/12**, fallback **3/3 each**, combined
recall **14/14 each**. All 45 answers were manually reviewed; residual invented
Wi-Fi/ordering procedures and an overbroad checkout-change requirement mean
the gates are not repeatably passing. Full per-case review and aggregate
generation timings are in the
[third-run report](../../tests/Concierge.Evaluation/results/2026-10-14-foundry-gpt-5-mini-low-production-3/results.md),
which links the first two. This supplements the historical Foundry live-call
limitation below with bounded retrieval/generation evidence, not full stay or
voice validation. No production provider default is changed.

**Knowledge-base and grading revision (2026-10-14/15).** Residual failures
came from both models inventing staff-contact and ordering routes the corpus
did not contain. Cecil approved new `zgy-services` facts: the front desk is
staffed 24 hours and reached by dialing 0 from the room phone, and room
service can be ordered by calling the front desk. Case ZGY-PARA-004 ("How do I
get hold of someone at the hotel if I need help?") was added; gates are now
Recall@5 ≥ 14/15, answer support 13/13, no-evidence 3/3, with the corpus
snapshot recaptured. Cecil also set these grading rules, recorded in the
package's `answerSupportRules`:

- A generic referral ("please contact the front desk", "ask hotel staff") is
  conversation, not a hotel fact, and does not fail support unless it states
  or contradicts a specific unretrieved fact (hours, phone number, cost,
  procedure).
- Naming a staff role, department, or service no retrieved passage establishes
  (for example, "parking attendants") is an invented fact and fails, in both
  answerable and no-evidence cases.
- Concierge limitations ("cannot place an order", "cannot change checkout")
  describe features not yet implemented, not hotel facts. They were removed
  from expected claims; omitting them passes, but claiming the concierge can
  perform the action still fails.

Under the package before the staff-role rule (SHA `8cf1bf63…`), six fresh
sequential runs passed every gate as originally graded: Ollama and Foundry
`gpt-5-mini` / `low` each scored 39/39 support, 9/9 no-evidence, and 45/45
Recall@5. Applying the staff-role rule to those answers fails one Ollama
no-evidence answer ("ask the hotel front desk or parking attendants").

**Staff-contact baseline runs (2026-10-15; package SHA
`ed607471baa1fe24cfc909defb7cbee4b3b6138bbbf3ca0076d329e6a42672d5`).** All
six full runs completed with low reasoning effort; all `run.json` files
record the current package hash, and every corpus snapshot verified. Results
were manually graded under the rules above:

| Provider / run | Answer support | No-evidence | Recall@5 | Latency mean / median / max (s) |
| --- | ---: | ---: | ---: | ---: |
| Ollama 1 | 13/13 | 3/3 | 15/15 | 5.01 / 4.05 / 21.16 |
| Ollama 2 | 13/13 | 3/3 | 15/15 | 4.23 / 4.43 / 11.67 |
| Ollama 3 | 13/13 | 3/3 | 15/15 | 3.56 / 3.22 / 5.79 |
| **Ollama aggregate (48 calls)** | **39/39** | **9/9** | **45/45** | **4.27 / 4.01 / 21.16** |
| Foundry `gpt-5-mini` 1 | 12/13 | 3/3 | 15/15 | 4.13 / 3.30 / 8.81 |
| Foundry `gpt-5-mini` 2 | 13/13 | 3/3 | 15/15 | 4.06 / 2.60 / 15.18 |
| Foundry `gpt-5-mini` 3 | 13/13 | 3/3 | 15/15 | 2.78 / 2.67 / 4.04 |
| **Foundry aggregate (48 calls)** | **38/39** | **9/9** | **45/45** | **3.66 / 2.75 / 15.18** |

Five of six individual runs pass all gates. Foundry run 1 fails ZGY-EXACT-004:
the answer says to "enter your room number when prompted for access", but the
retrieved passage only says guests use their room number to access Wi-Fi; it
does not establish that a prompt or this interaction step exists. Generic
front-desk/staff referrals pass, and none of these runs names an unlisted staff
role. Ollama's maximum call was 21.16 s; Foundry's was 15.18 s. These are
model-call measurements, not end-to-end voice latency. Per-run manual reviews
are recorded with raw results under `tests/Concierge.Evaluation/results/`
(gitignored).

**Wi-Fi prompt follow-up (2026-10-15; package SHA unchanged).** The production
instruction now explicitly prohibits inferring a login screen, prompt, or
sequence of access steps from evidence that only names a credential or access
method. `ConciergeGroundingInstructionTests.SystemInstructionsProhibitUnstatedOperationalSteps`
asserts this contract. Six fresh sequential runs used the updated production
prompt, the same package hash, and `low` reasoning:

| Provider / run | Answer support | No-evidence | Recall@5 | Latency mean / median / max (s) |
| --- | ---: | ---: | ---: | ---: |
| Ollama 1 | 13/13 | 3/3 | 15/15 | 4.71 / 3.79 / 20.52 |
| Ollama 2 | 13/13 | 3/3 | 15/15 | 4.53 / 3.90 / 11.25 |
| Ollama 3 | 13/13 | 3/3 | 15/15 | 5.66 / 5.40 / 10.29 |
| **Ollama aggregate (48 calls)** | **39/39** | **9/9** | **45/45** | **4.97 / 4.18 / 20.52** |
| Foundry `gpt-5-mini` 1 | 13/13 | 3/3 | 15/15 | 3.33 / 2.74 / 11.33 |
| Foundry `gpt-5-mini` 2 (retry run) | 13/13 | 3/3 | 15/15 | 2.25 / 2.10 / 3.25 |
| Foundry `gpt-5-mini` 3 | 13/13 | 3/3 | 15/15 | 10.58 / 3.48 / 68.64 |
| **Foundry aggregate (48 calls)** | **39/39** | **9/9** | **45/45** | **5.39 / 2.55 / 68.64** |

All six runs pass every gate. None adds the unsupported "when prompted" login
step; the Wi-Fi answers use only the room-number access fact and generic staff
referrals. The first Foundry run-2 attempt exited 134 during its third
answer after a canceled TLS request, before saving a result. It was repeated
once under a new run ID and completed; this was not a Postgres/Testcontainers
failure. Foundry run 3 had a 68.64-second maximum call, so its high mean
reflects a latency outlier. Full per-case reviews and raw records are under
`tests/Concierge.Evaluation/results/2026-10-15-*-access-guard-*/` (gitignored).
These measurements still do not constitute full stay, web-chat, or voice
acceptance.

**Completion criteria.** Per-session authorization is stored as a hash and bound server-side to a stay; each stay has one workflow; turns are ordered/idempotent; text and voice share the stay workflow; transcript flush is triggered by a configurable batch size or interval and at session end; eligibility uses local Eastern check-in through the end of checkout day; stay continuity is explicit, bounded, source-linked, and carried through Continue-As-New; retrieval is limited to five passages of 1,600 characters each; guest input is limited to 2,000 UTF-8 bytes; answers are limited to 1,024 tokens; and answer prompt construction uses an 8,000-token estimated working budget. Ollama's `num_ctx` is explicitly set and covered by a fake-server test. Byte-count estimates do not establish exact cross-provider token counts, and Foundry does not receive a per-request context-window setting. Generic guest errors and server-side diagnostics are in place. Older exact conversational recall remains deferred.

**Outstanding decisions and boundaries.** Broader semantic memory extraction and exact older-transcript answer recall remain deferred; the current pattern recognizer intentionally supports only narrow explicit forms. The 8,000-token byte-count estimate is not an exact tokenizer guarantee. Foundry is validated through the evaluation runner and live demo sessions; full-stay evaluation is out of scope for this demo (§3). Do not infer a Foundry per-request context-window guarantee. Browser UX and voice integration remain in steps 7–8. The authorized identity mapping for ACS telephone voice remains part of the later telephone phase.

## 7. Web chat: verification, conversation, full history, resume/end

**Acceptance status (2026-10-11).** The team review found a Web/worker data-converter mismatch blocking live sign-in, cross-stay recovery and stale-history races, transcript-listener loss after cancelling End, premature end-of-session flushing, missing Web failure diagnostics, and incorrect invalid-identity wording. Fixes align the converters, bind recovery to the server-derived stay ID, invalidate stale session outcomes, reconnect transcript listeners, wait for admitted turns before the end flush, log sanitized failure causes, and return “Guest not found”. The build is clean; 119 .NET and 2 browser-script tests pass. A live Aspire run with Ollama then verified: sign-in with the real October 19 deadline; a grounded answer; same-ID retry without duplication; forged and cross-stay credential rejection; End waiting for an in-flight reply and persisting it; history surviving a full AppHost restart; refresh recovery persisting a pending turn once; reverification as another guest dropping the prior pending turn and draft while same-stay reverification keeps them; scroll tracking after cancelling End; and a 204-turn transcript rendering untruncated at the latest message in about 2.6 seconds. The stale-history race cannot be forced through the in-process Blazor call and is covered by component tests only. Keyboard and contrast checks were not repeated.

**Follow-up review (2026-10-11).** The original seven findings were confirmed addressed, but Step 7 remains unaccepted pending re-review of two additional P2 fixes. Failed End now leaves a pending reply with a visible same-ID Retry path after Keep chatting, without accepting stale callbacks or discarding the separate draft. Four component regression cases cover HTTP 503 and unexpected failures with replies completed or still in flight, including history reconciliation and exact-payload retry. Light-theme muted text is now `#656d7e`: dates and timestamps against the transcript background measure 4.79:1; dark theme remains 6.80:1. A browser-script regression checks the 4.5:1 minimum for both themes. All 16 session-component tests and 3 browser-script tests pass; these fixes have not been rechecked in a live browser. The reviewing team confirmed keyboard submission and visible focus and did not reproduce test flakiness; Docker contention is an unverified explanation for the earlier failures.

**Scope decision (2026-10-10).** Step 7 designs and implements text chat only. Defer all browser voice features and their UI to step 8, including microphone/start-voice controls, microphone permissions, voice status and stop controls, and voice verification. Do not add placeholder voice controls to the step-7 screens.

**C# review follow-up (2026-10-11).** Application-owned stay recovery logs and Web API fallback logs now share the service's safe exception-type-chain diagnostics: no raw exception objects, messages, stacks, or stay identifiers are logged. Integration coverage captures the actual summary, persistence, and API fallback failure paths. Session commands use validated Verify/Authenticate/Invalidate factories and immutable properties; deserialization enforces the same invariants while retaining the existing JSON shape. Valid legacy commands retain byte-identical converter payloads and execute/replay successfully; malformed historical commands intentionally fail decoding. The focused diagnostics and command suite passes 31 tests. Extracting an explicit chat-session state model with named transitions from the component is deferred to the first Step 8 task; it is not claimed completed by the current recovery fixes.

**Design decisions (2026-10-10, approved).**

- Keep the approved responsive Coastal Calm designs: light-purple guest bubbles, light-red error states, an empty-chat state, a full-width expired-session banner above the composer, and the approved sign-in and end-chat screens. Remove the redundant "View full history" link. Use a centered `MMM dd` date divider for each message day, based on `America/New_York`, without "Today" or timezone text.
- Provide System, Light, and Dark themes. Default to System; persist an explicit theme choice in `localStorage`. Implement both themes with semantic CSS variables and the same responsive layout. Use the proposed dark palette as a starting point and verify readable contrast.
- Display completed answers, not streamed tokens. Show one "Preparing a reply…" status while waiting. Keep the composer editable during a pending reply, but disable sending; preserve any draft until the pending turn resolves.
- Store the stay-bound bearer credential, pending turn ID/payload, and unsent draft in tab-scoped `sessionStorage`, never the guest's last name or room number. Reuse the credential and draft across refreshes in the same tab; require verification after tab closure or End chat. Clear the credential and draft on confirmed End chat; retain the server-side transcript. Ask for confirmation before ending only when a draft or reply is in progress.
- On refresh, load history and reconcile the pending turn. If its result is not in history, resubmit the exact original channel, turn ID, and message once. This joins an in-progress same-ID workflow update or returns its cached completed result. Do not create another turn ID, duplicate the transcript message, or retry automatically in a loop. On an ambiguous failure, manual Retry uses the same ID and exact payload; preserve a separate composer draft. A session-expired response uses the approved sign-in-again banner instead.
- Initially load the complete stay transcript without pagination, positioned at the latest messages. Do not silently truncate history. Auto-scroll new replies only when the guest is within 80px of the bottom; otherwise preserve their position and show a "New message ↓" control that jumps to the latest message. Sending a message also moves to the bottom. The persistent composer remains outside the independently scrolling transcript.

**Approved API ownership and dependency boundary (2026-10-10).** WorkflowServer exposes HTTP health checks only and remains the sole PostgreSQL, schema-migration, and database-setup owner. Web owns the guest API routes (`POST /sessions`, `POST /turns`, `GET /history`, `POST /end`) and invokes Temporal directly; it has no WorkflowServer HTTP or PostgreSQL dependency. Worker session activities perform verification, credential-digest lookup, and invalidation against PostgreSQL, while the durable stay workflow continues to own conversation updates. Web generates the random bearer and sends only its SHA-256 digest to the session activity; the raw bearer is returned only to the caller and is never a Temporal argument/result. Last name and room number are verification inputs recorded as Temporal activity input by this approved design; they are not encrypted by this architecture. The session activity returns only server-derived session/stay context. Ending still flushes the stay workflow and then invalidates the session; it does not end the stay workflow. Interactive Server components call the same in-process concierge service used by the route handlers, not loopback HTTP or the worker. AppHost may retain `WaitFor(workflowServer)` strictly for readiness ordering.

**Session operations run as Standalone Activities (decided 2026-10-12, approved).** Session Verify, Authenticate, and Invalidate no longer run inside a `Concierge.Session` wrapper workflow. Web starts the registered worker activities (`VerifyAsync`, `AuthenticateAsync`, `InvalidateAsync`) directly as Temporal Standalone Activities by type name, using a unique activity ID per request (`concierge-session-{sessionId}-{operation}-{requestGuid}`) and the default ID reuse/conflict policies. Timeouts and retries are unchanged: 20-second start-to-close, retry from 500 ms up to 2 s, at most 3 attempts. Each activity validates its input and rejects malformed requests with a non-retryable `ConciergeSessionInvalidRequest` failure; any session-activity failure is still returned to the guest as `unavailable` (503). Rationale: each operation is one activity with no orchestration, so a wrapper workflow only added latency and billable actions. No preview SDK features are used. Requires Temporal Server 1.31 or later (CLI 1.7 or later); the Aspire dev container uses `temporalio/temporal:latest` (CLI 1.8.2, Server 1.31.2).

**Deliverable.** Replace the placeholder Web page with a guest flow: labeled last-name and room-number verification, a grounded chat view, visible loading/error states, complete current-stay transcript, and end/resume controls. Web owns the public API and calls Temporal directly; ACS Chat SDK is not required by the current architecture. A returning eligible guest sees text and voice turns for that stay; the displayed history is not automatically sent in full to the model. Preserve room numbers as entered strings and display hotel times in `America/New_York` while storing UTC.

**Completion criteria.** A guest can verify, ask a question, receive a completed answer, end an interaction, reopen the site, verify again, and see the complete stay history. Invalid identity shows “Guest not found” without revealing guest details. Expired stays cannot resume; forged and cross-stay credentials cannot fetch or display another stay's history. Refresh recovery and same-ID retry never duplicate accepted turns; unresolved failures remain actionable, and expired sessions retain the visible transcript while blocking sends. End-chat confirmation and client-side clearing follow the approved rules. Theme preference works in System/Light/Dark modes, with keyboard navigation, labels, focus, readable contrast, and error recovery across the primary flow. A server restart does not erase a persisted conversation, and pending turns remain visible without duplicate display. Verify a representative long transcript for usability and performance without truncating it.

**Outstanding decisions.** No Step 7 product or UX decisions remain open. Do not add streaming infrastructure or transcript pagination unless implementation evidence establishes a need and the scope is separately agreed. Step 8 owns browser voice and its interruption-specific behavior.

**Renderer decision (2026-10-12).** Assistant replies render in Web as sanitized, restricted Markdown (HTTPS links only); stored text is unchanged, and images/maps are deferred to typed, app-validated content.

## 8. Deepgram browser voice through the same workflows

**Design decisions (2026-10-11, approved).** These supersede the 2026-10-10 design boundary. Items marked *prototype gate* must be confirmed against the real Deepgram service before implementation relies on them.

- **First task.** Extract the explicit chat-session state model with named transitions from `Home.razor.cs` (the deferred Step 7 P3 review item) before adding voice.
- **Reconnect (reconfirmed 2026-10-13).** Reconnect stays manual. No automatic reconnect.
- **Relay backpressure (2026-10-13, approved).** The relay's microphone-to-Deepgram buffer is bounded. If it fills, Web ends the voice session and shows "Voice disconnected" with manual Reconnect, rather than silently dropping speech.
- **Entry and controls.** Chat offers a "Start voice" entry that opens a focused voice mode showing listening, thinking, speaking, and recovery states. The only session control is **End Chat**, with the same rules as web chat. Returning to text requires a valid stored credential or verifying again. After a dropped connection, show "Voice disconnected" with **Reconnect** and **End Chat**; reconnect is manual, reuses a valid credential, and reconciles outstanding work without a new turn. Refresh restores sign-in but never starts the microphone or replays an answer. Hiding the page suspends capture and playback until the guest selects **Resume voice**; navigating away closes voice without erasing stay memory.
- **Verification (revised 2026-10-12, approved).** A guest with a valid stay credential from chat enters voice without repeating identity. Otherwise use a listen-only Deepgram speech-recognition step to collect last name and room number, call the existing `POST /api/concierge/sessions`, and start the Voice Agent only after successful stay verification. This replaces the proposed pre-verification browser-side agent function: such functions cannot gate the first Think request. Pre-verification recognition cannot invoke the concierge model or the Think endpoint. Typed verification remains the fallback. An understood but invalid identity produces “Guest not found. Would you like to try again or end chat?”; guessed corrections are never submitted automatically. Credential issuance and resource limits for the pre-verification recognition path must be verified before exposing it. **STT relay (2026-10-13, approved).** Pre-verification audio goes from the browser to Web, and Web opens the listen-only Deepgram STT connection with fixed options and server-enforced duration and attempt limits. No Deepgram credential reaches the browser. Rationale: live probes showed that `/v1/auth/grant` tokens, including `asr:write`-only grants, also open Voice Agent sessions, and an open socket outlives the token's TTL.
- **Transport (revised 2026-10-13, approved).** The browser streams microphone audio to a Web WebSocket, and Web relays it to the Deepgram Voice Agent WebSocket using the server-held API key. No Deepgram token reaches the browser, and Web can close any voice session on End Chat, idle timeout, or credential revocation. This supersedes the direct browser connection using a `v1/auth/grant` token. Rationale: Deepgram grants cannot be scoped to Voice Agent and an open socket outlives its TTL. The interruption target must be re-measured with the extra hop. Prior art: the `acs-demos/experiments/deepgram-voice-agent` ACS↔Voice Agent bridge (server-held socket, barge-in). Deepgram performs `listen` and `speak`; its `think` provider calls a Web-hosted `/v1/chat/completions` endpoint. Deepgram never owns the model or the conversation. Web is exposed to Deepgram through the dedicated Aspire Dev Tunnel Think endpoint described below; WorkflowServer remains health-only.
- **Think endpoint exposure (2026-10-13, approved; HTTPS runtime gate passed).** AppHost declares a dedicated named HTTPS endpoint `think` on Web and injects its assigned local port as `THINK_PORT`. The anonymous Dev Tunnel references only `web.GetEndpoint("think")`; it does not expose the normal Web endpoint. Web permits only `POST /v1/chat/completions` on the listener whose `LocalPort` matches the configured `THINK_PORT`, and denies that path on every other listener. Path matching is case-insensitive and exact: a trailing slash is not accepted. `RequireHost` is not used because the request Host header is not a reliable listener boundary and may be rewritten or supplied by the client; the server-observed local port is the gate. All current Web listeners and the tunnel's local forwarding use HTTPS; there is no HTTP listener or redirection middleware. If `THINK_PORT` is missing or invalid, no listener is treated as Think and the path is denied everywhere (no fallback to the regular Web port).

  The first real Aspire 13.6.0 run showed that declaring an Aspire endpoint and injecting `THINK_PORT` does not itself make Kestrel listen on that port: Kestrel had only the addresses from `ASPNETCORE_URLS`, and requests to the Think endpoint timed out. The ASP.NET Core 10 [Kestrel endpoints documentation](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0) confirms that named `Kestrel:Endpoints` replace top-level `Urls` and that an HTTPS endpoint without its own certificate uses the default or development certificate. Web copies each Aspire-provided `ASPNETCORE_URLS` address into a named `Kestrel:Endpoints` entry and adds `Kestrel:Endpoints:Think` as `https://localhost:{THINK_PORT}`. This retains the current HTTPS UI listener while binding the dedicated Think listener with the existing development certificate.

  **Terminal pending-handler boundary (2026-10-13 local / 2026-10-14 UTC).** Think now uses a `MapWhen` branch before UI exception handling, HSTS, status-page reexecution, and antiforgery. Microsoft's [MapWhen API](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.builder.mapwhenextensions.mapwhen?view=aspnetcore-10.0) and [middleware branching documentation](https://learn.microsoft.com/aspnet/core/fundamentals/middleware/?view=aspnetcore-10.0#branch-the-middleware-pipeline) confirm that this branch does not rejoin the main pipeline. Its terminal `Run` delegate never dispatches an endpoint, including one already selected by `WebApplication` implicit routing. The allowed POST explicitly returns **501**, `application/json`, with `{"error":{"message":"The Think handler is not implemented.","type":"server_error","param":null,"code":"not_implemented"}}`. Other Think requests return empty **404** responses. A separate early gate denies the exact Think path on normal listeners before UI error/status middleware. This replaces the earlier gate's accidental Razor 400 HTML content-type error; it is not a successful Think response. No body processing or business operation exists in the placeholder, so no broad exception catch is added. API-only exception handling belongs with the future handler, never a UI `/Error` reexecution.

  **Public URL handoff verified.** AppHost sets Web's `THINK_PUBLIC_URL` from `thinkTunnel.GetEndpoint(web.GetEndpoint("think"))`. The Aspire 13.6.0 graph starts without a dependency cycle; Web waits for the tunnel endpoint to be allocated. Runtime resource metadata showed `THINK_PUBLIC_URL` equal to the allocated public HTTPS tunnel URL (with explicit `:443`) and `THINK_PORT` equal to Kestrel's Think listener port. No tunnel URL is passed as a `dotnet run` argument. The runtime tunnel is ephemeral and is not recorded here.

  **Bounded live regression evidence (2026-10-13 local / 2026-10-14 UTC).** `aspire ps` returned no running AppHosts before starting the exact AppHost with `aspire start --non-interactive --no-build --apphost src/Concierge.AppHost/Concierge.AppHost.csproj`. `aspire wait web` reached healthy and `aspire wait think-tunnel --status up` passed. Runtime metadata showed a single HTTPS `DevTunnelPort` targeting `web/think`; Web's direct Think listener was `20604`, and its UI listener was HTTPS `20605` (run-specific ports, not defaults).

  All **21 live probes passed**. Direct local and public tunnel POSTs to the exact path and its uppercase variant returned **501**, `application/json; charset=utf-8`, with identical 121-byte error bodies. On both Think routes, `GET /`, `GET /v1/chat/completions`, trailing-slash POST, `POST /synthetic-not-think`, `GET /health`, and `GET /Error` returned empty **404** responses with no content type. UI Think POST remained empty **404** even with the public tunnel hostname in `Host`. UI `/`, `/health`, and `/health/ready` returned **200**; unauthenticated `/api/concierge/history` retained its **401 JSON** response. All requests used ordinary curl certificate validation (`ssl_verify_result=0`), without `-k`, redirects, or trust changes.

  Focused real-entry-point tests cover Development and Production, allowed POSTs with and without a preselected throwing UI endpoint, denied methods/paths (including UI and health endpoints), absent/invalid port configuration, and preserved UI/API/health/status-page behavior. A non-localhost HTTPS test host makes Production HSTS leakage observable. Before the fix, eight of ten initial cases failed; after the fix, all **14** targeted cases passed (12 isolation cases plus two existing API error regressions). The solution build passed with **0 warnings and 0 errors**. The exact test AppHost was stopped with `aspire stop --apphost src/Concierge.AppHost/Concierge.AppHost.csproj`; `aspire ps` returned `[]` afterward. This evidence verifies pending-handler isolation and HTTPS forwarding, not a production Think handler, SSE framing, voice authorization, Temporal voice turn, or real Deepgram call. Production pipeline coverage uses TestServer, not a live Production AppHost.
- **Think-endpoint authorization.** Each request carries a separate short-lived voice credential bound to one voice session and stay, revoked on End Chat and on expiry, so leaked Deepgram settings cannot outlive the session (*prototype gate*: custom headers on `think.endpoint`).
- **Turns.** Ignore the message history Deepgram sends; submit only the latest user message as a voice turn to the stay workflow. The turn ID derives from the voice session ID, the user-message ordinal, and a hash of the message text, so Deepgram retries join the existing same-ID update (*prototype gate*: Deepgram retry behavior). Return the complete answer as one chunk with the required framing: assistant-role frame, text, terminal frame, and `data: [DONE]`. No streaming infrastructure is added.
- **Interruption.** On `UserStartedSpeaking`, stop playback immediately and discard buffered audio. Never cancel admitted Temporal/model work: the turn completes and persists. A question asked during generation is processed after the admitted turn through the same ordered workflow, and the superseded answer is not played. A reply interrupted before or during playback stores its full text marked `interrupted` (a new transcript column plus a workflow update); Web shows a small "Interrupted" label. No other channel marker appears in the transcript.
- **Relay interruption handling (2026-10-13, approved).** From the relay latency prototype (`acs-demos/experiments/deepgram-concierge-gates/relay-latency`; fake-mic Chrome): (a) Resume playback after an interruption on the user's final transcript (`ConversationText` with role `user`), not `AgentStartedSpeaking`, which Deepgram did not send in any live session. (b) `UserStartedSpeaking` can arrive more than once per utterance; flush and interruption marking must be idempotent. (c) Pace agent audio to keep the browser's unretired playback lead to approximately 400 ms; hold the remainder server-side so it can be discarded on interruption. The prototype implements a 380 ms send budget with 20 ms PCM frames and playback acknowledgements. Before pacing, p95 was 38 ms direct, 41 ms loopback, and 225 ms through the dev tunnel (maximum 262 ms). After pacing, fake-mic Chrome runs measured 20 interrupting trials each: loopback p95 40.6 ms (max 40.7 ms) and dev tunnel p95 105.4 ms (max 144.4 ms); measured playback lead did not exceed 380 ms. The tunnel run's clock-sync RTT reached 394.8 ms; even the RTT/2 conservative bound remained below 234 ms. Fake-mic evidence is a proxy, not acceptance. The real-microphone Chrome prototype run (`results/realmic-relay.jsonl`, 2026-10-13) passed with 22 interrupting trials out of 26 `UserStartedSpeaking` events: p50 35.8 ms, p95 45.3 ms, maximum 45.5 ms against the p95 <= 300 ms target. Cecil confirmed speakers, not headphones. Maximum measured playback lead was 380 ms; server-held audio discarded at interruption reached 5.04 s. This closes the prototype latency gate; production Chrome acceptance remains required. Rationale: Deepgram sends speech up to 5–7 s ahead of playback, and audio already sent to the browser cannot be flushed by the server.
- **Interruption target.** Deepgram defines no numeric target. Gate: p95 ≤ 300 ms from receipt of `UserStartedSpeaking` to audible silence over 20 interruptions in Chrome on the developer's Mac with built-in microphone and speakers. Revisit the number from prototype measurements before acceptance. Chrome is the only browser gated for Step 8.
- **Latency/interruption correction (2026-10-15).** The four live Think completions at
  20:03–20:04 UTC were 4,066 / 7,704 / 6,046 / 7,815 ms. Temporal history,
  not HTTP spans alone, establishes that turn 2 executed a continuity summary
  activity (2,704 ms), retrieval (93 ms), then an answer activity (4,799 ms),
  each on attempt 1, with no tool definitions in the two model inputs. The
  summary was necessary continuity work but was unnecessarily started on the
  next question's critical path. HTTP client spans end at response headers,
  not completion of response-body reading; the apparent gap after the first
  HTTP span was mostly still inside that model activity, not established
  Temporal retry/backoff. These measurements predate the separate 2026-10-15
  stay-memory update; summary/compaction references here describe the then-
  current path, not the new memory path. See Microsoft's [built-in HTTP client
  activity timing](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-builtin-activities#http-client).

  Compaction now starts after an accepted answer, without delaying its update
  result. The next turn joins any unfinished compaction before using context;
  Continue-As-New and expiry also join it. The first upgraded turn can still
  need the old pre-answer compaction. A per-turn deterministic patch preserves
  old command ordering while allowing already-running stays to adopt the change:
  .NET memoizes patch decisions, including false decisions made on replay
  ([SDK implementation](https://github.com/temporalio/sdk-dotnet/blob/1.20.0/src/Temporalio/Worker/WorkflowInstance.cs),
  [versioning guidance](https://docs.temporal.io/develop/dotnet/versioning)).
  An initial live check caught the run-wide patch staying on the old path;
  the corrected migration has explicit regression coverage. This changes
  scheduling within the existing workflow, not conversation ownership,
  prompts, retrieval, provider selection, or the completed-answer SSE protocol.
  The fixed prompt allowance was also corrected from 1,024 to 1,536 tokens:
  the existing expanded grounding instructions had outgrown its conservative
  byte/template guard; the then-current 31,728-token bound still fit 32,768.
  The 2026-10-15 stay-memory update supersedes that answer-context bound with
  the 8,000-token working budget in §6; the 32,768 value remains only the
  default model-window configuration.

  Live non-microphone checks used the existing seeded session/turn API with
  `channel=voice`, Foundry `gpt-5-mini` / `low`, and local Ollama embeddings.
  Three pre-change, back-to-back checkout queries took 6,404 / 5,357 / 9,080 ms.
  After the corrected migration, its first turn took 10,137 ms; three queries
  with 10 seconds between responses and the next request took 8,774 / 3,212 /
  2,521 ms. Their answer activities alone took 8,667 / 3,108 / 2,417 ms,
  confirming summary work was no longer ahead of them. Three immediate
  follow-ups still took 10,112 / 13,713 / 9,886 ms: they waited for unfinished
  summaries. All returned nonempty checkout answers containing the seeded
  checkout time. This is bounded behavioral evidence, not a full grounding
  evaluation, controlled provider benchmark, live Think/SSE retest, or
  first-audible-response measurement. It does **not** establish that poor
  voice latency is completely fixed; provider generation and rapid-turn
  compaction waits remain material.

  Final verification: all 289 core .NET tests passed with
  `dotnet test tests/Concierge.Core.Tests/Concierge.Core.Tests.csproj --no-restore --logger 'console;verbosity=minimal' -- xUnit.ParallelizeTestCollections=false`;
  all 7 `node --test tests/voice-play-worklet.test.mjs` cases passed.
  An earlier parallel suite run encountered test PostgreSQL connection
  timeouts; no production database or infrastructure settings were changed.
  All three existing stay histories, including mixed legacy/new turns,
  replayed successfully. Aspire resource rebuilds for Web and WorkflowServer
  passed without warnings/errors, and Web, worker, PostgreSQL, Temporal, and
  the Think tunnel were healthy afterward. The final post-restart voice-channel
  API probe took 3,306 ms, returned the seeded checkout time, and its same-ID
  retry returned the same answer/sequence with exactly one matching transcript
  turn. Both Web health endpoints returned 200. The app was left running for
  Chrome testing; no fresh microphone/Deepgram audible-response timing was taken.

  Interruption classification now comes from the browser worklet's last
  rendered block having nonzero PCM before flush, not server queues or
  unacknowledged-sample estimates. A matching acknowledgment is emitted only
  after an all-zero render block; ordinary idle speech, queued-only audio,
  drained audio, silent PCM, and duplicate flushes are covered by tests.
  Only `BargeIn=true` records qualify as browser-rendered interruption
  samples. Relay receipt to acknowledgment includes acknowledgment return
  transit and excludes prior Deepgram detection and hardware latency. It is
  not physically audible silence, and neither earlier four-sample timing nor
  these tests establishes the Chrome acceptance gate.

  **Live transcript interruption check (2026-10-19 UTC).** In voice session
  `aaef412e-8594-4974-9e3d-f6910fcc3f13`, the relay recorded three worklet
  silence acknowledgments: interruption 1 (`BargeIn=false`, 16.4 ms),
  interruption 2 (`BargeIn=true`, 9.6 ms), and interruption 3
  (`BargeIn=false`, 6.2 ms). The matching Temporal trace ran
  `MarkInterruptedAsync`; PostgreSQL shows voice turn 1
  (`Do you know where the hotel is located?`) as `interrupted=true` and voice
  turn 2 as `interrupted=false`. This verifies that the rendered reply token
  selected the correct persisted turn and that non-barge-in acknowledgments
  did not mark a turn. This is one qualifying live interruption, not a
  20-interruption Chrome run. The project owner accepted Step 8 as complete
  for the current demo scope on 2026-10-18; no further live sessions are
  required for this milestone.
- **Greeting.** Use Deepgram's `agent.greeting`: a welcome-back greeting when the stay already has turns, otherwise a first-time greeting. Greetings are not transcript turns.
- **Ending.** End Chat runs from the button, from a confirmed spoken request through an explicit end-session function (not transcript keyword matching), or after 2 minutes with no speech and no reply in progress. All three run the existing End flow: flush pending turns, invalidate the credential, close voice. The idle timeout gives no warning; the guest can start another chat. Reconnect restarts the idle clock (2026-10-18, user decision).
- **Errors.** Microphone denial, missing device, and unintelligible identity give specific messages and a route to typed verification or text chat. Microphone permission is not requested repeatedly.
- **Data.** Store no audio recordings. Persist only verified-stay conversation text and playback/interruption metadata; keep pre-verification speech out of the stay transcript. Opt out of Deepgram's Model Improvement Program with `mip_opt_out=true` (*prototype gate*: support on the Voice Agent endpoint; otherwise document Deepgram's standard retention).
- **Configuration.** The Deepgram API key is an Aspire secret parameter. Web configuration holds the STT model (default `nova-3`), the TTS model (default `aura-2-thalia-en`, expected to change), and the idle timeout (default 2 minutes). No other voice options.
- **Secret ownership (2026-10-11, approved).** As part of Step 8, centralize application secrets in AppHost user-secrets and Aspire secret parameters. Pass Foundry keys only to WorkflowServer under its existing configuration keys, and the Deepgram key only to Web. Remove WorkflowServer's `UserSecretsId` and update the README's secret setup commands to target AppHost. Unset Foundry keys must not prompt or block startup: Ollama needs no key, and Foundry retains its `DefaultAzureCredential` fallback. Existing WorkflowServer keys must be set again in AppHost. Do not add `.secrets.env` support or an example file.
- **Voice Agent leg uses `ClientWebSocket` (2026-10-13, approved).** This supersedes the SDK choice below for the Voice Agent relay only. The relay prototype found that SDK 7.1.3 still has no `History`/`LatencyReport` event types (both arrive live), and earlier gates confirmed it sends an invalid JSON `Close`. Web's Voice Agent leg uses `ClientWebSocket`, adapted from `acs-demos/experiments/deepgram-voice-agent/Agent/DeepgramAgentSession.cs`. The SDK (logging disabled) remains for the listen-only STT relay.
- **Deepgram SDKs (revised 2026-10-13, approved).** Web uses the `Deepgram` .NET SDK 7.1.3 for the listen-only STT relay, with logging disabled because its default logging writes credentials and tokens to the console. Do not use the SDK for the Voice Agent leg: 7.1.3 still has no `History`/`LatencyReport` types (both events arrive live) and sends an invalid `Close`. The Voice Agent leg uses `ClientWebSocket`, adapted from `acs-demos/experiments/deepgram-voice-agent/Agent/DeepgramAgentSession.cs`. With the relays, the browser no longer connects to Deepgram, so `@deepgram/sdk` is not needed.

**Deliverable.** Add browser microphone voice using Deepgram Voice Agent for speech recognition, turn handling, and speech playback, under the decisions above. Route every concierge model turn through the same stay-authorized Temporal workflow and configured `IChatClient`. Define the voice-turn state machine before implementation: `verifying → listening → thinking → speaking → listening`, with explicit `interrupted`, `disconnected`, `failed`, and `ended` transitions. ACS Call Automation is reserved for later telephone voice.

**Completion criteria.** The browser can verify a guest by voice, answer a grounded hotel question, and resume the same stay after a web interaction; the web transcript shows interrupted replies under the rule above. Voice questions execute through Temporal, including after reconnect/retry, without duplicate accepted turns. A real Deepgram run in Chrome meets the interruption target and confirms queued audio is discarded without a false full-answer transcript. Missing, forged, cross-stay, expired, or revoked credentials cannot begin or resume the Voice Agent or call the Think endpoint; the listen-only verification path cannot bypass this boundary. The real session also verifies the settings handshake, turn framing and `[DONE]` marker, spoken and idle End Chat, and recovery from a dropped connection. Every prototype gate above is resolved and recorded.

**Current demo acceptance (2026-10-18).** The project owner accepts Step 8 as
complete for now. This closes the demo milestone without claiming that every
original prototype gate was re-run: the real-microphone prototype latency
gate passed, automated tests cover interruption classification and queue
flush behavior, and a live Deepgram session confirmed that the rendered
playback token marks the matching transcript turn. The 20-interruption
production Chrome run was not performed for transcript marking and is not a
remaining requirement for this milestone. This acceptance supersedes any
remaining Step 8 validation language above for the current demo milestone.

**Outstanding decisions.** None for product or UX. Prototype gates above may force a recorded design change.

## Phase exit and deferred work

The phase is complete when a fresh local run needs only documented service/model configuration, seeds fictional data automatically, and demonstrates verification, grounded answers, durable same-stay continuity, complete web history, and browser voice through one Temporal conversation owner. Record the actual run and evaluation results; a successful build alone is insufficient. Preserve traces/logs and internal source references so a developer can inspect why a reply was produced.

After this gate, plan separate slices for ACS telephone voice; live Open-Meteo weather and Geoapify place/travel information; and explicitly labeled fake reservation, ticketing, and ride flows. None are dependencies of steps 1–8. SMS remains out of scope; Telegram remains undecided.

## Source of current decisions

- [Hotel ZGY architecture brief](../hotel-concierge-demo.md): product scope, identity rules, provider choices, retrieval, and evaluation.
- [Hybrid search/RRF research](../hybrid-search-rrf-research.md): prototype-backed retrieval guidance; candidate sizes and indexing still require Hotel ZGY evaluation.
- [ADR 0004](0004-concierge-platform-direction.md): channel and platform direction, including Deepgram experiment limits.
- [ADR 0005](0005-stay-memory-and-transcript-persistence.md): accepted memory, deadline, batching, and transcript behavior.
- [ADR 0006](0006-workflow-server-scaffold.md): exact scaffold status and validation boundary.
