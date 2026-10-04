# Phase 4 handoff: Aqua's own model runtime (backend)

For Astra: frontend, review and tests. Written 4 Oct 2026 by Claude (design and backend). Nothing here is committed; the
owner commits.

## 1. What this phase adds

With Settings' provider set to `llamacpp`, Aqua runs models itself with llama.cpp's `llama-server` instead of going
through Ollama. Ask and the agents work as before, with these differences:

- About 15% faster on the same card (qwen3.5:9b at 75.7 tokens/s against Ollama's ~66 on an RTX 4070 SUPER).
- Models that Ollama can't run well, such as Qwen3.8 27B with MTP heads at IQ2_S, which runs at 36.6 tokens/s with 32K of context on 12 GB.
- Control over context, cache type and MTP.
- A model library that reads the models Ollama has already downloaded in place, so there is no second copy.

The backend is complete and was live-checked through the app (section 8). The screens are yours: an **AI & models**
settings section and a **Models** tab. Section 5 is the design spec. Everything they need is on `Hub.LocalAi`.

## 2. Code map

| Where | What |
|---|---|
| `Core/Ai/Local/Gguf.cs` | Bounded GGUF header parser → `GgufInfo` (layers, KV-cache shape incl. hybrid and sliding-window, MTP heads, chat template, thinking/tools/effort, vision, embedding). |
| `Core/Ai/Local/OllamaStore.cs` | Reads Ollama's store (manifests → `blobs/sha256-…`), read-only; validates digests, sizes and GGUF magic. |
| `Core/Ai/Local/ModelLibrary.cs` | `LocalModel` list over Aqua's `models\` folder and Ollama's store, deduped by SHA-256; `Find(name)` accepts ids, Ollama names, aliases, `:latest`, file names. |
| `Core/Ai/Local/ModelDownloads.cs` | Hugging Face search, file lists at a commit, pinned suggestions, verified resumable downloads. |
| `Core/Net/VerifiedDownload.cs` | HTTPS-only download with size cap, SHA-256 check, `.part` + Range resume. |
| `Core/Ai/Local/HardwareFit.cs` | `GpuFacts`, `FitVerdict`, `FitEstimate`, context choice for the card. |
| `Core/Ai/Local/LlamaCatalog.cs`, `LlamaSetup.cs` | Pinned llama.cpp b11387 builds (CUDA 13.4 / 12.4, Vulkan, CPU x64/arm64) and install/remove. |
| `Core/Ai/Local/LlamaArgs.cs` | Server arguments per model and lane; `SpeedBook` (MTP auto). |
| `Core/Ai/Local/LlamaRuntime.cs` | One server per model, GPU and CPU lanes, health wait, failure reasons, unload. |
| `Core/Ai/LlmClient.cs` | `llamacpp` provider: routing, request body, timings, restart-once. |
| `Core/Settings/HubSettings.cs`, `SettingsStore.cs` | `Ai.Llama` settings and their validation. |
| `App/Services/LocalAi.cs` | **The screens' API** (`Hub.LocalAi`). |
| `App/Services/LlamaProcessHost.cs` | Starts servers hidden, in a kill-on-close job, with a clean environment. |
| `App/Platform/GpuReader.cs`, `Native.cs`, `SystemMonitor.cs` | Card facts (DXGI + NVML), job object, adapter vendor. |

## 3. `Hub.LocalAi`: the screens' API

`Changed`, `InstallProgressChanged` and `DownloadProgressChanged` are raised **on the UI thread**. The async actions
continue on the thread pool, so marshal back with `Hub.OnUi` or await from UI code as usual. Errors are thrown as
`LlamaSetupException`, `DownloadException` or `LlamaRuntimeException`. Their `Message` is plain English written to show
the user as it is.

**State**

| Member | Meaning |
|---|---|
| `InUse` | Ask runs on the runtime (provider is `llamacpp`). |
| `RuntimeReady` | A server exists: Aqua's install or a custom `ServerPath`. |
| `Installed` | `LlamaInstall(Server, Build, Backend)` or null. |
| `Build` | `"b11387"`, `"custom"` or `""`. |
| `Installing`, `InstallProgress` | Install running; `LlamaProgress(Step, Done, Total)`. |
| `Gpu` | `GpuFacts(Name, Vendor, TotalBytes, FreeBytes, CudaDriver, ComputeCapability, Driver)`; `Label`, `Present`, `Nvidia`. |
| `RecommendedBuild`, `Builds` | `LlamaBuild(Backend, Label, Archive, Runtime)`; `Size` is the download size. |
| `ModelsFolder`, `Models`, `Problems` | The library and the files it couldn't read (plain-English lines). |
| `Recommended`, `Have(r)` | Suggestions for this card (`ModelRecommendation(Role, Name, Why, Repo, Revision, File, Projector, …)`, `Bytes`). |
| `Downloading`, `DownloadProgress` | `ModelDownloadProgress(Step, Done, Total)`. |
| `Running` | `LlamaInstanceInfo(ModelId, ModelName, Lane, State, ContextTokens, MtpDraft, GpuLayers, TotalLayers, Error, Started)`; `PartlyOnCpu`. |
| `Speed` | `SpeedBook.Measured(id)`: tokens/s with and without MTP. |
| `Fit(model, context = 0)` | `FitEstimate(Verdict, WeightsBytes, CacheBytes, OverheadBytes, AvailableBytes, ContextTokens)`; `TotalBytes`. |
| `ForRole(role)` | The model for `"main"`, `"creative"`, `"background"` or `"search"`, or null. |

**Actions**

| Member | Notes |
|---|---|
| `InstallRuntimeAsync(build?)` | Default is the recommended build. Returns null in an E2E dry run. Cancel with `CancelInstall()`. |
| `RemoveRuntimeAsync()` | Stops the servers first; models are kept. |
| `DevicesAsync()` | Runs `llama-server --list-devices`. Returns lines like `CUDA0: NVIDIA GeForce RTX 4070 SUPER (12281 MiB, 11069 MiB free)`; an empty list means processor only. |
| `UseRuntimeAsync(on)` | Switches the provider; leaving stops the card's model. |
| `SetRole(role, model?)` | Main writes `Ai.Model`, so it is Ask's model. Ollama models are stored by their Ollama name, so the setting survives a switch back. Null means automatic for main and none for the others. |
| `SearchAsync(query)` | Public GGUF repositories, most downloaded first. Max 30; a query over 100 characters returns nothing. |
| `FilesAsync(repo)` | `HfRepoFiles(Repo, Revision, Gated, License, Models, Projectors, ContextLength, Architecture, Parameters)`; split files are left out. |
| `DownloadAsync(recommendation)`, `DownloadAsync(repoFiles, file, projector?)` | Return the new `LocalModel`, or null in an E2E dry run. One at a time. |
| `CancelDownload()`, `DiscardUnfinished()` | A cancelled download resumes next time; discard deletes `.part` files. |
| `RemoveModelAsync(model)` | Aqua's downloads only (`Source == Aqua`). Clears roles that pointed at the model. |
| `StopAsync(modelId?)` | Stops a running server; null stops all of them. |
| `RefreshAsync()` | Re-reads the folders. It also runs every 2 minutes and when the folder setting changes. |

**`LocalModel`**

| Group | Members |
|---|---|
| Identity | `Id` (`hf:owner/repo/file.gguf` or `ollama:name:tag`), `Name`, `Source` (Aqua / Ollama), `Aliases` |
| Files | `Path`, `Size`, `Bytes`, `ProjectorPath`, `Sha256`, `Repo` |
| What it can do | `Quant`, `Vision`, `Thinking`, `Tools`, `Embedding`, `Mtp` |
| Header and defaults | `Info` (`GgufInfo`), `Sampling` (Ollama's defaults) |

**Journal entries in E2E dry runs** (`e2e-journal.log`):

- Install: `llama-install <backend>`
- Removals: `llama-remove`, `model-remove <id>`, `model-discard-unfinished`
- Download: `model-download <id>`
- Server start: `llama start · <model id>`. Servers really start in E2E. Only installs, downloads and removals are intercepted.

## 4. Settings (`settings.json` → `ai`)

`provider`: `ollama` | `llamacpp` | `openai` (anything else becomes `ollama`). `model` is Ask's model for every provider.

| `ai.llama.` | Default | Values, validation |
|---|---|---|
| `backend` | `auto` | auto, cuda12, cuda13, vulkan, cpu |
| `serverPath` | `""` | Local drive path ending in `llama-server.exe`, else cleared |
| `modelsFolder` | `""` (profile `models\`) | Local drive path, else cleared |
| `useOllamaModels` | `true` | Read Ollama's store in place |
| `creativeModel`, `backgroundModel`, `searchModel` | `""` | Model id or Ollama name |
| `contextTokens` | `0` (auto for the card) | Clamped |
| `cacheType` | `q8_0` | f16, q8_0, q4_0 |
| `mtp` | `auto` | auto (measured), on, off |
| `idleUnloadMinutes` | `10` | Clamped; the server sleeps and frees the card |
| `cpuThreads` | `0` (half the cores) | Clamped |
| `reasoningEffort` | `medium` | low, medium, high (for models whose template takes it) |

Ollama's management (start/stop, *Stop Ollama for games*) applies only while the provider is `ollama`. While the runtime is
in use, `stopOllamaForGpu` stops the card's model when a game needs the card.

## 5. Screen design

The wording below is written to ship. Automation ids are proposals; keep them stable once chosen.

### Settings › AI & models

1. **Provider** (`settings-ai-provider`). Choices:
   - *Ollama*
   - *Aqua's own runtime (faster, more control)*
   - *OpenAI-compatible server*

   Picking the runtime when `RuntimeReady` is false shows the install card (step 2) instead of switching.
2. **Runtime card** (`settings-ai-runtime`). The text depends on the state:
   - **Not installed:** "Aqua's runtime isn't installed. **Install** downloads llama.cpp {Build} for {RecommendedBuild.Label}, {Size as GB}, from llama.cpp's GitHub releases. Each file is checked before it's used."
     - Button: `settings-ai-runtime-install`.
     - A *Build* drop-down shows `Builds`, defaulting to the recommended one. It's advanced, so tuck it behind *Other builds*.
   - **Installing:** a progress bar showing `InstallProgress.Step` and Done/Total, with a *Cancel* button.
     - The steps are "Downloading llama.cpp", "Downloading NVIDIA's CUDA libraries" and "Unpacking".
   - **Installed:** "llama.cpp {Build} · {Backend label} · {Gpu.Label}".
     - *Check devices* runs `DevicesAsync`. Show its lines; when the list is empty, say "It will run on the processor only. Is the graphics driver up to date?"
     - *Remove* asks first ("Remove Aqua's runtime? Your models are kept.").
   - **Custom server:** when `Build == "custom"`, show "Using your own llama-server: {path}".
3. **Advanced** (collapsed):
   - Context: *Automatic for your card* or a number.
   - Cache type, with each option's meaning:
     - `q8_0` is "Recommended: half the memory, near-identical answers".
     - `f16` is "Exact, twice the memory".
     - `q4_0` is "Smallest, slightly worse at long contexts".
   - MTP: *Automatic (measured)*, *On* or *Off*.
   - Unload after N idle minutes.
   - Processor threads.
   - Reasoning effort.
   - Models folder.
   - *Use the models Ollama has downloaded* (switch).
   - Server path (custom).

### Models tab (`models-page`)

- **Header line:** "{Gpu.Label} · {free GB} free now". With no card: "No graphics card Aqua can use: models run on the processor, slowly."
- **Roles row:** four slots, `models-role-main`, `-creative`, `-background` and `-search`. Each holds a picker of the library's
  models, with "Automatic" for main and "None" for the others. The hints:
  - Main is "Ask Aqua and briefs".
  - Background is "Agents, on the processor so they never wait for the card".
  - Creative is "Writing and roleplay (used from Phase 5)".
  - Search is "Search by meaning (Phase 8)".
- **Library list** (`models-list`). One row per model:
  - Name, `Quant`, size, source badge (*Aqua* / *Ollama*).
  - Capability chips: *Vision*, *Thinking*, *Tools*, *MTP*, *Embedding*.
  - Fit badge from `Fit(model)`:
    - *Fits*: "Fits · {ctx/1024}K context"
    - *Tight*: "Fits, little room left for games"
    - *PartlyOnCpu*: "Partly on the processor (slower)"
    - *CpuOnly*: "Processor only"
    - *TooBig*: "Too big for this PC"
  - Running state from `Running`: "On the graphics card · {ctx}K", "On the processor", "Starting…" or "Failed: {Error}". When `PartlyOnCpu` is true, add "· {GpuLayers}/{TotalLayers} layers on the card".
  - Measured speed from `Speed.Measured(id)`, when there is one.
  - Actions: *Use for Ask* (SetRole main), *Stop* (when running), *Remove* (Aqua's own only). Remove confirms with "Delete {name} ({size})? It can be downloaded again."
  - Ollama's models say "Managed by Ollama" instead of Remove.
- **Problems:** when `Problems` is non-empty, show a collapsed "{n} files couldn't be read" with the lines.
- **Suggested for your card** (`models-suggested`). One card per `Recommended` entry:
  - Name, Why, size `Bytes`.
  - *Download*, or *Downloaded* when `Have(r)` is true.
  - Main suggestions are already filtered to the card size; show at most one.
- **Find more** (`models-search`):
  - A search box searches on Enter and lists repos (downloads, likes, updated).
  - Opening a repo calls `FilesAsync` and lists `Models` with size, `Quant` and the `Fit` of a would-be model.
  - Disable everything when `Gated` is true, with "This model needs a Hugging Face account, which Aqua doesn't use."
  - Projectors are offered as "Also download vision (+{size})" when the repo has any. Pick the smallest F16/BF16 one.
- **Download bar** (`models-download`): shows Step, Done/Total and speed. *Cancel* says "Stopped. It carries on where it left off next time." Add *Discard unfinished downloads* under "…".
- **Errors:** show the exception message in an inline error line on the card that started the action. Don't use a dialog.

## 6. Behaviour worth testing

- **Settings validation**
  - Provider whitelist.
  - `serverPath` accepts only local drive paths ending in `llama-server.exe`; UNC, relative and other names are cleared.
  - `modelsFolder` must be a local drive path.
  - Clamping of the numbers.
- **Gguf:** hostile headers must throw `GgufFormatException` and never over-allocate:
  - huge string lengths and array counts;
  - more than 1M tensors;
  - n_dims 0 or 9;
  - truncated files.
  - Also: the KV shape of hybrid (qwen35) and sliding-window (gemma4 array pattern) models, MTP detection, and `QuantFromName`.
- **OllamaStore:**
  - A bad digest or a size mismatch is skipped.
  - Adapters are skipped.
  - Two names on one blob make one model with aliases.
  - The projector and params layers are read.
- **ModelLibrary:**
  - `Find` by id, Ollama name, `:latest`, alias and file name.
  - Duplicates by SHA-256.
  - `Remove` refuses Ollama models.
  - The header cache is invalidated by size or mtime.
- **VerifiedDownload:**
  - Refuses non-HTTPS.
  - Stops beyond the size.
  - A SHA mismatch deletes the file.
  - Resume uses Range after re-hashing the `.part`.
  - Cancel keeps the `.part`.
  - Stall detection.
- **ModelDownloads:**
  - `SafePath` (`..`, backslashes, absolute paths, invalid characters).
  - Repo and revision patterns.
  - Split files are refused.
  - The free-space check.
  - Gated and 401/403/404 messages.
  - `RecommendedFor` at 8, 12, 16 and 24 GB.
- **HardwareFit:**
  - Verdict boundaries.
  - `Context` limits (8K min, 64K, 128K on 24 GB).
  - Window-aware cache bytes.
- **LlamaCatalog.Pick**

  | Situation | Build |
  |---|---|
  | CUDA driver 13.4+ | cuda13 |
  | 12.4–13.3 | cuda12 |
  | Older NVIDIA or non-NVIDIA | vulkan |
  | No card | cpu |
  | Arm | cpu arm |

  An explicit preference wins.
- **LlamaArgs:**
  - The key is never in `Arguments`, only `Environment["LLAMA_API_KEY"]`.
  - `--host 127.0.0.1`, `--no-webui`, `--offline`.
  - CPU lane: `--device none -ngl 0 -t`.
  - Embedding: `--embeddings`.
  - MTP flags only when the model has MTP.
  - Ollama sampling defaults are passed.
- **SpeedBook:**
  - Tries with MTP and then without, needing 3 samples each.
  - Keeps MTP only at ≥5% faster.
  - Re-tests after 30 days or a new build.
  - Ignores answers under 64 tokens.
- **LlamaRuntime** with a fake `ILlamaProcessHost`:
  - One GPU instance at a time.
  - A ready instance is reused.
  - Health timeout leads to Failed with `Reason(line)` text.
  - A server that died is restarted once by `LlmClient`.
  - `UnloadGpuAsync` stops only the GPU lane.
  - `Dispose` kills all instances.
- **LlmClient (`llamacpp`)** against a fake OpenAI-style server:
  - The bearer key is sent.
  - The body has `chat_template_kwargs.enable_thinking`, `reasoning_effort` and `stream_options.include_usage`.
  - JSON schema as `response_format`.
  - `timings` are parsed into `LlmResult.Timings`.
  - Routing:
    - A named model goes to the GPU.
    - Background goes to the background model on the CPU if one is set.
    - Otherwise it reuses the model already on the card.
    - Otherwise it goes to main.
  - The health text with no runtime, and with no model.
- **LocalAi in E2E:**
  - Install, download, remove and discard only journal (section 3).
  - `SetRole` writes the right setting.
  - `UseRuntimeAsync(false)` stops servers.
  - Switching provider re-checks health.
- **Process host:**
  - The server ends when Aqua is killed (job object).
  - No `LLAMA_*` or `GGML_*` variables from the parent reach it.
  - Ollama's layout gets `cuda_v12` on PATH.

## 7. Security notes (also in SECURITY.md)

- **Runtime downloads:** pinned by size and SHA-256 to one GitHub release. Only `llama-server.exe` and DLLs are unpacked, with a 4 GB unpack cap.
- **Model downloads:** only `huggingface.co`, over HTTPS, at a 40-hex commit, each file checked against its listed SHA-256. No account and no tokens. Gated repos are refused.
- **Servers:**
  - Loopback only, a free port, and a random 32-byte key per start, sent in the environment (never the command line).
  - Only `/health` is open.
  - `--no-webui --offline --no-slots`, CORS limited to localhost, and logs at a level with no prompt text.
  - A clean environment, and a kill-on-close job.
- **Read-only access:** Ollama's store is never written. NVML is read-only.
- **No extra process rights:** no new rights to end processes beyond Aqua's own servers.

## 8. Live check (4 Oct)

- **Setup:** a scratch build and profile. Provider `llamacpp`; the server was Ollama's bundled `llama-server.exe` set as `serverPath`; the model was `qwen3.5:9b` from Ollama's store.
- **Result:**
  - The server started on the card with 64K context and MTP. It was ready in 8 s, and the first answer took 13 s including the load.
  - Notes from what you say worked ("Target property: flat in Leeds", "Budget: 180k").
  - No server was left after quitting.
- **Caveat:** the PC had a full-screen app using the card during the run, so the measured speed (36.7 tokens/s with MTP) is low.
- **Earlier probes, same card:**
  - qwen3.5:9b: 75.7 tokens/s; MTP with a 3-token draft was slower (60–69), hence the measured "auto".
  - Qwen3.8 27B IQ2_S: 36.6 tokens/s at 32K with 64/65 layers on the card.

## 9. Known gaps and next backend steps

- **Owner's OK needed:** the official pinned build hasn't run on this PC yet. Downloading it means 264 + 391 MB for CUDA 12.4, or 33 MB for Vulkan.
- **Files Aqua can't use yet:**
  - Models split across several files aren't downloaded.
  - Ollama's merged-vision blobs (projector inside the model file) are text-only on upstream llama.cpp, so Vision means a separate projector file.
- **Roles:** creative is stored but not routed until the Workbench (Phase 5). Search is for Phase 8.
- **Router mode:** llama.cpp's router mode isn't used, because Ollama's build has it compiled out. Switching the card's model costs a load (about 8 s for a 9B).
- **Still to build:**
  - A per-answer timing breakdown (plan / gather / load / prompt / write) on Ask's result, with `LlmResult.Timings` already in place.
  - A warm-up call to load the model while the user types.
  - Prompt-prefix stability, so llama.cpp's prompt cache hits more often.
  - Ending runaway reasoning gracefully (`/v1/chat/completions/control`).
- **Hugging Face search** lists gated repos too (the search API doesn't flag them). `FilesAsync` reports `Gated`, so the UI can block them there.

## 10. Other changes in this period

- **`LlmClient.IsOllama`** now means the provider is exactly `ollama`; it used to mean "not `openai`". Check any Ollama-only UI against the provider.
- **Notes from what you say (Phase 3 fix, live-verified):** the notes request starts with a `said` field, which stops qwen3.5:9b answering an empty list under a JSON schema. `ChatNotes` merges a note already contained in an existing one.
- **Ask:** pictures open only from saved local paths. One rule (`AnswerText.OpensWithItsApp`) decides which files open with their app.
