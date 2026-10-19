# ADR 0008: Voice Agent Runtime Lessons and Decisions

- **Status:** ACCEPTED; accepted as complete by the project owner on 2026-10-18.
- **Date:** 2026-10-16
- **Complements:** [ADR 0005](0005-stay-memory-and-transcript-persistence.md) (stay memory) and [ADR 0007](0007-hotel-qa-phase-one-implementation-plan.md) step 8 (browser voice)
- **Driver:** Cecil's live voice testing on 2026-10-15 and 2026-10-16, and the diagnosis that followed.

## Context

Browser voice uses Deepgram's Voice Agent with a custom Think endpoint. Every turn
runs in the Temporal stay workflow: `RetrieveAsync` (hybrid retrieval with Ollama
`embeddinggemma-2:740m`), then one `GetResponse` call (Foundry `gpt-5-mini`, reasoning
effort `low`, or Ollama `concierge-chat`). Live testing showed slow and verbose
replies, a wrong answer after revised speech, and a concierge that did not know the
authenticated guest. This ADR records what we learned and the decisions taken.

## Findings

| # | Observation | Evidence | Cause |
|---|---|---|---|
| 1 | Some voice turns took about 17 s | Temporal history for stay `10e544bb…`, 2026-10-15; HTTP headers returned in 2.2–5.6 s | Foundry generation variance. Replaying the same captured requests gave medians of 3.7 s and 4.6 s (ranges 3.5–8.0 s). Ollama medians were 5.5 s and 12.5 s. |
| 2 | The first question after idle was slow | 3.2 s embedding span | Ollama unloads models after 5 idle minutes. |
| 3 | "What can you help me with?" got a long mechanical list | Captured requests; both providers | Old instructions allowed three sentences and lists. Loosely related retrieved evidence added unrelated facts. |
| 4 | Follow-up turns waited on summary generation | Temporal history before `stay-memory-v1` | Compaction was a blocking model call inside the turn. |
| 5 | Revised speech could receive the earlier answer | Think endpoint turn-identity review | Turn ID omitted the spoken text. |
| 6 | The concierge didn't know the guest's name or room | Stay `10e544bb…`, 2026-10-16 11:40–11:41 UTC: retrieval 0.07–0.14 s, answers 1.9–10.5 s | Sign-in returns only session ID, stay ID and expiry (`ConciergeSessionContext`). Identity never reaches the workflow or the prompt. |
| 7 | Pool hours, pets and convenience store answered "I don't know" | Knowledge files at the time of the session | Correct grounded behaviour. Pool hours and store information were missing then; Cecil supplied them on 2026-10-16 and they are now indexed. The pet policy remains undocumented (deliberate case `ZGY-NONE-005`). |

## Decisions

1. **Latency is measured from Temporal history and replayed requests.** Activity
   scheduled-to-completed times are authoritative. OpenTelemetry `chat` spans can
   outlast the activity, so use spans only for breakdowns. Compare providers or
   prompts by replaying captured requests several times and reporting median and
   range. The success measure for voice is time from end of guest speech to first
   audible reply.
2. **The embedding model stays resident.** Every Ollama embedding request sends
   `keep_alive: "-1m"`, and `OllamaEmbeddingWarmup` loads the model at startup. A
   bare `"-1"` is rejected with HTTP 400.
3. **Spoken style lives in the instructions.** Use one or two short sentences, no
   lists, no unsolicited policies or closing offers, and a brief capability reply to
   greetings. The fixed-prompt allowance test (1,232 bytes) and all evaluation gates
   on both providers must pass after any change. On 2026-10-16, both providers scored
   support 13/13, no-evidence 3/3 and Recall@5 15/15.
4. **No blocking maintenance model calls on the answer path.** A turn is retrieval
   plus one answer call. Working context is an 8,000-token budget of recent complete
   turns plus explicit stay continuity memory, as amended in ADR 0005.
5. **Voice turn identity includes the spoken text.** Turn IDs combine the voice
   session, connection, user ordinal and the SHA-256 of the user text. Identical
   retries join the admitted turn; changed text becomes a new ordered turn.
6. **Workflow changes are replay-safe.** Changes to workflow command order use
   `Workflow.Patched` (for example `stay-memory-v1-*`) and a replay test. Activity
   inputs, such as prompt text, are not checked on replay. Prompt changes therefore
   don't need a patch, but they do change behaviour for running stays.

## User-approved decision: verified guest context

Cecil approved forwarding the sign-in information on 2026-10-16, specifically the
authoritative database identity, not the last name supplied by the client. This
section records that scoped approval as part of the accepted ADR.

- Extend the existing verification/session-binding reads and authentication join
  to return the guest's given name, family name and room number. Do not add a
  once-per-stay lookup Activity: the existing session operations can return these
  columns without another database round trip.
- Forward the stay-bound profile through Web and browser voice in both workflow
  start input and authenticated turn submissions. `UseExisting` ignores new
  start input, so the next authenticated turn refreshes an already-running stay.
- Add a separate system answer-context message with a trusted purpose/boundary
  wrapper and JSON-escaped data. `ConciergeInstructions` stay unchanged.
- Compute floor deterministically in C#: the first digit of a three-digit room
  (`112` → 1, `412` → 4, `812` → 8), or the first two digits of a four-digit room
  (`1212` → 12). One- and two-digit rooms remain supported with an unknown floor;
  the model must not invent it.

Constraints:
- **Not hotel evidence.** Guest identity comes from the database, never from what
  the guest says. It is not hotel evidence. Conversation claims cannot overwrite
  verified identity. A mismatched stay binding is rejected before the model call.
- **Token budget.** The entire serialized profile, trusted wrapper and message
  overhead count against the 8,000-token budget, including JSON escaping expansion.
  It does not consume the nearly full 1,232-byte instruction allowance. Recent
  exchanges/evidence use the remaining budget; required context that cannot fit
  fails closed rather than truncating identity.
- **Replay safety.** No new Activity or workflow command order is introduced, so
  this is a payload-only change and needs no new patch. Existing patches remain.
  New fields are optional for old payloads; the profile carries across
  Continue-As-New. Tests replay histories before and after profile forwarding,
  including the continued run.
- **Data handling.** The guest's name is sent to the configured model provider.
  Cecil approved this for the demo. Raw names and room strings are untrusted data,
  not instructions; no name is guessed when a profile is absent.

Rejected alternatives:
- A model-callable "look up guest" tool. It adds a model round trip of about 2–5 s
  to the turns where identity matters.
- Matching "what's my name" in code. This is brittle against paraphrases.

## Consequences

- The README "Stay memory and answer context" section and ADR 0005 describe the
  working-context policy. Older transcript recall remains deferred.
- Foundry variance remains outside our control. If full generation stays too slow
  for voice, the next candidate is incremental delivery through the existing
  Activity boundary, designed per the WorkflowStreams law.
- Guest name, room and floor forwarding have automated coverage in
  `AuthenticatedGuestContextTests` and `VerifiedGuestContextTests`, including
  old payloads, stay isolation, budgeting and replay. The stateless hotel corpus
  evaluation remains separate from authenticated guest-context tests.
- After rebuilding Web and WorkflowServer, live authenticated requests on the
  existing Cecil Phillip stay returned the correct name, room 112 and first
  floor. Pool hours, store location/hours/stock, and the specific-brand fallback
  also answered correctly. Receipt-to-complete HTTP times were 1.87-4.79 seconds;
  these were not end-of-speech-to-first-audible-reply measurements.
- Spoken brevity still needs live voice acceptance; the latest checks used the
  authenticated conversation API, not microphone playback.
