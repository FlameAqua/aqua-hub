# Aqua Hub roadmap

Adrian's priority order, agreed 5 November 2026. This repository document is the maintained roadmap; no external artifact
or agent handoff is required to understand the plan. Release baseline is 1.3.0 (`b021e9b`), with the subsequent Image Gen
reference upgrades committed as `86f5ec6`. Adrian retains direction, commits and releases.

## Delivery order

| Order | Work | Status |
|---|---|---|
| Done | Phases 1–3: hub, fixes, Ask tables/maths/notes/compression/chat images | Shipped through 1.3.0 |
| 1 | Finish Phase 4, including 4.5/4.6 | Current priority after documentation/maintenance |
| 2 | **5.3 / 5.4:** live streams and X | Keep; no reaction/personalization dependency |
| 3 | **7:** everyday wins | Next |
| 4 | **8:** powerful file and semantic search | Needs a dedicated design/acceptance pass |
| 5 | **9:** remaining voice and creativity features | Image Gen already delivered early |
| 6 | **6:** user-built agents and deeper health tools | Later |
| 7 | **5.1 / 5.2:** reactions, interests and For You | Deferred until the functional features above |

Streams, daily utilities and search deliver useful functions without first building a recommendation system. Search
should have explicit quality/privacy/performance acceptance, rather than being treated as an embedding-model checkbox.

## Finish Phase 4: AI foundation and readiness

Already implemented: one server per model; Aqua's pinned llama.cpp build with Ollama-engine compatibility selection;
custom path priority; verified model/runtime downloads; model library and header-based fit; provider-specific Main/Deep
choices; failure classification/backoff; compatibility record; shared generation GPU queue; Comfy Image Gen and Roleplay.

All generation uses the GPU. Editor (`ai.llama.backgroundModel`) serves News/Social/Foresight, falling back to Main.
Creative serves Roleplay with Main fallback. Search is reserved for CPU embeddings. Briefs/market analysis retain the
explicit Deep override. The 12 GB suggestion is Qwen3.5 9B Q5_K_M. Image Gen uses local ComfyUI/FLUX.2 Klein 4B.
There is no large background generation model on the CPU and no llama.cpp router mode.

### Readiness and reliability

| Remaining work | Acceptance |
|---|---|
| Scheduler run identity | Implemented shared pending/active completion identity under one lock; pause/disable/exit resolve waiters and shutdown joins workers. CI's fixture can miss its one-second run through polling. Test unchanged: controlled-signal joining/completion regressions and new CI acceptance remain open. |
| Owned shutdown | Implemented tracked LocalAi/Ask/LLM/image work, named subscription teardown and dependency ordering. Normal Quit joins with the dispatcher running; a 45-second/session-exit fallback leaves active dependencies alive until process exit. Live exit/cancel/recovery acceptance remains. |
| Model process containment | Starts reject missing jobs/failed assignment; newly started uncontained processes are killed. Failed termination blocks handoff. Verify creation/assignment failure and forced quit; the brief create-before-assign window remains. |
| Compatibility UI | Implemented engine label/note, separate preflight problem, honest unknown observations and explicit **Copy matrix**, preserving model rows/IDs. Offline provider/settings/matrix checks pass; native keyboard/theme and live provider round trips remain acceptance. |
| Preview and engine setting | Implemented serialized cancellable model/projector header checks, projector choice, weights-only fallback and `useOllamaEngine`. Header body reads honour cancellation and the cache is bounded. Live preview/cancel/install acceptance remains. |
| Device check | Implemented off-dispatcher probes using Aqua/custom or the enabled Ollama engine; Ollama-only cards show an honest install state. Verify with an actual Ollama-only scratch setup. |
| Useful image errors | Implemented mismatch explanations in connection/start/generate prechecks; mismatched pairs still reject submission. Verify actual server wording and recovery. |
| Reference editing | Strengthened bounded handle reads, clipboard background preparation and owned exit work; preserved content IDs, temporary uploads and retention. A persisted endpoint-only recovery marker restores uncertain reservations on restart. Automated picker/corrupt/missing/gallery/retention and fixture recovery checks pass. Drop/paste and actual upload/GPU recovery remain acceptance. |
| Compatibility evidence | Keep exact artifacts/settings with measurements. Existing record/matrix lacks full historical projector/converter/executable provenance; it is not a universal compatibility certificate. |

Relevant contracts are in [Architecture](ARCHITECTURE.md); security limitations are in [Security](SECURITY.md).
The retained [compatibility matrix](compatibility/local-ai.md) separates actual record observations from earlier scratch
acceptance. That evidence includes successful official Q5 and Ollama-engine Qwen loads, Gemma/27B loads, scratch crash
cleanup/idle stop and Comfy generation/edit/cancel/release. It does not close the remaining failure paths above.

Still needed live, only with an isolated build/profile and approved downloads: runtime install/cancel; GPU generation
plus CPU embedding coexistence; and real in-app Image Gen → chat → Image Gen unloading/reloading. E2E interception
cannot establish the real GPU handoff. Verify explicit-file grants and imported image retention before expanding search.

### Phase 4.5 / 4.6 experience work

- Implemented answer-local context/reading budgets from the managed server's reported/launch window and native Ollama's
  loaded context when available; explicit configured fallback for opaque external providers. Verify live model/provider
  switches and small-context long-document behaviour; Aqua cannot certify an opaque server's true window.
- Implemented per-answer **plan / gather / load / prompt / write** footer data. Plan/gather are wall phases; engine
  components overlap them and remain unknown when not supplied. Live timing/cache observations remain acceptance work.
- Implemented one cancellable idle-lane warm-up while typing. Foreground/background generation cancels it atomically;
  it never queues. Verify cancellation, GPU contention and model reuse with actual engines.
- Audited prefix stability: moved the volatile minute clock/location after Ask's stable policy; preserved DATA fencing,
  private-query approvals and distinct agent/planner roles. Earlier 162-token reuse is not proof of a defect across
  different tasks. No current live cache-hit or throughput improvement has been measured.
- Implemented bounded graceful reasoning completion for the pinned b11387 protocol, using the current completion ID.
  Unknown/custom/Ollama versions retain the bounded direct-answer fallback. Live supported/refused-control acceptance remains.
- `--image-min-tokens 1024` remains a candidate: pinned upstream documents the flag, but grounding/latency/VRAM has not
  been compared on both engines. It has **not** been enabled globally or adopted without that evidence.
- Established an offline .NET 10 Release synthetic baseline and bounded the UI to Adrian-approved 60-message pages,
  background Markdown rendering and per-answer preview limits. Measured memory/render reductions are in [Scoring](SCORING.md).
  Long-chat dispatcher stalls still exceed the proposed 50 ms target; broader interactive/theme/retained-memory acceptance
  is **open**, not a passed performance budget. The diagnostic is not desktop E2E or live model/GPU evidence.

Phase 4 is not complete. Adrian authorized extending/modularizing desktop E2E coverage on 5 November. The runner now
selects offline Models, Runtime, Ask, Images, Roleplay and Lifecycle groups; the full legacy suite requires `-All`.
See [Development](DEVELOPMENT.md#targeted-desktop-components) for commands and isolation/evidence limits.

**Modular E2E checks implemented and passing, 5 November:** the combined offline Phase4 group passed **14/14** in
about two minutes. It covers Models roles/provider settings and matrix honesty; 300-message Ask paging, Markdown preview
limits and full stored/exportable text; image tab drafts, validation, mismatch/corrected connection, gallery edits,
deletion/retention, missing references and valid/corrupt picker files; roleplay scene reset; fixture recovery through
busy/idle/free responses; and close/reopen/quit plus cancellation during an observed HTTP request. The native picker uses
its listed file's supported UIA Invoke action when the shell's split Open control exposes none. No synthetic input or
disabled assertions were used. A separate isolation check confirms offline fixtures need no legacy warm profile.

Selection/list/plan commands and empty-selection failure are verified; results include TRX and application/test binary
hashes. The normal Debug unit regression passed **1,218 / 23 skipped / 0 failed**. This automates these UI/service cases,
not the live engine/GPU and performance acceptance above. Historical failures and final evidence are recorded in
[Scoring](SCORING.md#5-november-2026-modular-e2e-completion).

Additional unit-level regression authoring remains outside this E2E authorization: controlled scheduler start/finish
signals, shutdown-with-active-workers, containment failure/failed-stop, post-header cancellation, context fallback,
warm-up contention and reasoning control. Desktop fixture passes do not close live engine/GPU or performance acceptance.

Mouse-button global shortcuts need a separate `WH_MOUSE_LL` design; RegisterHotKey cannot supply them. This is a carried
improvement, not a prerequisite for streams or everyday features.

## Next: 5.3 streams and 5.4 X

- **5.3:** Twitch and YouTube live streams. Twitch's API route through Adrian's own registered app remains a decision.
- **5.4:** X through Grok using an xAI key Adrian provides. Design access, disclosure, caching/rate limits and unavailable
  states when implementing; the existing local-AI privacy policy cannot be silently reused as a cloud-data promise.

These ship independently of reactions, interest decay or a For You ranking layer.

## Then Phase 7: everyday wins

The deck, reminders and scheduled tasks, calendar days, small daily conveniences, and Trading 212 beginning with CSV.
Define the deck and first daily workflows with Adrian before implementation. Scheduling must specify recurrence,
time-zone/DST changes, sleep/resume, missed tasks, restarts, deduplication, cancellation and notification behaviour.
Trading 212 begins with local import/validation, not account automation or trading authority.

## Then Phase 8: find anything

File indexing, CPU semantic search using EmbeddingGemma and a dedicated Search page. Spend a separate design pass on:

- Scope and permission model: allowed roots, sensitive files, explicit grants, reparse/short-name paths, encrypted or
  unavailable files and cloud placeholders. Recheck authorization when opening a result; an index entry is not access.
- Search quality: exact filename/path matches alongside lexical and semantic content search, filters, explanations and
  ranking. Build a representative relevance set, including exact names, paraphrases, ambiguous queries and no-match cases.
- Incremental indexing with bounded CPU/I/O, change reconciliation, pause/cancel, restart recovery and deletion propagation.
  Do not rescan whole drives on every query or silently hydrate remote files.
- Versioned extraction/chunks/embedding model, stable document identity and incremental rebuilds; avoid mixing incompatible
  vectors. Define storage limits and ensure removed roots/documents lose their indexed text and embeddings.
- A responsive query/result UI with measured foreground latency, initial-index cost, idle overhead and disk growth.
  Keep CPU embeddings independent of the generation GPU; assess resource contention on the actual target PC.

These are design requirements to refine with Adrian, not a claim that the existing Search model slot implements an index.

## Then Phase 9: remaining voice and creativity

- **9.1:** Read-aloud voices through audio.cpp, distinct from existing dictation/system-voice playback.
- **9.2:** Image Gen as an Ask tab already exists on ComfyUI/Klein; retain it rather than rebuilding the earlier proposal.
- **9.3:** Persistent Roleplay personalities/library and Workbench authoring. Roleplay's current session tab already exists.
- **9.4:** Uncensored model choices. Local operation alone does not remove a model's learned restrictions.

## Then Phase 6: your own agents

**6.1** Agent Engineer and recipe builder composed from Aqua's own parts, never arbitrary code. **6.2** Agent detail
views need further scope. **6.3** Deeper health checking may use an optional read-only admin helper, if Adrian chooses it.
Reuse the existing scheduling/permission model with explicit step inputs, outcomes and cancellation.

## Last: deferred 5.1 and 5.2

**5.1:** Like, Love, Less of this and Never show reactions, with decaying tag interests. **5.2:** For You with **Why this?**.
Preserve the ideas without making them dependencies of streams, X or search.

## Cross-cutting work and owner decisions

- Choose a licence, Twitch route, optional admin helper and Trading 212 scope beyond CSV.
- Verify the release/update rehearsal, private vulnerability reporting and remaining dependency-update follow-up.
- Improve deterministic scheduling/lifecycle checks, Windows/WPF test seams and a hermetic desktop scenario; baseline
  coverage before selecting a floor. Existing passing test counts do not establish complete coverage.
- Recheck carried chat issues: notes after answer replacement, total table/formula bounds and screen-reader table semantics.
- Keep maintained documentation small; no handoff or screenshot archives. Reviews remain in SCORING and acceptance work
  stays here. Model/runtime/Comfy model downloads and public publishing still need Adrian's explicit OK.
