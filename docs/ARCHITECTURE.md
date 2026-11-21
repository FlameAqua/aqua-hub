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

`HubCore.CreateAgents()` registers 16 agents: collectors feed clustering/editorial work, market analysis and Foresight;
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

### Manual agent recipes

Workbench owns authoring. A recipe has a stable ID, a revision, a paused flag and one or two typed steps. The fixed
`pc.health.check` capability takes no inputs. Optional `pc.health.report` consumes the health reading from step one
and a typed All or Actionable selection. Actionable includes warnings and problems. Core validates at save, review
and admission, and rechecks the saved revision before step access. Agents must review the saved revision and invoke
**Run now**. Workbench saving does not admit a run.

`RecipeService` follows AgentRuntime's owned worker and shutdown pattern. Manual runs use one worker globally,
a 40-second cooperative deadline and no replay or retry. Concurrent approvals for the same pending or active
revision join its run ID. One confirmation cannot start again, even after its run finishes. Edits and pauses invalidate
older reviews and cancel outstanding steps. Delete also removes that recipe's history. In-flight completion cannot
recreate deleted state. A profile reopen records persisted queued or running work as interrupted. Normal Quit
cancels and joins the owned worker. Manual recipes remain independent of the collector pause and scheduling tick.

The additive SQLite data holds up to 30 recipes, names up to 80 characters and purposes up to 200 characters. The
recipe store has a 512 KiB read limit and retains at most ten runs per recipe including active work. Each run
records its recipe revision, capability IDs, timestamps, typed outcomes and short counts. At most eight finding
headlines from the newest completed report step remain available in the running session. Clearing recipe history
removes finished rows and their preview. This PC retains its latest full report separately and offers **Clear report**.
Built-in history
remains in the same table with an additive outcome column. Old rows use their original success/failure flag.

### Live streams (5.3)

`StreamsSession` owns public-page refresh and Twitch sign-in/credential rotation. `StreamScoutAgent` requests checks
while Social is visible; source state lives in a separate bounded `StreamsSnapshot`, persisted as `state:streams`.
It is not inserted into feed-item search, Pulse or automatic AI prompts. Native rows retain identities between updates.
Persisted data is bounded and canonical provider URLs are revalidated before display/opening.

Both providers are opt-in under `social.streams`. Twitch checks followed live channels with a user token and the
`user:read:follows` scope. Public-client device OAuth uses an independent non-redirecting HTTP client, bounded JSON and
minimum polling intervals. Sign-in can be cancelled; credential replacement is serialised and versioned so disconnect or
client-ID changes reject late completions. Rotated refresh tokens are saved before validation, since they are single-use.
Stored access tokens are validated before first use and hourly while checks run; rejected access can renew once, and
rejected refresh clears the local connection. Secret-store writes/deletes are checked by reading back. Normal exit cancels/joins stream work
before HTTP disposal; Core async disposal has a shared identity, and synchronous disposal starts observed async cleanup.

YouTube reads only constructed HTTPS `/channel/<validated-id>/streams` addresses with existing reject-consent headers.
Responses are limited to 4 MiB, JSON depth/work is bounded and only recognised live badges in the selected channel tab
are accepted. Unknown page layouts fail rather than replacing cached data with an empty list. Checks cover at most six
selected channels every 15 minutes while Social is open; manual checks have a one-minute floor. `SourceHealth` handles
repeated failures, and separate attempted/success timestamps prevent rapid retries while retaining honest age/error data.
Navigation, hiding and minimising cancel visible-page work. Manual refresh uses the agent runtime and respects
pause/disable; shutdown joins refresh and sign-in before their HTTP clients are disposed.

`YouTubeAccount` separately owns optional Google Desktop OAuth and manually synced subscriptions. It opens the system
browser with authorization code/S256 PKCE and random state, listening only on a random `127.0.0.1` port for at most
three minutes. Callback host/path/state and duplicate parameters are checked; requests and responses are bounded.
It imports only validated Desktop client fields; configuration endpoints cannot redirect authenticated requests.
The YouTube API requests only `youtube.readonly`, paginates `subscriptions.list?mine=true` at 50 items/page and stops
at 20 pages/1,000 subscriptions with an explicit partial-list indicator. No automatic subscription polling is added.
API failures preserve the prior successful catalogue/time. Invalid refresh credentials require reconnect.

Access tokens stay in memory. Desktop configuration and refresh credentials use separate, read-back-verified Windows
credential entries suffixed by a profile-path hash. The private catalogue is stored outside HubState/feed/AI, at
`account:youtube-subscriptions`. Settings retain only chosen channel ID/name pairs in `social.streams.youTubeSelection`;
null preserves the previous first-six saved-channel behaviour, while an empty list remains empty. The searchable,
virtualised picker combines saved upload channels and imported subscriptions without changing the upload feed list.
Successful complete syncs reconcile selections; partial lists cannot prove an old selection was unsubscribed.
Disconnect/configuration replacement clears imported metadata and Google-only selections before revocation; epoch
checks reject late results. Manual upload channels remain. Normal/forced shutdown cancels, joins and disposes owned
Google work. Sign-in/sync can be cancelled and navigation cancels the page's operation.

## 3. AI routing and the GPU

`LlmClient` supports Ollama, Aqua's runtime (`llamacpp`) and OpenAI-compatible endpoints. Structured requests include
schemas and bounded repair; output still needs deterministic validation before publication or tool execution.

`PriorityGate` serializes GPU generation. Interactive work precedes waiting background work; it does not preempt an
already running request. An Ask answer reserves the slot across its tool loop and releases it while awaiting user approval
or a requested AI agent. Image generation uses the same gate. File search uses a separately owned CPU embedding process.

| Model choice | Use |
|---|---|
| Main | Ask and general generation |
| Deep override | Briefs and market analysis; Main when unset |
| Editor (`ai.llama.backgroundModel`) | News Editor, Social Pulse and Foresight; Main when unset |
| Creative | Roleplay and Workbench personality drafts; Main when unset |
| Search | Verified EmbeddingGemma Q8_0 for local file retrieval |

Text and image generation use the GPU lane. Read-aloud synthesis is a separate CPU operation. One generation model
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

The Image Gen panel gives the prompt, Generate and the canvas separate roles. Generation parameters and the ComfyUI
and gallery controls use one inspector with keyboard dismissal and focus returned to its entry point. Recovery opens
the ComfyUI inspector. Session messages update a dismissible overlay with screen-reader announcements. The status icon
can reopen the notice.
The overlay does not measure against the studio form or gallery.

Roleplay and Workbench personality drafting use the existing generation lane. Aqua's runtime uses Creative when set
and otherwise Main. A missing explicit Creative model fails with a link to its role selector. Other providers use
their active model. These requests offer no tools and omit raw text from recent-model diagnostics.

`PersonalityLibrary` stores a versioned, bounded list in the profile database under its own key. Workbench owns
library actions and populates an editable preview before an AI draft can be saved. Each saved template has a UUID,
name, character instructions and an optional opening scenario. Selecting a template in Roleplay chooses it for the
next scene. On New scene, `RoleplaySession` copies the selected template into a memory-only scene. Editing or deleting
its library entry leaves that snapshot usable. Reset cancels and invalidates the old stream. The next turn waits for
the old operation to exit so late chunks cannot reach the new scene. Returning to the tab shows the active scene.
The session bounds both visible turns and the history sent to the model; normal quit joins an active stream.

### Read aloud

Ask answers, briefing actions and Settings preview share `SpeechService` and one `ReadAloud` coordinator. Windows
voices are the default and follow the app language. The opt-in English Kokoro presets use audio.cpp's CPU process.
The selected accent determines Kokoro's phonemizer language. An unavailable installation, voice language or playback
device produces an actionable notice. Switching to Windows voices is an explicit Settings action.

The coordinator identifies which control owns the request. A repeated click on that control stops it; a different
request replaces it. Speech continues across pages with a Stop control in the shell. The coordinator limits input
before Markdown preparation, speaks prose and table rows, turns formulas into words and omits citation markers and
fenced code. It prepares at most 8,000 characters and 96 chunks, with a 60-character first chunk and up to 128
characters per subsequent chunk. Playback and synthesis run off the dispatcher. The worker can prepare one chunk
ahead while playing the current one. Stop, replacement and shutdown cancel and join owned work, including lookahead.
The first version exposes Stop, without a pause or resume control.

`KokoroSetup` verifies exact-size, SHA-256-pinned artifacts and stages an app-local install. The native process
uses two inference threads, starts on demand for one chunk and exits before another synthesis begins. It listens on
no network port and does not enter the generation GPU gate or the Search embeddings lane. The Windows adapter
validates the size and header of the returned WAV and plays from memory. The child runs in a Windows kill-on-close
job; playback completion and errors dispose the corresponding Windows objects. See [Security](SECURITY.md) for
temporary audio handling and [Scoring](SCORING.md) for measured limits.

## 7. Windows effects, storage and updates

### Local file search

The native Search files page accepts separate folder grants and optional content consent. SearchSession connects the
Search model role and Aqua's verified runtime to FileSearchService. An existing index reopens on startup. A fresh profile
opens the database on first use. Initialization runs off the dispatcher and the page can retry an unavailable store.
Search shares SQLite, WAL, FTS5, file exclusion rules, theme controls, shell-action policy and process containment with
the rest of Aqua. A separate profile database, `search.sqlite`, isolates its size budget and keeps private passages out
of feed and Ask queries.

The Deck and Command Palette open the same Search page. The palette accepts `Search files: <query>` and leaves the
query in the editable search field. The installed default profile can opt into **Search here with Aqua** in File
Explorer's folder and folder-background menus. Its static verbs launch the same executable with the folder as one
argument. A bounded current-user pipe passes that scope to the running instance in the same profile and Windows
session. The name, text and semantic results come from approved files inside the selected folder. **Review folder**
opens the usual consent form when the selected location has no approved coverage.

One worker consumes up to 256 coalesced watcher paths. A resumed or interrupted session compares the current tree with
committed file stamps. Overflow requests reconciliation. An active index also reconciles every 15 minutes. Each pass is
bounded to 100,000 visited entries per root and 64 path components. At most 16 disjoint roots, 20,000 files and 16,000
passages can be retained. Limits leave partial coverage visible. Files are identified by volume, file index and creation
time. Size, last write and NTFS change time invalidate stale passages. A rename replaces the path and regenerates the
title-dependent vectors. Separate file copies retain separate results.

Content support covers TXT, MD/MARKDOWN, CSV, TSV, DOCX, PPTX, XLSX, ODT, ODP and ODS. The reader consumes a verified
handle with an 8 MiB input cap, bounded package/XML expansion and a per-file extraction deadline. Output is limited to
32,768 UTF-16 code units and 64 KiB of UTF-8 passage text. Unsupported files are name-searchable. Changes to extraction,
normalization or chunking require a SearchText version bump. Incompatible document versions are discarded on reopen.
The model, server, pooling, dimensions and retrieval prompts identify the vector space. Incompatible vectors are cleared
for incremental regeneration. The main database caps at 496 MiB with operational room for WAL. Root removal requests
incremental vacuum. Live profile size includes temporary SQLite files.

Exact full paths and filenames lead. Other candidates come from partial or one-edit name matches, FTS5 passage rank
and a bounded cosine scan. Reciprocal-rank fusion combines file ranks with a constant of 60. A file contributes once
per signal. A cosine floor of 0.40 suppresses weak semantic-only candidates on the labeled fixture. The page publishes
up to 50 revalidated files, showing a match reason and up to 200 characters from a matching passage. A new query cancels
the previous request. Lexical results appear first. A missing runtime or model leaves them usable.

The supported model is Aqua's pinned EmbeddingGemma 300M QAT Q8_0 GGUF. A separate authenticated llama-server runs
with CPU device selection, zero GPU layers, one slot and two inference/batch threads. The embeddings API returns 768
mean-pooled, normalized values per query or passage. Search checks shape, finite values and norm. Input includes the
retrieval prefix and fits the model's 2,048-token window. A query takes priority between indexing requests. The idle
check releases the CPU process after approximately one to two minutes. Runtime installation/removal and Search model
removal join the CPU process before replacing its files. Quit joins Search work before disposing the shared process host.
The model uses the [Gemma terms](https://ai.google.dev/gemma/terms). See the
[model card](https://ai.google.dev/gemma/docs/embeddinggemma/model_card) and
[pinned server API](https://github.com/ggml-org/llama.cpp/blob/b11387/tools/server/README.md).

### Settings and contextual help

Settings groups its existing sections by task. Navigation, search and related links share the section and optional row
addresses, for example `social:youtube`. The page opens the destination disclosure before scrolling and refocusing.
Related links stay within the existing Settings editor, preserving its draft and merge behavior. Row descriptions remain
searchable when shown as contextual help. The Twitch, Google and channel disclosures retain their existing settings and
account handlers.

### Agenda, reminders and the deck

Upcoming projects the existing read-only agenda snapshot onto one selected local day. Agenda Scout widens its
existing read window to include the start of today and retains events that overlap that window. The UI uses pages
of 60 source events and preserves matching rows when the selected day or snapshot changes. Today is the entry
point for the floating deck. The shell owns the deck across navigation. Normalized position and hidden state are
profile settings. Move by dragging, with arrow keys on the bubble, or through UI Automation. Activating it opens shortcuts
for Ask, Launchpad, up to two pinned apps, today's agenda, a new reminder, the timer and hide. The shortcut direction
uses available space, and animations follow Aqua's and Windows' motion settings. The settings store replaces the file
atomically, including while shareable readers have it open.

Pinned applications use their Windows icons with a built-in fallback. Pinned web links use a globe glyph. An active
timer shows a clock above the remaining time on the Deck handle. Deck surfaces use the active theme's opaque popup and
accent brushes. Hover and press use the same accent and foreground
pair. Expansion animates position without changing the opacity of the controls.

The Deck timer accepts 1–60 minutes and creates a normal one-off reminder at a fixed instant. Optional duration
metadata lets the shell resume displaying the countdown after restart. Edits retain the metadata when the schedule
stays the same. Rescheduling removes it. Older reminder records remain valid. The shell derives remaining time from the persisted
deadline and selects the earliest active Deck timer. Its one-second display tick only runs while the deck and window
are visible. Stop deletes the selected timer through `ReminderService`.

`ReminderService` owns reminder policy and persistence. The existing `AgentRuntime` tick checks the next due instant,
local midnight, retry time and clock revision without reading storage on idle ticks. It schedules at most one reminder
check, including while collectors are paused, and joins reminder work before shutdown. The tray message window signals
clock, regional settings and power changes. The service serializes checks and edits on a worker with up to eight
outstanding operations. Once a SQLite transaction starts it finishes; queued operations can be cancelled.

New and rescheduled reminders start in the next five years. One-offs and snoozes keep an instant. Daily and weekly
schedules use local wall time. A one-off entered in a daylight-saving gap is rejected. A repeat advances by the gap,
and an ambiguous time uses the earlier instant. The latest due occurrence per repeat wins
after a gap. Work within five minutes can create an alert; older work is marked Missed. Each repeat retains at most
one outstanding occurrence alongside its next nominal date. A later occurrence replaces the earlier outstanding one.
Done removes that occurrence, or the whole record for a one-off. Snooze requests another delivery after ten minutes.

An additive SQLite table holds up to 200 reminder records, with titles limited to 120 characters and a 512 KiB stored
read limit. There is no completed occurrence history. The calendar feed cache and alert bell can be cleared separately.
A schedule identity, nominal occurrence and delivery attempt identify the bell row. The cursor and alert writes commit
in one transaction before the app requests delivery. This prevents duplicate delivery after restart or bell clearing.
A process exit between commit and a notification request can leave the reminder in the agenda and bell without a toast.
The existing notification policy governs pop-ups, including quiet hours and fullscreen. The Calendar reminders
switch controls imported event alerts. An immediate personal reminder requests the Windows notification sound, subject
to Aqua's notification policy and Windows sound settings. Held summaries are silent. The held-summary queue stays
in memory. A restart preserves the agenda and bell record while discarding the queued pop-up. Editing,
finishing or deleting an occurrence withdraws its pending Aqua alert. Windows controls notifications Aqua handed off.

A reminder can reference a saved Launchpad scene by ID. When the occurrence is Due or Missed, Upcoming offers the
current scene steps for review. The explicit **Run now** action checks the current scene and relevant configured app
targets against that review and atomically claims the occurrence before using the existing scene executor. Only one
confirmed reminder scene runs at a time. A run request remains recorded across snooze and restart. A new recurring
occurrence can offer its own run. The user marks Done separately. Cancel stops the remaining steps, and deleting,
snoozing or rescheduling the occurrence withdraws authority for the next step. Quit cancels and joins this owned work.
Effects already begun retain their existing OS semantics.

`IPlatform`, `ISecretStore`, `ILlamaProcessHost` and Ask's platform/tool adapters provide existing test seams. Core still
contains some Windows-specific path logic, and several UI services use static `Hub`; these are migration targets when
those features change. Keep new OS policy out of pages and new Win32 calls out of generic Core services.

SQLite (`HubDatabase`) uses WAL, parameterized statements, normalized FTS queries and persisted snapshots. The profile
owns settings, database, logs, caches, chat media, generated images and optional runtime/model files; Secrets live in
Windows Credential Manager. User model/output paths can point outside the profile and retain their separate ownership.

Health collection is read-only and unelevated. It records the check time and the timestamp of a recent memory sample.
Fixed checks include active Windows Firewall profile settings and up to 100 matching disk retry events from seven
days of the available System log. Firewall readings describe local settings, which applied policy can override.
Windows readings distinguish observed, partial, unavailable, unsupported, denied and timed out. An incomplete
check with no warnings or problems does not produce a healthy verdict. Timed-out synchronous Windows calls remain
owned by their probe until they return, limiting each probe to one in-flight read. Process actions recheck identity
and ownership and require the existing confirmation policy. Voice input is explicitly initiated through Windows
or Whisper, with an optional online Windows mode. Read-aloud output uses the separate offline speech coordinator.
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
