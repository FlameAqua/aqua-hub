# Prompt for the next agent: Aqua Hub design and backend

You're taking over as **designer and backend engineer** for Aqua Hub. The previous agent was Claude; this was handed over on 5 Nov 2026. Read this prompt first, then the files it names, before you change anything.

## The project

- **What it is:** Aqua Hub is a Windows 11 desktop hub written in WPF on .NET 10.
- **Where it lives:** `C:\Users\Adrian\Code\Projects\aqua-hub`, published at `github.com/FlameAqua/aqua-hub`.
- **What it does:** news, social, markets, a daily brief, Ask Aqua (a local-AI assistant with tools, notes, maths and tables), This PC health tools, Launchpad, scenes, and a llama.cpp model runtime of its own.
- **The latest release:** 1.3.0, commit `b021e9b`.
- **The owner:** Adrian. They decide direction, pass the reports between agents, and do every commit and release.

## How the work is split

| Who | What they do |
|---|---|
| You | Design each feature: how it works, its data, and its screens down to their wording, states and automation ids. Build everything behind the screens: Core, agents, the model runtime and the Windows side. |
| Astra (ChatGPT/Codex) | Builds the screens from your design, reviews your work, and owns all tests (unit and E2E). |
| Adrian | Decides, tries each phase, and commits and releases. |

The cycle runs like this:

1. You design and build the backend.
2. You write a report for Astra in `docs/handoff/phase-N.md`. Cover what was built and why, the API and data shapes, threading and failure behaviour, settings, the screen spec, security notes, what to test, and known gaps.
3. Astra builds the screens, reviews the work, tests it, and replies with fixes.
4. You address the fixes, and Adrian releases.

Log every review outcome in `docs/SCORING.md`.

When Adrian asks for a quick feature outside a phase, you may build its UI too.

## The roadmap

The roadmap is the private artifact **Aqua Hub Roadmap**: https://claude.ai/artifact/5r5VN4eaEQ2mLWsBdZApwp, slug `273ec40d-38bc-4487-be5c-7a3083a92c6f`. It was last updated on 4 Oct and is now **out of date**. Bring it up to date when you start, and keep it current.

Its phases and their status:

| Phase | Contents | Status |
|---|---|---|
| 1 | Ship it | Shipped (1.0–1.1) |
| 2 | Fixes and quick wins | Shipped (1.2.0) |
| 3 | Ask, upgraded: tables and maths, chat notes, compression, images in chats | Shipped in 1.3.0 |
| 4 | Aqua's own AI | Mostly shipped in 1.3.0; see below |
| 5 | Social, made yours | Next |
| 6 | Your own agents | Later |
| 7 | Everyday wins | Later |
| 8 | Find anything | Later |
| 9 | Voice and creativity | Later |

The later phases in detail:

- **Phase 5, Social, made yours:**
  - 5.1 Like, Love, Less of this and Never show reactions, with tag-based interests that decay.
  - 5.2 A For You card, with "Why this?".
  - 5.3 Live streams from Twitch (API with Adrian's own app; still a decision for him), YouTube and Kick.
  - 5.4 X through Grok, using the xAI key Adrian provides.
- **Phase 6, Your own agents:**
  - 6.1 An agent builder with an Agent Engineer. Agents are recipes made of Aqua's own parts, never code.
  - 6.2 Inside each agent.
  - 6.3 A deeper health check, with an optional read-only admin helper.
- **Phase 7, Everyday wins:** the deck, reminders and scheduled tasks, calendar days, small daily things, and Trading 212 (CSV first).
- **Phase 8, Find anything:** a file index, search by meaning (EmbeddingGemma on the CPU), and a Search page.
- **Phase 9, Voice and creativity:**
  - 9.1 Read-aloud voices with audio.cpp.
  - 9.2 Image Gen as its own Ask tab. This is **already built early, on ComfyUI**.
  - 9.3 Roleplay with personalities made in the Workbench. The Roleplay tab exists; the personality library doesn't yet.
  - 9.4 Uncensored model choices.

The decisions still open for Adrian are listed in the roadmap: a licence, the Twitch route, the admin health check, Trading 212, and the order of the phases.

**How reality differs from the 4 Oct roadmap.** Correct these in the artifact:

- **One server per model.** The runtime starts one llama-server per model. Router mode is compiled out of Ollama's bundled build.
- **Ollama's engine for Ollama's conversions.** Models Ollama converted itself can't be read by upstream llama.cpp, so they run on Ollama's bundled `llama-server`. qwen3.5:9b, for example, has three `rope.dimension_sections` where llama.cpp requires four.
- **The roles changed.** Astra changed them on 5 Oct:
  - All generation runs on the GPU; there is no background model on the CPU.
  - *Editor* (setting `ai.llama.backgroundModel`) runs the news, social and foresight editors and falls back to Main.
  - *Creative* is used by Roleplay.
  - *Search* is reserved for Phase 8.
- **The 12 GB suggestion changed.** It is now Qwen3.5 9B Q5_K_M, not the 27B IQ2_S.
- **Image Gen runs on ComfyUI.** It uses FLUX.2 Klein 4B through a local ComfyUI, not stable-diffusion.cpp with Z-Image.

## Phase 4: the state you inherit

Read these, in order:

1. `docs/handoff/phase-4.md` is Claude's backend handoff.
2. `docs/handoff/phase-4-frontend-review.md` is Astra's review. It scores the backend 7.5/10 and lists 7 recommendations.
3. `docs/handoff/phase-4-studio-setup.md` covers Astra's GPU queue, editorial routing, Image Gen, Roleplay and ComfyUI.
4. `docs/ARCHITECTURE.md` §8 and `docs/SECURITY.md`.

On 6 Oct Claude answered Astra's review. That work is in `b021e9b` but **has no report and no doc entries yet**. Writing them is your first job (see the steps below). It covered six areas:

- **Engine choice and the compatibility record** (new file `Core/Ai/Local/LlamaCompat.cs`, plus `LlamaRuntime`):
  - `ServerFor(model)` picks the engine. A custom server path wins. Otherwise Aqua's pinned build runs the model, unless it's an Ollama model that build can't read, or Aqua's build isn't installed; then Ollama's engine runs it.
  - `OllamaManager.EngineServer()` finds Ollama's engine at `<ollama>\lib\ollama\llama-server.exe`. The setting `ai.llama.useOllamaEngine` (default on) switches this.
  - A known rule for upstream llama.cpp: `rope.dimension_sections` must have exactly 4 entries. Such models never start on Aqua's build at all.
  - If a start on Aqua's build fails with a format error and the model came from Ollama, the runtime retries once on Ollama's engine.
  - Failures are classed as Format, Memory or Other.
    - A format failure is permanent until the file or the server changes.
    - Other failures make background requests back off for 1, 5, then 15 minutes. Interactive requests always try again.
    - In the user's real logs, the same doomed start had repeated more than 40 times in 16 minutes. This is the fix.
  - `CompatBook` (`llama\compat.json`) records each model file on each server:
    - the outcome, context, cache and MTP;
    - layers on the GPU;
    - VRAM for weights, cache and work, as llama.cpp reports it, including the vision projector;
    - load time, prompt and output speed, the GPU and the driver.
  - `Markdown()` exports the record as a compatibility matrix.
  - Ollama's engine servers stop after `idleUnloadMinutes`, so Ollama's files are never locked when it updates.
  - `UnloadAsync(modelId)` forgets that model's failures; this is what Astra's *Clear failure* button calls.
- **Health check:** it reports the active model's engine label and any known problem before trying. "auto" skips models nothing on the PC can run.
- **A model choice per provider:**
  - New setting `ai.providerModels`, handled by `SettingsStore.CarryModels`. Switching provider stores Model and DeepModel for the old provider and restores the new one's.
  - With nothing stored yet, an Ollama name carries over to the runtime and back. An `hf:` id doesn't carry over to other providers.
  - `LocalAi.SetRole("main")` writes the runtime's own slot while another provider is in use.
- **Fit before download:** `ModelDownloads.HeaderAsync` reads a GGUF header from Hugging Face with a ranged GET, capped at 64 MB. `LocalAi.PreviewAsync` turns it into a `LocalModel`, so `Fit` and `Problem` are exact before the download.
- **New `LocalAi` API with no UI yet:**
  - `Engine(model)` gives the engine's Label and a Note on why it was chosen.
  - `Problem(model)`, `Compatibility(model)` and `CompatibilityMatrix()`.
  - `PreviewAsync(...)` and `OllamaEngine`.
  - `RuntimeReady` now counts Ollama's engine too.
- **ComfyUI:**
  - `ComfyImageClient.PairingProblems` and `ImageModelInventory.Mismatches` catch file pairings that can't work, before anything is sent:
    - Klein 9B with the 4B Qwen encoder;
    - Klein 4B with the 8B encoder;
    - T5 or CLIP encoders, or FLUX.1's `ae` VAE;
    - model families that aren't Klein.

    Adrian's real ComfyUI log shows two jobs that failed this way.
  - `ComfyServerLauncher` now finds **Comfy Desktop** (`%LOCALAPPDATA%\Programs\Comfy Desktop\Comfy Desktop.exe`) and reads its output folder from `%APPDATA%\Comfy Desktop\settings.json`.

**Live acceptance on 6 Oct.** The PC has an RTX 4070 SUPER (12 GB) and driver 617.14. Every run used a scratch build and scratch profiles.

| Model | Engine | Context | Ready in | On the card | Writing speed |
|---|---|---|---:|---:|---:|
| Pinned Qwen3.5 9B Q5_K_M | Official b11387 · cuda13 | 8K, q8_0 | 4.6 s | 6,126 MiB (+876 MiB projector, uncounted in that run) | 58.5 tok/s; prompt about 2,680 tok/s |
| Ollama's qwen3.5:9b | Ollama's engine, chosen up front | 64K, MTP | 5.6 s | — | 71–79 tok/s |
| qwen3.8-heretic:27b IQ2_S | Official build | 32K | 8.6 s | 9,932 MiB | — |
| gemma4:12b-it-qat | Official build | 64K | 7.3 s | 8,085 MiB | — |

- The Q5 was downloaded through Aqua's own download code: 7.5 GB in 82 s, with the SHA-256 checked.
- Ollama's gemma4 conversion reads fine on the official build.
- Force-killing the app ended its server through the job object. Ollama's engine stopped itself when idle. No server was left after any quit.
- **Image Gen** ran against Adrian's own ComfyUI 0.38.2, headless on port 8288 with scratch folders:
  - 1024² at 20 steps: 23.5 s.
  - Model release confirmed in 1.0 s.
  - A reference edit took 48.5 s and changed only what it was asked to; the edit was faithful.
  - Cancel works.
  - VRAM peaked at 11.7 GB and returned to 1.4 GB.

**Still open from Astra's list:**

- A pinned compatibility matrix in the docs. Generate it from the record, and add the numbers above.
- Distinct compatibility and capacity errors: done in code, but it needs documenting.
- Live acceptance not yet done:
  - install and cancel of the runtime;
  - GPU/CPU coexistence, which needs an embedding model;
  - the in-app handoff between Image Gen and chat. E2E intercepts generation, so it can only be tested outside an E2E run.
- Performance budgets for theme switching and long Markdown chats.
- Mouse-button global shortcuts, which need a `WH_MOUSE_LL` hook design. `RegisterHotKey` can't do it.
- A persistent Roleplay character library (9.3).

**Phase 4.5 and 4.6, not built yet:**

- Reading limits that follow the context actually loaded.
- A per-answer timing breakdown (plan, gather, load, prompt, write). `LlmResult.Timings` already exists.
- A warm-up that loads the model while the user types.
- A prompt-prefix stability audit. The live logs show only the first 162 tokens of the agents' prompts come from the cache.
- Graceful ending of runaway reasoning (`/v1/chat/completions/control`).
- Candidate: `--image-min-tokens 1024` for Qwen-VL projectors. llama.cpp recommends it for grounding. Only Aqua's build may support it, so test it on Ollama's engine first.

**Uncommitted work in the tree.** Eight Image Gen files are uncommitted, probably later work by Astra:

- modified: `ComfyImageClient.cs`, `ImageArchive.cs`, `ImageGenerationModels.cs`, `ImageGenerationSession.cs`, `ImageGenerationPanel.xaml(.cs)`
- new: `ImageReference.cs` and `Platform/ReferenceImages.cs`

Read them, and ask Adrian what they are before you build on or around them. Never discard them.

## Rules that stay in force

- **Git:** never commit, push or tag. Adrian may release mid-session, so check `git log` and the GitHub runs before you report. `gh` isn't installed; use `curl -s https://api.github.com/repos/FlameAqua/aqua-hub/actions/runs?per_page=5`.
- **Tests:** don't write tests and don't run the E2E suite (`tests/AquaHub.E2E`); both are Astra's. Run the unit suite as a regression check:

  ```bash
  dotnet test tests/AquaHub.Tests -c Debug
  ```

  Run it from the repo's normal output folder; the UI-contract tests look for `AquaHub.sln` above the output folder. If your deliberate change breaks one of Astra's assertions, make the smallest fix and report it.
- **Adrian's own instance:** never touch, kill, drive or reconfigure it. That covers:
  - his installed Aqua (`%LOCALAPPDATA%\AquaHub.App\current\AquaHub.exe`) and the repo's `publish` folder;
  - his real profile, `%LOCALAPPDATA%\AquaHub` (reading is fine);
  - his Ollama (127.0.0.1:11434);
  - his Comfy Desktop settings and outputs.
- **Scratch only:**
  - Build into a scratch folder with `-p:OutDir=<scratch>\app-build\`. Don't run `scripts/build.ps1`.
  - Run the app as `AquaHub.exe --e2e --data-dir <scratch profile>`, with `manageOllama` and `startOllamaOnDemand` false, and Ask's folders pointing at an empty scratch folder or Use my PC off.
  - Copy Adrian's installed runtime into scratch rather than running it from his profile.
  - For live UI probes, use UI Automation patterns only: no synthetic keyboard or mouse input.
- **Process hygiene:** stop only the processes you started, matched by their scratch command lines (for example `--port 11650` with `test-model`, or ComfyUI's `--port 8288` with `comfy-scratch`).
- **Downloads:** each needs Adrian's explicit OK. That includes models, runtime builds and ComfyUI models. Publishing anything public needs his OK too.
- **No side effects on Windows:** no writes to the registry, Credential Manager or Adrian's settings. Never record the microphone, and never end real processes.
- **Repo files are LF.** Edit them by bytes. Write Python edit scripts with a file-writing tool rather than shell heredocs, which mangle backslashes.
- **Security posture.** Keep all of these:
  - everything downloaded is pinned and checked against its SHA-256;
  - local servers listen only on loopback, with a random key per start that travels in the environment;
  - the servers run in a kill-on-close job;
  - Ollama's store is only read;
  - one model on the GPU at a time.

## Your first steps

1. **Check the state:** `git status`, `git log`, the CI runs, and the uncommitted Image Gen files (ask Adrian about those).
2. **Report the 6 Oct work** in `docs/handoff/phase-4b.md` for Astra:
   - the new API and settings;
   - the screens it needs: the engine label and note per model; `Problem` before the user tries; a compatibility view with "Copy matrix"; `PreviewAsync` fit on search results; the `useOllamaEngine` switch; `Mismatches` in Image Gen's connection check; the Comfy Desktop launcher;
   - what to test;
   - the live results above.

   Add entries for it to ARCHITECTURE, SECURITY and SCORING, and put the matrix in the docs.
3. **Update the roadmap artifact:** the actual state, the differences listed above, and Phase 4.5 and 4.6 as the remaining work.
4. **Ask Adrian what comes next.** Either finish 4.5 and 4.6, or start Phase 5. Then design and build it, and end with a report for Astra.
