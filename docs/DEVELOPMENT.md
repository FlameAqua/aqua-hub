# Developing Aqua Hub

Start a fresh development chat with [MASTER_PROMPT.md](MASTER_PROMPT.md); the root [AGENTS.md](../AGENTS.md) points to that agreement.
This is the working guide for maintaining the existing WPF/.NET app. [Architecture](ARCHITECTURE.md) describes current
components; [Security](SECURITY.md) defines trust boundaries; [Roadmap](ROADMAP.md) owns incomplete work and priorities.
Keep those documents current with behaviour changes. Reviews/results belong in [SCORING.md](SCORING.md); separate
agent handoffs, screenshots, scratch binaries and run artifacts do not belong in `docs/`.

## Design changes around ownership

- A feature should have one owner for persisted state, one for its active operation, and a narrow UI-facing contract.
  Add data shapes, loading/empty/error/cancel states and stable automation IDs alongside that contract.
- Keep parsing, ranking, validation and other decisions in Core. Windows effects belong in adapters; code-behind should
  translate interaction into service calls. Supply dependencies explicitly to new feature services instead of reaching
  further into the static `Hub` registry. Refactor a touched seam in small steps rather than reorganizing the whole app.
- Reuse settings validation/merge, the agent runtime, source health and the GPU gate. Document whether an operation joins,
  replaces or queues existing work; neither repeated clicks nor a refresh timer should silently duplicate it.
- Publish replacement snapshots. Keep stable item IDs and UI rows; update changed properties/sections. New collections
  and query results need bounds or paging, especially file indexes, galleries and long chats.

## Interaction and feedback

- Use layout, spacing and contrast to make the primary action and its input obvious. Group related controls and reveal
  secondary settings on demand. Use the existing theme resources and check the narrowest supported window.
- Keep action labels and information people need to make a decision visible. Put supplementary explanation behind a
  named hint icon or a control tooltip. Give icon-only controls accessible names and expose important help through UI
  Automation. Mouse hover, keyboard focus and activation should all make help discoverable.
- Use `HintButton` for help that opens on click or keyboard activation. Its expanded hint dismisses on outside click,
  Escape, lost focus, navigation or window deactivation. Keep privacy and data loss warnings alongside the setting.
- Route contextual Settings links through a section and, when helpful, a specific row. Prefer an existing action or
  a link near the task. Search results and links should expand the destination when necessary and focus a usable input.
- Show transient progress, validation errors and confirmations in a pop-up over the owning surface. Use `FeedbackPopup`
  for short notices and an overlay outside the layout flow when progress needs its own Cancel action. Keep focus in the
  current task, provide dismissal and announce changes through live regions. A notice appearing or clearing must leave
  the position of the surrounding controls the same. Keep real content such as a gallery item, a transcript, a result
  list or a recorded compatibility observation in its view.

## Async work and resource lifetime

The dispatcher is for presentation. Keep HTTP, GGUF/document/image parsing, hashing, directory scans and process waits
off it. Await async calls; do not use `.Wait()`/`.Result` from UI paths. Notifications crossing from Core/platform callbacks
must marshal through the app boundary before updating observable UI state.

Every long-lived operation needs an owner, cancellation token, completion/error observation and shutdown order. Track
workers instead of unobserved fire-and-forget tasks. Cancel on replacement/navigation when work is no longer useful;
discard stale results with a request identity/version. Dispose cancellation registrations, subscriptions, timers, streams,
HTTP responses and leases. Subscription setup and teardown must be symmetrical, including anonymous callbacks.

Use async disposal or a bounded explicit shutdown path for services with active workers: stop scheduling, cancel/join
workers, then dispose the clients/storage they use. Never hold a UI thread or a lock while waiting for callbacks that
need it. Existing lifecycle gaps are tracked in the roadmap; this is the standard for new/touched services.

An operation's result must distinguish success, unchanged data, waiting, failure and cancellation. Cancellation is not a
new error alert. Preserve known-good cached data with its timestamp; do not present stale content as a successful refresh.
Use bounded retries only for recoverable failures. Compatibility, capacity, authorization and transport failures need
different recovery paths. Uncertain external job ownership is a recovery state, not permission to retry blindly.

## Testing strategy

Choose checks by the boundary changed, not by a desired test count.

| Boundary | Useful evidence |
|---|---|
| Pure policy/parser | Named input/output cases, malformed limits, culture and refusal cases |
| Storage/settings | Temporary profile/database, round trips, migrations, concurrent edits, deletion during active work |
| Network/provider | Injected handler/loopback fake, bounded bodies, redirects, timeouts, cancelled reads and bad responses |
| Scheduler/lifecycle | Controlled start/finish signals, multiple callers, cancellation/shutdown, exactly one operation/lease |
| Windows adapter | Narrow fake plus isolated integration evidence for effects/handles the fake cannot establish |
| WPF/UI contract | Accessible names/IDs and service-state mapping; rendering/interaction needs desktop verification |
| Live model/Comfy | Exact artifacts, settings, GPU/driver, workload and cleanup evidence; never inferred from HTTP fixtures |

For concurrency checks, use controllable completion signals and an injectable clock/time boundary when refactoring the
service. A fixed delay is an outer hang guard, not proof that a task has started. Do not make a failing assertion pass by
removing it, weakening its outcome, adding arbitrary sleeps or rerunning until green. Capture the failed evidence and
distinguish a product race from an unreliable fixture before changing either.

Use deterministic fake content/network by default. Live tests must be opt-in, identified as such and skipped explicitly
when prerequisites are absent. Test a failure followed by recovery and reuse, not just the happy path or a private helper.
Source/XAML string checks cannot replace behavioural checks. Current CI's count floor guards test discovery; it is not a
coverage measurement. A coverage baseline/floor and focused Windows/WPF seams are still backlog work.

## Routine checks and isolated builds

Run from the repository root, using the normal test output location (UI-contract tests find `AquaHub.sln` above it):

```powershell
dotnet test tests/AquaHub.Tests -c Debug
./scripts/check-docs.ps1
git diff --check
```

Do not redirect the unit suite's output to an unrelated folder. If the sandbox prevents its test host from connecting,
report that separately from a test failure and rerun with the required execution permission. A successful local run does
not supersede a failed CI run. Compare the commit, build configuration, SDK and environment before claiming a fix.

For an app build, keep output separate from the owner's running copy:

```powershell
$aquaScratch = Join-Path ([IO.Path]::GetTempPath()) ('aqua-hub-' + [guid]::NewGuid().ToString('N'))
dotnet build src/AquaHub/AquaHub.csproj -c Debug "-p:OutDir=$aquaScratch\app-build\"
```

Never run `scripts/build.ps1` or overwrite `publish` during this development workflow. Do not launch the installed app
or change the owner's profile to validate work. A live probe, when authorized, uses the scratch executable with
`--e2e --data-dir <scratch-profile>`, Ollama management/start-on-demand disabled and PC access off or limited to an empty
scratch folder. Copy installed engine files into scratch rather than running engines from the owner's profile.

`--e2e` is a collection of action intercepts, not an OS sandbox: model subprocesses and some reads can still be real.
Inspect the paths being exercised. Desktop automation probes use UI Automation patterns, with no synthetic keyboard or
mouse. Do not run the full legacy E2E suite or opt-in live tests as a routine regression check. Model/runtime/Comfy model downloads
need Adrian's explicit OK; no microphone recording, registry/vault/settings writes or owner-service control in probes.
Stop only processes started for the probe, identified by their scratch command lines, never by executable name alone.

CI first checks Markdown encoding, relative link targets and archive locations, then compiles the app and E2E project, runs Release unit tests plus Thai/Saudi culture passes and audits dependencies.
Runtime/normal unit dependencies restore locked; the E2E project currently restores its centrally pinned dependencies
without a lock file. Do not describe a compiled desktop harness as a passed desktop run. Keep test artifacts in temporary
or dedicated test-run locations rather than repository documentation.

### Off-screen visual checks

Prefer the unit suite and off-screen renders while Adrian is using the desktop. The focused Settings and Deck render
opens an inactive window outside the display area. It skips first-run discovery, agents and media setup. It uses a
new or empty scratch profile and disables AI, Ollama management, PC access and remote images in that profile.

```powershell
$aquaProfile = Join-Path $aquaScratch 'settings-profile'
$aquaCaptures = Join-Path $aquaScratch 'settings-captures'
$render = Start-Process -FilePath (Join-Path $aquaScratch 'app-build\AquaHub.exe') -WindowStyle Hidden -PassThru -Wait `
    -ArgumentList @('--e2e', '--data-dir', "`"$aquaProfile`"", '--snapshot', "`"$aquaCaptures`"", '--pages', 'settings-ui')
```

Check the exit code, `error.txt` if present and the eight PNGs. The render verifies disclosure and related Settings
navigation, and captures light and dark layouts. Physical drag, hover, focus, screen readers and native pop-up
dismissal remain desktop acceptance. Coordinate a visible desktop run when its interaction coverage is needed.

### Targeted desktop components

Adrian authorized modular E2E coverage for Phase 4 on 5 November. Use `scripts/e2e.ps1 -Component <name>` to run only
the affected UI/service contract; the default `Phase4` alias selects Models, Runtime, Ask, Images, Roleplay and Lifecycle.
The separate `Streams` component covers 5.3. On 6 November Adrian also authorized focused unit/provider tests alongside
E2E for this stage; the earlier test-authoring restriction does not apply to the new stream work.
The separate `Reminders` component covers the Phase 7 deck, day agenda, quick reminders, scene confirmation and
intercepted alert and scene effects. Select an individual case with `-Component Reminders -Filter T02`. Actual Windows notification delivery,
quiet hours/fullscreen, screen-reader use and physical sleep/resume remain manual acceptance.
`./scripts/e2e.ps1 -Component Creativity -Filter T01 -Root <scratch-parent>` runs the inactive, off-screen
read-aloud and personality flows. It uses the app's speech intercept, local template data and UI Automation button
patterns. The render exercises dark and light themes, narrow width, saved personality editing and scene creation.
Audio devices, physical interaction, model output quality and real concurrent engine resource use require separate checks.
`./scripts/e2e.ps1 -Component Recipes -Filter T01 -Root <scratch-parent>` uses fixture Windows readings in an inactive
off-screen window to verify the Workbench and Agents path, the explicit confirmation, cancellation, run selection and
the This PC status display. Real Windows reads, native deletion dialogs and physical accessibility remain acceptance.
`-Component Ask,Images` unions groups; `-Filter A24` or `-Smoke` narrows them. `-ListComponents` lists scope, `-Plan`
resolves the filter without building, and `-ListTests` discovers the selection without launching Aqua or creating profiles.
CI runs `scripts/check-e2e-selection.ps1`; it compiles, but does not execute, desktop tests. Dependencies must already
be restored; the runner uses `--no-restore`. `-NoBuild` explicitly reuses both builds and is appropriate only when current.

Offline component fixtures use UIA patterns, a fresh baseline/copy per run/class, disabled AI/global hotkeys/Ollama
management/collectors/media/updates and owned random-port HTTP fixtures. Synthetic input APIs throw in offline runner
mode. Offline fixtures included in a legacy/full run start empty instead of copying the legacy warm profile. Existing
Aqua processes are preserved; a process using the selected scratch build blocks rebuilding it. Cleanup
signals only process objects created by that fixture, not a PID/name scan. Paths under owner profiles, source/docs/publish,
drive/repository ancestors and linked roots are refused; copied profiles also refuse linked entries. Failed sessions retain
their own artifacts. The picker opens the scratch profile and avoids Recent files in E2E mode.

`-All` explicitly selects the full suite; `-Legacy -Component Settings` selects a legacy area. These retain interactive
warm-profile setup and may use desktop input, network or configured services. They require the owner's Aqua closed and
an idle desktop, and are not part of routine component verification. New offline fixtures keep offline app startup even
when included in `-All`. `--e2e` and the offline flag remain application guards, not an OS/network sandbox.

Each execution writes uniquely named scratch results: TRX, `results/run.json` (selection, app/Core/test assembly and
executable SHA-256 hashes, executed/passed/failed/skipped counts and elapsed time excluding builds), journals/logs and
failure screenshots. Empty or entirely unexecuted selections fail. Report fixture evidence separately from actual
engine, CPU embedding, Comfy generation/cancel/unload/reload, clipboard/drop and performance acceptance. A paging/rendering
test is not a latency or leak benchmark; an idle/free HTTP response is not proof that physical GPU memory was released.

## Performance acceptance

Measure before changing GC settings, adding broad caches or retaining more UI objects. Record Release build/SDK, machine,
profile size, warm/cold state and repeated samples. Separate Aqua private bytes/working set from engine CPU/RAM/VRAM;
an idle working-set trim is not evidence that live objects have been released.

Initial targets for Phase 4 measurement (not yet verified): visible theme changes within 100 ms at p95; ordinary UI
state updates within 50 ms at p95; responsive scrolling/cancellation for 300-message chats with long Markdown. Measure
dispatcher stalls and memory across repeated open/close/navigation cycles; establish an acceptable retained-memory
range from a current .NET 10 Release baseline before adding a numeric ceiling. Model throughput is hardware/workload
dependent: compare identical model/hash, context/cache, prompt, MTP and driver rather than enforcing a global tok/s value.

Reminders reuse the existing one-second scheduler tick. Idle reminder overhead is one deadline/revision check with no
disk I/O until the next due time, midnight, an explicit clock signal or retry. At most eight operations queue or run
together, with up to 200 schedules and one outstanding occurrence per schedule. A 200-schedule scratch profile with
a busy day and a batch of due occurrences is the representative load for measuring foreground latency and idle I/O.
The existing 50 ms p95 UI update target is still unmeasured for that workload. Indexing should define its own budgets
and reconcile changes incrementally with bounded work and a recovery scan.

For a repeatable offline UI measurement, build Release into scratch, then run that executable with
`--e2e --data-dir <empty-scratch-profile> --snapshot <scratch-results> --measure --pages performance`.
This diagnostic requires both isolation arguments and disables AI/Ollama management/PC access in that scratch profile.
It starts no collectors, media session, model or server and writes only `performance.txt`, not screenshots. It renders
300 synthetic messages, samples 20 theme changes and five navigation/show/hide cycles, reports full-render time as well
as first layout, dispatcher queue delay and managed/private/working-set memory. Full GC is explicit and included in the
workload; working-set trimming is not used. This is a diagnostic, not a desktop E2E or model/GPU acceptance result.

### Search verification

The normal unit project exercises grants, extraction, indexing, retrieval and cancellation. For the modular desktop
runner use a disposable location on fixed local NTFS, outside application data and the Aqua profile. The Search fixtures
must satisfy the production file policy. `./scripts/e2e.ps1 -Component Search -Filter T04 -Root <scratch-parent>`
runs the inactive off-screen renderer, including theme/width, real lexical retrieval, dry-run result actions and
revocation. T01 through T03 use the interactive desktop. Coordinate those separately.

For a real CPU retrieval or scale run, set `$aquaScratch` to an isolated build directory. Supply the already approved,
pinned GGUF and CPU x64 ZIP by path. The probe validates both artifacts and creates a fresh synthetic corpus, model
library and profile for each invocation. Each run keeps its own model copy. Use the project build, never publish.

```powershell
dotnet build tests/AquaHub.SearchProbe -c Release "-p:OutDir=$aquaScratch/search-probe/"
& "$aquaScratch/search-probe/AquaHub.SearchProbe.exe" quality $gguf $cpuZip $ntfsScratch
& "$aquaScratch/search-probe/AquaHub.SearchProbe.exe" performance $gguf $cpuZip $ntfsScratch
```

Quality uses ten labeled paraphrases among 30 distinguishable files. Aim for at least eight correct files in the first
three results and zero hits for the three unrelated controls. The probe also reports five held-out queries and
checks whether it can surface similar drafts together and distinguish the requested month. Use the unit corpus for filename/path precedence,
spelling, passage matching and changes. The scale workload uses 2,000 local files, 1,000 passages and 1,000 renames.
Release targets on the reference Ryzen desktop are 30 seconds for names/text, five minutes including embeddings,
250 ms p95 warm hybrid service latency, 50 ms p95 input-priority dispatcher delay, and 30 seconds to reconcile the
burst. Allow 64 MiB for the active workload's index and 800 MiB private memory for the CPU server.
After server unload, compare five idle minutes against 1% of one core, 32 MiB retained private growth and zero index
commits. Process I/O also includes runtime activity. For GPU coexistence, compare the same generation model, prompt
and settings with CPU indexing enabled/disabled, looking for uninterrupted residency and throughput within 10%.
Record hardware, build, workload, timing boundaries and pending physical checks in [Scoring](SCORING.md).

## Documentation, files and release hygiene

Keep maintained docs focused on contracts, current policy and open acceptance tasks. Remove dead links when deleting
artifacts. Do not check in user profiles, screenshots, logs, temporary build output or credentials. The archive directories
are ignored to discourage reintroduction. Deleting current files does not remove their bytes from existing Git history.

Repository text uses UTF-8 and LF. Use byte-preserving edits or explicit UTF-8 byte writes; do not let PowerShell defaults
convert whole files to CRLF/UTF-16. Before any recursive delete/move, resolve absolute targets, verify they remain within
the intended workspace, and reject unexpected links. Use native PowerShell file operations end-to-end.

Adrian handles commits, pushes, tags and releases. Check `git status`/`git log` and the latest GitHub runs before reporting;
he may commit during a session. Never stage/reset or discard another person's work. Keep an engineering review's findings,
changes and evidence separate from an independent score; no self-assigned replacement score is implied by a fix.
