# Aqua Hub architecture

Reviewed 5 November 2026 against `86f5ec6` (release baseline: 1.3.0). This document describes the current implementation;
requirements for new work are explicitly identified. Remaining work belongs in [ROADMAP.md](ROADMAP.md), security
boundaries in [SECURITY.md](SECURITY.md), and development/verification practices in [DEVELOPMENT.md](DEVELOPMENT.md).

## 1. System and dependencies

Aqua Hub is a Windows 11 desktop application built with WPF, C# and .NET 10. It presents collected news, social posts,
markets, calendar/weather information, Ask Aqua, PC health and app/scene controls. AI defaults to local operation;
an OpenAI-compatible remote endpoint is an explicit opt-in. There is no Aqua cloud backend or telemetry service.

```text
WPF pages and controls
        ↓
App services and navigation/session state ──→ Windows platform adapters
        ↓                                         ↓
Core application services ←──────────────── narrow platform interfaces
        ↓
Sources / analysis / storage / model clients / agents
```

| Layer | Responsibility | Boundary |
|---|---|---|
| `src/AquaHub.Core` (`net10.0`) | Parsing, analysis, settings, storage, source clients, agents, AI protocols and policy | No WPF reference. Mostly platform-neutral, with existing Windows path interop in `LocalFiles`. |
| `src/AquaHub/Platform` | Windows process/window APIs, media, speech, monitoring, file/image decoding and secret storage | Implements platform capabilities; OS effects remain here or in explicit app adapters. |
| `src/AquaHub/Services` | Composition, long-lived UI sessions, model/process ownership, navigation and user actions | Adapts Core state into UI-facing services and marshals UI notifications. |
| `src/AquaHub/UI` | Presentation, interaction and accessibility | Uses service contracts; avoids owning networking, model scheduling or persistence policy. |
| `tests/AquaHub.Tests` | Unit and behaviour checks against fixtures/fakes | Includes source/XAML contracts; these do not prove WPF rendering or real GPU behaviour. |
| `tests/AquaHub.E2E` | Desktop interaction checks using isolated profiles | Compiled in CI; running requires an interactive desktop and explicit test scope. |

The desktop runner selects named offline components by default; `-All` explicitly selects the older full suite.
`Execution`/`Component` traits compose group filters, with smoke/name filters narrowing them. Offline fixtures seed
synthetic storage through Core, drive the real WPF process through UIA and use owned random-port Comfy HTTP responses.
The explicit E2E offline environment flag suppresses collectors/media/updates/service startup; settings also disable AI,
Ollama management and global hotkeys. This is application isolation, not an OS sandbox or real GPU evidence. See
[Development](DEVELOPMENT.md#targeted-desktop-components) for execution/results and [Roadmap](ROADMAP.md) for remaining work.

`HubCore` composes Core services; `App.OnStartup` supplies Windows adapters and the model runtime. `Hub` is the current
static app registry. It is an existing coupling point, not a dependency pattern to extend into Core. Four direct runtime
packages cover SQLite, desktop speech, updates and maths rendering; exact versions live in `Directory.Packages.props`.

**Rules for new work:** keep policy in Core and effects behind narrow interfaces; pass required services explicitly;
prefer focused feature services to more `Hub` globals. Move policy out of code-behind when changing that feature.
Do not introduce a second scheduler, settings store or GPU owner. A wholesale dependency-injection rewrite is not needed.

## 2. Collection, scheduling and publication

`HubCore.CreateAgents()` registers 15 agents: collectors feed clustering/editorial work, market analysis and Foresight;
the brief combines their results, while Sentinel and housekeeping maintain alerts and stored data.

- `AgentRuntime` normally ticks each second. It bounds collectors to three concurrent runs and AI agents to one.
  An agent's changed result schedules its `After` dependants, normally two seconds later.
- Per-run limits are three minutes for collectors and twelve minutes for AI agents. Failures use a 30-second exponential
  delay, capped at 32 minutes and the agent's effective interval. Waiting is distinct from failure.
- Hidden-UI eco mode stretches eligible intervals ×3 for idle or ×2 on battery. Fullscreen/game policy can defer AI.
- `RunAndWaitAsync` selects a pending/active run's completion task under the same lock as start and completion. Concurrent
  callers join that identity; cancelling/timing out a waiter does not cancel the shared run. Run now joins active work.
  Pause/disable/shutdown resolve pending waiters explicitly. Shutdown cancels and joins both the scheduler and workers.
  The old CI failure also has a timing-based fixture: its Running poll can miss a one-second execution. That test was
  not modified; a controlled-signal concurrency regression remains owner-reserved work.
- Source clients use bounded HTTP responses, validators and per-host cooldowns. `SourceHealth` separately slows
  repeatedly missing feeds and reports them without logging the same failure every minute.
- Collectors and initial ranking do not use LLM tokens. Editors cache outputs by their input content and keep a
  deterministic fallback when the model is unavailable. Stale AI output expires by product (for example Pulse/Foresight).

`HubState` publishes replacement snapshots and topic notifications. Treat published values as immutable; a read-only
collection interface alone does not enforce this. `UiThrottle` coalesces bursts on the dispatcher, and stable row IDs
preserve selection/expansion. This reduces refresh work; it is not a guarantee of one WPF layout pass.

Settings are copy-on-write. `SettingsStore` validates, migrates and atomically persists settings; forms use a three-way
merge so unrelated changes from the tray or another page survive. New settings need defaults, validation, migration
where needed, and a clear owner. Do not save a stale whole settings object from a page.

## 3. AI routing and the GPU

`LlmClient` supports Ollama, Aqua's runtime (`llamacpp`) and OpenAI-compatible endpoints. Structured requests include
schemas and bounded repair; output still needs deterministic validation before publication or tool execution.

`PriorityGate` serializes GPU generation. Interactive work precedes waiting background work; it does not preempt an
already running request. An Ask answer reserves the slot across its tool loop and releases it while awaiting user approval
or a requested AI agent. Image generation uses the same gate. CPU embedding work has a separate lane for future search.

| Model choice | Use |
|---|---|
| Main | Ask and general generation |
| Deep override | Briefs and market analysis; Main when unset |
| Editor (`ai.llama.backgroundModel`) | News Editor, Social Pulse and Foresight; Main when unset |
| Creative | Roleplay; Main when unset |
| Search | Reserved for Phase 8 CPU embeddings |

All current generation runs on the GPU lane; there is no large background CPU generation model. One generation model
is resident at a time, and switching models costs a load. This gate cannot control other applications' GPU workloads.
`ai.providerModels` remembers Main/Deep by provider type, not external server address. First-switch Ollama names can carry
between Ollama and llama.cpp; `hf:` IDs do not carry to another provider. Setting runtime Main while another provider is
active updates the runtime's saved choice. Creative/Editor/Search remain runtime settings.

## 4. Local runtime and model library

`Ai/Local` separates GGUF parsing (`Gguf`), memory estimation (`HardwareFit`), library/store ownership (`ModelLibrary`,
`OllamaStore`), discovery/downloads (`ModelDownloads`), installation (`LlamaSetup`), launch arguments (`LlamaArgs`) and
server lifecycle (`LlamaRuntime`). `Services/LocalAi` is the presentation API; `LlamaProcessHost` performs Windows starts.

- Runtime downloads are pinned in `LlamaCatalog` (currently b11387). Backend choice follows hardware/driver support.
  Models use public Hugging Face commits with size/SHA-256 verification and resumable partial files. Aqua reads Ollama's
  store in place; it never rewrites that store or deletes its models.
- There is one `llama-server` per model; Ollama's bundled build has no router mode. A custom executable path wins.
  Otherwise prefer Aqua's pinned build. With `useOllamaEngine` on (default), Ollama's bundled engine can run incompatible
  Ollama conversions or supply an engine when Aqua's build is absent. Absence of a preflight problem is not proof of support.
- The known upstream RoPE rule rejects an array length other than four before launch. Unexpected format failure on an
  Ollama-source model gets one bundled-engine retry. Memory errors do not trigger that fallback.
- `LlamaCompat` distinguishes format and memory failures; unrecognized recorded failures are Other. Format blocks persist
  per model/server key. Non-format failures during loading back background work off for 1, 5, then 15 minutes. Interactive
  requests bypass temporary backoff, not format blocks. Process-creation errors use the same non-format recording/backoff path.
- A named unload clears recorded failures/backoff; static header rules still apply. Aqua's server can sleep while idle;
  Aqua-owned Ollama-engine processes stop after the idle interval, releasing their executable/model handles.
- Endpoints are loopback-only with random per-start keys in the environment and constrained arguments/environment.
  Starts fail when job creation/assignment fails; assignment failure kills the new Aqua-owned process and reports an
  error. Host shutdown and launch are serialized. Normal model handoff confirms process exit before forgetting its owner;
  a failed stop blocks another load. Containment failure disables further host starts until restart; unconfirmed failed-start
  processes also block image handoff and remain owned for exit cleanup. Process creation still precedes assignment briefly. See Security.

`HardwareFit` estimates weights, projector, cache and work with desktop headroom. GGUF metadata improves the estimate,
but actual load/offload determines capacity. The 12 GB suggestion is Qwen3.5 9B Q5_K_M. Do not turn a memory estimate into
a compatibility claim, or a format error into advice to free VRAM.

`ModelDownloads.HeaderAsync` reads a ranged HTTPS GGUF header, capped at 64 MiB, with a linked timeout and bounded parser.
It caches metadata by listed file digest. `PreviewAsync` builds a would-be `LocalModel` for fit/capabilities/preflight;
this does not verify the full file's SHA-256. Complete downloads do that.

| `Hub.LocalAi` contract | Meaning |
|---|---|
| `Engine(model)` | Future engine choice (`Path`, `Kind`, `Label`, optional `Note`) |
| `Problem(model)` | Known preflight block or missing engine; null is not a success certificate |
| `Fit(model, context)` | Estimated capacity at a context/cache choice |
| `PreviewAsync(...)` | Header-derived model metadata for a selected file/projector |
| `Compatibility(model)` / `CompatibilityMatrix()` | Recorded observations / Markdown export |
| `RuntimeReady`, `Installed`, `OllamaEngine` | Readiness can come from Ollama even with no Aqua install |
| `StopAsync(id)` | Stop and clear that model's recorded failures; null stops all without clearing all failures |

`LocalAi` change/progress notifications marshal to the UI thread; Core runtime events do not. It tracks refresh, health,
install, download, browse, preview and device work, coalesces health checks, serializes refresh and detaches every named
subscription before cancelling/joining. Device probes run off the dispatcher and can use Ollama's engine without an Aqua
install. Models keeps stable library/file rows, shows engine choice/note and known problems independently of capacity,
and exposes observations plus explicit Copy matrix. Unknown layers are never inferred as all layers on the GPU.

Search-file previews are explicit and serialized: changing projector selection or navigating away cancels the old request.
Both model and selected projector headers are inspected. The header cache holds at most 128 entries; cancellation reaches
transport reads during synchronous GGUF parsing on a worker. A failed preview retains weights-only wording. A header is
not full-file verification, proof of projector pairing or inference success. Runtime settings expose `useOllamaEngine`
and distinguish a pinned install from an available Ollama-only engine.

`CompatBook` stores up to 200 latest model/server observations in `llama/compat.json`, not a start-by-start history.
Known model hashes identify model keys; fallback keys use path/size/mtime. Server keys are kind/size/mtime fingerprints,
not executable hashes. Entries hold setup, offload, server-reported GPU buffers (including recognized projector logs),
load time and eligible-answer mean throughput. Transient failure may leave an earlier success intact; show current
runtime state too. `SpeedBook` separately measures whether MTP earns its memory/complexity. The retained
[matrix](compatibility/local-ai.md) labels observations, historic acceptance and provenance gaps separately.

## 5. Ask, chat state and presentation

`AskPlanner` forms a bounded plan; `AskAgent` gathers authorized hub/web/file context and streams a bounded tool loop.
`SourceBook` tracks attribution; lexical citation checks are a heuristic, not factual verification. `ThinkingGuard`
limits repetitive reasoning. The pinned b11387 engine with a thinking-capable model arms per-request reasoning control;
the server-issued completion ID and per-start key target only that request. Successful control continues the same stream,
with a ten-second/4,000-character grace bound. Unsupported engines, refused control or failure to answer use the bounded
direct-answer fallback. Custom and Ollama engine versions are not assumed to support the pinned protocol.
Public content, files, prior turns, notes and summaries are data, never authority to execute actions; Security defines
the approval and private-data rules. A model produces tool arguments, not an unrestricted executable command.

`AskSession` owns navigation-persistent UI state; `ChatStore` owns stored chats, summaries, notes and associated media.
Deleted chat IDs prevent an in-flight answer from recreating a deleted chat. Retention and privacy changes include media
cleanup. Attached-folder grants are session-only. Context compression uses a message fingerprint and is invalidated by
editing earlier messages. Before gathering, Ask resolves the selected/fallback model and obtains its actual local window
from the managed server's props, falling back to its launch context. Native Ollama uses a reported loaded context when
available; opaque external providers use the requested/configured size, which Aqua cannot independently certify.
An answer-local settings copy drives reading/chunk limits and model-window caps; planning, reading and writing carry that
context explicitly. Provider changes cancel open answers/compression and clear their old context meters.

Answer footers expose plan/gather wall time and available engine load/prompt/write components. Those model components
occur inside planning/gathering/writing and are not additive with phase totals; unreported measurements stay unknown.
Post-answer note/title/compression work is tracked separately. Typing schedules one cancellable warm-up after 700 ms;
only an idle, available Aqua-managed local lane can load. Acquiring a real generation reservation atomically cancels
warm-up, including background work; warm-up never queues. Model/provider changes, navigation, empty input and exit cancel it.

Ask displays at most 60 messages with Earlier/Later/Latest controls, as selected by Adrian. Sending returns to Latest;
the full conversation remains the storage/context/export source. Each Markdown preview is capped at 32,000 characters
and 200 top-level blocks, with an explicit full-copy/export hint. Render timers run at background priority, stop on
unload/invisibility and skip unchanged content. The minute clock/location now follows stable Ask policy; task-specific
agent/planner prefixes remain distinct and DATA fencing is unchanged. No cache-hit or quality improvement is inferred.

`AnswerMarkdown` parses blocks; `MarkdownView` renders selectable WPF content. Maths uses guarded `MathText`/`MathView`
and XAML-Math, with source text as fallback. Whole-answer copying preserves Markdown. Source/XAML contracts cover some
accessibility rules; native table semantics, very long messages and rendering limits need dedicated verification.

## 6. Image Gen and Roleplay

Image Gen uses a fixed native FLUX.2 Klein workflow on a configured loopback ComfyUI server. It is not an arbitrary graph
importer. `ComfyImageClient` handles discovery, filename-based pairing checks, job-scoped cancellation and recovery.
`ImageGenerationSession` owns the GPU lease until completion and confirmed release; uncertain jobs keep it reserved.
Comfy allocator statistics are not a whole-driver VRAM reading. Independently submitted Comfy jobs remain outside Aqua's
scheduler. `ComfyServerLauncher` discovers user-installed Desktop/portable launchers without installing them.

Connection, startup and generation prechecks include pairing mismatches as well as missing nodes/models. A small
`generated-images/gpu-recovery.txt` marker stores only the endpoint before submission. Confirmed release removes it before
releasing the lease; deletion failure retains recovery state. Startup restores the reservation before agents run. Recovery
waits for an idle queue and confirmed allocator release; it does not resume result retrieval or claim driver-level release.
Unreadable recovery metadata keeps the reservation and requires the original server address to be selected explicitly.

`ImageArchive` owns generated PNG/settings files, tracked server copies and expiry. Imported references are prepared by
Windows decoders (`ReferenceImages`): orientation/colour conversion, bounded canvas, white background and metadata-free
PNG. Content-named local copies make edits repeatable after Comfy clears its temporary uploads. References remain while
an archived edit, draft or active job needs them. This storage is separate from chat-picture retention.
File reads hold one bounded read handle, including gallery imports and retained-reference validation; changed/corrupt
content is rejected before submission. Clipboard dimensions are checked before background scaling/PNG encoding. Native
decode cannot be interrupted mid-frame; its operation is joined on normal shutdown.

The Roleplay tab uses Creative/Main with bounded session context and no Ask PC/web tools. A persistent personality library
does not exist yet; it belongs to Phase 9. Image Gen shipped early from that phase.

## 7. Windows effects, storage and updates

`IPlatform`, `ISecretStore`, `ILlamaProcessHost` and Ask's platform/tool adapters provide existing test seams. Core still
contains some Windows-specific path logic, and several UI services use static `Hub`; these are migration targets when
those features change. Keep new OS policy out of pages and new Win32 calls out of generic Core services.

SQLite (`HubDatabase`) uses WAL, parameterized statements, normalized FTS queries and persisted snapshots. The profile
owns settings, database, logs, caches, chat media, generated images and optional runtime/model files; Secrets live in
Windows Credential Manager. User model/output paths can point outside the profile and retain their separate ownership.

Health collection is read-only and unelevated. Process actions recheck identity/ownership and require the app's existing
confirmation policy. Speech is explicitly initiated: local Windows/Whisper paths and an optional online Windows mode.
Whisper is an optional verified download, not a dependency installed at every start.

Velopack handles per-user installation and updates. Release metadata fixes the GitHub repository; source builds without
that metadata do not check for updates. Installed copies check on a schedule, download only on request and install at
restart. Settings/profile data live outside the versioned installation directory. CI/release restore locked runtime/test
dependencies, compile the app/E2E harness, run unit/culture checks and audit packages; CI does not run desktop E2E.

## 8. Efficiency and evolution

Normal Quit keeps the dispatcher alive while scheduling stops and owners cancel/join Ask, image, LocalAi and LLM work,
then stops the model runtime and disposes Core dependencies. Initialization is joined too. The overall wait is bounded
at 45 seconds; Windows session exit/timeout cancels owners and closes the process job but leaves still-used dependencies
alive until process exit. Uncertain Comfy work retains its reservation/recovery marker; Aqua never sends global interrupt.
Stopping update/Ollama timers prevents new periodic work during shutdown; this is not a rewrite of their management policy.

Existing controls include conditional/batched requests, response/parse limits, bounded scheduling, content-keyed AI
caches, stable rows, dispatcher coalescing, retained main/flyout windows, display-sized cached images, adaptive telemetry,
idle trimming and the workstation GC configuration in `AquaHub.csproj`. They are implementation choices, not proof that
every page or workload meets a latency/memory budget.

Earlier .NET 9 Debug observations (Windows 11 / Ryzen 7 9700X / RTX 4070 SUPER) were about 13–18 MB private working set
at tray steady state, 80 MB with Today open and 115 MB after visiting pages. Those historical numbers are not a .NET 10
baseline or a Release performance guarantee. Language/image model processes and VRAM must be measured separately.

The 5 November offline .NET 10 Release diagnostic established a synthetic 300-message baseline and measured the 60-message
view separately. Results and limits are in [Scoring](SCORING.md); long-chat dispatcher stalls still exceed the proposed
50 ms target. The diagnostic uses an isolated profile and starts no model, collector, media or external server.

New work must define bounded storage/queue/parse sizes, cancellation, subscription ownership, failure states and a
performance acceptance workload. Follow [DEVELOPMENT.md](DEVELOPMENT.md) for verification and proposed budgets.
Prioritize shutdown/task ownership, single-run scheduling, model-process containment and focused UI service seams before
adding long-lived reminders, an indexer or user-built agents. Their concrete acceptance work is tracked in the roadmap.
