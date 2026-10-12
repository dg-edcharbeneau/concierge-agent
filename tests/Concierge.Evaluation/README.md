# Hotel ZGY retrieval evaluation package

This directory contains the contract and data for `hotel-zgy-retrieval-eval`.
There is one package. When Cecil approves a change, edit it directly.

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
  approved the facts, cases, and gates.
- All 7 planned sources are `approved`. All 15 cases are `active`.
- Corpus absence for the 3 no-evidence cases was confirmed against the full
  recorded snapshot. The corpus contains related passages but no evidence for
  front-desk charger lending, parking prices or non-guest parking terms, or a
  pet policy.

This package records no retrieval or answer results.

## Cases and gates

| Category | Cases | Expected evidence keys |
| --- | --- | --- |
| `exact_term` | 7 | 7 |
| `paraphrase` | 3 | 3 |
| `multi_source` | 2 | 4 |
| `no_evidence` | 3 | 0 |

The package uses these initial demo gates. They are package-specific and are
not universal benchmarks. The count-based minimums are authoritative:

- Recall@5: at least 13 of 14 expected evidence keys (`threshold` 0.90;
  ceiling(0.90 × 14) = 13). Only the combined (production) retrieval arm is
  gated. Lexical-only and vector-only Recall@5 are comparison numbers, not
  pass/fail.
- Answer support: 12 of 12 answerable cases.
- No-evidence behavior: 3 of 3 cases.

Case IDs ZGY-NONE-001 and ZGY-NONE-002 were removed because approved facts now
cover guest Wi-Fi and EV charging. Their IDs are not reused.

For a no-evidence answer to pass, the assistant must not guess. It must say
that the hotel information does not establish the answer and direct the guest
to hotel staff. The retrieval result does not have to be empty. A related
passage does not count as sufficient evidence. For example, phone chargers in
guest rooms and conference rooms do not establish front-desk charger lending.

## Corpus snapshot

The evaluation corpus is every Markdown file under `knowledge/hotel-zgy/`.
`corpusSnapshot` records the repository-relative path, `sourceId`, SHA-256 of
the exact file bytes, and byte length of each file. The snapshot was captured
on 2026-10-09 after the approved corpus updates and before any comparison.
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
| `knowledge/hotel-zgy/amenity-hours.md` | `zgy-amenity-hours` | `ccb7dcab88041162223ec74ef9856222ae62d31371b7a16508d4ed7d5d2bbd65` | 230 |
| `knowledge/hotel-zgy/connectivity.md` | `zgy-connectivity` | `c8816230b0328063dc81fc96bc4afbee1d0e3af6d125e08e652e25187f64f74a` | 244 |
| `knowledge/hotel-zgy/parking.md` | `zgy-parking` | `0bad9f1b48beda2f8c30ecd04aecdf2d5e903fac9673a6c3d3b6adf89536d14b` | 251 |
| `knowledge/hotel-zgy/policies.md` | `zgy-policies` | `7a028d3b7eea34057fba7ee0120a505f08c1f57a0904f96ba6ef92719866e54a` | 427 |
| `knowledge/hotel-zgy/property-overview.md` | `zgy-property-overview` | `15b4ce88cc164475d28c7220eb6c6e53d619c56559357c3ba75bc097c683e3c7` | 369 |
| `knowledge/hotel-zgy/services.md` | `zgy-services` | `e00dbb57ec1561838b1de05499f89a47e3d8bec4ef92f15de3513be2e4e7d7f0` | 859 |

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

On 2026-10-09 the validator passed and all 24 negative-test cases passed.
These checks verify only the package shape, consistency, and corpus snapshot.
They do not produce retrieval or answer-quality results.

## Contract summary

- The package evaluates retrieval evidence and grounded-answer behavior. It
  does not evaluate provider configuration or represent measured results.
- Retrieval uses the final deduplicated source-section evidence-key list.
  Exact lookup results keep their source and section identity.
- An answerable case passes only when the response answers every part of the
  question and covers the expected claims, every hotel fact it states is
  entailed by a passage actually retrieved for that case (any retrieved
  passage, not only the expected evidence keys), and it adds no invented or
  contradicting hotel fact. Expected evidence keys measure Recall@5 only.
- No-evidence cases require a staff fallback rather than an unsupported
  answer.
- The corpus snapshot hashes gate every run, and each `run.json` records the
  package SHA-256.
