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

### Recipe authority and retention

Saved descriptions, step bindings and findings are data. The manual recipe runner uses a separate fixed allow-list for
Windows health checks and local report formatting. It does not inherit Ask's per-chat tools, file grants or the Search
index. The explicit run review authorizes one saved revision. Edits, pause and delete revoke access for future steps.
The runner has no provider, network, shell, scheduler, process launch or elevation capability. A recipe does not initiate
Windows maintenance; This PC offers those actions through the existing user-initiated controls.

Recipe history contains run IDs, revision numbers, capability IDs, times, outcomes and counts. It excludes raw Windows
logs and diagnostic detail. The latest report step can display up to eight headings until the app closes. The profile's
latest full This PC report is separate from recipe history and can include local device names and installed software
names. Internal event checks keep only the relevant count or summarized findings. Direct This PC model summaries use
the local route and are excluded from the Agents model output inspector. Explicit Ask requests continue to follow
Ask's private-data and provider policy.

## 3. Files, parsing and output

`LocalFiles` checks configured roots and per-chat folder grants, canonicalizes existing Windows short-name components
using `FindFirstFileW`, rejects traversal/sensitive names and refuses links below an allowed root. Attached roots are also
judged by their resolved target. Unresolvable aliases/root checks fail closed. Windows Search results are checked again;
an index hit is not authorization. Searches skip system/app-data/cache locations and cloud-only placeholders.

Files explicitly attached by the user are a separate grant: current `CanRead` applies the sensitive filename/extension
guard but bypasses ordinary root/folder/link checks for that exact file. Do not document that exception as identical to
root-based access. Search has its own handle-based authority checks below. Checks on mutable filesystem
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

### Search folder authority

Each grant records a chosen folder and its Windows identity. Name indexing is the default. Content indexing requires
separate consent for that folder. The local profile retains paths, names, identity/change stamps, size and dates.
Content consent also retains extracted passages, a full-text index and derived vectors in ordinary SQLite storage.
Other software with access to the profile can read these. Queries have no saved history. The Search service sends
queries and passages only to its authenticated, Aqua-owned loopback CPU model. Ask attachments, temporary chat grants
and Windows Search results confer no background indexing rights. Search folder approval grants no Ask access.

Explorer's opt-in uses two per-user static verbs for the installed default profile. The verbs supply a folder scope.
The current-user pipe accepts bounded paths and has a connection/read deadline. The existing consent form still
requires approval before indexing a new folder. Turning off the option or uninstalling removes the menu entries
owned by that executable.

The Windows adapter accepts explicit subfolders on fixed local NTFS drives with an unambiguous drive letter. It refuses
UNC/device paths, whole drives, alternate streams, noncanonical components, short-name aliases, reparse points,
multiply linked files, hidden/system/encrypted files, cloud placeholders, credential names, skipped application/system
locations and Aqua's profile. Ancestor handles disallow rename while a child is checked. Metadata opens use no-recall
and open-reparse-point flags. The data handle must match the inspected identity and allow current read access.
Extraction uses bounded text and passive XML/package readers. Document links, scripts and macros are never interpreted.

Cached snippets and actions require a fresh approved root, matching file stamp and readable handle. Locked or
temporarily unavailable entries can remain on disk, hidden from results until revalidated. Confirmed missing files
are removed. Removing a folder deletes its rows, FTS entries and vectors in a transaction. Disabling content deletes
its old index and rebuilds names under a new grant. Non-reused IDs prevent pending writes from recreating revoked
data. These are logical deletions. Freed database pages, journals, backups and older disk bytes can persist.
Open uses the existing file-type allowlist. Other types can be revealed in Explorer. The external handler ultimately
resolves the supplied path under the current user's authority after Aqua hands it to Windows.

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

Read aloud uses local Windows voices or the opt-in audio.cpp CPU process. It does not activate a microphone. The
Kokoro process receives one bounded text chunk through a file in the profile's dedicated speech work directory.
Aqua drains process output without logging it and reads at most 4 MiB of WAV data into memory. The text and WAV are
deleted after synthesis, including cancellation and failure. Aqua attempts to remove abandoned speech work at the
next startup if a crash or forced exit leaves it behind. Standard deletion provides no guarantee of forensic erasure.
The Windows backend keeps synthesis in memory. Provider changes and voice recovery require explicit user actions.

Workbench personality templates are ordinary profile data. They carry character text and an optional scene opener.
They do not grant file, PC or web authority, change global instructions, or participate in Ask memory. Aqua keeps the
scene, transcript and unsaved Workbench drafts in memory. Deleting a template removes the library entry, while an
active scene can keep its in-memory copy until reset or exit. Explicit transcript Copy uses the clipboard.
If a non-loopback model endpoint is configured and allowed, Roleplay sends the scene and bounded recent conversation
and Workbench sends the draft description. Both surfaces disclose the remote destination before sending. Raw creative
outputs are omitted from Aqua's recent-model diagnostics. External model servers have their own retention policies.

## 5. Local model engines and downloads

Runtime, Whisper and Kokoro archives are fixed vendor releases with expected size/SHA-256. Extraction restricts retained executables
and libraries and caps unpacking. Public model files are fixed Hugging Face revisions with expected size/SHA-256;
corrupt same-size files must not be trusted. Partial downloads resume only through the verified-download path, and a
complete hash mismatch discards the partial file. A bounded remote GGUF-header preview is metadata inspection, not
whole-file verification or a guarantee of fit.

The Kokoro install uses the pinned audio.cpp v0.9.5 Windows CPU ZIP, an audio.cpp Kokoro 82M Q8_0 GGUF and the
eSpeak-ng Windows DLL and data from the pinned `espeakng-loader` archive. Aqua verifies the full archives before
extracting the CLI, its libraries and the phonemizer files. It does not run a Python installer, use an audio.cpp web
server or send spoken text to the download hosts. The [audio.cpp licence](https://github.com/0xShug0/audio.cpp/blob/v0.9.5/LICENSE)
and [Kokoro weights](https://huggingface.co/hexgrad/Kokoro-82M) are Apache 2.0. The
[eSpeak-ng phonemizer](https://github.com/espeak-ng/espeak-ng/blob/1.52.0/COPYING) is GPL-3.0-or-later.

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

## 6. Chat, image and stream retention

### Stream accounts and metadata

Twitch's client ID is public settings data; its token pair/account metadata live only in `AquaHub/twitch.connection` in
Credential Manager, never in settings JSON. Device sign-in requests only `user:read:follows` and opens Twitch's validated
HTTPS activation address in the browser; no app secret, password capture, embedded login or Aqua backend is used. The
authenticated HTTP client refuses redirects. Responses and credential syntax/size are bounded; raw provider errors or
token values are not put in user errors or logs. Refresh rotation is serialised, replacements are saved before further
network work, and cancelled/obsolete sign-in cannot restore a disconnected account.

Disconnect deletes local credentials and cached Twitch observations, then requests revocation of the current access
token. If Twitch cannot confirm revocation, the UI explains how to remove the connection in Twitch settings. Switching
the public client ID clears the old local connection. Saved stream observations include public broadcast titles and
followed-channel metadata; they are unencrypted local profile data, separate from posts and automatic AI summaries.

YouTube checks public channel pages without Google keys, browser cookies or sign-in, using the existing reject-consent
headers. The page adapter does not execute JavaScript or render remote HTML. It extracts recognised live metadata under
strict response/depth/work bounds and constructs canonical provider links. Checks are opt-in, visibility-bound and
cached; third-party page changes can interrupt detection. E2E uses synthetic observations and intercepts browser/auth
actions; it cannot certify real account login, credential-vault operations or account revocation.

Optional Google connection requests `youtube.readonly` solely to list subscriptions. It does not request write/chat
permissions or change the user's subscriptions. Desktop configuration import accepts only bounded installed-client
fields; token/API destinations are fixed in code. Browser OAuth uses S256 PKCE, fresh state and a temporary loopback
listener bound to `127.0.0.1`, checking host/path/state and rejecting duplicate parameters. The listener is bounded by
time/request limits and closes on cancel/navigation/exit. It serves only static text without reflecting OAuth values.
Provider HTTP refuses redirects and bounds parsing/pagination. Tokens/codes/raw provider errors never enter logs or AI.

Google access tokens stay in memory; bounded refresh tokens and Desktop client configuration use separate read-back-
verified Credential Manager entries under `AquaHub/youtube.google.*`, scoped by a hash of the profile's database path.
The installed client secret is not a confidential server credential and is never put in settings JSON. Imported names
and channel IDs are unencrypted private local data in `account:youtube-subscriptions`; only explicitly selected live
channel IDs/names enter settings. Neither catalogue nor live observations enter feed search or automatic AI context.
Manual sync preserves successful cached data/time on transport failures and reports partial lists at 1,000 entries.
Disconnect clears the refresh credential, imported catalogue and Google-only selections before requesting revocation;
the client configuration remains for reconnecting. Manual upload channels remain. Stale completions cannot restore
removed data. Local deletion follows normal SQLite/settings semantics; it is not secure erasure of historical file bytes.

Publishing this source does not publish the user's runtime OAuth tokens. Twitch explicitly treats client IDs as public;
Google Desktop clients are public clients whose installed-client secret cannot be kept confidential. An identifier alone
does not authorise account access, and PKCE binds Google's authorization code to the initiating request. Public clients
do not prove the authenticity of an arbitrary distributed binary; user consent and trusting the app remain necessary.
Keep personal client exports outside Git even though the Desktop client fields are not account access tokens. Common
`client_secret_*.json` and `credentials.json` filenames are ignored; this does not detect every renamed export or remove
anything already committed. Access/refresh tokens and confidential web-app secrets must remain private.

Google's consent branding can expose the configured user-support email to people authorising the client. Use an
appropriate project contact for a shared app; a public client ID is not a guarantee of anonymous publisher identity.
Google documents that display in its [branding guidance](https://support.google.com/cloud/answer/15549049).
Aqua requests no Google email/profile identity scope, but `youtube.readonly` grants broader YouTube read access than the
subscription-list endpoint Aqua actually calls. Cached channel choices/subscriptions can reveal interests if the local
profile/database is shared or committed. Windows Credential Manager does not defend against malware running as the
same Windows user. These are implemented boundaries and remaining risks, not a claim of zero vulnerabilities.

### Chats and generated images

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

Personal reminders store plain text in the local profile database. The reminder and its bell entry are readable by
the signed-in Windows user. A visible notification can expose the title through Windows notification settings.
The scheduled effect is an Aqua alert with internal navigation to Upcoming and a Windows tone when notifications
and sound are allowed. Personal reminders stay outside calendar ingestion, Ask context, feed search and external
requests. An explicit delete or one-off completion
removes the live record and its pending bell entry; normal SQLite/WAL retention applies to their historical bytes.
Deck position and visibility are local profile settings. Quick timer duration is optional metadata on the local reminder.
Pinned app bubbles invoke the existing Launchpad app launcher on explicit activation with the current pinned entry.

An optional scene ID is a reference to an existing saved Launchpad scene. Due processing can create the alert.
Execution requires a separate human review and **Run now** in Upcoming, with the current scene and configured
targets checked against the review. Removal or replacement invalidates a stale action. The local occurrence claim
prevents an accidental duplicate run. Scene actions use the existing configured Launchpad targets and effect guards.

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
