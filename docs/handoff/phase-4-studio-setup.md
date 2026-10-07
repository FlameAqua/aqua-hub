# AquaHub GPU editor and Ask studio — setup and handoff

5 October 2026. Follow-up to `phase-4-frontend-review.md`. Changes remain uncommitted.

## What changed

- Feed collectors and initial clustering/ranking remain deterministic: conditional requests, source-health backoff, caching, and configured sources. Fetching does not spend LLM tokens.
- All llama.cpp generation now uses the shared GPU queue. Interactive work goes ahead of waiting background jobs; a running request finishes before the next starts.
- News Editor, Social Pulse, and Foresight use the **Editor** selection (the existing `ai.llama.backgroundModel` setting), falling back to Main. Daily briefs and market analysis retain the Deep override, falling back to Main. Other background helpers use Main. They no longer inherit an arbitrary creative model that happens to be loaded, or launch a second large CPU model.
- On this 12 GB card, the catalog now recommends **Qwen3.5 9B Q5_K_M** instead of the severely compressed 27B IQ2_S suggestion. Editor and Creative can share Main without duplicate downloads or simultaneous model residency.
- Ask Aqua now has **Chat**, **Image Gen**, and **Roleplay** tabs. Existing Chat drafts and history remain separate.
- Image Gen has base/fast model variants, a prompt composer, canvas presets, sampling quality, reproducible seeds, connection/model discovery, progress, cancellation, a saved local gallery, full-size viewing, PNG export, regenerate, reference-image editing, confirmed deletion, a folder shortcut, image expiry, and a ComfyUI start button. Custom model choices must be compatible with the selected Klein 4B workflow; this is not an arbitrary-workflow importer.
- Roleplay streams through the selected Creative model or Main, with persona/scenario setup, bounded conversation context, stop, reset, and transcript copy. It does not expose Ask's web, file, or PC tools. Scene state survives navigation in the current app session; it is not yet a persistent character/chat library.

## Download before live testing

### 1. Language model — one shared main/editor model

In **Models**, use the 12 GB suggestion **Qwen3.5 9B · Q5_K_M**. Aqua downloads the model and its optional-for-text vision projector, verifies hashes, and registers them in its library. Then set **Main** to it and leave **Editor** and **Creative** at **Use Main (recommended)**. Clear an old Deep override if you want briefs to use it too.

The exact catalog artifacts are pinned to revision `725b4640b3d92a9d36037c79ac04a4378d9e08bd` of `unsloth/Qwen3.5-9B-GGUF`:

| File | Download | Bytes | SHA-256 |
|---|---|---:|---|
| Qwen3.5-9B-Q5_K_M.gguf | [GGUF](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/725b4640b3d92a9d36037c79ac04a4378d9e08bd/Qwen3.5-9B-Q5_K_M.gguf) | 6,577,841,312 | `e90fa097659415cfdd6240ed1e620f4062473cb7a9ddc9d9b75abeef8094c2e0` |
| mmproj-F16.gguf | [Vision projector](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/725b4640b3d92a9d36037c79ac04a4378d9e08bd/mmproj-F16.gguf) | 918,166,080 | `f70dc3509053962b0d0d3ee8a7eacebf5d60aa560cad78254ae8698516ae029f` |

Prefer the in-app download: copying a GGUF into the folder alone does not register it. Start live testing with **8,192 context tokens**, **q8_0 cache**, and the CUDA runtime. Q5 is a moderate weight quantization; it is not lossless, and task quality still needs evaluation. This is a fresh GGUF candidate, not a repair of the incompatible Ollama blob reported earlier.

### 2. Image backend — ComfyUI, running locally

Install a current [ComfyUI Windows build](https://docs.comfy.org/installation/desktop/windows) with NVIDIA GPU support, or use its portable distribution. Update it if the connection check reports missing native Klein nodes. Start it, and enter its local address in **Ask Aqua → Image Gen → Local image server & models**. The initial address is `http://127.0.0.1:8188`; some desktop installations use a different port. Use the address shown by your installation.

Aqua can start an installed ComfyUI process using **Start ComfyUI**. It checks whether the configured local server is already running first. Choose `ComfyUI.exe` or the portable `run_nvidia_gpu.bat` once under **Choose launcher**; common NVIDIA portable/desktop locations are detected. The launcher runs hidden from its own directory, with no extra shell commands. Aqua waits up to a minute for the configured address; slower starts can be rechecked with **Check connection**. ComfyUI remains an independent process, and Aqua does not stop it when it exits. Python, ComfyUI, and model installation remain manual. Aqua builds and submits workflows directly, so importing/editing a graph in the ComfyUI browser is unnecessary.

Download these three files into your **ComfyUI model directory**, not Aqua's GGUF library:

| File | Approximate download | Destination within ComfyUI |
|---|---:|---|
| [flux-2-klein-base-4b-fp8.safetensors](https://huggingface.co/black-forest-labs/FLUX.2-klein-base-4b-fp8/resolve/main/flux-2-klein-base-4b-fp8.safetensors) | 4.09 GB | `models/diffusion_models/` |
| [qwen_3_4b.safetensors](https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/text_encoders/qwen_3_4b.safetensors) | 8.04 GB | `models/text_encoders/` |
| [flux2-vae.safetensors](https://huggingface.co/Comfy-Org/flux2-dev/resolve/main/split_files/vae/flux2-vae.safetensors) | 336 MB | `models/vae/` |

This uses the undistilled Klein **Base** model for flexibility, official FP8 diffusion weights, and the full-precision text encoder. ComfyUI manages loading/offloading the encoder and diffusion model in stages; disk sizes are not a promise that everything fits resident together. Leave smart memory management enabled. Start at 1024×1024, Standard (35 steps), then compare Draft (20) and Fine (50). The graph uses CFG 5 and Euler, matching the official Comfy base workflow structure.

Optional speed variant: [flux-2-klein-4b-fp8.safetensors](https://huggingface.co/black-forest-labs/FLUX.2-klein-4b-fp8/resolve/main/flux-2-klein-4b-fp8.safetensors), also in `models/diffusion_models/`. Select **Klein 4B · fast, 4 steps**; the encoder and VAE are shared. This is an optional later comparison, not required for the first test.

After copying files, click **Check connection**. Aqua reports missing nodes and filenames explicitly. The model dropdowns populate from the server. Use Aqua's **llamacpp** provider for coordinated sharing with chat; separately managed Ollama/OpenAI-compatible GPU servers cannot be unloaded by this integration.

The official Klein weights have training-time mitigations and are **not guaranteed fully uncensored**. The [publisher's model card](https://huggingface.co/black-forest-labs/FLUX.2-klein-base-4B) explicitly describes these. This implementation supports compatible local checkpoint selection without claiming that choosing a local runtime removes learned restrictions.

## GPU ownership and failure handling

Image generation acquires the same interactive reservation as Ask, waits for an existing answer to finish, and unloads Aqua's GPU llama instance. Chat and background jobs wait during image generation. Once the image job ends, Aqua requests Comfy model unloading and checks that Comfy's queue is idle and allocator statistics show only a small residual allocation before releasing the reservation. The next chat/editor request reloads its language model.

ComfyUI returns its own job identifier; Aqua follows that identifier. Cancellation deletes only Aqua's queued job, or uses the current job-scoped cancel API. Older servers without this API may finish the image before cancellation can return. Global `/interrupt` is never used. A missing submission acknowledgement, lost connection, stuck job, or unconfirmed memory release keeps the reservation held; **Check GPU release** retries recovery. If ComfyUI stopped, restart it and retry. Other applications and independently submitted ComfyUI jobs remain outside Aqua's scheduling control.

## Image library actions and expiry

**Regenerate** restores the selected image's prompt, model family/files, canvas, sampling steps, and original editing reference (if any), then creates a new image with a random seed. **Edit image** uses the selected image's actual ComfyUI output as a Klein reference. Enter the desired changes and choose **Apply image edit**; the original remains intact. **Clear** leaves reference-edit mode. The native graph adds `LoadImage`, `VAEEncode`, and positive/negative `ReferenceLatent` conditioning as appropriate for Base/Distilled. Editing needs no extra model downloads.

Editing requires the original ComfyUI output copy. If it has been deleted or expired, Aqua reports that before queueing inference. Restore the named PNG to the server output folder, or clear the reference and regenerate from a complete text description. Older archive records without an output path restore their prompt/settings for revision instead of claiming reference editing is available.

Generated PNGs and JSON settings are saved in `<Aqua profile>/generated-images/`; **Open image folder** opens that location. **Delete** presents an inline confirmation, then removes only the selected PNG, metadata, and its tracked server copy when configured. Exported copies remain. Preview/gallery state refreshes after deletion.

**Keep images** offers 1, 7, 30 (default), or 90 days, or **Keep forever**. Expiry is separate from chat retention. It runs at app startup, hourly while open, and when the setting changes; if Aqua was closed, overdue images are handled next launch. Cleanup scans all archive metadata, including images beyond the recent-gallery limit, and uses the recorded creation time. Export with **Save as** to preserve a copy independently of expiry.

Select **ComfyUI output folder** so deletion and expiry also remove Aqua's tracked server copies. Recognized portable launchers can detect this folder. If it is unconfigured, Aqua explicitly says its policy covers only the local gallery; server copies remain. Unrelated ComfyUI images are untouched. New outputs use unique `AquaHub/<GUID>` prefixes. Cleanup rejects escaping/reparse paths and preserves metadata for retry if the configured server folder is unavailable or a delete fails. Legacy, manually copied, or failed-job outputs without archive records require manual cleanup. No cloud image service is used.

## Implementation map

- `src/AquaHub.Core/Ai/LlmClient.cs`: editorial routing, GPU scheduling, image reservation.
- `src/AquaHub.Core/Ai/Prompts.cs`: Story/Pulse/Foresight editorial classification.
- `src/AquaHub.Core/Ai/Local/ModelDownloads.cs`: pinned Q5 recommendation.
- `src/AquaHub.Core/Ai/Imaging/ComfyImageClient.cs`: native generation/edit graphs, discovery, HTTP job lifecycle, bounded image retrieval, cancellation/recovery, memory-release confirmation.
- `src/AquaHub.Core/Ai/Imaging/ImageArchive.cs`: profile-owned persistence, deletion, and expiry.
- `src/AquaHub/Services/ImageGenerationSession.cs`: navigation-persistent job state, scheduled retention, and GPU lease ownership.
- `src/AquaHub/Services/ComfyServerLauncher.cs`: local installed-server discovery and launch.
- `src/AquaHub/UI/Controls/ImageGenerationPanel.*`, `RoleplayPanel.*`: studio UI and separate roleplay session.
- `src/AquaHub/UI/Pages/AskPage.*`: mode navigation; `ModelsPage.xaml.cs`: Editor/Creative role wording.

## Validation and remaining live acceptance

| Check | Result |
|---|---|
| Release app build | Passed, 0 warnings and 0 errors. |
| Full Release unit suite | **1,218 passed, 23 existing opt-in live-test skips, 0 failed** (1,241 total). |
| Combined desktop regression | **8 passed, 0 failed**: Models, existing Ask interactions, and studio actions, in 35 seconds. |
| Final studio-only recheck | **2 passed, 0 failed**, in 7 seconds on the final isolated Release binary; E2E build has no warnings. |
| Visual checks | Dark/light studio layouts at 1440px and 820px; model filenames and sampling labels are now visible. Private captures are under `docs/screenshots/private/phase4-studio/`. |

The unit fixtures cover reference graph wiring, missing references before submission, unsafe output paths, targeted deletion, expiry across more than 30 records, timestamp semantics, keep-forever, unavailable folders, cancellation, uncertain job ownership, and GPU-memory release. Desktop tests cover separate drafts, model selection, reference-edit mode, regeneration, folder/start actions, expiry persistence, confirmed deletion of archive and synthetic server copy, and preview removal. Startup/generation/folder launching are intercepted in the desktop test profile. Actual ComfyUI launch and inference remain live acceptance work.

The combined run is `docs/screenshots/private/phase4-studio/runs/20261005-200131/`; the final studio recheck and populated reference-edit screenshot are in `20261005-200520/`. `git diff --check` also passed.

No multi-GB models were downloaded and no real image inference or new-model GPU benchmark was performed in this implementation pass. The existing owner app was not stopped; app builds and desktop tests used an isolated executable and synthetic profiles. The full unrelated desktop suite was not rerun.

Live acceptance after downloading:

1. Load the pinned Q5 model at 8K/q8_0. Confirm the earlier RoPE array error is absent and record VRAM, prompt throughput, and generation throughput.
2. Run News Editor, Pulse, and Foresight. Check source fidelity, attribution, handling of contradictory stories, local relevance, and sentiment evidence. Compare several cached prompts with the old model rather than judging from parameter count alone.
3. Queue chat during an editorial request. Confirm chat runs next; a started background request is not forcibly interrupted.
4. Generate a 1024×1024 Klein Base image; confirm llama unload, image saved/previewed, allocator-confirmed release, and successful chat reload.
5. Cancel one queued image and one running image. Check that unrelated ComfyUI jobs continue and Aqua retains its reservation if the server disconnects.
6. Compare Standard/Fine image outputs and record peak VRAM/latency. Test narrow/light/dark UI with a real image, not only empty states.
7. Start the installed ComfyUI from Aqua, confirm the configured address and output directory, then regenerate and reference-edit a saved image. Check reference fidelity, not only API success.
8. Export a keeper, delete another image, and expire a dated test image. Verify Aqua and tracked Comfy copies disappear while exports and unrelated server outputs remain.

## Assessment for Claude

The overall backend assessment remains **7.5/10**, with the detailed rationale in `phase-4-frontend-review.md`. The service boundaries made this extension feasible, but role routing needed clearer semantics: collecting feeds should stay deterministic, editorial analysis should have an explicit model choice, and arbitrary loaded models must not determine background behavior. The new GPU reservation makes image/LLM sharing explicit, with recovery for uncertain server jobs.

Next priorities: validate the pinned runtime/model combination on this exact GPU, keep provider-specific role selections, and add a real ComfyUI integration run to the compatibility matrix. Consider a persistent character library for Roleplay and stronger lifecycle handling for independently managed GPU servers in a later phase. The current HTTP fixtures and desktop checks are substantial coverage, but they cannot establish model quality, real VRAM fit, or production ComfyUI compatibility.

## References

- [Official ComfyUI Klein workflow and model guide](https://docs.comfy.org/tutorials/flux/flux-2-klein)
- [Official ComfyUI server API implementation](https://github.com/Comfy-Org/ComfyUI/blob/master/server.py)
- [ComfyUI memory accounting implementation](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy/model_management.py)
- [FLUX.2 Klein Base publisher card](https://huggingface.co/black-forest-labs/FLUX.2-klein-base-4B)
- [Official Klein Base editing workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_image_edit_4b_base.json)
- [Official Klein Distilled editing workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_image_edit_4b_distilled.json)

The HTTP fixtures test the API contract; they do not certify a particular installed ComfyUI version or replace the live steps above. GPU-memory confirmation uses Comfy's allocator statistics rather than measuring all driver allocations. Keep an initial live VRAM measurement in the release checklist.
