# Aqua Hub — Architecture

Aqua Hub is a private, agent-driven personal hub for Windows. A set of small agents continuously
collect, cluster, analyse and summarise information (news, local social chatter, markets, prediction
markets, agenda, weather, PC health) and a native Windows shell presents it: a full dashboard, a
taskbar quick panel, a global command palette and toast alerts. All AI runs on the local machine.

## 1. Technology decisions

| Concern | Choice | Why |
|---|---|---|
| UI framework | **WPF on .NET 10 (LTS)** | Native, DirectX-rendered, no embedded browser engine; sub-second start. Measured footprint in §5. First-class Win32/WinRT access (tray, DWM Mica/Acrylic, global hotkeys, media sessions, Core Audio, speech). |
| Rejected: Electron | — | Bundles Chromium + Node (150 MB+ disk, 300 MB+ RAM). Explicitly out of scope. |
| Rejected: Tauri | — | Lighter than Electron but still a WebView2/Chromium multi-process runtime (+80–150 MB RAM), and needs the Rust + MSVC toolchains. |
| Rejected: WinUI 3 | — | Heavier runtime/packaging story for unpackaged apps and no lighter than WPF in practice. |
| Language | C# 14 | Memory-safe, fast, great Windows interop. |
| Storage | **SQLite** (Microsoft.Data.Sqlite, WAL, FTS5) | Zero-admin, crash-safe, full-text search for “Ask Aqua”. |
| Local AI | **Ollama** (default) or any **OpenAI-compatible** local server (LM Studio, llama.cpp, vLLM) | Swap models freely; loopback-only by default. |
| Default model | `qwen3.5:9b` (auto-detected) | Strong instruction following + structured output at ~6 GB VRAM; fits a 12 GB GPU with room for context. `qwen3:14b`/`gemma3:12b` optional for long write-ups. Falls back to smaller models (`qwen3.5:4b`, `gemma3:4b`) on smaller GPUs. |
| Third-party runtime deps | **4** (Microsoft.Data.Sqlite → SQLitePCLRaw; System.Speech for offline dictation; Velopack for Setup and updates, §13; XAML-Math — WpfMath → XamlMath.Shared — for formulae in Ask's answers, §10) | Everything else is BCL/Windows SDK: HTTP, JSON, XML, WinRT, Win32. Whisper is an optional download, not a package (§11). |

## 2. Solution layout

```
src/AquaHub.Core     platform-neutral engine (net10.0) — fully unit-tested
  Settings/          HubSettings (JSON), copy-on-write SettingsStore, validation and versioned
                     migrations, SettingsSearch, Shortcuts (clash rules), ISecretStore
  Data/              HubDatabase: items + FTS5, JSON snapshots, LLM cache, HTTP validators, alerts, runs, prices
  Net/               HttpFetcher: HTTPS-only, size caps, conditional GET, retries, per-host cool-down
  Feeds/             FeedParser: RSS 2.0 / RSS 1.0 (RDF) / Atom, XXE-safe
  Sources/           RSS/Google News, Reddit (RSS), Mastodon, Bluesky, Hacker News, YouTube,
                     Yahoo Finance (batched), Polymarket, Kalshi, Open-Meteo, Nager.Date,
                     economic calendar, Finnhub earnings, ICS calendars (RRULE expansion)
  Analysis/          text tools, cross-source StoryClusterer + ranking, TechnicalIndicators
  Markets/           FxRates (exchange rates), Portfolio (value, gain, history), MarketBar (the
                     chart row), SymbolLinks (a symbol's page on quote sites)
  Ai/                LlmClient (Ollama/OpenAI-compatible, schema outputs, priority gate),
                     Prompts, AskService (RAG), CommandInterpreter (allow-listed intents)
  Agents/            AgentRuntime scheduler, HubState blackboard, 15 agents, fallbacks, digests
src/AquaHub          Windows shell (WPF)
  Platform/          Win32/WinRT: tray, hotkeys, DWM effects, system monitor (CPU/RAM/GPU/VRAM/
                     processes), media sessions, Core Audio, app catalog/launcher, speech, vault
  Services/          composition (Hub), windows, actions/scenes, theme, images, tray controller,
                     taskbar integration (jump list, thumbnail media buttons, alerts badge)
  UI/                design system (Theme/*.xaml), controls (charts, gauges, icons, FlowGrid),
                     shell (MainWindow, FlyoutWindow, CommandPalette, Onboarding), pages
tests/AquaHub.Tests  xUnit (840+): parsers, security cases, clustering, indicators, ICS, commands,
                     LLM plumbing, DB, settings merge, notification policy, scheduling/back-off,
                     Sentinel de-duplication, fact checks, the update flow (fake updater), your place,
                     Ask's model picker, Ask's Markdown and LaTeX (formulae run through XAML-Math's own
                     parser), chat notes, compression and pictures, a XAML accessibility contract (no
                     name or id on an element without an automation peer), the collectors and AI agents
                     against a stand-in network (FakeNetwork) and model server (FakeOllama); plus an
                     opt-in live run (AQUAHUB_LIVE=1)
tests/AquaHub.E2E    UI Automation suite that drives the real app (every page, button, window)
                     in a sandboxed --e2e profile where side effects are journalled, not performed
```

## 3. Agent pipeline

```
 Collect                         Understand                    Write                    Watch & keep
 ───────                         ──────────                    ─────                    ────────────
 News Scout ──(new items)──────▶ Story Curator ──────────────▶ News Editor (AI)
 Social Scout ─────────────────▶ Social Pulse (AI)
 Market Watch (60–120 s, 1 req)  Market Analyst (AI, hourly)
 Prediction Scout ─┐
 Agenda Scout ─────┴──────────▶ Foresight (AI)
 Weather                                                       Chief of Staff (AI brief) ◀── all of the above
                                                                                         Sentinel (alerts, 1 min)
                                                                                         Model Warden · Housekeeper
```

* **Scheduling** – `AgentRuntime` ticks every second; each agent declares its interval (adaptive:
  markets are fast only while an exchange is open) and `After` dependencies, so a collector finishing
  with new data triggers its consumers within ~2 s. Failures back off exponentially (30 s → 32 min).
  `RunAndWaitAsync` runs one agent now and waits for that run — or for the one already under way, rather than
  queuing another — up to a timeout: Ask's *run_agent* (§10).
* **Concurrency** – collectors run in parallel (bounded), AI agents are serialised and share the single
  GPU slot through a **priority gate**: interactive requests (Ask, palette) jump ahead of background work.
* **Eco mode** – intervals stretch ×3 while the user is idle and ×2 on battery (UI hidden).
* **Game mode** – `SHQueryUserNotificationState` detects full-screen D3D games / presentations; AI work
  pauses (agents report *Waiting*), toasts are held and delivered as one summary afterwards, and scenes
  can unload the model to free VRAM.
* **Caching** – LLM outputs are keyed by a hash of their inputs (e.g. a story's member articles), so the
  model only runs on genuinely new content. Feeds use ETag/Last-Modified conditional GETs.
* **Graceful degradation** – every AI product has a deterministic fallback (extractive summaries,
  keyword pulse, indicator-based market view, list-style foresight, compact brief). The hub is useful
  with no model installed.
* **Freshness & offline** – collectors record their last *successful* fetch per area; the title bar shows
  "News updated 4m ago" from that (not from UI or telemetry updates). When requests to several hosts fail
  at the network level the hub shows an offline banner with the time its data is from, and the collectors
  run again as soon as any server answers.
* **Checking the model** – market digests state whether exchanges are open ("figures from Fri close"),
  weather digests carry units, a percentage Foresight quotes must belong to the market that sentence is
  about (not merely to some market) and a "favourite" it names must be the leading outcome (else the
  sentence is replaced with the data), posts the pulse files under a topic must share a real word with it,
  and repeated JSON keys are merged rather than silently dropped.
* **Stale AI output expires** – while the model is unavailable, AI-written text stands in for a limited time
  (pulse 6 h, Foresight 12 h, the market view only for the same trading day); after that the deterministic
  view replaces it, and every AI stamp carries its day ("yesterday 21:14").
* **News accuracy** – clustering compares an article with the story's *core* words (present in ≥30% of its
  articles), not just its closest member, so unrelated stories can't chain; place names never count as the
  shared words that link headlines, and a weak match between headlines naming different places is refused.
  A story is local only when most of its articles mention the area (or most come from local outlets and
  some do). Sources are counted per publisher ("TheJournal.ie" = "The Journal"). The local Google News
  search is qualified with the country and drops publishers that don't look local (domain, name).

### Structured output

Agents request JSON. The schema is sent as the server-side constraint **and** restated in the prompt
as a compact TypeScript-like shape (some model/server combinations ignore the constraint when
“thinking” is disabled — observed with Ollama 0.30 + qwen3.5). Outputs are normalised (fences, echoed
schema wrappers, single-key envelopes) and get one repair attempt; refusals are rejected. Prompts tell
the model that the data is live and newer than its training data so it neither “corrects” nor refuses
current facts.

## 4. Data flow & state

`HubState` is the blackboard: agents publish immutable snapshots (collections are replaced, never
mutated), raise a topic (`news`, `markets`, …) and persist a JSON snapshot so the UI is populated
instantly on the next launch. Pages subscribe only while visible and coalesce bursts of updates
through a `UiThrottle`, so a background refresh never costs more than one layout pass per page.

* **Stable rows** – story rows are reused by id and updated in place, so summaries arriving never collapse
  an expanded story or move what you're reading; when new stories arrive while you're scrolled down, a
  "N new stories" pill offers them instead of jumping. The News Editor publishes summaries in batches.
* **Page state** – pages visited while the window is open stay warm (scroll, filters, chosen symbol and
  range, settings section). The Ask conversation lives in an app-wide session that keeps streaming while
  you look elsewhere and is kept in the local database (optional, Settings → Privacy).
* **Settings forms merge** – a form saves only the fields it changed (a three-way merge against the copy it
  loaded), so a do-not-disturb toggle from the tray, a hotkey fallback or first-run app discovery made
  meanwhile is never reverted; forms also refresh when settings change elsewhere.
* **Your place** – a new profile has none (`LocationSettings.IsSet`): until you choose one the weather agent and
  the Google News search for your town don't run, the brief's local section is called *Local*, and prompts leave
  the place out rather than guess (Ask's time zone is then Windows' own). Choosing a place, from the search or
  *Use my location* (`WindowsLocation`: Windows' geolocator at town accuracy, rounded to about a kilometre, then
  OpenStreetMap's Nominatim for the town and Open-Meteo for its time zone), goes through
  `LocalePacks.ChoosePlace`: the country's outlets, subreddits, holidays, index, units and currency on a first
  choice or a move abroad (only values that are still defaults or the previous country's), and within a country
  the new town's subreddit and hashtag in place of the old town's. Profiles from before keep Dublin, as they
  saved it.
* **Markets** – the chart row is the `Indices` and `Macro` lists (`MarketBar` moves, removes and adds, and the page
  keeps its tiles when nothing about them changed). The portfolio (`Portfolio`) values holdings in your base
  currency with `FxRates`: Yahoo pairs such as `EURUSD=X`, inverse pairs and crosses through the dollar, and
  quotes in minor units (GBp, ZAc, ILA) divided by 100. Market Watch fetches only the rates your holdings need (for
  their prices and for the currency you paid in) and keeps their daily history; the chart replays today's
  holdings over each day's closes at that day's rate, starting once every holding has a price. A holding without a
  price or a rate is listed apart rather than added in the wrong currency. Typed numbers accept "0.5" and "0,5"
  (`Numbers`, the culture deciding a thousands separator).
* **Settings search and shortcuts** – `SettingsSearch` scores every row's title, extra keywords, section and
  description (every word has to match; plurals count) and takes you to the row with its control focused.
  `Shortcuts` checks a new global shortcut against the other one and Aqua's own keys, and asks Windows whether it's
  free; the hotkey box also notices a combination Windows or another app keeps (its key-up arrives without the
  key-down). Aqua's own global hotkeys pause while you record one.
* **Settings versions** – `SettingsStore` migrates older files once, step by step (version 4 added the Gaming &
  internet sources, matched by id or host so a source you already had isn't added twice).
* **Only what changed** – the dashboard view model raises just the properties of the section that changed
  (telemetry every 2.5 s no longer re-evaluates the whole page).
* **Alerts** – `HubContext.RaiseAlert` stores an alert (optionally once per key), refreshes the bell and hands it to
  the platform, which applies do not disturb, quiet hours and game mode. Windows reports a click on one of the
  tray icon's notifications without saying which: when only one was shown since the last click, the click opens
  that alert; after several, or on a summary of held alerts, it opens the bell's list. Opening an alert anywhere
  (the list, an in-app banner or a notification) marks just that one read, and its banner closes; *Clear* and *Mark
  all read* also take the alerts' banners away. A banner stays while the pointer or keyboard focus is on it.

## 5. Performance & footprint

* No browser engine, no web server, no background polling from the UI.
* The dashboard and quick panel are created on first use and then **hidden rather than destroyed**. A leak
  probe (open, close, force a GC, check a weak reference) showed that WPF keeps a closed window alive through
  an internal GC handle whenever its page held data-bound content — every open/close cycle leaked a whole
  window and page. Reusing one instance bounds memory (committed memory stays flat across repeated
  open/close), makes reopening instant, and pages stop listening for updates while hidden.
* While nothing is on screen, a compacting GC and a working-set trim run a couple of minutes after each burst
  of agent work (only when more than 4 MB was allocated since the last trim), and 20 s after the UI is hidden.

Measured footprint (Windows 11, Ryzen 7 9700X, RTX 4070 SUPER, .NET 9, Debug build — the ReadyToRun Release build from `scripts/publish.ps1` JIT-compiles less, so its private memory is, if anything, lower):

| State | Task Manager "Memory" (private working set) | Committed (private bytes) | Working set incl. shared DLLs |
|---|---|---|---|
| Tray only, first minutes | ~63 MB | ~94 MB | ~190 MB |
| Tray only, steady state (after idle trim) | 13–18 MB | ~75 MB | 35–52 MB |
| Dashboard open on Today | ~80 MB | ~125 MB | ~185 MB |
| After visiting every page | ~115 MB | ~160 MB | ~250 MB |
| Closed to the tray (hidden, after the trim) | 6–11 MB | ~125 MB | 14–26 MB |
| …after 5 more open/close cycles | 6 MB | 125 MB (flat) | 14 MB |

The dashboard rows come from `--snapshot <dir> --measure` (§7), which shows the real window off-screen.

The "incl. shared DLLs" column is large because WPF initialises Direct3D even for a hidden tray window,
which maps the GPU driver (~135 MB of shared, read-only image pages on NVIDIA); that memory is shared with
every other GPU-accelerated app and is not what Task Manager reports. The .NET heap itself is ~7 MB; the
local model lives in Ollama's process and VRAM, not in Aqua Hub. Concurrent GC was measured and rejected:
it cost 4–9 MB more committed memory for no visible benefit.
* Batched market data (one request for all instruments), conditional feed requests, capped responses.
* Images decode off the UI thread at display size and are disk-cached (7 days / 200 MB).
* Telemetry sampling adapts: 1.5 s on the PC page, 2.5 s with any UI open, 20 s in the background.
* Process list from a single `NtQuerySystemInformation` snapshot (no per-process handles), layout
  validated at runtime with a safe fallback.
* GC tuned for an always-on app (workstation, non-concurrent, `ConserveMemory=7`, `RetainVM=false`).
* *This PC → Aqua Hub itself* shows the live private working set, committed memory and threads.

## 6. Extending

* **New source** – add a fetcher in `Sources/`, call it from a collector agent, map to `FeedItem`.
* **New agent** – derive from `Agent`, register it in `HubCore.CreateAgents()`; it immediately appears on
  the Agents page with status, telemetry and *Run now*.
* **New command** – add the action to `Prompts.CommandActions`, validate it in
  `CommandInterpreter.Validate`, execute it in `ActionExecutor`.

## 7. Visual QA

`AquaHub.exe --snapshot <dir> [--data-dir <profile>] [--pages today,news] [--wait 240]` runs the agents
against live data in an isolated profile and renders every page off-screen (dark, light, narrow, onboarding,
flyout, palette) to PNG — no windows appear. Used for design review and regression checks.

`AquaHub.exe --snapshot <dir> --measure [--data-dir <profile>]` is the footprint check behind §5: it shows the
dashboard off-screen without capturing (captures allocate large bitmaps) and writes `memory.txt` with the memory in
the tray, on Today, after visiting every page, hidden to the tray and trimmed, and after five more show/hide cycles —
which must not grow (the reason the dashboard is reused rather than destroyed; see §5).

## 8. Local model server

`OllamaManager` starts and stops a *local* Ollama (never a remote endpoint). The server is identified as the
process listening on the configured port (`GetExtendedTcpTable`); stopping ends that process tree and Ollama's
tray app if it runs from the same folder (checked by path; remembered and relaunched on the next start), starting
runs `ollama serve` with no window. The server counts as busy — so an idle stop waits — while a model is loaded,
another program has a connection open to it (a terminal `ollama run`/`pull`, a chat app: the TCP connection table)
or it is downloading a model. Your own Turn off is never undone automatically (no on-demand or scheduled start)
until you turn it on, and a Turn on during a game holds until the game ends. The decisions are a pure, unit-tested policy (`OllamaPolicy`):
sustained GPU contention (a full-screen game, or any app keeping the GPU above 60% for a minute while the server has no model loaded — so it is never mistaken for Ollama answering another app) stops
it and it restarts two minutes after the GPU frees up; no model loaded and no requests for N minutes stops it;
interactive requests, "Regenerate" and scheduled briefs may start it (`LlmClient.StartServer`), background
agents never do — so an idle stop really frees the memory. Quitting Aqua stops a server Aqua itself started.

## 9. Taskbar

The tray icon is a raw `Shell_NotifyIcon` (no WinForms) on a hidden message window that also receives global
hotkeys and theme/power broadcasts: click toggles the quick panel, double-click opens the dashboard,
middle-click plays/pauses, and the tooltip is a multi-line glance (weather, first index and whether its
figure is from a close, alerts/DND/AI state, next event within 12 h, what's playing) trimmed to Windows'
127 characters by priority. The icon is re-rendered only when its state key (alerts, AI paused, market
trend) changes. The main window's taskbar button gets previous/play/next thumbnail buttons drawn for the
*taskbar's* theme, and a count badge for unread alerts; the jump list's tasks start the exe with
`--flyout`, `--palette`, `--read-brief` or `--page …`, which the single-instance channel forwards to the
running copy (named events per command, scoped to the profile) or handles on a quiet cold start.

## 10. Ask Aqua: retrieval, tools and modes

Ask is an agent with tools (`Core/Ai/Assistant`), not a single prompt. Every answer runs the same pipeline:

1. **Context from the agents.** The situation digest (time, weather, markets, agenda, today's brief) plus
   `HubSearch` over everything collected: clustered stories with their AI summaries and every outlet, articles
   and posts from the full-text index (30 days), the social pulse, prediction markets, your agenda and quotes.
   Only items that name what the question names (NIRSpec, SpaceX, "Dublin Bus") are kept, so a loosely related
   article never gets a number to be cited for the wrong claim; each item's date is spelled out ("Mon 28 Sep 2026
   (20h ago)"), so answers don't call yesterday "today". Each item gets a citation number from a per-answer
   `SourceBook`, which also keeps what the source said. *Tell me more* on a story passes the story
   itself: its summary, key points, every outlet's headline, snippet, time and link, and the text of the newest
   readable articles (`WebReader` + `ArticleExtractor`; paywalls and Google News redirect links are skipped).
2. **Planning** (`AskPlanner`). One fast JSON call (no reasoning, schema-constrained) decides what the
   question needs, with the last few messages of the chat, the allowed folders, your memories and skills in
   view: an intent (chat, feeds, web, page, site, files, screen, act), 1–3 keyword search queries (never the
   question pasted in), links to open, a site to search within (Twitter → x.com), the words a file's name
   probably contains, a file kind, folders you named, what a picture shows ("a blue flame"), whether it's
   recent, the answer format and a fitting skill. Rules make the same plan without the model (and fill gaps
   in its plan): links you typed are always opened and only links from the chat are (an address the model makes
   up is never fetched), a "query" that is just the question is replaced by its subject, unknown kinds, folders
   and skills are dropped. With files attached, a message about them ("summarise the attached notes") searches
   nothing on the PC unless it asks for other files. Questions about your own records ("when is my boiler
   service due?") search your files when Use my PC is on. The plan is shown as the first activity step.
3. **Gathering.** Measured on `qwen3.5:9b`: with answer-style rules in the prompt the model called
   `search_files` in 0 of 3 tries for "when is my boiler service due?", and never searched the web for facts it
   thought it knew. So Ask gathers what the plan says before the model answers: a taught skill's fixed steps;
   the screen; files — the Windows Search index plus a breadth-first walk of the allowed folders (your own
   folders first, system, program, app-data, cache and game folders never walked, OneDrive placeholders never
   downloaded), every name word counting and files matching more words ranking higher, and, when you described
   a picture, the likeliest images looked at by the vision model (several per call) so "my logo photo of a
   blue flame" finds `my_logo5.png`; links you gave, and to search a site its most relevant links followed;
   web searches with the best pages read (script-only sites such as X are linked, not read). Switches that are
   off are named in the context so the answer can tell you which one to turn on.
4. **The tool loop** (up to 6 rounds) streams text, reasoning and tool calls from `LlmClient.ChatStreamAsync`
   (Ollama native tools, images and `think`; OpenAI-compatible servers too). The system prompt says what Aqua
   can do right now (so it never claims it can't open a link it can open) and that earlier answers may be wrong.
   The GPU is reserved for the answer so background agents can't interleave — but released while you're asked
   for an OK, so an unanswered card never stalls them. `ThinkingGuard` stops
   reasoning that runs past its budget or goes in circles ("wait, actually…", the same sentence again and
   again) and `FinishAsync` then writes the answer directly without reasoning — as it does when a turn ends
   empty or runs out of room. `AnswerText` moves narration ("The user is asking… Let me check…") and
   "Thinking Process:" blocks from the answer into the Reasoning panel. The stream ends with token counts,
   which drive the chat's context meter. Before the chat links citations, `SourceBook.Verify` drops any [n]
   whose sentence shares too little with what source *n* said (small models sometimes cite the nearest number);
   sources only seen as search snippets are labelled as such.

| Switch | What it adds |
|---|---|
| (none) | Feeds + `search_hub`, `get_story`, `run_agent` (below). |
| **Web** | `web_search` (DuckDuckGo HTML by default, Google News for recent events, Wikipedia as fallback; SearXNG or Brave with your key), `read_webpage`. A provider's bot check is reported, never bypassed. |
| **Research** | A fixed plan instead of the loop: the model writes 3–4 queries → parallel searches → up to *N* pages (relevance, established outlets, at most 2 per site) → a streamed report (answer, *Key findings*, *Where sources differ*, *What to watch*), every claim cited. |
| **Think** | Reasoning on (`think: true`, sent only to a model with Ollama's `thinking` capability), shown collapsed as *Thought for n s*. |
| **Use my PC** | `search_files` (name words, kind, folder), `read_file` (documents; pictures are shown to vision models), `list_folder`, `take_screenshot`, `system_status`, `read_clipboard`, `open_item`, `launch_app` (Launchpad, any Start-menu app, Windows Settings pages), `media_control`, `run_scene`, `do_not_disturb` (Aqua's own notifications); with *Let Ask operate apps*: `list_windows`, `read_window` (UI Automation: numbered buttons, fields, links, menu items), `focus_window`, `click` (invoke, toggle, select, expand), `type_text` (value or keystrokes, never password fields), `press_keys` (Ctrl/Shift/Alt with letters, digits, F-keys and navigation keys; never the Windows key or Alt+F4), `close_window` (a close request). |
| Attachments | Files (text, code, CSV/JSON, HTML, Word, Excel, PowerPoint, OpenDocument, RTF, PDF, images), pasted images, a screenshot of the screen or a snip. Office files are read from their XML (`Documents`), PDFs are rendered and read with Windows OCR (`Windows.Data.Pdf` + `Windows.Media.Ocr`), and images go to vision models as images (with OCR text for the rest). A **folder** (`AskOptions.Folders`, kept with the open chat, not saved) gives that chat `search_files`, `list_folder` and `read_file` inside it, with Use my PC's rules, even while Use my PC is off; its newest entries go into the context. |

**Safety.** Tool results are fenced as untrusted data. Tools are classed Hub / Web / Private / Act:
*Act* tools (open, launch, media, clipboard) show **Allow / Allow for this chat / Don't allow** in the chat first
(Settings › Ask Aqua › Ask before acting), with the whole text, keys, query or address; screenshots Ask decides
to take need an OK too. Typing that presses Enter and key presses need an OK each time. Once an answer has read
anything that isn't your own words (`AskRun.SawUntrusted`: pages, results, feed items, files, the screen, app
windows, the clipboard, attachments), typing, clicking, key presses and closing windows ask every time, even with
approvals off. Keystrokes only go to the approved window (`DesktopWindows.IsInFront`, checked before every
chunk); `DesktopRules` keeps anything that runs typed commands (terminals, File Explorer and the Run dialog,
editors with a terminal, script hosts), system tools, sign-in prompts, password managers and Aqua itself off
limits, and stops `launch_app` starting shells, installers and admin tools. Once an answer has seen private
data (files, screen, attachments, clipboard), anything that would reach the internet — a search, a page or a
link opened in the browser — needs an OK, so a page can't steer the model into sending your data out; so does
one carrying names from your calendar or from what Aqua remembers. Files are read only
inside the folders you allow (resolved paths, so `..` can't escape) or that you attached, never credential
files or keys, and programs, scripts and shortcuts are never opened. `WebReader` checks every connection
(redirects included) at connect time and refuses loopback, private, link-local, CGNAT and unique-local
addresses. Unanswered approvals are declined after three minutes.

**Aqua's agents** (`AgentJobs`, `RunAgentTool`). A message that asks in so many words — *refresh the news*,
*update my markets and the weather*, *get the latest prices*, *rerun the market analyst*, *write me a fresh brief*,
or *refresh it* after a question about the weather — runs those agents before anything else (collectors side by
side, then the AI ones), so even a model that never calls tools answers from what they brought in. The context says
what each did and, after the news, lists the stories that weren't there before; the planner is told, so it doesn't
search the web for "the news". *Run* and *rewrite* need a name straight after them ("run the numbers on my
portfolio" runs nothing; "rewrite this story" is about your text). For follow-ups the model has `run_agent`, with
a fixed list of jobs — news, social, markets, predictions, agenda, weather, brief, market analysis, pulse,
Foresight — never an agent by id. Each job runs at most once per answer (a repeat returns the same outcome
without a second activity line) through `AgentRuntime.RunAndWaitAsync`, which waits 45 s to 4 minutes depending on
the job; the answer lets go of the model while an AI agent writes, as it does while you're asked for an OK. The
prompt forbids claiming a refresh that no tool result shows. A question about your portfolio gets your holdings
as the Markets page's card values them.

**Answers** render as selectable rich text (`MarkdownView`): select and copy any part, right-click → *Ask about
this* or *Search the web for this*, or use *Copy*, *Read aloud*, *Ask again* (the last answer is rewritten in
place) and *Dig deeper* (the same question as research) under each answer. Citations open the page; a file
named in the answer becomes a chip, and file paths and file links in the text are clickable: documents,
pictures and media open with their app, anything else is shown in Explorer (never run). A link the model writes
out counts as a citation too. "Remember …" / "Forget …" answers offer *Undo* and *Answer it instead*.
Your questions are selectable as well, with *Copy* and *Edit and ask again* (in place: the question and what
followed are replaced; ↑ in an empty box edits the last one).

**Tables and maths.** `AnswerMarkdown` parses an answer into blocks — headings, paragraphs, lists nested to any
depth (bulleted, numbered, task lists), quotes, rules, code, tables and display maths — and their inline spans, and
`MarkdownView` lays them out in a `FlowDocument`. A Markdown table becomes a native WPF table (a shaded header row,
each column's alignment, widths shared by how much each column holds). LaTeX — `$…$`, `\(…\)`, `$$…$$`, `\[…\]` or a
bare `\begin{align}` — is drawn by XAML-Math (`MathView`; inline formulae sit on the text's baseline). XAML-Math
knows a subset of LaTeX, so `MathText.Normalize` first rewrites what models write into it (`\dfrac`, `\mathbb` and
the other font commands, `cases`, `bmatrix` and the other matrices, `aligned`, `\tag`, Unicode such as ×, ≤, π, ²,
½), and `MathText.Drawable` turns away formulae long or nested enough to overflow its recursive parser; whatever it
still can't draw is shown as its source. Prices stay text: as in Pandoc, `$` opens maths only with no space after it
and closes it only with no space before it and no digit after, so "$5 and $10" is two prices. A table or formula
still streaming in shows as text until it's complete. Copying keeps the Markdown: everything selected copies the
answer as written, a table, formula or block selected whole copies its source, and part of a block its text with
formulae as their LaTeX.

**Chats** (`ChatStore`, `AskSession`). Every chat is kept in the local database — an index plus each chat's
messages — with a title from the first question (renamed by the model once it has answered). The history
panel groups chats (Starred, Today, Yesterday, Previous 7 and 30 days, Older), searches titles and messages,
and stars, renames and deletes them. Unstarred chats not used for *Delete chats after* days (30 by default,
or never) are deleted at start-up; *Keep Ask chats* off deletes them all. Each chat is a `ChatThread` with its
own answer in flight, so an answer keeps streaming in a chat you've left. Everything a chat keeps beside its
messages — notes, compression, pictures — is deleted with it.

**Notes** (`ChatNotes`, `UpdateNotesTool`). Each chat has notes — facts, decisions and open questions — that go into
every answer's context ahead of the situation digest (so they're the last thing trimmed), even once the messages
they came from are no longer sent. The model keeps them with `update_notes` (at most six changes an answer, up to 400
characters a note, 60 notes; a note repeating one already there — in other words, but with the same numbers — is
refused). The notes panel beside the chat shows them as plain text under three headings, and you can edit it: an edit
keeps notes Aqua added while you typed, and lines you wrote are marked as yours. Notes Aqua wrote count as untrusted
(they came from what the chat read), so an answer that reads them asks before acting as if it had read a page.
They're separate from Memory: they belong to the chat, and go into its export.

**Compression** (`ChatCompressor`). *Compress* in the context meter's popup — or, with *Compress long chats on
their own* on, a check after each answer, once there are more messages than a question sends or the last answer
filled 80 % of the largest window Ask may use — replaces whole exchanges at the start of the chat (all but the last two) with a summary
the model writes under fixed headings (*What was asked*, *What Aqua found*, *Decisions*, *Open questions*), told to
keep every name, number, date, price, decision and source; a reply that isn't clearly shorter than what it replaces
is refused. A long chat is compressed in steps, each on top of the summary so far. The model then gets the summary
instead of those messages, and the chat says what it saved ("about 9,800 → 1,100 tokens"); the messages stay in the
chat and its history, dimmed, and *Undo* sends them in full again (and stops that chat compressing on its own). A
summary carries a fingerprint of the messages it stands in for, so editing or re-asking one of them drops it. After a
failed automatic attempt it waits two exchanges before trying again.

**Pictures** (`AskRun.Saw`, `ChatMedia`). The screenshots Ask takes and the pictures it reads, or looks at while
searching for one you described, show as thumbnails in the answer — each once, at most eight, the ones that match
first; a click opens a screenshot in a window of its own and a picture on the PC with its app. With Settings ›
Privacy › *Keep pictures with chats* (on by default) they're kept with the chat in
`%LOCALAPPDATA%\AquaHub\chat-media\<chat>`: screenshots and pasted pictures whole, pictures on the PC as a 320-pixel
thumbnail beside their path (the file stays where it is, and opens from there; if it has gone, the thumbnail opens).
Pictures you attach to a question are kept the same way and open from their chip. Files are named from a hash of
their content, so a picture is kept once and a name read back from the database can only reach that folder. The
folder goes with its chat (folders whose chat is gone are tidied away when Ask loads its history), and switching the
setting off deletes every chat's pictures.

**Model.** A picker in the composer chooses the model Ask answers with (Settings › AI's model until you pick
one). Every call of an answer goes to it (`LlmRequest.Model`): planning, reading a long page in parts, looking
at pictures, writing, and the chat's title, so Ollama never swaps models in the middle of an answer; a pick
that's since been uninstalled falls back to the usual model. Think and Research are remembered for each model
(`AskSettings.ModelModes`), and Think is greyed out for a model without a thinking mode (Gemma 3, for one),
which `LlmClient.CanThinkAsync` finds out without waking the model server.

**Context.** Automatic by default: the window grows only when a prompt needs it (a new `num_ctx` reloads the
model), up to 64K, or 128K on a graphics card with 24 GB or more: the model's memory for the conversation grows
with the window, and once it doesn't fit on the card the model spills into system RAM and slows to a crawl.
Past that limit a prompt is trimmed like a fixed window rather than sent whole (Ollama would quietly cut its
start, the instructions). A fixed window (8K–128K) leaves out the oldest messages first, then the end of the
gathered material.
The meter under the Ask box shows the last answer's prompt + reply tokens against the window; its popup changes
the window and how many earlier messages each question sends, and compresses the chat (above).

**Voice.** The microphone button dictates into the Ask box: with Whisper once installed, offline with Windows'
desktop speech recognizer (`System.Speech`) — nothing leaves the PC with either — or, if you choose it, Windows
online speech recognition (§11). Listening stops after a few seconds of silence; *Send when I stop speaking* sends
it straight away.

**Workbench** (`Workbench`, `SkillDrafter`). A skill is a name, when to use it, example requests, instructions
for the model and optional fixed steps — calls to Ask's own tools with `{parameters}` or `{input}` filled in
from the request. The local model drafts skills from a plain description and a catalogue of the tools and their
arguments; `Workbench.Validate` removes unknown tools and arguments before you see the draft, and *Improve*
revises it from your feedback. At answer time the planner (or a close match on the example requests) picks a
skill: its steps run through the normal approval rules and its instructions join the prompt. Memories ("remember
that…" in Ask, or added in the Workbench) go into every answer's prompt and the planner's.

**Tested** with a scripted fake Ollama (planning, tool calls, tool results, thinking, looking at pictures,
reasoning cut short, narration moved out, memories and skills, images for vision and OCR text for non-vision
models, declined actions, a chat's notes and summary in the context, `update_notes`, compressing a chat, the
pictures an answer shows), the formulae models write run through XAML-Math's own parser, and live against `qwen3.5:9b` (`LiveAskTests`, `AQUAHUB_LIVE=1`): story questions, a
web lookup, finding someone's X account and linking it, searching through a site you give it, a research report,
a question about local files, finding a picture by what it shows (and again from a follow-up), an image, and
reasoning.

## 11. Voice input

`Platform/VoiceInput` dictates with Windows' own recognizers, or with Whisper once you install it:

- **Whisper** (`Core/Speech`, optional). *Install* in Settings › Ask Aqua › Voice runs `WhisperSetup`: whisper.cpp's
  Windows CPU build (x64 or Arm64) from its GitHub release b5130 (= v1.9.4) and a quantized model from the whisper.cpp
  project's Hugging Face repository at a fixed commit — Base (57 MB), Small (181 MB, the default) or Large v3 Turbo
  (547 MB), the English-only file for English speakers. Each download streams to a `.part` file, must equal its
  pinned size (it's cut off the moment it goes over) and SHA-256 (the digests GitHub and Hugging Face publish), and
  gives up after 60 s without data; only `whisper-cli.exe` and its libraries come out of the archive; `whisper.json`
  records what's installed, and can only name `whisper-cli.exe` inside the folder. Switching model keeps the
  program and replaces the model; an install carries on if you leave the page, and can be cancelled. While you
  dictate, `MicCapture` records the chosen (or default) microphone into memory, and its level tells when you've
  stopped speaking — Whisper writes nothing until the end. On stop, `SpeechAudio.SpeechPart` cuts the speech out of
  the quiet around it (room noise, clicks and silence never reach Whisper, which invents "Thank you." for them) and
  turns quiet recordings up; `WhisperRunner` starts `whisper-cli -f - -of … -nt -np -sns` and writes a WAV to its
  standard input, so the audio is never on disk (an output name makes it print to standard output, and with no
  output format it writes no files); `WhisperCli.Clean` drops non-speech tags ("[BLANK_AUDIO]", "(music)") and
  subtitle credits. Live (`LiveWhisperTests`, `AQUAHUB_WHISPER`, Windows' own voice as the speaker — never the
  microphone): Small (English) wrote the Dublin weather question back word for word, and the owner's test phrase —
  heard by Windows' recognizer as "The Doc One Two Tree Number tree" — as "testing 1 2 3 number 3".
- **On this PC** (`System.Speech`, in-process). The recognizer can only open the default microphone itself, so
  for a chosen one `MicCapture` records it with the wave-in API (16 kHz, 16-bit mono; Windows converts) into a
  `PcmPipe` that the recognizer reads as a stream. A read waits until it can be filled (a short read ends the
  audio), and stopping ends the pipe rather than calling `RecognizeAsyncStop`, which blocks until a stream
  input ends — verified with synthetic speech (Windows' own voice, never the microphone) through the same pipe.
  Phrases are kept whole (longer end-silence), unsure ones are kept rather than dropped, and each keeps the
  recognizer's other guesses.
- **Windows online speech** (WinRT). It only dictates in the languages Windows lists, so `SpeechLanguage.Pick`
  chooses the nearest (English (Ireland) → English (UK)); it always uses the default microphone.

For Windows' recognizers, `DictationTidy` then has the local model correct what was misheard, with the phrases
and their guesses side by side (as footnotes, the model kept the first guess). Its instructions give no example
mishearings (shown "whether" → "weather", a model "fixes" every "whether") and say a sentence that reads naturally
stays exactly as it is. A reply that isn't a correction — an answer, much longer or shorter, or unlike what was
heard in words and letters — is thrown away. Live (`qwen3.5:9b`): sentences with sound-alike words come back word
for word, "whats the whether…" becomes "What's the weather…", and a phrase too garbled to rebuild without guessing
is left as heard. Whisper's words go in as written. While you dictate with Windows' recognizers, a line under the
Ask box shows the level and says they often mishear, with a link to set up Whisper (Settings can turn it off).
Settings › Ask Aqua has a live test (level, heard, after tidy-up) and Windows' microphone setup and voice training.

## 12. This PC: health check and toolkit

`HealthInspector` reads what Windows already knows, all read-only and without administrator rights: drives
(`DriveInfo`), drive health (`MSFT_PhysicalDisk`), Windows Update's own history (the update agent's COM API;
nothing is searched for or downloaded) and pending restart, blue screens and unexpected shutdowns (System log)
and app crashes (Application log), the antivirus Windows Security reports (`SecurityCenter2`), devices with
errors (`Win32_PnPEntity`), battery wear, startup apps (Run keys and Startup folders, minus the ones switched
off), temporary files and the Recycle Bin. WMI is called late-bound through Windows' scripting object (no
`System.Management`); its queries are synchronous, because the forward-only flags make every property read fail.
Each check has a time limit, and one that can't run is listed as *not checked* instead of guessed.
`PcHealth.Evaluate` (Core, unit-tested) turns the facts into findings with a level, what to do and the Windows
tool that helps; `HealthService` keeps the last report and asks the local model for a short summary. Ask's
*check_pc_health* tool reuses a report under ten minutes old.

`ProcessActions` describes a selected app from its program file (`QueryFullProcessImageName` + version info)
and ends it only after you confirm: windowed apps are asked to close (their helpers close with them), background
ones end at once, *Force end* is offered only if a polite close didn't work, and processes Windows needs (and
Aqua and its model server) are refused. `MaintenanceTools` is a fixed list of Windows' own tools; nothing
passed in is ever run.


## 13. Setup and updates

Releases are built by `.github/workflows/release.yml` when a `v*` tag is pushed: it tests, publishes a
framework-dependent ReadyToRun build and packages it with Velopack (`vpk pack`, pack id `AquaHub.App`) into
`AquaHub.App-win-Setup.exe`, a portable zip and full/delta packages, then publishes the GitHub release. The build
records the repository it came from as assembly metadata (`UpdateRepository`); source builds leave it out.

`Program.Main` runs `VelopackApp` before WPF starts: it handles Setup's install/update/uninstall hooks (uninstalling
removes the Start with Windows entries that start this copy). Velopack's own apply-at-start is off: an update that was
downloaded but not installed is applied by `App.OnStartup` in the first instance only, after the single-instance
check, so a second launch (a jump-list task, a shortcut) just forwards its command and never closes a running Aqua
Hub. Setup installs per user into `%LOCALAPPDATA%\AquaHub.App`, beside, never inside, the profile in
`%LOCALAPPDATA%\AquaHub`.

`UpdateService` (app) and `UpdatePolicy` (Core, unit-tested) split the work. An installed copy checks the releases
a few minutes after start and then about every 20 hours (2 hours after a failed check) while *Check for updates
automatically* is on; a new version raises an alert (once per version, and again after ten days or more if it's
still not installed), which opens Settings › About. Nothing downloads
until *Download* there; then *Restart now* hands over to Velopack's updater, which waits for Aqua to exit, swaps the
versions and starts it again with the same profile. If you don't restart, the update installs at the next start.
In an E2E session *Check now* is journalled instead of performed.

A new version can't be missed: a banner across the main window (`UpdatePolicy.BannerFor`: *Download* with a
progress bar, *Cancel*, *Restart now*, *What's new*) and an accent dot on Settings in the sidebar. *Not now* puts
the banner away until the next version or step (`UpdatePolicy.BannerKey`: on offer, then ready to install); the dot
stays. After an update, the alerts about that version are removed from the bell. The diagnostics summary says how
this copy was installed and where the updater stands. `--demo-update [ready]` swaps GitHub for a pretend release
(Debug builds and E2E dry runs only) that "downloads" in a few seconds and installs nothing, for trying all of
this; the E2E suite's `A18_UpdateTests` uses it.
