# Aqua Hub project instructions

Read [the master prompt](docs/MASTER_PROMPT.md) before planning or changing this repository. It defines the working
agreement for fresh chats, including design communication, implementation, self-review, verification and completion.
Follow its links to the maintained docs and relevant source; do not treat old chat summaries or review scores as current state.

Adrian's current task and explicit decisions set scope. Routine authorized work should proceed without repeated permission
requests. Product decisions with material consequences should be explained and resolved with him before dependent work.

Adrian owns commits, pushes, tags and releases. Preserve existing working-tree changes. Protect his running app, profile,
Ollama and ComfyUI; use isolated scratch builds/profiles for probes. Downloads and public publishing need his explicit OK.

Self-review the final diff, run the checks appropriate to the change within current test permissions, and distinguish
observed results from assumptions and remaining acceptance work. Keep the roadmap and relevant contracts current;
do not create handoff or screenshot archives in `docs/`.

## Interface design

- Make the main input and action obvious through size, position, contrast and spacing. Group related controls and
  reveal secondary options on demand. Keep labels concise and remove copy that repeats what the controls already show.
- Put supplementary explanations behind accessible hint icons. Keep privacy, permission and data loss warnings visible
  where the user makes the decision. Clicked help must dismiss with Escape, an outside click or navigation.
- Show transient errors, progress and confirmations as dismissible pop-ups that leave nearby controls in place. Keep
  lasting content, such as results and account connection state, in the view.
- Use Aqua's native theme and keep icons legible over busy content. Verify hover, focus and pressed states, dark and
  light themes, narrow windows, keyboard access and reduced motion. Use opaque surfaces and contrasting foregrounds
  for floating controls where transparency would make them hard to read.
- Place contextual Settings links near the task and take the user straight to the relevant control. Reuse existing
  actions or notices where possible to limit visual clutter. Shortcuts and scheduled workflows preserve existing
  permission and confirmation boundaries.

## Non-disruptive verification

Prefer unit tests and [off-screen visual checks](docs/DEVELOPMENT.md#off-screen-visual-checks) using scratch builds
and profiles. Coordinate interactive verification that would occupy Adrian's display. Report separately what was
verified off-screen and what needs physical input, native Windows behavior or live services.
