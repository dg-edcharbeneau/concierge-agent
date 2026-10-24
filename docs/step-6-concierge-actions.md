# ADR 0009 Step 6: Concierge actions

## Current acceptance status — 2026-10-24

**Step 6: COMPLETED — accepted for the demo by Cecil on 2026-10-24.**
Technical review passed with documented demo limitations; the current
timeout/retry settings are retained for the Foundry demo profile.
The review reran 88 existing timeout/recovery tests (88 passed, none failed or
skipped), checked live nonsecret settings and reconciled retained measurements.
The earlier missing concurrency/provider/recovery measurements are available;
those lists below are dated history, not new work required for this decision.

The acceptance retains the current numeric limits. Observed roughly
20–23-second slow turns and an awkward but safely blocked post-booking
acknowledgement remain explicit demo limitations. The 180-second budget starts
when a turn reaches its processing slot; it does not include time behind an
earlier turn or the pre-action persisted-turn lookup, and is not an end-to-end
HTTP or audible-voice deadline. Physical browser audio/interruption checks keep
their separate voice/UI gates. Cecil explicitly accepted the documented demo
limitations; Step 6 is complete within that scope. The original owner-acceptance closure changed only
documentation and did not claim a new test run. Subsequent state corrections
have a [separate verification record](../.artifacts/reviews/2026-10-24-step6-can-guard/results.md).

[Current technical review, criteria matrix, measurements and recommendation](../.artifacts/reviews/2026-10-23-step6-acceptance/results.md).

## Implementation context

This note records the Step 6 action-tool limits and evidence available when
choosing them. It is not a Phase 2 live-acceptance claim.

The implementation was simplified on 2026-10-21 after the project owner
directed that the demo not retain `Workflow.Patched` compatibility paths.
Existing Temporal and application database data was cleared before restarting
the AppHost. New model-classified ride actions are gated by workflow-owned
offers; a ride offer remains open for two guest turns. Model calls use three
Temporal activity attempts. Weather activities retry transient provider
failures; side-effecting ride tool activities remain single-attempt. That reset
removed histories incompatible with the then-current command order. The later
retained post-reset histories were replayed with production tracing: 14/14
passed during the cancellation follow-up.

## Implemented bounds (timeout and retry policy, 2026-10-23)

Every call has an explicit retry owner. **Recommendation: retain the current
numeric settings for the Foundry demo**, accepted by Cecil on 2026-10-24
with the limitations above. The initial before/after run is supplemented by the later
concurrency, provider and real-store recovery measurements in the
[current review](../.artifacts/reviews/2026-10-23-step6-acceptance/results.md).
"Before" is the build measured in
`.artifacts/reviews/2026-10-23-timeout-baseline/baseline.md`; in that build
`ServiceDefaults` also added the Aspire standard resilience handler (HTTP
retries) to every `HttpClient`. "Evidence" is from `after.md` in the same
directory (Foundry `gpt-5-mini`, sequential load) unless stated.

| Call | Retry owner | Activity limit | Inner limit | Before | After | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| Whole action turn | Workflow deadline (no retry) | 180 s; retrieval/model/tool activity schedule-to-close and start-to-close clamped to the time left | n/a | 180 s | 180 s (unchanged) | 180 s is the active-action safety ceiling, not a latency target or full HTTP/voice deadline. Observed guest-turn sample max 22.55 s; later proposal 20.335 s. Current deadline/recovery regressions pass. |
| Model call (`GetResponse`) | Temporal: 3 attempts. SDK retries off (`maxRetries: 0`) | 20 s per attempt, capped at a third of the remaining turn; schedule-to-close and a workflow timer bound the whole call | `AI:Chat:RequestTimeoutSeconds` (Foundry 15 s, Development 18 s, max 18 s), further capped at the attempt's remaining time less a 2 s margin; covers the slot wait and the full stream | 20 s × 3; request 15 s (Foundry) / 180 s (Development, longer than the attempt) | 20 s × 3; request 15 s / 18 s | Foundry after-policy sample: 155 attempts, one cut at 15 s and recovered; successful max 12.88 s. Later 16/16 concurrent requests passed; chat slot wait max 7.434 s. Values retained; no stable tail-SLO claim. |
| Retrieval (`RetrieveAsync`: DB lookups + one query embedding) | Temporal: 3 attempts, 1 s → 5 s backoff. Embedding SDK retries off | 10 s per attempt | `AI:Embeddings:RequestTimeoutSeconds` 8 s (max 8 s), capped at remaining attempt time less 2 s | 60 s × 3; request 60 s (Foundry) / 180 s (Development) | 10 s × 3; request 8 s | Measured query embedding sample P95 0.53 s, max 1.04 s; retrieval max 1.06 s. Current shortened-allowance/slot/body tests pass. Retrieval failures have bounded Temporal retries. |
| Ingestion embeddings (`HotelSetup`) | Temporal: setup activity, 10 attempts, 1 s → 30 s backoff | 5 min per attempt | `AI:Embeddings:IngestionRequestTimeoutSeconds` 60 s (1–298 s) per section | Shared `RequestTimeoutSeconds` | Separate 60 s setting | Separate production ingestion-client timings: synthetic 400/800/1,600-character inputs returned 768-d vectors in 0.522/0.255/0.292 s. Not a bulk corpus benchmark or an interactive Step 6 turn. |
| `GetWeather` | Temporal: 3 attempts, 1 s then 2 s backoff, on timeout, network error, HTTP 408/429/5xx. No HTTP retry | 12 s per attempt | Up to 2 provider requests (Geoapify geocode + Open-Meteo), 5 s each, covering headers and body; 0.5 s cleanup margin | 12 s × 3 + global HTTP handler | 12 s × 3, no HTTP retry | Measured per-attempt sample P95 2.35 s; two 5 s Open-Meteo timeouts recovered on attempt 3 (14.06 s activity). Later comprehensive weather 120/120 and current Web action run 36/36 passed. |
| `FindNearbyPlaces` | None (1 attempt) | 8 s | 1 Geoapify request, 5 s | 15 s | 8 s | Three direct production-service operations: 0.460–0.537 s; live model-callable nearby path also passed. Current one-attempt/header/body bounds verified. |
| `GetPlaceOpeningHours` | None (1 attempt) | 8 s | 1 Geoapify request, 5 s | 15 s | 8 s | Three direct production-service operations: 0.757–0.914 s; live model-callable hours path also passed. Current one-attempt/header/body bounds verified. |
| `GetRoute` / `ProposeRide` | None (1 attempt) | 18 s | Up to 2 place lookups + 1 route request, 5 s each | 55 s | 18 s | Three direct route operations: 0.798–1.046 s. Live proposal, pending-time change and explicit confirmation passed. Model retries, not route time, dominated a 20.335 s slow proposal. |
| `GetRideStatus` / `GetRideHistory` / `ProposeRideCancellation` / `ConfirmRideCancellation` | GET: ride HTTP client, 3 attempts × 3 s, 0.5 s constant delay. DELETE: sent once | 12 s | 10 s total pipeline; `HttpClient.Timeout` 11 s (includes body) | 12 s; GET-only HTTP retries | 12 s; per-method policy explicit | Status/history/staged cancellation and confirmed cancellation exercised in retained Web/voice runs. Current retry/single-send and ambiguity regressions pass; the HTTP read policy fits its outer activity. |
| `ConfirmRide` | None. POST sent once; a timeout is an unknown outcome, never resent | 15 s | 10 s total; 11 s client | 15 s | 15 s | Retained successful POST activity sample max 0.13 s; live booking/confirmation passed. Current hung-mutation test proves one send and an unknown-outcome response on timeout. |
| RideService store-booking / cancel activity | Temporal (RideService): 5 attempts; idempotent store | 4 s per attempt, 8 s schedule-to-close | Store bounds itself to its attempt and schedule deadlines less 0.25 s | 30 s × 5 | 4 s × 5, 8 s total (fits the 10 s HTTP call) | Current persistence/recovery tests pass. Separate real test-database denial/restoration drill recovered the original booking and cancelled it once; cancellation 0.030 s. No running-app outage claim. |
| RideService reconcile-booking activity | Temporal (RideService): retries until schedule-to-close | 5 s per attempt, 2 min schedule-to-close, after a 2 s grace | Reads the DB; no resend | None (workflow could hang when the store activity exhausted its cap) | New | Real disposable-PostgreSQL drill: admission failed after 7.081 s, reconciliation observed SQLSTATE 55000 and recovered 1.018 s after restoration; one booking row. Background reconciliation outlives the failed HTTP request. |
| Hotel address geocode (startup, no activity) | HTTP standard handler: up to 4 attempts, base delay 1 s, within total cap | n/a | 5 s per attempt, 20 s total, 25 s overall including body | Global handler | Explicit startup-only policy | Separate startup-only bounded retry client, with body covered by the 25 s deadline. Direct geocode service measurements 0.268–0.433 s; current nesting/cancellation tests pass. This is not a startup cold-network benchmark. |

Booking ambiguity is reconciled through RideService history and cancellation
ambiguity through ride status. The Step 4 RideService idempotency contract
protects a matching booking retry.

**Nesting rule.** Inner work, waits and cleanup fit within the activity
allowance; active-action activities, activity queueing and retries are bounded
by the remaining 180-second action budget. The earlier ordered-turn wait and
persisted-turn lookup are outside that clock; this is not an end-to-end HTTP
or voice deadline. Startup validation enforces the model and
embedding part: `RequestTimeoutSeconds` must be at most the activity allowance
less the 2 s completion margin (chat 18, embeddings 8), and
`IngestionRequestTimeoutSeconds` must be 1–298. A larger value stops the worker
with a message naming the key and the limit.

The per-tool limits above remain **execution** caps. A separate
schedule-to-close timeout uses the remaining turn budget, so a queued tool
expires without executing later. This applies to single tools and
concurrent multi-day weather calls. Inspection of TemporalCommunity.Extensions.AI 0.14.2
(`DurableAIFunction.CreateActivityOptions`, verified source commit `5339ed1`)
confirmed the adapter only supplies start-to-close, retry policy, and summary.
The outbound interceptor bounds the actual SDK schedule rather than copying
the package's internal serialized input/output contracts. A timer-only tool
race was rejected because abandoning an await would leave queued side effects
eligible to execute later.

Implementation: `ConciergeTurnDeadlineInterceptor.cs:6-36`,
`Program.cs:92-94`, and `ConciergeStayWorkflow.cs:973-1004` under
`src/Concierge.WorkflowServer/` (workflow files are in `Workflows/`).
The active deadline is scoped to the admitted turn (`ConciergeStayWorkflow.cs:373-374,
467-469`). After dispatch, confirmation offers are consumed and the workflow
returns the rendered result or curated uncertain outcome directly
(`:762-783`), before a generic deadline/context reply or another model call can
replace it. Unknown booking outcomes direct the guest to ride history; unknown
cancellation outcomes direct them to ride status. Neither asserts success or
failure or automatically retries unsafe work.

## Captured evidence and limitations

The checked-in latency snapshots are sanitized metadata from existing traces;
they contain no guest text or new provider requests:

- `.artifacts/reviews/2026-10-15-voice-latency/evidence.json` records answer
  model activities of 3,019 ms and 3,018 ms, plus a 35,223 ms summary call.
  The long call was summary maintenance, which Step 6 does not add to the
  answer path.
- `.artifacts/reviews/2026-10-16-voice-regression/evidence.json` records
  answer calls of 17,190 ms and 17,426 ms, with retrieval at 3,225 ms and
  55 ms respectively.

The 20-second per-attempt model activity cap is above those recorded answer
calls and the configured 15-second provider request cap. Each attempt is also
clamped to the remaining turn budget divided among the configured attempts.
Action turns no longer perform the historical blocking summary-maintenance
call.

Offline analysis of the captured Foundry replay results is recorded in
`.artifacts/reviews/2026-10-16-voice-regression/model-replay-review.md`.
For the historical `gpt-5-mini/low` profile, 33 completed calls had a 2.727 s
median, 1.416–120.148 s range, and 24.467 s nearest-rank P95; two exceeded
20 s. The eight HTTP 500s and one timeout are included where durations exist,
but four failures lack elapsed times. This makes 25 seconds a sample-based
candidate, not an established SLO. No timeout was changed: the replay used a
direct HTTP harness rather than the current .NET client, and the tail/failure
trade-off is not specified. No captured weather, route, or RideService
requests are available for replay.

**Current timeout review: PASS WITH DEMO LIMITATIONS; values retained and
accepted for the demo on 2026-10-24.** Later measurements cover the previously missing conditions. The
owner accepted the demo experience on 2026-10-24. This closes the Step 6
timeout acceptance item; no additional concurrency/provider benchmark is implied. See the
[current acceptance review](../.artifacts/reviews/2026-10-23-step6-acceptance/results.md)
for measured slow replies, deadline scope and qualifications. Earlier direct-HTTP
replays above do not justify increasing the current .NET client's limits.

The previous web live check (`just live-concierge-actions`) passed 11
scenarios against real Geoapify, Open-Meteo, RideService and Azure Foundry
`gpt-5-mini` on 2026-10-20. The updated 12-scenario gate passed on 2026-10-20;
it covers the exact polite booking request, punctuation-normalized confirmation,
complimentary ride cost, complete weather/route sentences, ride status/history/
cancellation, and the absence of simulation wording, unsolicited fare follow-ups,
full country names and spoken IDs. Ride replies are workflow-rendered; a history
lookup retains IDs only in model context when it must select a ride for an
explicit cancellation. Ride status and history replies state the pickup time for a
booked or driver-assigned ride and the estimated or actual arrival time (pickup plus
route duration) for a ride in progress or complete; they omit driver, car and plate.
The voice channel has not been live-checked by this gate.
Automated tests now drive the full 180-second elapsed deadline against real
Temporal activity-slot starvation; see the regression evidence below. They do
not validate provider latency percentiles or replace the live acceptance gate.

### Queue deadline regression evidence (2026-10-21)

`tests/Concierge.Core.Tests/ConciergeTurnDeadlineTests.cs:17-147` runs four
isolated hosts concurrently, each with one remote activity slot occupied by a
real blocker: single weather, paired weather, booking, and cancellation.
Temporal history must show queued tool activities timing out without any
started event. Releasing the blocker and executing a sentinel activity proves
polling resumed without executing expired tools. The tests also assert
uncertain confirmation replies, consumed offers rejecting another confirmation,
and a subsequent question and End update succeeding. These use the unchanged
180-second production constant in real time: an executing blocker prevents
test-server time skipping.

The timer-wins regression (`:149-195`) uses the actual stay workflow and the
time-skipping host. A test-only interceptor delays delivery of a completed
model activity by 181 seconds; the production deadline timer wins, the late
tool response is ignored, and a following question and End succeed. This
isolates the timer race without introducing public test configuration.

Affected tests passed **41/41**, no skips:
`dotnet test tests/Concierge.Core.Tests/Concierge.Core.Tests.csproj --filter 'FullyQualifiedName~ConciergeTurnDeadlineTests|FullyQualifiedName~ConciergeActionToolWorkflowTests' --logger 'console;verbosity=minimal'`.
Removing the production interceptor registration caused the starvation
regression to fail, confirming it detects the missing queue bound.

Full Core verification also passed **578/578**, **0 failed, 0 skipped**,
in **3m 4s**:
`dotnet test tests/Concierge.Core.Tests/Concierge.Core.Tests.csproj --no-restore --logger 'console;verbosity=minimal'`.

On 2026-10-21 the gate grew to 18 web scenarios. The new ones are phrasings
that failed in live session 0021: two-day weather, a route to a
generalized place name, plain agreement after a proposal ("Yeah. Let's do
that.") that must not book, "Confirm the ride.", and a confirmation with no
open offer. All 18 passed against the same live services; see ADR 0009, "Live
session 0021 fixes".

At this point in the earlier implementation, the obsolete model instruction
to repeat proposals exactly had been removed, but confirmation still depended
on phrase rules. The historical 18-scenario check and those phrase semantics
are superseded by the staged design below.

The 2026-10-21 live voice follow-up fixes
(`action-ride-intent-followup-v3-{seq}`) are historical. New executions use
model-selected tools rather than phrase authorization rules.

Cleanup validation on 2026-10-21: 85 prompt/allowance/action tests and 29
stay-workflow/replay tests passed. Both package validators and both sets of
24 negative validation cases passed. Fresh full package runs were manually
graded against the expected claims and actual retrieved passages:

| Provider | Combined Recall@5 | Answer support | Staff fallback |
| --- | --- | --- | --- |
| [Ollama `concierge-chat`](../tests/Concierge.Evaluation/results/2026-10-21-ollama-prompt-cleanup-1/results.json) | 20/20 PASS | 17/18 FAIL | 5/5 PASS |
| [Foundry `gpt-5-mini`](../tests/Concierge.Evaluation/results/2026-10-21-foundry-prompt-cleanup-key-1/results.json) | 20/20 PASS | 18/18 PASS | 4/5 FAIL |

Ollama omitted the expected overnight-storage restriction in ZGY-MULTI-002.
Foundry omitted the staff referral in ZGY-NONE-003. The initial Foundry
token-authenticated attempt failed with a tenant mismatch before any answers;
the completed run used the existing AppHost API key in memory.
Each linked grade file has an adjacent `run.json`.

The package runner evaluates the hotel-Q&A instructions (`ConciergeInstructions`),
not the action instructions. These results therefore do not test the action-prompt
edit. They also cannot establish that the Q&A prompt was uninvolved: the captured
Q&A instructions contain "Do not offer an action or further help the guest did
not ask for", and the cases had passed in earlier runs. Attribution remains
unresolved. The Foundry miss did not lack retrieved contact evidence: both the
2026-10-18 and 2026-10-21 captures include the front-desk passage; the earlier
answer explicitly advised dialing 0, while the failed answer did not.

## Q&A prompt follow-up (2026-10-21)

`ConciergeInstructions` now explicitly treats relevant evidence-supported
qualifications or limits and required referrals to hotel staff as part of the
answer, not unsolicited additions. This directly changes the prompt used by the
package runner; the action instructions and package/rubric were not changed.
The fixed-prompt allowance and Q&A prompt unit tests passed 37/37.

Both providers completed the full package run. I manually checked all 23
answers per provider against the expected claims, actual retrieved passages,
and the existing package rules:

| Provider | Combined Recall@5 | Answer support | Staff fallback |
| --- | --- | --- | --- |
| [Ollama `concierge-chat`](../tests/Concierge.Evaluation/results/2026-10-21-ollama-relevant-limits/run.json) | 20/20 PASS | 18/18 PASS | 5/5 PASS |
| [Foundry `gpt-5-mini`](../tests/Concierge.Evaluation/results/2026-10-21-foundry-relevant-limits/run.json) | 20/20 PASS | 18/18 PASS | 5/5 PASS |

The two earlier misses pass in these runs: Ollama's ZGY-MULTI-002 answer
includes the same-day cutoff and unavailable overnight storage; Foundry's
ZGY-NONE-003 answer says the hotel information does not establish front-desk
charger lending and directs the guest to the front desk. Both actually
retrieved the relevant passages (including the front-desk contact details for
ZGY-NONE-003). The other answerable and staff-fallback cases also passed manual
review. These results are consistent with the clarification but do not isolate
its effect from provider/run variation; earlier runs had also passed each
respective case.

The action prompt was exercised through the rebuilt worker using the live
Foundry web check. The first run passed 17/18: the model requested only
today's weather for "the next two days". Two prompt-side fixes were tried
live and rejected. A system-prompt hint still produced one-day calls in 3/3
runs. A tool-description or schema hint made ride requests draw "now or
later?" questions in 5/6 runs. Restoring the original schema passed 18/18
twice. The accepted fix is the workflow-side two-day backstop
(`action-two-day-weather-v1-{seq}`, see ADR 0009). With it, the check
passed **18/18 in three consecutive runs**, and the full Core suite passed
598/598. Voice and stale-history remediation remain outside this check.

A live session on 2026-10-21 asked for "the next 3 days in Plantation"; the
model called today, tonight and tomorrow and the workflow refused three calls
as unsafe. The backstop now covers "the next N days" (2–7): any all-GetWeather
response for one place is rewritten to one call per day from today
(`today`, `tomorrow`, `day+2`…`day+6`) and run in parallel, up to seven calls.
An explicit "starting", "beginning", or "from tomorrow" keeps tomorrow as the
first day ("the next three days starting tomorrow" is `tomorrow`, `day+2`,
`day+3`); a span that would pass `day+6` is shortened to end there. A weekday
start ("starting Friday", "from this Saturday", "starting on Monday") becomes
workflow-only `{weekday}+N` periods (`friday+0`, `friday+1`, `friday+2`). The
workflow does not hold the hotel calendar, so the weather activity resolves
each one against the hotel-local date: the weekday's next occurrence, today
included, plus N. A day past `day+6` returns `BeyondForecast` with its date and
is rendered as "The forecast for Saturday, Oct 24 isn't available yet"; it is
not reported as a failure. Starts the policy cannot map ("starting next Friday",
"starting the 25th", "beginning October 25", "from next Monday") are not
rewritten; the model's own calls run, and the reply names the days it covers.

**Open: model reliability.** In the first live run after this change
(2026-10-22), "What's the weather for the next three days starting tomorrow?"
got "Do you mean the weather at Hotel ZGY or somewhere else?" with no tool
call. The instructions say to default weather to Hotel ZGY and never ask, so
the model ignored them; the workflow rewrite never ran because there were no
weather calls to rewrite. The next run passed every scenario. One passing run
does not resolve this: it stays open until repeated live checks show an
acceptable rate.

Measured 2026-10-22 with `just live-weather-reliability` (20 rounds, 100 turns on
one stay, `gpt-5-mini`): the four phrasings below called `GetWeather` in 80 of
80 turns, and no turn asked where the guest meant. "What's the forecast for this
weekend?" (asked on a Thursday) got Saturday and Sunday in none of 20 turns:

| Weekend outcome | Turns |
| --- | ---: |
| `GetWeather("tomorrow")`: Friday only | 8 |
| `GetWeather` with no period: today only | 6 |
| `GetWeather("Saturday")` + `("Sunday")`, rejected by the period check | 4 |
| No tool call; repeated an earlier Saturday answer | 2 |

The phrasings that always called the tool were "…next three days starting
tomorrow?", "…like today?", "…next two days?" and "Will it rain tomorrow?". The
check counts tool calls, not whether the chosen days were right. With 0 of 20
failures for the original phrasing, the true rate could still be up to about
15% (rule of three). The weekend result is a reproducible defect, not
model noise: the schema has no weekday or weekend period, and the model's
natural weekday call is rejected.

**Weekend and weekday fix (2026-10-22).** The period schema now advertises a
weekday name ("its next occurrence, today included"), validated in any case.
"This weekend", "the weekend" and "weekend weather" (not "next/last weekend",
and not when a weekday is also named) rewrite any all-GetWeather response to
workflow-only `weekend+0` (Saturday) and `weekend+1` (Sunday). The activity
resolves them against the hotel-local date:

| Hotel-local day | "This weekend" fetches |
| --- | --- |
| Monday–Friday | The coming Saturday and Sunday |
| Saturday | Today and tomorrow |
| Sunday | Today only; Saturday returns `Elapsed`, rendered "Saturday has passed; today is the remaining weekend day." |

Every weather activity in a turn receives the turn's start time
(`Workflow.UtcNow`) as a trusted argument and computes "today" from it, so
parallel calls and retries that run after hotel-local midnight resolve the
same calendar. Open-Meteo is asked for one past day so that late retry can
still read its date. Days past `day+6` keep the `BeyondForecast` reply.

When the model's first response has no tool call but the guest asked a
weekend or "next N days" weather question and named no place other than the
hotel, the workflow makes the weather calls itself (`workflow-weather-d{n}`).
The action prompt now explicitly requires `GetWeather` for every weather
question, says to pass the guest's named place, and forbids reusing earlier
forecasts or general knowledge. A deterministic prompt test verifies that this
guidance is present even when an earlier forecast is in the conversation.
This is guidance, not enforcement: with a named place ("in Key West this
weekend"), the model's reply still stands if it omits the tool call. A
deterministic fallback would schedule weather activities on a no-tool workflow
path, changing the Temporal command sequence for that input; under the
no-patching law this requires an approved history reset. No reset was needed:
the named-place live acceptance check below passed 20/20 after the prompt and
resolver fixes. The no-tool path remains a model-compliance risk rather than a
structurally enforced guarantee.

Deterministic tests cover all seven hotel-local weekdays and assert the exact
next occurrence (today included), weekend outcomes, and a single supplied
hotel-local anchor even when the activity clock has advanced across midnight.
Live Saturday/Sunday validation is not available on Friday, 2026-10-23; those
branches remain supported by deterministic evidence until their respective
hotel-local dates.

**Acceptance check.** `just live-weather-reliability [stays] [rounds]` (default
5 fresh stays × 4 rounds × 6 phrasings) now fails unless every turn: ran
`GetWeather` that turn; got `Available` forecasts whose `localDate` set equals
the expected hotel-local dates (plus `Elapsed` Saturday on Sundays); and
answered with this turn's forecasts. For each expected day, the part of the
answer about that day must state that day's returned high and low (rounded °F)
and its conditions. Every temperature in the answer must come from this turn's
results, and the answer must name no other weekday, "today" or "tomorrow". A
reused earlier answer therefore fails on its temperatures even if it names the
right days. The Key West weekend phrasing also fails unless every weather
activity passes `Key West` as its `place`. It exits 1 on any failure.
`dotnet run --file
tests/Concierge.LiveChecks/weather-reliability.cs selftest` checks the checker
offline: it accepts a rendered answer and a model-written combined answer, and
rejects stale temperatures, swapped days, a foreign temperature, day names with
no forecast, and wrong conditions.

Run 2026-10-22 (Thursday, `gpt-5-mini`, after `just reset`): **100/100 passed,
20/20 per phrasing**. In all 20 weekend turns the model called `GetWeather`
itself: 16 with `Saturday` and `Sunday`, 4 with `Saturday` only; the weekend
rewrite added Sunday in those 4. Synthesis did not fire live; it is covered
only by the deterministic test. Limits: this run was on a Thursday, so the
Saturday and Sunday branches are verified only by deterministic tests; 0 of 20
failures per phrasing still allows a true failure rate up to about 15%; at the
time, the location-clarification miss from the first live run had not been
reproduced; voice was not measured. The 2026-10-23 run below reproduces a
named-place resolution failure.

Rerun 2026-10-22 with the forecast-content comparison (same model, 5 × 4):
**100/100 passed**. The check does not record whether each answer was rendered
by the app or written by the model, so it does not show how often the model's
own wording would pass.

Run 2026-10-23 with the named-place phrasing added (5 fresh stays × 4 rounds ×
6 phrasings): **100/120 passed**. All 20 hotel-only turns for each of the five
existing phrasings passed. All 20 Key West weekend turns called `GetWeather`
with a Key West place argument, so the no-tool behavior did not recur in this
sample. However, none returned an `Available` forecast; the guest-facing
answer asked the guest to clarify Key West. The acceptance run therefore
reproduced a named-place resolution failure, not a missing tool call.

**Diagnosis and source fix (2026-10-23).** The clarification branch is produced
when `GetWeather` returns `WeatherResultStatus.NeedsClarification` with no
candidates; `OpenMeteoWeatherService` returns before requesting Open-Meteo.
`GeoapifyPlaceResolver.ResolveAsync` intentionally restricts local destination
lookups to Florida and then to Miami-Dade County or within 60 km of Hotel ZGY.
Geoapify's valid Key West match is in Monroe County and beyond that radius, so
the confidence-qualified record was filtered out for each `Key West` spelling.
That left no candidates and yielded the clarification response.

Weather now uses a weather-specific resolver path that skips only this
hotel-area restriction. The existing confidence threshold, input validation,
and unique-match requirement remain; route resolution continues using the
Miami-area restriction. The deterministic regression first confirms the
ordinary local resolver rejects the Monroe County result, then confirms the
weather service returns `Available` forecasts for both Saturday and Sunday
with Key West coordinates. The `Key West`, `Key West, FL`, and
`Key West, Florida` regression passed **3/3**; all `PlaceWeatherTests` passed
**40/40**. `DrivingRoutesTests` passed **18/18**, confirming route resolution
keeps its local restriction.

The post-fix live check on 2026-10-23 passed **120/120** across 5 fresh stays,
4 rounds, and 6 phrasings (20/20 each), including Key West weekend forecasts.
The named-place calls used the requested place, returned both expected weekend
dates, and the answers matched the returned forecast content. Saturday-as-today
and Sunday-as-today behavior remains verified only by deterministic tests
until those hotel-local dates.

ADR 0009's "Step 6 live-check amendments" section records the behavior changes
found live: the action-turn budget (now 21,000 tokens), argument-free ConfirmRide, unrequested
ride-action handling, hotel-local time context and status-based ride lists.

## Nearby places and stay dates (2026-10-21)

`FindNearbyPlaces` answers "nearest pharmacy"-style questions. The model passes
one `category` from a fixed schema `enum` (pharmacy, grocery, convenience,
restaurant, cafe, bar, fast_food, atm, bank, hospital, clinic, gas_station,
mall, museum, cinema, park, beach, attraction, post_office). The tool-call policy
rejects any other value with a curated reply, before an activity runs. The
activity maps the key to
[Geoapify Places categories](https://apidocs.geoapify.com/docs/places/#categories)
and sends one request for places within 8 km of the hotel coordinates
(`filter=circle`, `bias=proximity`). It keeps the three closest named places.
The search origin is always Hotel ZGY. The workflow renders the reply itself:
a numbered markdown list on web, plain sentences on voice, or a curated
no-results or failure reply. There is no extra model call.

`GetPlaceOpeningHours` is a separate named-place lookup for questions such as
"What are Wayan Miami's hours?" It accepts one place name, searches Geoapify
Places by that name using the nearby-search categories and a 60 km Hotel ZGY
search area, and returns only one match's provider-listed
`opening_hours` ([Geoapify Places API](https://apidocs.geoapify.com/docs/places/)).
The workflow renders that value directly (for example,
`17:30-21:30; Th 17:30-22:00; Fr,Sa 17:30-22:30`). When the place is found but
the provider has no hours, the reply says they are not listed; it does not
substitute a nearby-place list. No match returns a name-specific not-found
reply, while multiple distinct matches prompt the guest to clarify. Duplicate
records for one venue (for example an OSM node and its building) merge only when
they share the venue name, house number, and street and lie within 75 m; nearby
branches and differently named neighbours stay separate and are listed with their
street addresses. The
model-callable lookup is covered by the live Step 6 check, which verifies
`GetPlaceOpeningHours` in Temporal history and rejects a nearby-search fallback.
Two live runs on 2026-10-22 returned Wayan Miami's provider-listed hours this way.

Verified guest context now also carries hotel-local check-in and checkout dates,
with their weekdays, and a server-computed `Nights`, so "how long is my stay?"
is answered from verified data. Both changes add to the fixed action context.

### Action-turn budget (2026-10-22)

`ActionContextBudgetBoundaryTests` builds the worst supported action turn:

- given and family names of 100 characters that JSON-escape to six bytes each,
  and the widest possible stay dates (year 1 to 9999, a seven-digit `Nights`),
  since stay length has no limit;
- a 2,000-byte guest message;
- stay memory whose *formatted* text — labels, sequence and supersedes
  references, and the unresolved-request note — is exactly 1,024 bytes (eight
  labelled items reach it; the byte cap binds long before the 32-item cap);
- a ride-offer note with 200-byte place names, every tool including `end_chat`,
  the 1,024-token answer allowance, and the 512-token estimation headroom.

It then adds two single-tool rounds, the most the workflow allows for
single-call tools, each with arguments at the 2,000-byte validation cap and a
result at the 2,048-byte compact cap. Estimated tokens (UTF-8 bytes plus
allowances, not provider counts):

| Stage | Required context | With stay facts | With full profile |
| --- | ---: | ---: | ---: |
| First model call | 12,568 | 12,778 | 14,240 |
| After one maximum single-tool round | 16,649 | 16,859 | 18,321 |
| After two maximum single-tool rounds | 20,730 | 20,940 | 22,402 |
| After a seven-day weather round (512-byte results) | 18,035 | 18,245 | 19,707 |

"Required context" is what trimming cannot remove: instructions, memory, the
offer note, the guest message and tool results, with evidence at its
placeholder. Once tool results need room, earlier turns are trimmed first. The
full profile is then replaced by *stay facts* (about 200 bytes): the same
verified room, floor, check-in and checkout dates and nights, without names,
and an instruction to use no guest name. Evidence is trimmed last. Stay facts
are never removed, so every model call in a turn has the verified stay values.
Only the guest's names are lost, and the model is told not to use one. Ride
tools take identity from workflow state, not the prompt.

The earlier 12,000-token budget rejected this turn before the first model
call. The budget is now 21,000, the smallest round figure that fits two maximum
single-tool rounds with stay facts. The first call keeps the full profile;
after two maximum rounds the final call has stay facts instead, at 20,940 of
21,000. That 60-token margin is small: any growth in instructions, schemas or
the stay-facts text breaks the worst case, and the boundary test fails when it
does. The test also adds maximum rounds past the workflow's limit. Optional
context is then trimmed and `FitActionContext` fails, so the workflow returns
its context-limit reply. It never drops memory, stay facts, the guest message
or tool results.

The budget does not cover every allowed tool combination. Parallel weather
results are capped per result (2,048 bytes), not per round. In the worst-case
turn, only three cap-size weather results fit; a fourth gets the context-limit
reply (`FullWeekWeatherAtTheResultCapFailsClosedWithStayContextRetained`).
Observed weather results are about 350 bytes, and a round whose results all
render directly makes no further model call. This bounded fallback is accepted
for the demo.

Startup validation checks the declared `AI:Chat:ContextWindowTokens`, not what
the deployed model actually accepts. The declared values come from Foundry's
documented 272,000 input tokens for `gpt-5-mini`
([Foundry models](https://learn.microsoft.com/azure/ai-foundry/foundry-models/concepts/models-sold-directly-by-azure))
and the Ollama `concierge-chat` Modelfile's `num_ctx 32768`. Neither was
measured against the running model. A deployment with a smaller real window
would pass validation and fail at the provider.

This work is about context capacity. It does not show that replies are faster,
and a larger allowed context can make long turns slower.

**Historical open items from this budget-validation pass.** The later current
acceptance review supersedes their status; the original observations remain
below for context. Current timeout/provider/recovery evidence is recorded there.
Saturday/Sunday calendar branches also have controlled-clock coverage; physical
browser voice and stable tail-latency claims remain qualified.

**Items recorded at that time:**

- Timeout evidence: captured Foundry replays were analyzed offline; the
  historical `gpt-5-mini/low` sample has a 24.467 s P95 but incomplete failure
  durations, and no captured weather, route, or RideService requests are
  available. No timeout values were changed; the gate remains open.
- Weather reliability: after the final prompt shortening, the live acceptance
  check passed **120/120** on Friday 2026-10-23, including 20/20 Key West
  weekend turns per phrasing. The same-day Saturday/Sunday branches still need
  live checks on those hotel-local dates. The separate "Hotel ZGY or somewhere
  else?" clarification miss remains unverified.
- Flaky tests: 39 targeted/related executions passed without reproducing the
  earlier NewStay, weather-timeout, or unavailable-feed failures. The
  queued-tool deadline test no longer asserts that Temporal records the
  timeout event within a narrow elapsed-time window; it verifies the scheduled
  timeout, safe timeout result, and that queued tools never start, including
  after the blocker is released. The blocker timeout now exceeds the test
  watchdog. The full Core suite then passed **731/731**. Temporal's delayed
  recording of timeout events under suite pressure remains unexplained.
- Voice latency: representative voice turns have not been re-measured since
  the budget and instruction changes.

Names share `ConciergeVerifiedGuest.MaxNameLength` = 100 characters. Setup
rejects longer seeded names, and verification (web form, API and session
activity) rejects a longer last name. The last-name limit was already 100;
given names had no limit before.

## Current staged ride design (2026-10-21)

The approved fix removes natural-language booking, cancellation, and
confirmation phrase checks. The model's tool selection classifies intent;
workflow-owned state still gates every side-effect boundary:

- `ProposeRide` resolves and stores one ride offer and never books. `ConfirmRide`
  executes only when the model calls it to indicate clear confirmation and the
  workflow still holds the pending proposal within its two-guest-turn lifetime.
  The workflow supplies the stored proposal ID, not model arguments.
- `ProposeRideCancellation` reads one verified, scoped ride and stages a
  specific offer naming its destination and pickup time. It never cancels.
  `ConfirmRideCancellation` can execute only while the workflow holds that
  pending cancellation offer within its two-guest-turn lifetime; the workflow
  supplies the ride ID. An uncertain cancellation attempt consumes the offer.
- If the model needs a ride ID, `GetRideHistory` returns it only in tool
  context, then the model selects a specific ride for the staged offer. IDs
  remain absent from guest-facing replies.
- There are no workflow patch markers or legacy phrase-policy branches. The
  workflow retains offers across Continue-As-New; the database and workflow
  history were reset before deploying this incompatible demo change.

The action prompt changes only to describe this tool contract. The fixed prompt
allowance remains 1,536 tokens; the action budget is described above.
No hotel-Q&A evaluation package, cases, or rubric were changed.
Model and tool calls execute as Temporal activities. Model activity failures
are retried up to three attempts and converted to a guest-facing fallback if
all attempts fail; weather retries transient provider failures and the other
tool activities remain single-attempt.

## Current live gate status (2026-10-21)

The AppHost was restarted after deleting the `temporal-data` and
`concierge-postgres-data` Docker volumes. The new Temporal namespace contained
only the fresh completed `Concierge.HotelSetup` workflow at verification time.
The smoke check (`caffeinate -i dotnet run --file
tests/Concierge.LiveChecks/concierge-actions-smoke.cs`) passed runs 1 and 3,
including booking after a pause and "Book it for right now." Run 2 failed
multiple expectations, including turn 18 where the smoke expected
`ConfirmRide` but the model made no tool call. Therefore repeatable live
acceptance is **not established**; the run-2 intent misses need investigation.

## Automated evidence

The workflow tests cover model-selected contextual proposals, no booking from
a proposal, offer confirmation within two turns, stale and consumed offer
rejection, staged cancellation, history lookup with tool-only ride IDs, and
trusted guest/stay scope. Tests also cover durable activity dispatch and
paired results, two-round/three-call exhaustion, action context/result bounds,
deadlines, `end_chat`, voice farewell, model retry exhaustion, and readable ride
results. Old-version replay fixtures were removed because the project owner
chose a database/workflow reset instead of patch-based compatibility.

The full Core suite passed **573/573**; the focused workflow/policy suite
passed **78/78** after the final timeout-allocation adjustment. Live runs 1
and 3 passed; run 2 failed multiple expectations. Functional live evidence
does not close the separate provider/tool timeout-evidence gate above.

## Step 6 status update (2026-10-22)

The first fresh-history `just live-concierge-actions` run, before the safeguard
described below was added, exercised the full scenario set and failed one
assertion. After a South Beach ride was booked, the guest said "Yes, please";
the model called `ProposeRide` again for the same destination and pickup time.
The other scenarios, including Wayan and Walgreens, proceeded, and no other
failure was reported. The run output was in the session transcript; it was not
saved as a repository log.

The workflow now closes that specific consumed offer deterministically:
`ConciergeStayWorkflow` blocks a `ProposeRide` matching the pickup,
destination, and schedule of the immediately just-consumed offer before the
activity runs, then returns a curated response. Changed ride details remain
eligible. The regression test is `BookedOfferIsClosedOnTheNextTurn`. This
addresses the observed repeated-proposal path; it does not establish live
acceptance.

Post-fix automated evidence:

- `ConciergeActionToolWorkflowTests`: **54/54 passed** (2m03s).
- Latest full Core suite: **726/726 passed**, 0 failed and 0 skipped (3m07s).
- `git diff --check` passed.
- `DurableToolActivityTimeoutReturnsSafeResultAndContinuesTurn` and
  `UnavailableFeedCannotStopOverdueRideLifecycleBeyondFourPublicationDeadlines`
  each passed in isolated reruns (39s and 1m20s). Their earlier suite failures
  were not reproduced in isolation; the cause remains unknown.
- The queued-tool regression originally observed a delayed timeout event
  (786.665s instead of the expected 170–195s) while four local Temporal
  environments ran concurrently. Its harness now uses a single host/local
  environment, preserving the 180-second timeout and the no-execution and
  usable-next-turn assertions. Its focused rerun passed **1/1** (3m02s), and
  the latest full suite passed. This does not identify the original delay's
  cause.

**Live acceptance remains OPEN.** There has been no post-fix live acceptance
rerun. The AppHost is still running pre-fix workflow code. Because this
workflow change has no patch-based replay compatibility, deployment requires
another `just reset` against fresh history. That reset deletes the AppHost's
Temporal and PostgreSQL volumes; approval for this second deletion has not
been given. Do not treat the earlier live run or automated tests as a
post-fix live pass.

Other acceptance gates remain open: there are no empirical provider/tool
timeout percentiles or captured-request replay evidence; voice latency and
content have not been revalidated against the current prompt and budget; and
weather still has the documented named-place/no-tool and Saturday/Sunday live
limitations, plus the location-clarification miss that has not been
reproduced. `NewStayWorkflowContinuesAfterPreviousWorkflowWasTerminated`
failed in an earlier full-suite run and later passed; its cause remains
unknown despite the latest full-suite pass. These gates, and the pending
post-fix live run, mean Step 6 is not established as finished.

### Follow-up validation (2026-10-22/23)

The project owner approved the second reset. `just reset` succeeded, the exact
Concierge AppHost was restarted, and WorkflowServer and web became healthy. The
AppHost therefore loaded the first duplicate-offer safeguard for the second
live check. Post-fix `just live-concierge-actions` run 2 was captured at
`/Users/cecilphillip/.copilot/session-state/b647e2a6-9996-46ec-9a47-d8383fe3dedc/files/step6-actions-run-2.log`.
It **failed four smoke assertions around turns 29–31**: the safeguard was too
broad and blocked an explicit request to book the same South Beach ride
immediately after booking. The following "Book it for right now" then
re-proposed it, and "Yes, please" confirmed it. Other turns continued to Wayan
and Walgreens. This was not a passing live run.

The safeguard was subsequently narrowed: it blocks an identical just-consumed
offer only when the current guest text is an exact normalized bare
acknowledgement from a small allowlist. Explicit requests to book the same
destination remain model-driven and allowed. New tests cover both rejection
after an acknowledgement and explicit same-route rebooking plus confirmation.
No phrase matching was reintroduced to decide general ride intent or
confirmation.

After this latest source change, Artemis reported
`ConciergeActionToolWorkflowTests` **55/55** and
`ConciergeToolCallPolicyTests` **68/68**. The independently run full Core suite
passed **727/727**, with 0 failed and 0 skipped (3m06s); `git diff --check`
passed. The rewritten
`QueuedToolsExpireAtWholeTurnDeadlineAndNeverExecuteAfterSlotRelease` uses one
local Temporal environment rather than four concurrent environments; its
focused test passed **1/1** (3m02s), and the full suite now passes. Earlier
failures in the weather-timeout and unavailable-feed ride-lifecycle tests
passed in isolation, but the cause of their prior suite failures remains
unknown.

**Live acceptance remains OPEN.** There has been no live check against the
latest narrowed safeguard. The AppHost is still running the earlier, broader
safeguard because its source changed after the second reset. Since workflow
history is intentionally reset rather than patched, a third `just reset` is
required before loading the latest code; approval for that reset has not been
requested or given. Do not treat the second live run as a pass.

Other open gates remain: provider timeout percentiles and captured-request
replay evidence are missing; voice latency and spoken-answer correctness have
not been revalidated against the latest prompt and budget changes; weather
reliability still has the named-place/no-tool-call limitation (a workflow-order
fix is blocked without an approved history reset) and Saturday/Sunday live
limitations (those dates have not yet occurred), plus the prior
location-clarification miss. The cause of the
intermittent `NewStayWorkflowContinuesAfterPreviousWorkflowWasTerminated`
failure remains unknown despite the latest full-suite pass. Step 6 is not
claimed finished.

### Follow-up after the third reset (2026-10-22/23)

The project owner explicitly approved the third reset. `just reset` succeeded;
the exact Concierge AppHost restarted, and `workflow-server` and `web` became
healthy with the latest narrowed safeguard loaded.

`just live-concierge-actions` then passed three consecutive times against the
latest unchanged code: runs **3, 4, and 5**. Each exited 0 and printed
“Step 6 live check passed.” The logs are outside the repository:

- Run 3:
  `/Users/cecilphillip/.copilot/session-state/b647e2a6-9996-46ec-9a47-d8383fe3dedc/files/step6-actions-run-3.log`
- Run 4:
  `/Users/cecilphillip/.copilot/session-state/b647e2a6-9996-46ec-9a47-d8383fe3dedc/files/step6-actions-run-4.log`
- Run 5:
  `/Users/cecilphillip/.copilot/session-state/b647e2a6-9996-46ec-9a47-d8383fe3dedc/files/step6-actions-run-5.log`

Each run used a random temporary guest/stay and unique workflow ID; no reset
was performed between these three runs. All three exercised both sides of the
ride regression: an explicit new request for the same South Beach ride
immediately after booking was allowed and confirmed, while the following bare
“Yes, please” caused no `ProposeRide` or `ConfirmRide`. Runs 3 and 5 returned
curated/no-offer answers for that acknowledgement; run 4 asked for
clarification. Each was an allowed no-action outcome. The suite also exercised
current weather, route, nearby pharmacy and cinema, Wayan Miami hours, stay
dates, ride booking/history/cancellation, weekend/weekday dates, and Wayan and
Walgreens proposals. Three passing action runs establish a repeatable
functional live gate for these checks, not representative statistical
reliability.

The **live action gate is now repeatably passing 3/3**. This does not close
broader Step 6 acceptance. Captured-request replay and provider-timeout
percentile evidence remain absent. Voice latency and content have not been
revalidated against the latest prompt/budget; prior voice-session logs include
latency but no answer transcript. Weather still lacks live Saturday/Sunday
branch validation (those dates have not yet occurred) and post-fix live
validation of Key West weather resolution. The 2026-10-23 run's 20 Key West
turns all called `GetWeather` but returned `NeedsClarification` because the
shared resolver applied its Miami-area filter; the weather-specific resolver
fix is covered deterministically but has not been live-validated. Prompt
guidance alone still does not enforce a tool call, and a deterministic no-tool
fallback remains blocked because its workflow-order change requires a
separately approved history reset. The earlier hotel-location clarification
miss remains unreproduced. The intermittent
`NewStayWorkflowContinuesAfterPreviousWorkflowWasTerminated` cause remains
unknown despite the latest full-suite pass. The latest automated evidence
remains: full Core suite **727/727**, 0 failed and 0 skipped; action workflow
tests **55/55**; tool-policy tests **68/68**; and the focused deadline-queue
test **1/1**. The single-local-environment harness is included in the full
suite. These results do not establish that all Step 6 gates are closed.

## Timeout and retry consistency (2026-10-23)

The current values are in [Implemented bounds](#implemented-bounds-timeout-and-retry-policy-2026-10-23).
The decision is recorded in ADR 0009, "Timeout and retry policy amendment
(2026-10-23)".

### Policy changes

- **One retry owner per call.** `ServiceDefaults` no longer adds the Aspire
  standard resilience handler to every `HttpClient`; service discovery is kept.
  Location providers have no HTTP retry (Temporal retries weather only). The
  ride client sets its policy per method. The startup geocoder has its own
  policy because no activity wraps it. Chat and embedding SDK clients send
  each request once (`maxRetries: 0`).
- **Inner limits follow the attempt.** Model and embedding calls are capped at
  the attempt's remaining time less 2 s (`ActivityCallBudget`). Tool activities
  cancel provider and ride calls 0.5 s before their start-to-close
  (`RemainingActivityAllowance`).
- **Ingestion has its own bound.** `AI:Embeddings:IngestionRequestTimeoutSeconds`
  (default 60, max 298) applies to setup embeddings. Retrieval keeps the 8 s
  `RequestTimeoutSeconds`.
- **The ride booking workflow no longer hangs.** When the store activity uses up
  its 8 s cap, the workflow waits 2 s, then runs `reconcile-booking` to read the
  stored outcome. It never resends the booking. Found in review (Ares).

### Evidence

Method: Temporal history plus OTel spans, as in ADR 0008 decision 1. Files:
`.artifacts/reviews/2026-10-23-timeout-baseline/` (`baseline.md`, `after.md`,
`extract.py`).

| Measure (seconds) | Before | After |
| --- | --- | --- |
| Guest turn, median / P95 / max | 3.22 / 8.30 / 21.26 | 3.29 / 7.09 / 22.55 |
| Chat attempt, P95; attempts cut at 15 s | 6.78; 2 of 159 | 5.21; 1 of 155 |
| Slowest successful chat attempt | 10.28 | 12.88 |
| Embedding P95 / max | 0.78 / 1.04 | 0.53 / 1.04 |
| `GetWeather` terminal failures | 2 | 0 |
| SDK-level retries | 0 | 0 |

- The worst case did not improve: one 15 s chat cutoff plus its retry still
  gives a turn of about 20–23 s.
- A 15 s chat limit leaves about 2 s above the slowest successful attempt
  (12.88 s).
- Open-Meteo failed differently in each run (503s before, 5 s timeouts after),
  so part of the weather gain is provider variance.
- The historical 24.5 s replay P95
  (`.artifacts/reviews/2026-10-16-voice-regression/model-replay-review.md`)
  was not used to choose values. Its failure durations are incomplete.

### Verified behavior

- Full Core suite after all review fixes: **794 passed, 0 failed, 0 skipped**.
- `just live-weather-reliability 5 4`: **120/120** after the change. This run
  was on the build deployed before the review fixes. The final build also
  passed **120/120**.
- Ride lifecycle smoke: **PASS** on the final build.

### Failures

- **Historical failure, since addressed in the latest live run:** the earlier
  `just live-concierge-actions` runs reported 47 failed assertions after the
  timeout/retry change (baseline output was truncated at at least 39). Their
  histories showed mixed `GetWeather` calls, but the subsequent weather-
  instruction restoration eliminated those calls in the captured follow-up.
  The remaining failure was a same-route ride request selecting history rather
  than a new proposal; that behavior is addressed and validated below.

### Measurement status after acceptance review

- **Technical timeout review: PASS WITH DEMO LIMITATIONS; accepted for the demo
  by Cecil on 2026-10-24.** Later evidence includes 16 concurrent successful requests, direct
  Geoapify/nearby/hours measurements, live cancellation and pending-offer changes,
  representative ingestion-client timings and a real disposable-store recovery
  drill. These are bounded samples, not stable tail statistics or a new API for
  modifying an already-booked ride. The 88 current timeout/recovery tests pass.
  [Current measurements and decision](../.artifacts/reviews/2026-10-23-step6-acceptance/results.md).
- **Ollama: not a Step 6 blocker** (owner decision, 2026-10-23). Ollama chat
  and embedding latency, including cold model load, is unmeasured; both runs
  used Foundry. The Development chat limit is 18 s, not 180 s, so load local
  models before use.
- **Action acceptance: validated for the latest run only.** The updated prompt
  selection rule passed the full live action check once, including the
  previously failing same-route rebooking chain. This is not evidence of
  statistical reliability or closure of the broader Step 6 gates; see
  "Latest action-selection fix and validation" below.
- **Local secret migration.** Startup validation now rejects
  `AI:Embeddings:RequestTimeoutSeconds` above 8. The maintainer's user secret
  was lowered from 60 to 8. An older secret above 8 stops the worker with a
  message naming the key and the limit; lower it or remove it.

## Approved weather-instruction restoration (2026-10-23)

The owner approved restoring the earlier weather sentence and deferring tool
description refinement. Only that sentence changed in application code;
two existing prompt-text tests were aligned with it. The tool descriptions,
schemas, model settings, budgets and workflow side-effect boundaries are unchanged.

The preceding controlled comparison used 149 valid model requests. Earlier
wording passed 27/27 isolated checks versus 12/27 for the current wording;
single-tool mode alone passed 9/27. Across 34 captured inputs per wording,
unrelated weather calls fell from 21 to 0 and mixed batches from 11 to 0.
These were model-selection experiments, not workflow acceptance.

After restoring the instruction, the workflow-server resource rebuilt with
zero warnings/errors and became healthy. Targeted prompt, budget and tool
regressions passed **20/20**. Temporal history verifies the deployed sentence
in all 34 model requests of the real 33-turn action run. That run had **zero
mixed batches and zero unrelated weather calls**, but exited 1 with four
ride-sequence assertion failures. A new same-route booking request selected
history instead of a proposal, causing later expectations to diverge; the
subsequent confirmation used a real pending offer rather than bypassing a guard.

The full `just live-weather-reliability 5 4` check passed **120/120**, exiting
0: five fresh stays, four rounds and six phrasings. Each phrasing passed
20/20, including hotel and Key West weekend requests. The check verifies
requested dates and answers against each turn's returned forecasts rather
than tool invocation alone. The weather-routing correction is verified in
these scenarios. At this point in the recorded sequence, the action gate
remained open because of the ride-selection failure; the later action-selection
validation is recorded below. No reset or commit was performed.
Details and retained evidence:
[restoration verification](../.artifacts/reviews/2026-10-23-action-mixed-investigation/fix-verification/results.md).

## Latest action-selection fix and validation (2026-10-23)

A controlled replay of captured turn 29 used the original request, tool
schemas, and provider, with three repetitions per prompt arm (Foundry
`gpt-5-mini`, low reasoning; no tools executed). Baseline selected
`ProposeRide` 1/3, `GetRideHistory` 0/3, and no tool 2/3 (median 4.11 s,
range 2.53–5.08). The experiment did **not** reproduce `GetRideHistory`
selection. Results for the prompt variants:

| Prompt arm | Turn-29 selection | Median latency (range) |
| --- | --- | --- |
| Closed-offer clarification only (A) | `ProposeRide` 3/3 | 1.79 s (1.59–1.91) |
| History-intent clarification only (B) | `ProposeRide` 2/3 | 3.32 s (2.56–5.47) |
| Both clarifications (A+B) | `ProposeRide` 3/3 | 2.93 s (2.56–3.22) |

Only A was implemented. The added wording is: “Do not propose or book the
already-booked ride again. If the guest makes a new explicit request for
another ride, even to the same destination, call ProposeRide.” The ambiguous
history wording and tool descriptions were left unchanged; the existing
confirmation safeguard (“Do not call ConfirmRide or
ConfirmRideCancellation.”) remains. These few replay samples make A
suggestive, not a proven cause or a guarantee of reliable model selection.
The baseline replay's failure to select history also means the cause of the
original live selection is not established.

Validation:

- Focused workflow tests for `BookedOfferIsClosedOnTheNextTurn` and
  `ExplicitSameRideRequestAfterBookingCreatesAndConfirmsNewProposal`: **2
  passed, 0 failed, 0 skipped**.
- `dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs
  selftest`: **2 passed, 0 failed, 0 skipped** (root action-selection failure
  counted once; exact South Beach three-turn chain blocks downstream actions).
- Final deployed build, `just live-concierge-actions`: **exit 0; 33 passed, 0
  failed, 0 skipped**. The previously booked South Beach ride → explicit
  same-route request → `ProposeRide` → `ConfirmRide` → bare “Thanks.” chain
  passed without an extra ride tool. No stray `GetWeather` was injected into
  ride action selection.

This is one full live run, not statistical reliability. The timeout gate
remains open, and this result does not claim all Step 6 acceptance gates are
closed. Replay samples and output are retained at
`.artifacts/reviews/2026-10-23-action-mixed-investigation/turn29-prompt-experiment/`.

## Acknowledgement regression and retained verification (2026-10-23)

The exact “Yes, please.” live case is restored alongside “Thanks.” Each has
its own proposal → successful confirmation prerequisite chain; the second
acknowledgement is not merely appended after the first, when the
just-consumed-offer marker would no longer apply. Dependency failures still
fail their root scenario and block the dependent turns.

The expanded `just live-concierge-actions` run passed **36 scenarios, 0 failed,
0 skipped**, exit 0. These are scenario counts, not individual assertion
counts. `BookedOfferIsClosedOnTheNextTurn` and
`ExplicitSameRideRequestAfterBookingCreatesAndConfirmsNewProposal` passed
**2/2**, with both TRX outcomes parsed. The smoke self-tests passed **3/3**,
including a blocked-chain case for each acknowledgement.

Retained Temporal history verifies both acknowledgements immediately followed
their own successful booking and executed no ride tools. On “Yes, please.”,
the model nevertheless requested `ProposeRide`; the existing deterministic
guard blocked dispatch. On “Thanks.”, the model selected no tools. The safety
check passes, but model selection is not uniformly correct. Keep the guard.

The prior team's reported 33-scenario run was not recovered; this follow-up
retains a new expanded run. No production code, tool descriptions, model
configuration or safety guards changed. No full-suite, voice, concurrency or
timeout acceptance checks were run. The original selection cause remains
unproven, and the timeout gate and broader Step 6 acceptance remain open.

[Verification report and raw evidence](../.artifacts/reviews/2026-10-23-action-mixed-investigation/acknowledgement-verification/results.md).

## Current-build bounded signoff measurements (2026-10-23)

The project owner approved the bounded signoff pass. No production or maintained
test source changed; all 153 files in the recorded source/config/test manifest
match before/after. The current Core suite passed **794/794**, with parsed TRX
and no skips. Comprehensive weather passed **120/120** across five fresh stays,
four rounds and six questions. Controlled-clock weekend cases passed for all
seven weekdays. These do not require waiting for a real Saturday or Sunday.

Two waves of eight simultaneous fictional stays completed **16/16 HTTP 200**
with the worker's two chat slots. Guest turns had a 5.98 s median and 12.04 s
maximum. Chat slot waits reached 7.434 s; embedding waits reached 1.565 s.
Direct production-client Geoapify header timings and whole-operation timings
are now captured, including nearby places and hours. Whole operation maxima
were 1.046 s for routes, 0.537 s for nearby places and 0.914 s for hours.
Synthetic ingestion payloads of 400/800/1,600 characters returned 768-dimensional
vectors in 0.522/0.255/0.292 s. No actual-corpus ingestion timing is claimed.

Seven further web turns passed, including changing a pending pickup from 10 AM
to 11 AM before confirming and then cancelling it. This is a proposal change,
not a new booked-ride modification feature. A separate real PostgreSQL outage
drill committed a booking, withheld completion and disabled connections to a
disposable test database. Admission failed after 7.081 s; reconciliation saw
SQLSTATE 55000, recovered the original row 1.018 s after restoration, and the
production cancellation service produced exactly `Booked → Cancelled`.
Feed publication was stubbed and admission used a direct Workflow Update;
the running app's database was untouched.

**Voice remains qualified.** Generated speech traversed the real Web relay,
Deepgram ASR/TTS, Think tunnel and Temporal actions. Required weather, route,
book/confirm, status, staged cancellation, history and End paths ran; End
returned 204 and the credential subsequently returned 401. Nine spoken inputs
became 11 distinct completed turns. `Yes, cancel that ride.` was split into
`Yes.` (which cancelled once) and `Yes. Cancel that ride.` (which then said it
could not find an upcoming ride to cancel). The goodbye input also split.
This is not evidence of duplicate admission of one turn ID, and no duplicate
mutation occurred, but the cancellation follow-up is misleading for the guest.
The probe's exit 0 checks transport/tool presence, not reply coherence. Physical
microphone/speaker playback and interruption timing were not tested.

One successful web proposal took **20.335 s**, including a model activity of
18.955 s across two attempts; the tool itself took 0.888 s. The original first
failure was truncated in Temporal history, so its cause is not established.
These bounded samples do not establish stable tail percentiles or an SLO.
Numeric settings remain unchanged and provisional. No acceptance gate was
waived; **Step 6 remains open** pending the voice-coherence decision and review
of the provisional timeout evidence.

[Signoff report, commands and raw evidence](../.artifacts/reviews/2026-10-23-step6-signoff/results.md).


### Confirmed cancellation recall follow-up (2026-10-23)

The stay workflow now retains one typed, confirmed cancellation outcome, bound
to the workflow-owned ride and carried through Continue-As-New. Only the ride
service's committed 204 response produces the confirmation marker; response
text, a repeated ordinal, errors and uncertain outcomes do not establish success.
The voice text fingerprint and admission semantics are unchanged.

Later model context and rendered empty-history replies preserve that outcome.
A repeated confirmation does not cancel again. A replacement proposal sharing
the earlier trip's spoken destination label and pickup instant is explicitly
rendered as a **new booking requiring fresh confirmation**, with the earlier
cancellation stated. That display comparison never merges venue/ride identities
or authorizes an action; other destinations and times retain their normal flow.
No maintenance model call or delay was added; the 21,000-token budget is unchanged.

Verification: **194/194** targeted tests, **36/36** live action assertions and
**13/13** history replays with the production tracing interceptor passed. The
real-Deepgram relay probe reproduced `Yes.` followed by `Yes. Cancel that ride.`;
the later answer correctly reported the cancellation. One booking and one
cancellation activity ran; End returned 204 and the credential then returned 401.

**Still open:** the model sometimes treats “Actually, don't cancel it” as a
replacement request. Its response now states that the earlier cancellation has
already happened and asks for separate new-booking consent; no replacement is
booked automatically. Eliminating that unwanted proposal, broader revision /
reconnect semantics, physical browser audio interruption verification and review
of provisional timeout evidence remain outside this scoped fix. **Step 6
acceptance remains open.** Failed probes are preserved alongside the final run.

[Cancellation recall report and exact evidence](../.artifacts/reviews/2026-10-23-cancellation-recall/results.md).


### Cancellation intent and request-memory correction (2026-10-23)

The preceding unwanted-replacement finding now passes a strict fresh voice
check. The trusted cancellation note explicitly explains that “don't cancel”
cannot undo a committed cancellation. Typed booking/cancellation confirmation
also hands off only its workflow-owned source request from `unresolved-request`
to `request-history`; unrelated requests remain unresolved. This preserves the
original guest text and says a linked action was confirmed, without claiming
that every part of a compound request was fulfilled. ADR 0005 records the handoff.
Uncertain results and model prose do not establish success; old history without
authoritative source provenance is not retrospectively reclassified.

Controlled replay recorded 0/5 unwanted proposals with explicit negation and
0/5 with the combined prototype, versus 2/5 plus a timeout for baseline. The
memory-only arm produced 4/5 unwanted proposals, so stale memory alone is not a
proven cause. These are small samples, not reliability rates.

Verification: **122/122** targeted tests, **14/14** saved-history replays with
production tracing, a zero-warning/error solution build and healthy rebuilt
workflow server. The strict Deepgram relay run passed eight synthetic utterances
(ten completed turns), including the split “Actually,” → “Actually, don't cancel
it.” The completed negation made no ride-tool call and reported that cancellation
had already happened. A later explicit same-route request produced a fresh,
unbooked proposal. Exactly one booking and one cancellation ran; End returned
204 and the credential subsequently returned 401. Complete-update correlation
and actual model context confirmed the memory handoff; no workflow task failed.

This verifies the reported case in one fresh generated-speech session. Physical
browser audio/interruption timing, broader reconnect/revision handling and the
provisional timeout acceptance decision remain open. The 21,000-token budget,
voice fingerprint, providers, models and timeout settings did not change.
**Step 6 is not marked complete.**

[Correction report, exact regressions and evidence](../.artifacts/reviews/2026-10-23-cancellation-intent/results.md).

The maintained current-build action smoke subsequently passed **36/36**, with zero failures or skips. Source/script hashes remained unchanged during these live checks.

Owner-approved supplemental captured-request checks: **15/15 reviewed pass** across three negation and twelve explicit new-request cases. Raw phrase-based scoring was 14/15 (exit 1): one correct “remains canceled” reply was a documented scorer false negative. There were no provider errors or tool executions. Destinations and hotel-local pickup instants were checked; application source/settings were unchanged. This is bounded prompt evidence, not a reliability-rate claim. [Raw results and separate review](../.artifacts/reviews/2026-10-23-cancellation-intent/results.md#owner-approved-captured-request-counterchecks).

### Post-acceptance state corrections — 2026-10-24

Continue-As-New now carries the consumed ride offer and its sequence. The existing next-turn-only acknowledgement guard survives rollover after either a confirmed or uncertain booking attempt; an explicit new same-route request can still create a new offer. Empty consumed-offer defaults initialize a new stay; Continue-As-New carries the current state explicitly. Earlier workflow versions and input shapes are not supported.

A dispatched cancellation of a different ride with an unconfirmed outcome clears the earlier ride's automatic cancellation fallback. Merely staging that different ride does not clear it; a same-ride known confirmation remains authoritative. Completed history/transcripts are retained. Without an open offer, the reply states that no new cancellation was submitted and directs the guest to check current ride status, rather than claiming nothing was cancelled or reporting a different ride's success. No mutation retry, extra model call, or general action ledger was added. The latest applicable cancellation note remains bounded to one receipt; this change does not introduce an expiry policy.

[Reproduction, regression tests, replay and exact follow-up source snapshot](../.artifacts/reviews/2026-10-24-step6-can-guard/results.md). These are post-acceptance correctness changes. Step 6's owner-approved demo scope and the timeout settings approved on 2026-10-23 are retained.

### Current-contract cleanup — 2026-10-24

At Cecil’s direction, removed older-input/replay compatibility tests, the obsolete
ride-result renderer and its selector, and support for model-supplied proposal IDs
on ConfirmRide. Current confirmation accepts no arguments and uses only the
workflow-owned pending offer. Verified-guest authorization, offer lifetime,
Continue-As-New state and retry deduplication remain current requirements.
Fresh current-code replay tests remain; retained histories from earlier versions
are not an acceptance gate. [Verification](../.artifacts/reviews/2026-10-24-current-workflow-contract/results.md).
