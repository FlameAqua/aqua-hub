# Phase 4 — frontend, backend review, and QA handoff

Prepared by Codex / Astra for Adrian and Claude, 4–5 October 2026.

## Outcome

Implemented the Phase 4 Models page and runtime settings against `Hub.LocalAi`, reviewed the backend, fixed the defects found during review, and addressed Adrian's UI follow-up list. Changes are uncommitted, as requested in the original handoff. The owner's running Release app and real settings/model store were not changed by QA.

The automated suite and desktop verification results are recorded below. Runtime installation and model downloads were intercepted in desktop tests; tiny synthetic GGUF files and fake process/HTTP hosts exercised the backend. These results do **not** validate live GPU inference or the performance figures in Claude's handoff.

## Assessment of Claude's work

**7.5/10 for the Phase 4 backend as handed over.** This is an engineering judgment about the code and evidence reviewed, not a measured quality metric or a rating of unreviewed phases.

The architecture is strong: the UI has a usable service boundary, model ownership is clear, parsing is bounded, downloads are pinned and verified, and process/runtime behavior is injectable for tests. The handoff is unusually useful: it describes contracts, failure states, design intent, and test seams concretely.

The main deductions are for integration readiness. Existing same-size files bypassed digest verification; aliases were lost when libraries deduplicated; an unsupported-header cache changed behavior across scans; automatic context could violate the stated 8K minimum; and the supplied Qwen model exposes a real conversion/runtime compatibility gap. Process environment isolation and cancellation cleanup also needed tightening. The claim that the backend was complete was stronger than the available cross-layer evidence warranted.

Keep the service boundary and explicit failure messages. For the next phase, make exact model artifacts, runtime revisions, and reproducible acceptance tests part of the completion criteria.

## Frontend delivered

### Models

- Added Models to sidebar navigation, command palette, single-instance page routing, and Ask's page commands.
- Added hardware/free-memory summary, Main/Creative/Background/Search role pickers, capability chips, source badges, fit/context estimates, running/failed state, layer offload, and measured speeds.
- Added library refresh, use for Ask, stop, clear a cached failure, and confirmed removal for Aqua-owned models. Ollama-owned files remain managed by Ollama.
- Added a readable problems expander, card-specific download errors, suggestions, Hugging Face repository search and file browsing, gated-repository feedback, and optional vision projectors.
- Added progress, transfer rate, cancel/resume messaging, and confirmed discard of unfinished downloads.
- Role and suggestion grids adapt to width. Stable observable collections prevent stale WPF automation peers and avoid replacing every row during runtime updates.
- Unknown downloaded-file metadata is labeled as a **weights-only estimate**; it is not presented as a verified full-memory fit.
- Creative and Search roles explicitly identify their future-phase use.

### Settings › AI & models

- Added explicit provider switching and a runtime card for not installed, installing, installed, and custom-server states.
- Selecting a missing runtime retains the current provider. Successful installation can finish the requested switch; a dry-run install cannot accidentally activate a nonexistent executable.
- Added recommended/alternative runtime builds, cancellation, device detection, removal, and a Models shortcut.
- Added context, cache, MTP, idle unload, CPU threads, effort, model folder, Ollama inclusion, and custom-server settings.
- Provider-specific controls now show when relevant. The existing brief-model override remains visible so it cannot silently supersede Main.
- Lost-focus path edits save, advanced search results expand their containing section, and model-folder/Ollama-inclusion changes refresh the library promptly.

## Backend review findings and fixes

| Finding | Change | Regression evidence |
|---|---|---|
| An existing model/projector with the expected size was trusted without checking its bytes. | Hash existing artifacts before reuse. Same-size corruption no longer bypasses verification. | Same-size corrupted file fixture and verified-download tests. |
| Deduplicating Aqua/Ollama copies lost names and IDs; bare names did not consistently resolve `:latest`. | Preserve both stores' identities/aliases and resolve default tags. | Duplicate-blob and lookup tests. |
| Projector/adapter headers entered the cache before rejection. A second refresh could admit them. | Cache only accepted model headers. | Both rejected types tested across successive scans. |
| Automatic context could select 4K although the contract promised at least 8K. | Use the 8K floor, except when the model itself supports less. | Memory-bound context and trained-context tests. |
| Unbounded integer parsing of custom server output could throw in an asynchronous process callback. | Bounded `TryParse` and offload-count consistency checks. | Malformed/oversized output exercised by the runtime fake. |
| Device discovery cancellation could leave its helper process alive. | Kill a still-running helper in `finally`. | Code review and clean build; real process cancellation was not separately exercised. |
| The process host inherited unrelated environment variables. | Start from a minimal Windows loader/temp environment, then add explicit runtime variables and the supported Ollama CUDA layout. | Code review; real child-environment and kill-on-close acceptance remain to be run. |
| Repeated permanent GGUF metadata failures retried the same doomed load. | Cache the array-length incompatibility for unchanged model/server files; give a specific explanation. A file/path change or unload clears it. | Exact supplied Qwen log, Failed state, and no second process launch. |
| Baseline local-file security regression: Windows short names could hide sensitive `.ssh` segments. | Resolve actual `~` aliases with direct `FindFirstFileW` lookup and fail closed on unresolved aliases; preserve normal and nonexistent path suffixes. | Existing failing security case now passes, plus path-preservation and fail-closed tests. |

### The reported Qwen3.5 failure

The supplied error is specific: `qwen35.rope.dimension_sections` contains three values, while this llama.cpp loader expects four. It is not evidence that the card has insufficient free VRAM.

Upstream's [Qwen3.5 loader](https://github.com/ggml-org/llama.cpp/blob/master/src/models/qwen35.cpp) requests four sections. Its [conversion code](https://github.com/ggml-org/llama.cpp/blob/master/conversion/qwen.py) pads the three-section default to `[11, 11, 10, 0]`. The [server options](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md) provide scalar metadata overrides; an array override is not a supported repair route.

Aqua now explains this and stops repeated launches for unchanged inputs. **The incompatible Ollama blob is not repaired or modified.** Use that model through Ollama, or obtain a compatible GGUF. Models › Clear failure permits a deliberate fresh attempt after addressing the cause. No large replacement model was downloaded during this work.

These links describe the upstream source inspected during review. For release evidence, Claude should pin the exact source revision and GGUF digest used for the performance claims rather than relying on moving `master` links.

## Adrian's additional UI issues

| Request | Result |
|---|---|
| Wheel scrolling over Ask messages crashes on `Run`. | Ancestor traversal distinguishes visual and content elements; text runs use their logical parent. |
| Context, screenshot, and notification controls reopen on the second click. | Shared popup dismissal keeps the owner click available to toggle closed. ContextMenu needs an additional input-stage guard because its menu mouse capture dismisses before the button receives Click; this was caught by real pointer tests. Outside click, Escape, deactivation, and navigation dismiss appropriately. |
| Notification count is off-center. | Explicit Segoe UI typography, line height, and centered alignment inside the badge. |
| Caption buttons do not align with other toolbar icons. | Aligned the app title row with the native 32px caption area; retained native window controls. Follow-up: the command box is 24px high with 4px vertical clearance and left-aligned to the page's 32px content inset, instead of centered within spare toolbar space. |
| Rename chats. | Current heading offers a rename menu and inline editor; saves refresh both the header and history. Existing history rename remains available. Changing chats cancels an open title editor. |
| Stock/ETF prices lack currency. | Preserve explicit non-symbol currencies such as CAD/CHF and display “currency unavailable” when required metadata is absent. Do not guess the user's base currency. |
| Shift-click Doctor's Toolkit as administrator. | Shift requests Windows' `runas` verb; ordinary activation keeps its existing behavior. Cancellation remains handled. Actual UAC prompts were not invoked during QA. |
| Theme changes feel delayed. | Apply the selected theme immediately, then persist through normal autosave; skip redundant resource-dictionary replacement when the effective theme/accent is unchanged. |
| Shortcut punctuation and other keys. | Expanded capture, normalization, and Windows virtual-key mapping. Plain cancel/clear/navigation behavior in the shortcut editor is preserved. **Mouse-button global shortcuts are not implemented**; the UI explains this limitation. |
| News/Social refresh with busy feedback. | Page-level refresh controls disable while a request or collector run is active, join the runtime's existing work, and show checked/paused/disabled/error feedback. News also waits for curation. Social's empty-state refresh uses the same guarded path. |
| Qwen repeatedly fails. | Specific compatibility diagnosis and permanent-failure retry suppression; see the remaining model requirement above. |
| User prompt Markdown. | Prompts use the existing selectable Markdown renderer; editing and whole-message copying retain the source text. |
| Generic “Plan: answer from what Aqua already knows”. | Direct conversation produces no completed planning entry. Actual searches, attachments, skills, and feed preparation still have meaningful descriptions. |
| Retry with extra user context. | Added “Regenerate with extra guidance” beside Ask again, with an inline editor and cancel. Regeneration uses the existing resend path and keeps the original request plus the supplied guidance. |

## Verification

| Check | Final result |
|---|---|
| Release solution build | Passed, 0 warnings and 0 errors. |
| Release unit suite | **1,174 passed, 23 skipped, 0 failed** (1,197 total). Skips are the existing opt-in live/network/model/audio tests. |
| Phase 4 and UI follow-up desktop suite | **9 passed, 0 failed**, in 26 seconds: `A19_ModelsTests`, `A20_AskUiTests`, `A21_UiPolishTests`. Theme switching was additionally rechecked on the final isolated Release build: 1 passed. |
| Desktop test build | Passed, 0 warnings and 0 errors. |
| Locked restore / dependency audit | Locked restore passed; the solution's three projects reported no known vulnerable packages, including transitive packages, at the time checked. |
| Whitespace validation | `git diff --check` passed. |

The original baseline was 1,118 passing, 23 skipped, and one failing local-file short-name security test. The final suite adds 55 passing cases and repairs that baseline failure. The old `Ctrl+Tab` rejection expectation was updated to match the requested expanded key support.

The ordinary build script stopped when it tried to overwrite files held by the owner's running Release app. Instead of stopping that app, the Release build and unit tests used `-p:BaseOutputPath=<repo>/docs/screenshots/private/phase4/release-bin/`. The desktop tests used a copied Debug executable, `--e2e`, and separate synthetic profiles. The full pre-existing desktop suite was not run; some of its tests require a configured live model, credentials, and real feed data.

Reproduce the focused desktop run with the repository's E2E harness, setting `AQUAHUB_E2E_EXE`, `AQUAHUB_E2E_PRISTINE`, and `AQUAHUB_E2E_RUNS` to isolated locations, then filtering on the three test classes above. The successful final combined run is `docs/screenshots/private/phase4/runs/20261005-092059/` if retained locally; consult the run folder's `run.log` for individual steps.

The unit suite covers malformed GGUFs, hybrid/sliding-window cache estimates, aliases and ownership, HTTPS/digest/size enforcement, range resume, retained partial files on cancel, download eligibility/free space, runtime arguments, MTP sampling, lane/reuse/unload behavior, failed starts, and the OpenAI-style runtime request/response contract. Invalid-dimension fixtures were corrected to encode real tensor records, so they exercise the intended parser branch.

Desktop tests use isolated dry-run profiles and a tiny synthetic model. They cover role persistence across navigation, missing-runtime/provider behavior, intercepted downloads/install, advanced settings, external-provider round trips, notification pointer toggles/outside/Escape dismissal, theme switching, and page refresh feedback. Ask tests verify persisted chat renaming, actual pointer toggles, screenshot-menu reopen/Escape, italic Markdown through UI Automation, raw Markdown copy/edit, guided retry cancellation, and wheel movement over a rendered long prompt without a UI exception. They do not validate a newly generated model answer.

Visual QA artifacts live under `docs/screenshots/private/phase4/` (Git-ignored). Models and runtime settings were rendered in dark/light themes at 1440px and 820px; desktop screenshots include native caption controls and feed headers. Do not publish these private QA captures automatically: the normal media integration can show the owner's currently playing media even in a synthetic profile.

The screenshot-menu correction follows the capture/dismissal behavior in WPF's [MenuBase implementation](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/MenuBase.cs). A final visual check also found a toolbar DND icon retaining its old theme brush; it now uses a dynamic resource reference.

No existing tests were deleted merely because they originated in debugging. A newly introduced duplicate currency-format assertion was dropped because it exercised an unchanged helper already covered by the suite, rather than the changed WPF display code. The former assertion that `Ctrl+Tab` is invalid was updated for the expanded keyboard support; multiple-primary-key and unsupported mouse-key rejection remain tested.

## Recommendations for Claude / remaining acceptance work

1. **Pin a reproducible runtime/model compatibility matrix.** Include exact server build, backend, GGUF digest/converter, projector digest, driver, context/cache, and MTP state. An Ollama manifest being readable does not prove that upstream llama.cpp accepts the blob.
2. **Run live GPU acceptance before calling the phase release-ready.** Cover installation/cancellation, device discovery, cold/warm load, token streaming, GPU/CPU coexistence, idle unloading, job-object cleanup on app termination, and recovery after replacing the incompatible Qwen model. Recheck the handoff's speed numbers on those exact artifacts.
3. **Keep compatibility errors distinct from capacity errors.** The current specific failure suppression covers the reported array mismatch; extend classification deliberately as reproducible failures arrive. Do not blindly retry a model-format failure or silently rewrite another application's model store.
4. **Clarify role migration across providers.** `Ai.Model` is shared across Ollama, llama.cpp, and external servers. Provider-specific saved selections would avoid carrying an HF identifier into an unrelated endpoint. Keep the legacy brief override explicit until roles absorb it.
5. **Maintain one source of UI truth.** Expose state transitions and action outcomes through the service API; keep network/process work off the UI thread and marshal notifications consistently. Avoid replacing collection rows on every health tick.
6. **Treat fit as an estimate until metadata is known.** Repository/file size alone cannot justify a full context-memory verdict. A future bounded remote-header probe could improve pre-download advice.
7. **Follow up on measured UX rather than extra one-off tests.** Add performance budgets for theme resource replacement and very long Markdown chats if slowness remains reproducible. Mouse-button global shortcuts need a separate Windows hook design; they cannot be added to `RegisterHotKey` as another virtual key.

Phase 5 performance/answer features mentioned in the original handoff (per-answer timing display, warmup, prefix caching, and runaway reasoning controls) remain outside this Phase 4 frontend pass.

## Subsequent GPU/editor and studio work — 5 October 2026

See [phase-4-studio-setup.md](phase-4-studio-setup.md) for the later GPU editorial routing, revised Q5 model choice, Chat/Image Gen/Roleplay tabs, ComfyUI generation/reference editing/startup, gallery deletion and expiry, exact downloads, and updated validation. The historical results above describe the original review pass; the follow-up records the expanded suite and remaining live GPU acceptance work.
