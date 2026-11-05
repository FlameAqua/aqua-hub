# Aqua Hub security and privacy

Reviewed 5 November 2026 against `86f5ec6`. This is the implemented policy and its known limits, not a claim of a complete
security audit. See [Architecture](ARCHITECTURE.md), [development checks](DEVELOPMENT.md) and the
[remaining work](ROADMAP.md). Report vulnerabilities privately using the repository's Security tab when available;
do not put credentials or private captures in public issues.

## 1. Trust boundaries

Aqua runs as the signed-in user. Its normal features do not require elevation. The optional .NET Desktop Runtime
installer can request elevation, and explicitly chosen Windows maintenance tools can have their own prompts.
There is no Aqua account/backend or telemetry. Local AI is the default; a remote AI endpoint is an explicit opt-in.
Model subprocesses Aqua starts listen on loopback, not a network interface accessible to other computers.

| Boundary | Allowed data/effects | Enforcement |
|---|---|---|
| Internet → collected/Ask content | Bounded text/images, treated as untrusted | Source parsers, `HttpFetcher`, public-only `WebReader`, rendering limits |
| Ask → local files/Windows | Only configured/attached access and approved actions | `LocalFiles`, tool policy, `DesktopRules`, Windows adapters |
| Private context → internet | Explicit approval where the private-data policy applies | Per-answer/chat privacy state and visible query/address approval |
| App → model engine | Explicit local/opted-in endpoint, constrained subprocess start | `LlmClient`, `LlamaArgs`, `LlamaProcessHost` |
| App → filesystem | Profile-owned artifacts or explicit user-selected locations | Validated paths, identifiers, ownership rules and bounded parsers |
| Download → executable/model | Verified artifacts before installation/use | Fixed release/commit, expected size/SHA-256, restricted extraction |

This does not sandbox trusted user-installed executables, defend against a compromised Windows account, or prevent
other applications from using the GPU. A malicious program running as the same user can inspect or alter local data;
an environment-carried API key avoids command-line disclosure but is not isolation from such a program.

## 2. AI authority and approvals

Background editorial agents have no PC tools. The command interpreter returns an allow-listed action validated against
configured apps/scenes/pages; it cannot introduce arbitrary executables, paths or shell commands. Workbench skills are
recipes of existing tools, not executable code.

Ask classifies tools by hub/web/private/action access. PC actions show their exact target/text/keys in the chat's approval
flow according to settings. Some actions can be allowed for a chat; key presses and typing that submits with Enter need
individual approval. After untrusted content has been read, clicking, typing, key presses and closing windows require a
fresh approval even when the general acting preference is off. Unanswered approvals expire after three minutes.

`Prompts.Fenced` fences tool/page/file content, prior turns, notes and summaries as data and defuses nested fence marks.
Fencing is a prompt-injection mitigation; actual protection comes from deterministic tool validation and approvals.
Neither model text nor a fetched document can approve an action or expand allowed folders.

`DesktopRules` excludes Aqua's own windows, shells/terminals/Run, File Explorer, command-capable editors, script hosts,
system/security/sign-in prompts, password managers and password fields. Keystrokes target the approved window and
recheck foreground identity before each chunk. The key allow-list excludes Windows-key combinations and Alt+F4.
`launch_app` does not start shells, installers, scripts or admin tools. User-configured Launchpad/scenes and manually
selected maintenance tools are separate explicit features, not a way for model output to bypass this policy.

After a chat has read private context (files, screen, attachments, clipboard or holdings), searches, page requests and
browser links need approval showing the query/address. That private state persists with the chat so later turns cannot
silently export it through notes/summary/history. Private names/numbers from calendar, memories or chat notes also enter
the egress checks. Ask directly fetches only supplied/authorized chat links or links discovered through its search flow;
invented model URLs are not automatic fetch authority. Remote AI opt-in is a separate disclosure choice: approved context
sent to that endpoint leaves the PC. Local operation claims must not be shown for a remote endpoint.

## 3. Files, parsing and output

`LocalFiles` checks configured roots and per-chat folder grants, canonicalizes existing Windows short-name components
using `FindFirstFileW`, rejects traversal/sensitive names and refuses links below an allowed root. Attached roots are also
judged by their resolved target. Unresolvable aliases/root checks fail closed. Windows Search results are checked again;
an index hit is not authorization. Searches skip system/app-data/cache locations and cloud-only placeholders.

Files explicitly attached by the user are a separate grant: current `CanRead` applies the sensitive filename/extension
guard but bypasses ordinary root/folder/link checks for that exact file. Do not document that exception as identical to
root-based access. It needs its own path/identity review before Phase 8 reuses this policy. Checks on mutable filesystem
paths are not a complete defence against a same-user process swapping a file between check and open.

Remote HTML is reduced to text, never embedded as an executable page. XML readers restrict DTD/entity handling; Office
documents are parsed without running macros. Response sizes, document/tensor/array counts, recursion and time are bounded.
PDF OCR and image decoding use Windows components, not a claim that malformed native-decoder inputs are harmless.

Links opened by the app are absolute HTTP(S) addresses without credentials; configured app protocols have their own
explicit paths. Ask file links open documents/pictures/media, while program/script/shortcut and other unsupported types
are revealed rather than executed. Credential/key/password-store filenames and extensions remain denied by file policy.

Maths rendering has a recursion guard: oversized/deep/line-heavy formulae stay text; XAML-Math rendering uses a dedicated
large-stack thread. Markdown does not execute code. Total formula/table complexity and table accessibility remain open
limits to verify; a per-formula guard alone does not bound an entire long chat.

## 4. Network access and secrets

`WebReader` is the public-page path: it bypasses proxies and checks resolved addresses at connection time, including
redirected hosts. It rejects loopback, private, link-local/metadata, CGNAT and IPv6 unique-local addresses; it limits
redirects and page bodies (5 MiB). Do not replace it with the general feed HTTP client for model-selected URLs.

`HttpFetcher` handles configured sources with size/time limits, HTTP validators and host cooldowns. It supports HTTPS
(and loopback HTTP) and uses system proxies. Its policy is not WebReader's public-address policy. AI clients bypass
system proxies; remote endpoints require explicit opt-in. ComfyUI endpoints are configured loopback endpoints only.

API keys, Bluesky app passwords and Mastodon read tokens live in Windows Credential Manager, not settings or logs.
Bluesky session state stays in memory; failed sign-in backs off and disconnect removes the stored secret. Feed/social
connections are read-only. Logs redact query strings, but error text and diagnostic exports can still contain paths,
model names or other private content; inspect them before sharing. Remote publisher images disclose the user's IP and
can be disabled. No claim of anonymous network access is made.

Location is unset until chosen. Town search uses Open-Meteo; **Use my location** explicitly requests Windows location,
rounds it and sends it to Nominatim to resolve a town, then Open-Meteo for time zone. Only the selected town/rounded
coordinates persist. Offline Windows dictation and Whisper keep audio local/in memory; online Windows speech is a
separate opt-in. Recording occurs only when the user initiates dictation/test, never as background monitoring.

## 5. Local model engines and downloads

Runtime/Whisper archives are fixed vendor releases with expected size/SHA-256. Extraction restricts retained executables
and libraries and caps unpacking. Public model files are fixed Hugging Face revisions with expected size/SHA-256;
corrupt same-size files must not be trusted. Partial downloads resume only through the verified-download path, and a
complete hash mismatch discards the partial file. A bounded remote GGUF-header preview is metadata inspection, not
whole-file verification or a guarantee of fit.

The custom engine path takes priority. Otherwise Aqua may use Ollama's installed `lib/ollama/llama-server.exe` when
upstream cannot read an Ollama conversion or Aqua's build is absent (`useOllamaEngine`, default on). User-installed
Ollama/custom engines are trusted local software, not binaries verified against Aqua's catalog. Ollama's model store
remains read-only. Format blocks/backoff prevent repeated doomed loads; Clear failure never rewrites GGUF metadata.

`LlamaArgs` binds to 127.0.0.1 with a random per-start key via `LLAMA_API_KEY`, disables the web UI/slots, requests offline
operation, and uses constrained argument lists. `LlamaProcessHost` starts hidden with a minimal loader/temp environment.
It requires a kill-on-close Windows job before returning a managed process. Job creation failure rejects starts;
assignment failure kills the just-started Aqua-owned process and raises a visible error. Launch and host disposal are
serialized. Model handoff confirms process termination before forgetting ownership; failed termination blocks another
load. A containment failure disables host starts until restart. If failed-start cleanup cannot confirm exit, the host
retains that exact process and blocks image handoff too. Creation still precedes assignment briefly, and forced-exit/failure-path verification remains open. A job object
manages lifetime; it is not an OS security sandbox or an atomic suspended-process launch.

Reasoning control is armed only for the pinned b11387 engine and thinking-capable model. A bounded authenticated request
uses the completion ID obtained from that same stream. Unknown engine versions use the direct-answer fallback; no
global slot control is issued. The optional image-min-token flag remains disabled pending model/engine-specific evidence.

Aqua manages only its own bundled-engine subprocesses. Its separately configurable Ollama-service management identifies
the local listener and installation, checks busy/other-client/download state for automatic idle stop, and respects manual
off. It must not be used by tests against the owner's service. Other local/remote servers remain independently managed.

`llama/compat.json` records local model IDs/hashes, engine, GPU/driver, errors and performance. Model fallback keys/errors
may include paths. Copy/export is explicit and never needs prompts, answers or API keys. Server kind/size/mtime is a
compatibility-cache fingerprint, not cryptographic authenticity. Exact artifact provenance is incomplete in older records.

## 6. Chat and image retention

Chats, notes, summaries and brief retained source excerpts live in the local database. Unstarred chat retention defaults
to 30 days; deleting a chat or disabling stored chats removes its associated data and cancels in-flight saving. Per-chat
attached-folder grants are not stored. Screenshots/pasted chat pictures are kept whole when **Keep pictures with chats**
is on (default); linked/attached file pictures keep a path and thumbnail. Disabling that setting deletes chat-media files
and recorded pictures. Content-derived names prevent a stored image identifier from selecting arbitrary filenames.

Image Gen is separate: generated PNGs/settings and prepared reference copies live in `generated-images`. References are
metadata-free, content-named PNGs prepared from explicit picker/drop/paste or gallery actions, then uploaded to Comfy's
temporary folder for editing. Native preparation enforces input byte/pixel and canvas limits, fixes orientation/colour,
and flattens transparency. These references are copies, unlike linked chat pictures. They remain while an archived edit,
draft or running job needs them; cleanup keeps all references if unreadable metadata makes ownership uncertain.

The gallery's retention/deletion policy covers Aqua's files and, when configured, validated tracked Comfy output copies.
Exported copies and unrelated server images remain. Comfy Desktop output discovery reads its settings without editing
them. A user-selected Comfy launcher is trusted installed software; Aqua does not install/hash-pin Comfy/Python/custom
nodes. Recognizable filename pairing guards are not tensor verification or approval of arbitrary workflows.

The shared GPU lease is kept while Comfy job ownership/completion or memory release is uncertain. Cancellation targets
Aqua's job; it does not send global interrupt. Allocator release checks are not a guarantee about all driver VRAM.
Imported-reference preparation, upload and retention paths still need dedicated acceptance; successful older reference
editing does not certify this later feature.

Reference-file and gallery-import reads now bound allocation against the opened handle, including growth/read races;
retained copies must match their content ID and PNG signature. Clipboard dimensions are bounded before background
scaling/encoding. Decoder work cannot be interrupted mid-frame; normal exit joins it. This is bounded native decoding,
not a sandbox for Windows image codecs.

Before image submission, `generated-images/gpu-recovery.txt` records only the loopback endpoint, never prompts or image
bytes. Its presence restores the GPU reservation before scheduling on restart. It is removed only after confirmed
allocator release. Corrupt/unreadable metadata keeps the block and asks for the original server address explicitly;
choosing a different server cannot establish release by the original one. Recovery waits for an idle queue and never
sends a global interrupt. A crash can lose a result not yet archived; recovery does not retrieve it automatically.

Normal Quit stops scheduling and cancels/joins owned work before client disposal, while the dispatcher still runs.
After the 45-second bound or Windows session termination, still-used dependencies remain alive until process exit and
the model job closes. An unresolved Comfy reservation/marker remains; external Comfy processes are not killed on exit.

## 7. Persistence, updates and supply chain

Profile data is under `%LOCALAPPDATA%/AquaHub` (or an explicit profile override); the versioned installation is separate
under `%LOCALAPPDATA%/AquaHub.App`. User-selected model/output folders have their own ownership rules. No database,
image archive or log is claimed encrypted at rest; Credential Manager protects stored secrets for the Windows user.
Uninstall keeps the profile unless the user removes it. Start with Windows is opt-in, using the per-user Run entry.

Installed updates use the GitHub repository embedded in the release; source builds without it do not check. Downloads
and installation are explicit actions, with package SHA-256 checks and installation at restart. These checks do not
replace publisher trust: a compromised release publisher can publish matching hashes. Packages are not code-signed.

`nuget.config` restricts source mapping and requires trusted repository signatures; direct versions are centrally pinned.
CI/release use locked restore, dependency audit and fully pinned action SHAs, with read-only job permissions and checkout
credentials removed from the working copy. An actual install/update rehearsal and confirmation of private vulnerability
reporting availability remain release work. Licence choice remains Adrian's decision.

## 8. Review limits and next checks

This pass inspected service ownership, scheduling, network/file boundaries, process launch and image-reference storage.
The later authorized offline component run exercised the real WPF process using synthetic profiles, UIA patterns and
owned loopback HTTP fixtures. Offline startup suppresses collectors/media/updates and service management; test settings
disable AI, global hotkeys and PC access. Copying test profiles rejects linked entries. E2E pickers open scratch and avoid
Recent files. These guards are not an OS/network sandbox. Intercepted effects and fixture idle/free responses do not
establish live inference or physical GPU release. No penetration test, microphone recording or installed update was done.
Known gaps are retained as acceptance tasks in [ROADMAP.md](ROADMAP.md), including containment/shutdown failure-path
acceptance, explicit-file grants, imported references and end-to-end private-data policy. Documentation must not mark these
closed without evidence from the relevant failure paths.
