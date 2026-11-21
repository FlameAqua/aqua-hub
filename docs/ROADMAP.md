# Aqua Hub roadmap

Version 1.4.0 shipped at `fb474d7` with Phase 7 and the Settings and Deck refinements. Adrian owns direction,
commits and releases. The release tag and the outstanding live acceptance items below track different milestones.

## Delivery order

| Order | Work | Status |
|---|---|---|
| Done | Phases 1–3: hub, fixes, Ask tables/maths/notes/compression/chat images | Shipped through 1.3.0 |
| 1 | Phase 4, including 4.5/4.6 | Implementation and modular checks delivered; targeted live/performance follow-ups remain |
| 2 | **5.3 / 5.4:** live streams and X | 5.3 linking confirmed by owner; remaining acceptance below. 5.4 deferred: paid APIs conflict with free-only scope |
| 3 | **7:** everyday wins | Deck, quick reminders, day agenda and confirmed scene actions implemented. Trading 212 deferred by Adrian |
| 4 | **8:** powerful file and semantic search | Local implementation and scratch CPU retrieval verified. Desktop and concurrent GPU acceptance remain. |
| 5 | **9:** remaining voice and creativity features | Read-aloud and personality flows implemented. Native audio, physical interaction and live model acceptance remain. |
| 6 | **6:** user-built agents and deeper health tools | Later |
| 7 | **5.1 / 5.2:** reactions, interests and For You | Deferred until the functional features above |

Streams, daily utilities and search deliver useful functions without first building a recommendation system. Search
should have explicit quality/privacy/performance acceptance, rather than being treated as an embedding-model checkbox.

## Finish Phase 4: AI foundation and readiness

Already implemented: one server per model; Aqua's pinned llama.cpp build with Ollama-engine compatibility selection;
custom path priority; verified model/runtime downloads; model library and header-based fit; provider-specific Main/Deep
choices; failure classification/backoff; compatibility record; shared generation GPU queue; Comfy Image Gen and Roleplay.

All generation uses the GPU. Editor (`ai.llama.backgroundModel`) serves News/Social/Foresight, falling back to Main.
Creative serves Roleplay and personality drafting with Main fallback when unset. Search is reserved for CPU embeddings. Briefs/market analysis retain the
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

Phase 4's implementation and modular E2E coverage have been delivered. Live, lifecycle and performance acceptance
remains open. Adrian authorized extending/modularizing desktop E2E coverage on 5 November. The runner now
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

Additional regression backlog: controlled scheduler start/finish signals, shutdown-with-active-workers, containment
failure/failed-stop, post-header cancellation, context fallback, warm-up contention and reasoning control. Adrian expanded
test permission to unit/provider and E2E tests for the new stage on 6 November. Desktop fixture passes do not close live
engine/GPU or performance acceptance; ongoing usage findings should also be recorded here as they appear.

Mouse-button global shortcuts need a separate `WH_MOUSE_LL` design; RegisterHotKey cannot supply them. This is a carried
improvement, not a prerequisite for streams or everyday features.

## 5.3 streams; 5.4 X deferred

- **5.3 implemented:** Social has a native Live streams card, source status and safe browser links. Sources contains
  explicit Twitch/YouTube switches (off by default). Twitch uses an owner-registered **Public** client with browser/device
  sign-in and read-only followed-stream scope; YouTube uses public channel pages, as Adrian selected. Updates run while
  Social is open: Twitch at most once a minute, YouTube for up to six chosen channels every 15 minutes; Check now has a
  one-minute floor. Failed checks preserve old data with its check time and error, and navigation/exit cancels owned work.
  Streams remain outside AI Social summaries. Unit/provider and targeted desktop checks cover the new boundaries.
  Optional Google Desktop OAuth imports/syncs a read-only subscription catalogue (manual sync, bounded at 1,000), with
  an explicit six-channel live chooser. Saved upload channels remain separate. Twitch setup now distinguishes Client ID
  from app name and gives prominent inline connection feedback.
  **Owner feedback, 6 November:** Adrian confirms Twitch/YouTube link correctly and the channels appear as expected.
  Actual long-term refresh, cancellation, disconnect/reconnect and live/offline transitions remain usage acceptance;
  this report does not independently verify those paths. Public YouTube markup can change. Current public-page probes
  found zero live streams on Gamers Nexus and two on Sky News.
- **5.4 deferred by Adrian, 6 November:** implementation was conditional on a free integration. Official
  [xAI X Search](https://docs.x.ai/developers/tools/x-search) bills fetched posts/profiles plus model tokens, and
  [X API reads](https://docs.x.com/x-api/getting-started/pricing) require pay-per-usage credits. X account linking can use
  [native OAuth/PKCE](https://docs.x.com/fundamentals/authentication/oauth-2-0/authorization-code), but it does not make
  account/timeline API reads free. No Grok key, X connection or billable request has been added. A future paid proposal
  needs explicit scope/budget approval, visible cloud-data disclosure and caching/spend controls. Phase 7 remains next
  for its dedicated design discussion; deferral does not authorise implementing that whole phase automatically.

These ship independently of reactions, interest decay or a For You ranking layer.

## Phase 7: everyday wins

- Today reopens the floating Deck, which stays within Aqua's main window across navigation. The movable bubble
  expands to Ask, Launchpad, up to two pinned apps, today's agenda, a new reminder, a 1–60 minute countdown and Hide.
  Position and visibility are profile settings. The countdown uses the local reminders store.
- Upcoming shows scheduled reminders alongside read-only events for a selected day. It offers an all-reminders view,
  editing and deletion, and **Done** or **Snooze 10 minutes** for due items. One-offs keep their instant. Daily and weekly
  repeats follow the local PC clock. On launch or wake, the latest occurrence within five minutes can
  alert, and older occurrences appear as **Missed**. Immediate reminder notifications request the Windows tone.
  See [Architecture](ARCHITECTURE.md#agenda-reminders-and-the-deck)
  for the schedule, notification and retention contracts.
- Adrian approved linking a reminder to a saved Launchpad scene. When the item is due or missed, **Run scene** opens
  a review of its current steps. **Run now** explicitly starts it. Done remains a separate action.
- Adrian confirmed the reminder workflow, corrected theme labels and tone in his build. Real scene execution,
  quiet hours/fullscreen, physical sleep/resume, changing system time zone, screen-reader announcements and the
  large-day responsiveness workload remain manual acceptance.
- Adrian deferred Trading 212. When resumed, it starts with a redacted sample and a locally validated CSV preview.
  Calendar writes, additional account connections and sync need separate agreement.

## UI refinement between phases

- Image Gen now opens on the prompt and canvas, with generation parameters and ComfyUI setup available in a drawer.
  Verify the setup, generation and error recovery with the installed ComfyUI engine in dark and light themes.
- Apply the [interaction and feedback standard](DEVELOPMENT.md#interaction-and-feedback) to subsequent UI work.
  Verify the Deck's physical drag, keyboard traversal, reduced motion and screen-reader announcements in a normal build.
- Settings groups related preferences, reveals detailed Social setup on demand and supports links to specific rows.
  Try mouse and keyboard dismissal of Ask's mode hint, Deck hover in both themes, and Social source setup and related
  links in the owner build before preparing the next release.

## Phase 8: local file search

Search files now supports explicitly approved fixed local NTFS folders. Names and paths are the default.
Per-folder content consent adds plain text and Office/OpenDocument passages to a private SQLite index.
Aqua opens Search from the Deck and Command Palette. The installed default profile can enable folder-scoped
search in File Explorer's **Show more options** menu.
Aqua can rank paraphrases with a verified EmbeddingGemma CPU process. Folder approval, updates, recovery,
result actions and storage limits are documented in [Architecture](ARCHITECTURE.md#local-file-search) and
[Security](SECURITY.md#search-folder-authority). See [Scoring](SCORING.md) for the tested workload and results.

Owner acceptance still includes the native folder picker, open/reveal with installed file handlers, physical keyboard
and screen reader use, the installed Explorer menu and its removal, a representative personal corpus, and
timing isolated CPU indexing alongside GPU generation.
The initial scope leaves OCR, PDF content, media understanding, archives, removable and network drives, and Ask integration deferred.

## Phase 9: voice and creativity

- **9.1:** Read aloud shares a single owner across answers, briefings and preview. Windows voices remain the default.
  The optional audio.cpp Kokoro installation exposes four English presets on supported Windows x64 CPUs. Generated
  WAV and off-screen results are in [Scoring](SCORING.md). Audible quality, pronunciation, device behaviour and
  coexistence with real CPU Search and GPU generation remain acceptance.
- **9.2:** The existing Image Gen tab, ComfyUI/FLUX.2 Klein workflow, references, gallery and GPU reservation stay in place.
- **9.3:** Workbench saves reusable personality templates and reviews model drafts before saving. Roleplay starts
  from a template snapshot and keeps its conversation in memory across tabs for the running session.
- **9.4:** Use the existing Models library and Creative role. Roleplay shows the route and points missing-model
  recovery to that selector. Model training affects refusals, style and consistency regardless of where it runs.
  Live quality and fit for any chosen Creative model remain owner acceptance.

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
