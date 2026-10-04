# Independent review rounds

An independent reviewer agent (separate context, read-only access) scores the product after each
iteration, using the original brief, the source, the test suite and headless screenshots of every screen.
The target is an overall score of **8/10 or higher**: four rounds for the original brief, then up to three more for
each batch of owner feedback. Round 1 scored
viability, usability and design; from round 2 the criteria are **functionality**, **usability**,
**implementation** and **design** (each /10, plus a holistic overall /10).

| Round | Viability | Usability | Design | Overall | Outcome |
|---|---|---|---|---|---|
| 1 | 8.0 | 7.4 | 7.8 | **7.7** | Below target → round 2 |

| Round | Functionality | Usability | Implementation | Design | Overall | Outcome |
|---|---|---|---|---|---|---|
| 2 | 7.0 | 7.6 | 7.7 | 8.2 | **7.4** | Below target → round 3 |
| 3 | 7.6 | 7.8 | 7.9 | 8.4 | **7.8** | Below target → round 4 |
| 4 | 7.7 | 7.9 | 7.6 | 8.3 | **7.7** | Owner feedback of 28–29 Sep reviewed; below target → round 5 |
| 5 | 8.1 | 8.1 | 8.0 | 8.4 | **8.1** | **Accepted** — the round-4 issues verified fixed in code, tests and live runs |
| 30 Sep batch | 8.1 | 8.3 | 7.8 | 8.6 | **7.9** | The one review of the batch (no further rounds, as agreed); its findings were then fixed — see the end |

## Round 1 → 2: what changed

The reviewer's 15 issues, and what was done about each:

| # | Issue (round 1) | Change |
|---|---|---|
| 1 | Quick panel could run off short screens | Height capped to the monitor's work area; header and command box fixed, body scrolls; re-anchors when its size changes. |
| 2 | Wrapped text clipped (market line, disclaimer, "why it matters") | Horizontal StackPanels with MaxWidth replaced by Auto/star grids everywhere. |
| 3 | News list jumped to the top on every summary | Rows reused by story id and updated in place; "N new stories" pill when scrolled down; editor publishes in batches. |
| 4 | Ask chat lost on navigation; Markets/Settings state reset | Conversation moved to an app-wide session (kept in the local DB, optional); visited pages stay warm while the window is open. |
| 5 | Palette query unreadable | Input sized for 18 px text (44 px tall, no vertical padding). |
| 6 | "Updated just now" always; no offline state | Per-area freshness from collectors' last successful fetch; offline banner with data time; automatic catch-up on reconnect. |
| 7 | Saving settings could revert other changes | Three-way merge of only the fields a form changed; forms follow outside changes. Unit-tested. |
| 8 | Pulse not local/personal | Global Bluesky trends kept out of the pulse (shown separately, labelled); optional Bluesky Following and Mastodon home timelines; editors for YouTube channels, extra feeds, Bluesky feeds, Kalshi categories and the AI server key. |
| 9 | AI claims not checked | Session-aware market digests, weather units, Foresight odds/favourite checks, duplicate-key merge. Unit-tested. |
| 10 | Wasted UI work; dishonest memory claim | Section-only property notifications; cached flyout lists; inspector keeps open entries; measured footprint published; idle memory trim. |
| 11 | Empty columns; noisy predictions | Predictions full width; market brief in the right column; date outcomes chronological, past/resolved ones hidden; leading outcome emphasised. |
| 12 | Copy/polish | "just now"/"5m ago", plurals, "Sep" not "SEPT", friendly housekeeping text, "AI calls since start", styled-Unicode posts folded to plain text, round chart ticks, sharp artwork, neutral scene steps, standard Run buttons. |
| 13 | Keyboard & accessibility | Arrow/Enter/Esc in every search popup; hotkey recorder; quiet-hours time pickers; AA contrast in light theme (and for any accent); charts and gauges describe their values to screen readers; bell-off icon for do-not-disturb. |
| 14 | Tray presence | Onboarding tip + button to pin the tray icon; optional market trend on the tray icon (the formerly unused ticker setting). |
| 15 | Loose ends | Settings UI for all JSON-only options; held toasts implemented as documented; Wind down scene really reads the brief; locked restores; signed-package requirement. |

## Round 2 → 3: what changed

The round-2 reviewer's 15 issues, what was done about each, and the extra work done in the same round:

| # | Issue (round 2) | Change |
|---|---|---|
| 1 | False "cross-checked" local news (Dublin, California merged with a South Africa story) | Local search qualified with the country and filtered to local publishers; clustering checks the story's core words (no chaining) and never links stories by place names alone; "local" needs most of the coverage; one count per publisher. Regression tests use the real headline pairs. |
| 2 | Fact checks nearly vacuous | A quoted percentage must belong to the market the sentence is about; pulse posts must share a word with their topic; market prompt told that a listing suffix isn't what a fund holds. Tested. |
| 3 | Stale AI output when the model is down | AI text expires (pulse 6 h, Foresight 12 h, market view same trading day) and falls back to the deterministic view; stamps carry the day. |
| 4 | Media state never cleared | Title, artwork and play icon reset when the player closes; quick panel shows "Nothing playing · Play something on …"; a title-less idle session is "nothing playing", not "Unknown title". |
| 5 | Card subtitles hard-clipped | Card header is a grid: title and badge keep their size, the subtitle trims with an ellipsis and a tooltip; market subtitle shortened ("Closed · Fri close"). |
| 6 | Defaults hard-wired to Ireland | Picking a city abroad applies a locale pack: local outlets, subreddits, hashtags, keywords, holidays, stock index, units, economic currency — keeping anything you added. Tested both ways. |
| 7 | Ask dead-ends offline | Offline (or paused) Ask answers from what the agents collected, with numbered sources; the subtitle follows the model's status live. |
| 8 | Unfilled footprint tables, missing screenshot, silent live test | Tables filled from measurements (`--measure`); screenshot added; the live test reports *skipped*. |
| 9 | "Pause media" could start playback | Does nothing when there is no media session. |
| 10 | This PC memory figures disagreed | Process memory is the private working set (as Task Manager shows); sortable by the visible CPU or Memory column; CPU bars have a track. |
| 11 | "▲ 0 pts" | Rounded away from zero; changes that round to nothing are hidden. |
| 12 | Empty/degenerate states | Model-output inspector empty state; agent footers without stray separators; empty "Installed models" hidden; Free VRAM disabled without a server; "Get Ollama" hint. |
| 13 | Markets header | Shorter search placeholder; live indices named ("FTSE 100, ISEQ") instead of Yahoo exchange codes. |
| 14 | Clouds invisible in light theme | Cloud colours come from the theme. |
| 15 | Some settings saved only on focus loss | AI endpoint, keep-alive and Mastodon instance save as you type. |
| — | Taskbar icon features | Jump list; thumbnail media buttons; unread-alerts badge; tray double-click (dashboard) and middle-click (play/pause); multi-line tooltip; tray menu with alerts, media, scenes and Ollama; `--page`/`--read-brief` forwarded to the running instance; quick panel's PC strip and next event open their pages. |
| — | Memory leak on window close | A leak probe found every closed dashboard stayed in memory (a WPF-internal handle); the dashboard and quick panel are now hidden and reused (memory flat across open/close); double subscriptions when re-navigating to the shown page fixed. |
| — | Local model management (owner feedback) | Turn Ollama on/off from the sidebar popup, This PC, Agents, the tray and Settings; it stops while a game needs the GPU and restarts afterwards, stops after N minutes unused, starts when you ask; a self-dismissing "Local AI is off · Turn on" notice. All configurable; policy unit-tested. |
| — | Owner feedback: input caret | The caret sat 10 px right of the placeholder because the padding was applied twice; placeholder, caret and text now start at the same x (measured). |
| — | Owner feedback: Launchpad | Apps can be removed (right-click → Remove from Launchpad) with Undo. |
| — | "Closing keeps Aqua running" did nothing | The switch now works: off means closing the window quits. |

## Round 3 → 4: what changed

| # | Issue (round 3) | Change |
|---|---|---|
| 1 | The fact checker substituted another market's lead ("Saudi pipeline" → the Fed's "25 bps increase", matched through "October") | Market matching weighs words by how few market titles use them, ignores months, weekdays and generic words, and needs a clear winner (≥1.5× the runner-up). An item is replaced with a market's lead only when its own title clearly names that market; otherwise just the unsupported sentence is removed. Tests use the real market list of 28 Sep. |
| 2 | "Madonna tops VMAs" still labelled Local | Local needs most headlines to name the area, or the lead headline (or the lead local outlet's own summary) to name it with most *publishers* local — counted per outlet. Test uses the real five-article cluster, including the "Irish actor at the VMAs" piece. |
| 3 | Asking didn't start a stopped Ollama | Ask starts it first (when on-demand start is allowed and you didn't turn it off), and the Ask page has a "Turn on local AI" button when it's off. |
| 4 | Stopping could interrupt other programs; docs overstated safety | An idle stop waits while another program has a connection open to the server or it is downloading a model (TCP connection table); the tray app is stopped only if it runs from the same folder as ollama.exe (checked by path). SECURITY and ARCHITECTURE corrected. |
| 5 | Manual on/off not respected | Your Turn off holds (no on-demand, scheduled or post-game start, and no "turn on" notice) until you turn it on; your Turn on during a game holds until the game ends. Policy tested. |
| 6 | Offline Ask ignored structured data | Offline answers list your agenda, the crowd's odds, markets and weather for those questions; news is cited only when it matches two of the question's own words. Tested. |
| 7 | Brief bullets filed under the wrong section | Each AI brief bullet is compared with what each section kind was fed; one that clearly belongs elsewhere moves there (or is dropped). A section's kind comes from its title first ("Social Pulse" is local chatter whatever icon the model gave it), then its icon; an unchanged brief is re-checked too. Tested with the Le Pen example. |
| 8 | Locale-pack leftovers | Every shipped Irish outlet (incl. Silicon Republic) is switched off abroad; an index you added yourself is kept. Tested. |
| 9 | Stale ARCHITECTURE §7 | Rewritten for the current `--measure`. |
| 10 | Raw URLs as post titles | Social post titles drop links ("RE: https://…"), fall back to the linked card's title or site, and use the first sentence when long. Tested. |
| 11 | Empty message line on agent cards | Collapsed when empty. |
| 12 | Launchpad removal only by right-click | Visible Edit mode on the Apps card: each tile gets a remove badge (its own button for keyboards and screen readers); Undo as before. |

## Owner feedback, 28 Sep (evening)

| # | Feedback | Change |
|---|---|---|
| 1 | "Tell me more about: SpaceX Starship…" answered that the context had nothing on it | Root cause: Ask's retrieval asked the database for items published *after now + 14 days*, so it never found anything. Fixed (regression test), and *Tell me more* now passes the story itself — summary, key points, every outlet's headline, snippet, time and link — plus the text of the newest readable articles. Live: the same question reads Ars Technica and TheJournal.ie and answers with cited sources in 4–7 s. |
| 2 | Select, copy and probe answers | Answers are selectable rich text: copy any part, right-click → *Ask about this* / *Search the web for this* / *Copy the whole answer*; under each answer *Copy*, *Ask again* and *Dig deeper*. |
| 3 | Attach screenshots and documents | Paperclip, screenshot menu (this screen with Aqua hidden, *Snip an area…*, paste an image), Ctrl+V of images or files, drag and drop. Text, Markdown, CSV, JSON, code, HTML, Word, Excel, PowerPoint, OpenDocument, RTF, PDF (Windows OCR) and images (vision models see them; OCR for others). |
| 4 | Look through and use my computer | *Use my PC*: search and read files in the folders you allow, list folders, screenshots, system status, the clipboard, and — with Allow / Allow for this chat / Don't allow — open files, links and apps or control media. |
| 5 | Browse the internet; find information itself | *Web*: DuckDuckGo (or SearXNG, or Brave with a key), Google News for recent events, Wikipedia as a fallback, and an SSRF-safe page reader. |
| 6 | Modes: think deeper, research | *Think* (reasoning shown collapsed) and *Research* (planned searches, several pages read, a cited report). |
| 7 | Use what the agents already found | `search_hub` / `get_story`: stories with summaries and every outlet, articles and posts (30-day index), the pulse, odds, agenda and quotes — with links and citation numbers. |
| 8 | Settings and command palette | Settings › *Ask Aqua*; palette: *Ask about my screen*, *Ask about a file…*, *Research a topic*, *Find a file on my PC*, *Research: “…”* / *Search my PC: “…”*; the tray's *Ask about my screen*. |
| 9–10 | Ars Technica; Gamers Nexus | Ars Technica was already a source (now also matched by the News filter); Gamers Nexus added to News (its RSS) and Social (its YouTube channel), once, for existing profiles too. |
| 11–14 | Chart hover price; empty Social tabs; @handle YouTube links; News header on wide screens | Exact prices with cents on hover; each empty tab explains why and offers the fix; @handle, /c/, /user/ and video links resolve (name and logo fetched); the header shares the cards' column. |
| — | Found while testing | A web search or link carrying details from your calendar needs your OK; offline answers include web results when Web is on. |

## Owner feedback, 29 Sep

| # | Feedback | Change |
|---|---|---|
| 1 | Keep many chats; expire them unless starred | Every chat is kept (local database) in a searchable history panel — grouped Starred / Today / Yesterday / Previous 7 and 30 days / Older — with star, rename and delete, a title from the first question (renamed by the model after its first answer), and *Delete chats after* (7, 30, 90 days, a year or never; 30 by default) for unstarred ones. An answer keeps streaming in a chat you've left. The old single conversation is imported once. Palette: *New chat*, *Chat history*, and earlier chats by title. |
| 2 | Modern chat features: context usage and settings | A context meter under the Ask box (last answer's tokens against the window) with a popup to set the window (automatic, 8K–64K) and how many earlier messages are sent; a fixed window leaves out the oldest messages first and says so in the footer. |
| 3 | Microphone | Dictate into the Ask box — offline with Windows' own recognizer by default, or Windows online speech if you choose it (Settings › Ask Aqua › Voice); stops after a pause; optional *Send when I stop speaking*. Answers can be read aloud. |
| 4 | Deeper computer integration: open and interact with apps | *Let Ask operate apps* (with Use my PC): list windows, read a window's controls (UI Automation), switch to it, click buttons/links/tabs/menu items, type into fields, press shortcuts, close windows — each with your OK, always again after reading web pages; never Aqua's own windows, terminals, security prompts, password managers or password fields. *launch_app* opens any Start-menu app or a Windows Settings page; *run_scene* runs your scenes and *do_not_disturb* quiets Aqua's notifications (so a "focus time" skill can do both). |
| 5 | A workbench to teach Aqua | New **Workbench** page: describe a skill in your own words and the local model drafts it from Aqua's own tools (name, when to use it, example requests, instructions, fixed steps with values filled in from your request); check it, *Improve* it from feedback, *Save and try it* in a new chat, switch it off, edit or delete it. Skills are used when a request fits (the planner picks them). **Memory**: facts Aqua keeps in mind in every answer — added there or by saying "remember that …" in Ask ("forget …" removes them). **What Aqua can do** lists every tool and what it needs. |
| 6 | Can't copy or edit my own question | Questions are selectable text with visible *Copy* and *Edit and ask again* (in place: the question and what followed are replaced); ↑ in an empty box edits the last one; the Ask box has Cut / Copy / Paste. |
| 7 | Web searches the question verbatim; claims it can't open a link | Ask now **plans** first (one fast JSON call, shown as the first step): 1–3 keyword queries (never the sentence), links to open, a site to search within (Twitter → x.com), file-name words, a file kind, folders and what a picture shows. Links you give are opened; to search through a site it follows the page's most relevant links and adds a `site:` search. The system prompt says exactly what Aqua can do right now, so it no longer claims it can't browse. Live: "Find the Re:Zero leaker Ice on twitter and link me it" → *https://x.com/rezero_ice*; "Look through https://arstechnica.com/space/ …" → the newest SpaceX articles with links. |
| 8 | "Find me my logo photo of a blue flame" failed until guided | Three causes, all fixed: the search needed *every* word of the question in the file name (now any word, ranked by how many match); with a whole drive allowed it spent its time budget inside `C:\Windows` (system, program, app-data, cache and game folders are no longer walked, your own folders go first, and the Windows Search index is used); and it couldn't tell which picture was a blue flame (it now *looks* at the likeliest pictures with the vision model). Pictures joins the default folders. Live: finds `my_logo5.png` among decoys in one go, and from a follow-up ("it's the blue flame one, somewhere in my Pictures"). |
| 9 | Reasoning loop with no answer | Reasoning that runs past its budget or goes in circles is stopped and the answer written directly; empty or cut-off turns are answered again without reasoning; "The user is asking… Let me…" narration and "Thinking Process:" blocks move from the answer to the Reasoning panel; earlier answers are flagged as possibly wrong so the model corrects them instead of agonising. |
| — | Found while testing | A page that timed out could abort a whole answer (now it's skipped); a link the model writes out counts as a citation and is clickable; stopping Ollama from a profile on another port no longer closes Ollama's tray app. |

## Round 4 → 5: what changed

Round 4 reviewed both batches of owner feedback. Every example the owner gave now works live (the blue-flame logo,
Ice on X, searching through a site, keyword queries), but the reviewer found eleven issues — three of them high.
One disclosure: the reviewer's probe ran *Use my PC* with the default folders, so the local model read two of the
owner's own documents on this PC (nothing left it); round 5 runs with scratch folders only.

| # | Issue | Change |
|---|---|---|
| 1 | High: with a file attached and *Use my PC* on, "Summarise the attached notes in two bullets" searched the PC for "notes", read unrelated documents and mixed them into the answer | The planner is told what's attached; a message about the attachments ("the attached…", "this screenshot", or no request to find other files) searches nothing on the PC, whatever the model plans; "attached", "summarise", "bullets" and similar are never file-name words. Unit, agent and live tests with that exact question. |
| 2 | High: typed text and key presses weren't bound to the approved window | Before every chunk of keystrokes Aqua checks the approved window (or its own dialog) is really in front and not off limits; if Windows keeps something else in front, nothing more is typed and the answer says so. Single-line fields are set directly through UI Automation, without keystrokes. |
| 3 | High: a path to running commands (Explorer's address bar, the Run dialog, Start-menu shells) guarded only by approvals | File Explorer (and so the Run dialog), code editors with a built-in terminal, script hosts and more join the off-limits apps (`DesktopRules`, unit-tested); *launch_app* refuses shells, terminals, the Run dialog, installers, script files and admin tools. "After web" became "after anything untrusted": once an answer has read pages, results, feed items, files, the screen, app windows, the clipboard or attachments, typing, clicking, key presses and closing windows ask every time, even with approvals off. Typing that presses Enter and key presses are never "allowed for this chat". |
| 4 | Approval cards hid what would be sent; some web paths skipped approval | Cards show the whole query, address or text; a link opened in the browser follows the web rules (after private data, or carrying private details, it needs an OK); links the planner opens go through the same rules, and only links you gave (now or earlier in the chat) are opened. |
| 5 | Citations didn't reliably support the claims | Only feed items that name what the question names get a number; `SourceBook.Verify` drops any [n] whose sentence shares too little with what source *n* said; snippet-only sources are labelled (chip *· snippet*, footer "… (n only from search snippets)"); sources carry their date ("Mon 28 Sep 2026 (20h ago)") and the model is told to date events from them; shops and their SEO blogs are read only when nothing better turns up. Live: the NIRSpec answer now cites ESA and NASA (it had cited an unrelated article); *Tell me more* says "on Monday 28 September", not "today". |
| 6 | A found file couldn't be opened from the answer | A file named in the answer becomes a chip; file paths, `file:` links and paths in backticks are clickable (documents, pictures and media open with their app, anything else is shown in its folder, never run; right-click: *Show in folder*, *Copy the path*). |
| 7 | "Remember/Forget" hijacked ordinary messages | Only a single statement counts: "note" is no longer a trigger; a second request ("— what's the weather then", ", can you…"), a second sentence or "forget that, …" is answered normally; "forget …" that matches nothing is answered normally too. Memory answers offer *Undo* and *Answer it instead*. |
| 8 | Memories could reach the web unchecked | Names in what Aqua remembers join the private-details check for searches and links. |
| 9 | "Newest" articles weren't the newest | Links from a site are dated from their addresses and listed newest first when asked; queries drop "the newest articles about". |
| 10 | An unanswered approval held the model for three minutes | The model is released while you're asked and taken back after. |
| 11 | Copy and layout | *Tell me more* reports each article it read as its own step (the summary line counts them); the Ask box placeholder is "Ask anything…" (the keys are in its tooltip); the context-window option reads "Automatic"; feed furniture ("Read more: …") is never a summary. |
| — | Found while testing | With Web on, factual questions are looked up even when the model thinks it knows; questions about your own records search your files; the model is told never to claim it searched or checked something it didn't; citations written as “[PAGE 5]” become [5]. |

## Round 5 (accepted, 8.1): follow-up

The reviewer verified every round-4 fix in code, unit tests and live runs, and accepted round 5 at 8.1. Its remaining
medium and low findings were then addressed (no further rounds, as agreed):

| # | Finding | Change |
|---|---|---|
| 1 | "The newest articles … with links" on a given site listed one item, once Aqua's own brief headline | Root cause: listing pages link each article twice (picture, then headline) and the link reader kept the textless first one, so most article links were lost. Only links with words count now (regression test). Requests for articles, links, stories and the like are lists; the site's articles are listed one per bullet with the link copied exactly; Aqua's own situation summary is labelled "not a source" and today's brief is left out when you give a link; a made-up address on a site Aqua read is swapped for the real link with the matching title, or dropped. Live: three real, dated SpaceX articles with their links, newest first (the test now asks for at least three). |
| 2 | Research depth varied | Research always searches from at least three angles and reads at least four pages; "Where sources differ" only appears when two different sources disagree. |
| 3 | A denylist can't cover every app with a command runner | Settings › Ask Aqua › *Apps Ask may operate*: any app except the off-limits ones, or only the apps you list; approval cards name the window and its app ("“Inbox – Outlook” (OUTLOOK)"); Alt+F11 (Office macros), Alt+Tab, Alt+Esc, Ctrl+Esc and Ctrl+Shift+Esc are refused. |
| 4 | Links from Aqua's own earlier answers counted as links "you gave" | Only your own messages count. |
| 5 | Stray code fences around file paths | Fenced blocks render as code (paths in them open), a fence or `<code>` around a lone path becomes an inline path, and non-breaking spaces and a copied "[n]" placeholder are tidied. |
| 6 | "What's happening near me today?" mixed in news from elsewhere | Local stories come first (even when the question names nothing to search for) and are marked local; the model is told where you are and to say where anything else happened. |
| 7 | App tasks need an approval per step | Kept by design: after Aqua has read text that isn't yours (an email, a web page), each keystroke-level action asks, because that text could otherwise steer it. Without such text, "Allow for this chat" still covers repeated steps (except Enter and shortcuts). |
| 8 | Weak live assertions | The Think test checks the arithmetic (16:45, 16:55, ten minutes); the site test needs three distinct article links; the file test checks the facts from the file. |

## Owner feedback, 30 Sep

Fourteen items from the owner, worked through one by one with tests; then one review scoped to this batch.

| # | Feedback | Change |
|---|---|---|
| 1 | Is each chat's context kept when you come back to it? | Messages and the context meter already were; what Aqua *read* wasn't, so follow-ups lost it. Each answer now keeps short excerpts of its sources (saved with the chat for the last six answers) and a follow-up gets the relevant ones back as "FROM EARLIER IN THIS CHAT". |
| 2 | Screenshots only showed Aqua's own screen | *take_screenshot* takes a screen by number or an app window by name ("my second monitor", "the Spotify window"), lists the screens, and the Ask composer's menu offers each screen and window. |
| 3 | Workbench *Improve* failed with "invalid JSON" | The model copied the capitalised keys it was shown; the skill is now shown in the reply's own shape and any JSON reply's keys are matched to the schema whatever their case or style (regression test with the owner's reply). |
| 4 | Is the log capped? A log of just the problems? | The log rolls at 2 MB while Aqua runs (and keeps the 2 MB before); a separate problems log keeps every warning and error with its full exception and stack. Settings › **Debug**: open either log, copy recent problems, verbose logging, and a diagnostics summary to paste into a report. |
| 5 | Open a URL seen on screen when it matters | When a question is about the page on screen and Web is on, Aqua reads the address it can see — only then, and through the same approval rules as any link. |
| 6 | A long web-novel chapter was summarised only part way | "Summarise this page" reads the whole page: up to ~26k characters at once, longer pages in parts with notes on each part; the report format runs to the end, and *read_webpage* can fetch later parts. Live test with a full chapter. |
| 7 | Copy a whole chat to forward it | *Copy chat* (button, chat menu, palette) and *Save as Markdown*: every question and answer with its plan, searches, pages read, reasoning and sources. |
| 8 | Voice input: poor accuracy, no device choice, "requested language is not supported" | The online recognizer now dictates in the nearest language Windows offers (English (Ireland) → English (UK)); pick the microphone (Aqua records it and feeds the recognizer — verified with synthetic speech through the same pipe); phrases the recognizer was unsure of are no longer dropped; its other guesses are kept and **the local model tidies up** what was misheard, with *Undo* (after the review it only touches words that plainly don't fit — M1 — and the owner's own example needs **Whisper**, added last: see below); Settings has a live test (level bar, heard / after tidy-up) and Windows' microphone setup and voice training. Found on the way: stopping a stream-fed recognizer blocks until the audio ends, so a chosen microphone is stopped by ending its audio. |
| 9 | "Ask Aqua" on a story overwrote the latest chat | Every outside entry point (story, palette, tray, screen, file, research, This PC) opens a new chat. |
| 10 | Teach the agent to break problems down; more tools | A "how to work it out" section (pin down the question and each part, use what's known, compare sources, work numbers with tools, check before answering); exact *calculate*, *date_math* and *convert_units* tools; a query per part of a question; *check_pc_health* (below). |
| 11 | No music app in the Launchpad | Windows' default apps are used: the default music player for play/pause and the media keys, and "open my browser / email / photos / PDF reader" for *launch_app*. "Play …" without a Launchpad music app searches YouTube Music in the browser (Windows' player can't search for music). |
| 12 | Small windows hid the search box and "Turn on local AI" | The title bar's command box shrinks before anything overlaps; the Ask header wraps. |
| 13 | YouTube channels showed "YouTube Channel" and no logo | YouTube puts the name and logo far into the page; the reader now scans the whole head, with fallbacks, and fills in missing names and logos of channels already added. |
| 14 | Make This PC a 10/10 | **Health check** (drive space and health, Windows Update and pending restarts, blue screens and app crashes, antivirus, device errors, battery wear, startup apps, clutter — read-only, with a local-model summary, *Copy report*, *Ask Aqua about it*, and the right Windows tool per finding; Ask can run it). Top processes: select an app for what it is (description, maker, file), *Open file location*, *Search online*, *Ask Aqua*, and *End task* / *Close app* (asks first, closes windowed apps politely, never ends what Windows needs or Aqua itself), *Show more*, and the list holds still under the pointer. **Doctor's toolkit** of Windows' maintenance tools; each drive opens in Explorer or Disk Cleanup; the gauges link to Resource Monitor, Task Manager, graphics and network settings; the network card names the connection. Found on the way: hiding Aqua on This PC and bringing it back left Top processes empty. |
| — | Found while testing (this batch) | Settings › Debug's problem list used a style only the Ask page had (it would have failed on the first problem shown); expander headers were drawn in the system's black on the dark theme; *date_math* refused the times a model writes ("today 14:10", "2026-09-30T14:10") and "2h35m", so the live train-and-bus question fell back to mental arithmetic and got the bus wrong — it now reads times and gives the gap between two ("3 h 5 min"), and the live test is exact again. |

## 30 Sep batch: the final review (7.9) and what changed

One independent reviewer, scoped to this batch only (no further rounds, as agreed), ran the unit and live suites, a
snapshot with the health check and its own probes, all on scratch copies and the private model server. Aspects:
functionality 8.1, usability 8.3, implementation 7.8, safety & privacy 7.6, visual design 8.6, tests 8.0 — overall
**7.9**, just under the bar because of three medium defects it judged small to fix. Per item: context per chat 7.5,
screenshots 8.5, Workbench Improve 9, logs 8, on-screen link 7, whole pages 9, copy chat 8.5, voice 7.5, new chats 9,
problem solving and tools 8, default apps 8.5, small windows 8.5, YouTube 8.5, This PC 8.5. Everything it found was then
fixed:

| # | Finding | Change |
|---|---|---|
| M1 | The voice tidy-up rewrote correct speech ("The doc said I should rest" → "Testing! Said I should rest"; "whether" → "weather"): its instructions quoted the owner's own mishearings, and two live tests used those same phrases | The instructions give no example substitutions and say a sentence that reads naturally stays exactly as it is; only dictation the recognizer was unsure of is tidied (a phrase under 0.8 — never Windows online speech's "high"); a new live test gives four correct sentences with sound-alike words (the reviewer's two among them) as unsure and requires every word back. Live afterwards: all four come back word for word, "whats the whether…" still becomes "What's the weather…", and "what is the way the light in Dublin" becomes "what is the weather like in Dublin" (3 of 3 probes; once it was kept as heard). But the owner's own example is no longer fixed: the model's attempts at it drop or reorder words ("The Doctree"), the guard refuses them, and it stays as heard — its live test now checks only that it's never made worse. Whisper (below) is the fix for it. |
| M2 | 3,000 nested brackets crashed the app (stack overflow in *calculate*) | Expressions over 500 characters and nesting over 64 deep are a plain error; signs are read in a loop, not by recursion; and a sign now applies after a power, as in maths (−2^2 = −4). Regression test with the reviewer's input. |
| M3 | "Will it rain tomorrow?" brought back the last chapter and an old screenshot's text (any question of two words or with an "it" did), and screen text was saved with the chat for 30 days | An earlier source comes back only when the question's key words come together in it (within a few words — "heat pump grant"), it names what the question names, or the question plainly refers back ("the article", "what else did it say"). Screen and clipboard text have their own kinds and are never kept with a chat; SECURITY.md says what is kept. |
| M4 | The on-screen link could be one from the page body, not the address bar (browsers hide "https://" there) | Addresses are taken in reading order, top of the screen first, from the text read off the screen only; the tool lists them "top first". Test with the reviewer's case. |
| M5 | End task didn't refuse Ollama's model runner (`llama-server`) or Windows' background processes, as SECURITY.md said | Anything in Ollama's folder (its runners, whatever they're called) and the tray app are refused; so are Windows' background processes and background programs Aqua can't identify; Windows' own apps with a window are only ever asked to close (never forced) and the confirmation says they're part of Windows; only your own session's processes are touched; the rules are checked again at the moment of ending. SECURITY.md now says exactly this. |
| L1 | A crash that ends the app could miss the problems log (queued to a background writer) | `Log.Fatal` writes what's queued and then the crash straight to both files; the last-chance handler uses it. Test. |
| L2 | Long dictations lost their end (only the first 1,500 characters were sent; a reply less than half as long was accepted) | Long dictations are left as heard rather than tidied in part; a reply much shorter than what was heard is refused. |
| L3 | When a window couldn't draw itself, the capture copied that part of the screen — possibly a window on top — and leaked a bitmap | Only the window itself: otherwise the capture fails and the model is told to capture its screen instead; the bitmap is always released. |
| L4 | With a fixed context window, a whole page was cut off its end again to fit | The whole-page budget (and the length of the notes on each part of a long page) follows the room a fixed window has; an 8K window gets shorter notes rather than a cut-off ending. Test. |
| L5 | The diagnostics summary said "nothing personal" but copied raw problem messages | Problem messages and the model's error are masked: user name, PC name, folders, file names (the extension stays) and web addresses (the host stays). Tests. |
| L6 | *take_screenshot* refused "left" and "second" | Numbers, first…fourth, left, middle, right, last, main and other/secondary all work; an unknown one says what does. Tests. |
| L7 | A stray doc comment; the microphone picker cut long names mid-word; the health summary said "shutdown" for a restart | Fixed; long device names end in "…" with the full name as a tooltip; the summary is told to keep the findings' own words. The item 11 row above said "play …" used Windows' player: it searches YouTube Music, and now says so. |

## Owner follow-up, 30 Sep: Whisper

The owner's direction on voice: it needn't be perfect, just working and better than before; while recording, a note
should say recognition isn't perfect and point to installing Whisper, from a Settings option that installs it
offline. Built and tested the same day (no review round — none further, as agreed):

| What | How |
|---|---|
| The note while dictating | Under the Ask box while you dictate: the microphone level and, with Windows' recognizers, "Windows' speech recognition isn't perfect and often mishears — install Whisper…" with *Set up Whisper* (opens Settings › Ask Aqua at the Whisper row) and *Hide* (also a switch in Settings). With Whisper, it says Whisper writes when you stop. The Settings voice test ends with the same pointer. |
| Install in Settings | *Install* downloads whisper.cpp v1.9.4's Windows build (8 MB, GitHub) and a model (Base 57 MB, **Small 181 MB**, or Large v3 Turbo 547 MB; English-only for English speakers) with a progress bar and *Cancel*; each file must match its pinned size and SHA-256 or it's deleted; only `whisper-cli.exe` and its libraries are kept. Installing switches voice input to Whisper; *Switch model* and *Remove Whisper* (asks first) are there too. |
| Dictating with it | The chosen microphone is recorded into memory; the level decides when you've stopped; the speech is cut from the quiet (silence and noise never reach Whisper) and given to `whisper-cli` through a pipe — never a file. Non-speech tags and made-up "Thank you."s after a little noise are dropped. |
| Verified | The real install from GitHub and Hugging Face into a scratch profile (both checksums matched; 4 s on this connection). Live, with Windows' own voice as the speaker: the owner's phrase came back "testing 1 2 3 number 3" (Windows' recognizer: "The Doc One Two Tree Number tree"), the Dublin weather question word for word, silence not sent. 43 new unit tests: installer against a fake server (success, wrong checksum, too big / too small, 503, plain HTTP, no disk space, stall, cancel, one install at a time, switching model, remove, an install record pointing elsewhere), speech trimming, arguments, output clean-up, the setting. Not run here: dictating from a real microphone (never recorded in testing) — the owner's to try. |


## Phase 1, 1 Oct: updates, notifications, 128K context, privacy sweep

From the roadmap's first phase (no review round, as agreed):

| What | How |
|---|---|
| Update check and one-click update | Velopack 1.2.161 (MIT; installs per user, verifies each package's SHA-256, swaps versions in one step, delta updates). Installed copies check the project's GitHub releases a few minutes after start and about daily (Settings › About can switch it off); a new version raises one alert, which opens Settings › About with what's new, *Download* (progress, *Cancel*) and then *Restart now*, or it installs at the next start. Source builds never check; E2E journals *Check now*. `release.yml` tests, packages and publishes a release when a `v*` tag is pushed. |
| Notifications | Clicking a Windows notification opens its alert and marks it read when it's the only one shown since the last click (Windows doesn't say which was clicked; corrected after the review: after several, the click opens the bell's list); opening an alert from the bell or an in-app banner marks just that one read, closes the list and dismisses the banner; a summary notification opens the bell's list; the list follows alerts that arrive while it's open. |
| 128K context | Settings › Ask Aqua and the Ask context popup offer 128K; automatic grows through 64K and 96K to 128K when a chat needs it. |
| Start with Windows | An installed copy takes over the entry from a build run from source; a source build never takes it back; uninstalling removes it. |
| Privacy and security sweep | No names, e-mail addresses, keys, tokens or personal paths in the committed files (the review found the commit's author e-mail is a personal address: see the Phase 1 review below); the E2E suite no longer points at a developer's temp folder or types a real name; screenshots with a name, what was playing or press photos moved to a git-ignored folder, and the README's Today screenshot has the greeting's name painted out. Code scan: no certificate bypasses, unsafe serializers or string-built SQL; files from answers open only as documents and media. CI pins the target framework (a newer SDK on the runner would break the locked restore) and runs the E2E job on demand. |
| Verified | 29 new unit tests (538 pass): the release address, the check schedule, update alerts and notes, one alert read at a time, alerts raised once per key, the Start with Windows hand-over, 128K growth and validation. The app starts through the new entry point (a settings snapshot run), and the E2E project compiles with new checks for *Check now* and for opening an alert lowering the unread count. Not run here: the E2E suite (the owner's; run on 1 Oct, see below). The first real release (v1.0.0) then ran green on GitHub; an installed copy updating itself hasn't been exercised yet. |

## Phase 1 review (7.1) and test-pipeline audit (5.2), 1–2 Oct: what changed

After the owner's first full E2E run (98 tests: 50 passed, 48 failed, 33 minutes), two independent agents worked
read-only on a frozen snapshot of v1.0.0 and the run's artifacts. The **reviewer**: functionality 7.4,
implementation 7.6, security & privacy 6.4, usability 7.0, docs 6.5 — overall **7.1**. The **testing-pipeline
auditor**: unit tests 7.5, E2E suite 3.0, CI/release 4.5, scripts 4.0 — overall **5.2**. Both were under the 8 bar.
Everything below was acted on and shipped in 1.1.0 (CI and the release workflow green on GitHub); as the owner
directed on 2 Oct, no further review round followed, so there are no re-scores.

**The E2E run.** The auditor classified the 77 failed checks: 21 stale tests (names, ids and controls that had
changed on purpose), 22 harness or test-design flaws (6 of them cascades from one failure), 16 timing or focus races
(9 in the tray menu), 7 unexplained lookups, 5 accessibility gaps, 3 host-dependent, 2 product bugs (plus 4 tests
that failed only through the palette crash) and 1 data gap. All were addressed:

| Kind | Change |
|---|---|
| Product bugs | The command palette closed twice on Esc/Enter (a logged crash); the scene editor collapsed when a step was added or removed; the connection test's detailed result was overwritten; Esc didn't close the alerts or Local AI popups; stopping read-aloud didn't reset in dry runs; *Pause media* with nothing playing wasn't journalled. |
| Accessibility | Panels with a name or id but no automation peer (Local AI popup, Ask's chat history, approval card, dictation bar, download progress…) are `GroupBorder`/`GroupGrid`s that screen readers and UI Automation see; buttons with an icon and text are named from their text; list items announce what they show instead of a record dump; the hotkey boxes, holdings, sources, calendars, volume sliders and drives have proper names; Ask's answers are readable through UI Automation (the rich text keeps one document). |
| Races | `--tray-menu` brings Aqua to the front first (4 of 4 opened in a probe); the quick panel's re-open guard applies only to tray clicks; the System page no longer rebuilds its drive and process lists every 1.5 s (rows stay put, and the process list holds still while it has keyboard focus). |
| Stale tests | 14 tests updated (A01–A17, Z01) to the current ids, names, controls and keyboard model. |
| Harness | The fixture no longer restarts a dead app behind a test's back (an exit is always reported; a test that ended the app hands the next one a fresh copy); owned dialogs (file pickers, message boxes) are found under their owner and closed after a failure; a failed check returns to the page it started on, and a test stops after three timeouts in a row; only UI Automation hiccups count as transient (a nested wait's timeout or a test bug surfaces at once, and a timeout names the last error it tolerated); Ask's tests read the answer itself, not the longest caption near it; the crawler presses one of each kind of *Remove* per page and flips every switch back; each profile copy without a place gets Dublin. |

**The reviewer's findings:**

| # | Finding | Change |
|---|---|---|
| H1 | The public commit's author e-mail is a personal address (name and birth year) | Not a code fix: the owner decides (steps given; it's on the 1.1.0 commit too). The sweep's claim was corrected to "in the files". |
| M1 | A second launch with a downloaded update force-killed the running copy (Velopack applied it before the single-instance check) | Velopack no longer applies at start by itself; the first instance applies a ready update before any window opens, and second launches only pass their command on. |
| M2 | The update flow had never run and had no tests | The flow is `UpdateMachine` in Core behind an `IUpdater` seam, with 12 tests against a fake (check, failure wording, alert once, download with progress, cancel, retry, ready at start, apply failing, an alert failing). The installed-copy rehearsal is the owner's (steps given; updating an installed 1.0.0 to 1.1.0 is the first real one). |
| M3 | A notification click could only open the latest alert | After several notifications, a click opens the bell's list; the docs say how it works. |
| M4 | The target framework flipped with the installed SDK; the scripts ignored failures | An explicit `AquaNet` (now **.NET 10**, the owner's request) and `global.json`; lock files regenerated; `build.ps1` and `e2e.ps1` stop at a failed step. |
| M5 | `e2e.ps1` sent `quit` (the app knows `--quit`), so the warm profile was killed | Fixed; the suite also compiles before the interactive setup step. |
| M7 | A failing alert could relabel a good check or download as failed; a once-key was used up before its alert was stored | Alerts are raised outside the state changes and guarded; the key is marked only after the alert is stored. Tests. |
| M8 | Release and CI hygiene | Actions pinned to commit SHAs (checked against their tags) with Dependabot; no stored checkout token; tag name and token through the environment; releases only from tags on `main`; the delta download only when there's a previous release, without `continue-on-error`; the vulnerability audit fails the build; timeouts and a concurrency group; CI no longer runs on tags. |
| M9 | The on-demand E2E job in CI could only fail | Removed; CI compiles the suite instead. |
| M10 | Doc claims that didn't match the code | Corrected (dependencies, .NET version, test counts, notification clicks, update-check requests, the runtime's UAC prompt, update wording). |
| L1, L2, L4, L5, L8, L9, L13 | Velopack's "not installed" warning in Problems; an unguarded start-up call; raw error text; double *Restart now*; banners that vanished under the pointer or outlived *Clear*; the A02 bell test matching a banner; portable copies counted as installed | Fixed (plain failure wording, a re-entrancy guard, banners that pause while in use and close with *Clear* / *Mark all read*, a portable copy never takes over Start with Windows). |
| L19 | Automatic context could grow to 128K on any GPU | Automatic stops at 64K unless the card has 24 GB or more; past the limit a prompt is trimmed rather than overflowing (the auditor's M9). Tests. |

**The auditor's pipeline findings:** the E2E project builds with the .NET 10 SDK and in CI (C1); the harness
fixes above (C2, H1–H3, H8, H9); the 12 peerless names and ids fixed, with a XAML contract test in the unit suite
that caught the one first missed, the Social page's empty state (H4); the vulnerability audit gates (H12); the release's publish restores in locked
mode — the app's lock file now includes win-x64 and one locked restore covers the ReadyToRun publish, checked
with a local publish and by the 1.1.0 release (H13); `--quit`, compile-first and the scripts' exit codes (H7, M1,
M2); the log tests no longer run alongside others (M7); the alert's Settings target has a contract test (M10);
quiet hours' fallback has a unit test instead of a stale E2E test.

**The owner's requests from the same review:**

| Request | Change |
|---|---|
| No default location | A new profile has no place: onboarding asks (search, or *Use my location* through Windows' location with permission, town-level and rounded to about a kilometre, looked up with OpenStreetMap), and it can be skipped. Until a place is chosen there's no weather or local news search, Today's weather card offers *Choose a place*, prompts leave the place out instead of inventing one, and Ask uses Windows' time zone. Choosing a place sets up its country (outlets, subreddits, holidays, index, units, currency); a move swaps them and keeps what you added. Profiles from before keep Dublin. A map picker would need an embedded browser (WebView2) and map tiles: left as the upgrade path, as search plus location covers it. |
| .NET 10 | Done (1.1.0 runs on .NET 10; Setup, or the first update from 1.0.0, installs the runtime if it's missing). |
| A model selector in Ask | A picker in the composer; Think and Research are remembered for each model; Think is greyed out for a model without a thinking mode (Gemma 3); every call of an answer stays on the picked model, so Ollama doesn't swap models mid-answer. |
| GitHub hardening | The workflow side is done (above); the repository settings to change are the owner's (listed in the hand-over). |

**Verified:** 594 unit tests pass (23 live ones skipped), 56 more than at the review; the app builds with no
warnings; the E2E suite compiles (running it is the owner's); UI Automation probes of a scratch build in the
sandbox: onboarding with no place, *Use my location* (simulated), Today's *Choose a place*, Settings setting up
Cork with the Irish pack, the title bar's names, Ask's answer text, the tray menu. CI on `main` and the v1.1.0
release passed on GitHub. Dependabot's first pull request failed CI because the UI-test project's lock file wasn't
updated with the unit tests' (it isn't in the solution); `dependabot.yml` now covers both folders in one pull
request.

**Carried to the next phase:** the update rehearsal on an installed copy (M2) and the e-mail decision (H1) — the
owner's; update discoverability (M6: a banner and a badge when a version is ready); the Updates card's progress
as a real progress bar (L10); release notes from a changelog (L11); install kind in the diagnostics (L12); tests
for the Start with Windows registry plan (L14); a LICENSE and `.gitattributes` (L16, L17); from the audit, shared
UI ids for app and tests (H5), a hermetic E2E run with a fake model server and network and a generated warm
profile (H6), tests for the collectors and AI agents (H14), the slow timing-based unit tests (M6), the XXE test's
weak assertion (M8), a single-pass crawler and a smoke subset, a coverage ratchet, and a release smoke test with a
draft release.

## Phase 2, 2 Oct: fixes and quick wins

The owner's 2 October list (three bugs, twelve ideas) went into the roadmap; Phase 2 took the bugs and the ideas that
need no new accounts or packages.

| # | Item | What was built |
|---|---|---|
| 2.1 | No update notice after updating | At start-up, the "available" and "ready" alerts for the version now running (or an older one) are taken out of the bell, so an update that has been installed isn't offered again. |
| 2.2 | Now Playing follows the media | Artwork is read again when the title changes and a late picture for an older title is dropped; a player that closed, or stopped with nothing to resume, counts as nothing playing (a quiet check every few seconds catches players Windows doesn't report closing). |
| 2.3 | Plainer wording everywhere | A pass over every page, card, setting, empty state and tooltip: what a setting does in a line, without the reasons; errors still say what to do. |
| 2.4 | Settings search and a Shortcuts page | A search box (Ctrl+F) finds any setting by its title, description or extra keywords and takes you to it with its control focused. *Shortcuts* lists every shortcut; the global ones are recorded by pressing the keys (Esc cancels, Backspace clears, a reset button restores the default), and a clash with the other global shortcut, with Aqua's own keys or with Windows or another app is shown before it's saved. Aqua's own global hotkeys pause while you record one. |
| 2.5 | Markets, your way | *Edit charts* on Markets moves, removes and adds the charts in the top row; the watchlist's *Edit* opens its settings; holdings take fractions of a share, a cost per share and the currency it was paid in ("0.5" and "0,5" both work); a symbol's detail links to Yahoo Finance, Google Finance and its exchange where known (each address checked). |
| 2.6 | Portfolio card | Value, today's move and the gain since bought (in the currency paid), holding by holding with weights, in a currency you choose; a 1M/6M/1Y chart of today's holdings at past prices and that day's exchange rate. Rates come from the quote service, only for the currencies your holdings need; a holding without a price or rate is listed apart. |
| 2.7 | Gaming and internet news | A *Gaming & internet* category: Kotaku, Eurogamer, Video Games Chronicle, Dexerto, Insider Gaming and the Daily Dot (each feed checked). Existing profiles get them once (settings version 4). |
| 2.8 | Attach a folder in Ask | A folder button, or a dropped or pasted folder: Ask searches and reads it for that chat, even with Use my PC off, with Use my PC's rules (no app data, system or key folders, no password or key files). |
| 2.9 | Ask runs Aqua's agents | "Refresh the news and tell me what's new", "get the latest prices", "write me a fresh brief", "refresh it" after a question about the weather: those agents run first and the answer starts from what they brought in (after the news, the stories that are new). The model can also call `run_agent` with a fixed list of jobs, each at most once per answer; while an AI agent writes, the answer lets go of the model. Live on `qwen3.5:9b`: four requests refreshed what they named, and a follow-up with no refresh asked didn't claim one — the first try did ("The agents have refreshed the weather"), which the prompt now forbids and "refresh it" now covers. The same check showed Ask couldn't see the portfolio card's holdings; a question about your portfolio now gets them (and answered with the card's figures). |
| 2.10 | Updates you can't miss | A banner across the window (*Download* with a progress bar, *Cancel*, *Restart now*, *What's new*, *Not now* until the next step) and an accent dot on Settings; real progress bars on the Updates card and the Whisper download. `--demo-update [ready]`, a pretend release for Debug builds and E2E dry runs, made it testable: a UI Automation probe walked offer → download → ready → *Restart now* (refused: nothing to install) → *What's new* → *Not now*, and `A18_UpdateTests` does the same in the E2E suite. |
| 2.11 | Test debt from the Phase 1 audit | The collectors (news with conditional requests, social, markets with exchange rates, predictions, agenda, weather) and AI agents (News Editor, Chief of Staff, the model offline) tested against a stand-in network (`FakeNetwork`, behind an internal `HttpFetcher` constructor) and the scripted model server; the scheduler's tick and follow-up delay can be shortened for tests (the unit suite went from 9.7 s to 2.5 s); the XXE tests prove a DTD's external entities are never fetched and a feed or Word file using one is refused; an E2E smoke subset (`-Smoke`, 8 tests); `.gitattributes`; the diagnostics summary says how the copy was installed and where the updater stands. |

**Verified before the review:** 757 unit tests (734 pass, 23 live ones skipped), 140 more passing than at the end of Phase 1; the app
builds in Debug and Release with no warnings; the E2E suite compiles and `-Smoke` selects its 8 tests (listed, not
run: running it is the owner's). UI Automation probes of a scratch build in the sandbox: Settings search and the
Shortcuts page, the chart row's edit mode and links, the portfolio card's figures, the update banner end to end,
and Ask on a private model server (port 11600, not the owner's) refreshing the news, prices and weather.

## Phase 2 review (7.5) and test-pipeline audit (6.2), 2–3 Oct: what changed

Two Sonnet agents worked read-only on a frozen copy of the Phase 2 tree (1.2.0, 4df7b4e). The reviewer scored
functionality 7.5, implementation 7.5, security and privacy 7, usability and accessibility 7.5 and docs 8: 7.5
overall, under the bar. The test-pipeline auditor scored unit coverage 7.0, test quality 6.0, the E2E changes 6.5,
CI and scripts 6.5 and the Phase 1 carry-overs 5.0: 6.2. Both were cut off once by a usage limit and resumed from
their notes; the auditor's session ended at the limit just after it wrote its report. Everything below was acted on;
under the one-round rule there's no re-score.

| # | Finding | Change |
|---|---|---|
| M1 | "0,125" (English) or "0.125" (German) shares were stored as 125: a lone separator before three digits counted as thousands even after a 0, and "1.5.2" read as 152 | `Numbers.TryParse` reads a separator as thousands only in real groups ("1,234,567", "1.234,5", Indian "1,00,000"; a lone one only when it's the culture's own); otherwise it's the decimal point, or the text isn't a number, and the watchlist box says so instead of saving it (L12). Tests in English, German, French and Indian formats. |
| M2 | Holdings reached the model without the leak gate files and the screen have, and "my portfolio website" or "Berkshire's holdings" brought them in | Holdings come in only for a question about your own ("my portfolio", "our ETF holdings", "the portfolio"), and they count as private data: a search or link after them waits for your OK. Tests for both; SECURITY.md's leak row lists holdings. |
| M3 | Requests about your own things started agent jobs, AI ones of up to four minutes included ("a fresh analysis of this contract", "update my calendar with a dentist visit"), while real requests were missed ("I'd like a fresh brief") | The AI agents' work counts only by its full name; a topic word that names something of yours ("the prices in my spreadsheet") or something named after a topic ("the weather widget", "my news sources") isn't a job; a message with attachments or a folder runs nothing first. "Refresh Aqua's agents", "New brief please", "a fresh brief and a new market analysis" and "update the app and refresh the news" are recognised. The reviewer's probe phrases are 27 new test rows. |
| M4 | "Refreshed … just now" for runs that brought nothing ("No place chosen yet", the brief's offline fallback, "Brief is current") | A collector counts as refreshed only if it marked its part of Aqua fresh after the request (`HubState.FreshAt`); an AI agent whose run changed nothing, or that was waiting, is reported as not refreshed (`AgentStatus.NothingNew`). Tests for each. |
| M5 | A folder attached to a chat turned every later question into a file search ("What's the capital of France?") | Only questions about files, or what's "in here", search it; the model's file words are dropped for other questions, and "nothing matched" is said only for a file search. Theory test. |
| M6 | A folder stayed readable for the whole chat, with no sign of it and no way back | A chip above the box for each folder the chat can read, with × to stop sharing it. SECURITY.md says so. |
| L1–L3 | A repeated `run_agent` call still let go of the model and took it back; a failed job, asked for again, sank the answer; no limit on the jobs run first | A repeat keeps the model; it runs inside the tool's error handling; the jobs run first get five minutes in all, after which an AI job isn't started and the answer says so. Tests. |
| L4 | The brief's request stayed set when no run took it up, so a later scheduled run rewrote the brief unasked | It's withdrawn when the answer stops waiting, and a run already under way that missed it is followed by one that takes it up. Tests. |
| L5 | With the model off or paused, "refresh the news" refreshed nothing | The collectors asked for still run (they don't need the model) and the answer from the feeds starts with how it went; the AI agents' work says it waits for the model. Test. |
| L6 | Now Playing: a hung Windows media call could stall every later refresh, a late picture could stay hidden, and the same cover on two tracks in a row was hidden | Each media call gives up after 3 s, like a failed one; the artwork request is kept for the refresh that wins; the same-cover rule applies only while a new title settles, with one more look when no picture came. |
| L7 | The hotkey probe's id could collide with a live hotkey after a long session | Ids restart with each registration pass, and the probe uses one outside their range. |
| L8 | Portfolio: a comment said the opposite of the gain rule; a holding with no stored prices was silently missing from the chart; the chart's end could disagree with the card | The comment and README are corrected, such holdings are named under the chart, and the chart ends today at the card's value. Test. |
| L9 | The chart row showed nine tiles but took any number | Nine at most: the add box says when the row is full. |
| L10 | The folder survey followed junctions | It skips them, as the searches do. |
| L11 | Disclosure copy trimmed where you decide | One clause back each: where Whisper and updates come from and how they're checked, where keys are kept, what the update request carries, "no code runs" and "Kept only on this PC" in the Workbench, and the Markets disclaimer's "made on this PC" and "see a licensed adviser". |
| L12 | Accessibility and feedback gaps | The update banner is a live region, announced once per version and step; a lone key in a shortcut box says to hold Ctrl, Alt or Win; picking a shortcut from Settings search no longer focuses its box (which pauses Aqua's hotkeys); a watchlist box that isn't a number is marked; the Markets link target is in the contract test; past eight attachments, a *Not attached* chip counts the rest. Ctrl or Alt with one key stays refused: apps use those themselves (decided). |
| L13 | The attached-folder test failed when the repo lives under AppData | It's skipped there, with the reason (`ReadableFolderFact`): Ask refuses such folders by design. |
| L14 | Doc imprecisions | The chart row's rebuild, the Nasdaq and NYSE links, F5 (the in-app list was the wrong one) and SECURITY.md's rows for holdings and folders. Private vulnerability reporting is off on the repository: the owner's switch. |
| L15 | Drift: the shortcut list was kept in step by a comment; `--demo-update` raised a real alert | A test checks the window's keys against the Shortcuts page both ways; the pretend release raises no alert. |

**Found while fixing:** Ask's folder rules judged a path by its spelling, so an 8.3 short name (`SSH~1` for `.ssh`)
slipped past the sensitive-folder check, and a junction or symbolic link inside an allowed folder could lead
anywhere. Short names are now spelled out before checking (`LocalFiles.LongPath`), and no link is followed or read
through, a linked file included. Tests (this PC makes short names, and Developer Mode lets the link tests run).

**The auditor's findings:** TH1, the 1-in-75 flake: the shared temporary-folder fixture cleared every SQLite
connection pool in the process while other tests used theirs; it now releases only its own database files
(`HubDatabase.ReleaseFile`). TM1 is L13. TM2–TM5: a holding paid in a third currency (CHF) fetches and uses that
rate; "downloaded again only when it changed" counts the conditional requests; stored rates survive a reload; the
editor keeps its cached summaries when stories regroup; the planner hears what was just refreshed. TL1: the two
culture-dependent outputs (a time stamp's day, the battery percentage) are fixed in the code, not the tests. TL2:
the XXE tests' listener accepts and closes, so a regression fails at once instead of after 100 s. TL3: the slow
agent takes 1 s. TL5: the link and shortcut tests above. TL6: the test settings point at a closed port, never a
developer's Ollama. TL7: the E2E project's stale comment, a hang timeout on CI's test step, and the test context
disposes its clients.

**Dependabot:** its xUnit runner update (pull request 2) fails CI because it updated the unit tests' lock file but
not the UI tests' (they aren't in the solution). Not merged; the options are in the hand-over.

**Verified:** see the Phase 3 follow-up below (both rounds of fixes were tested together).

**Carried to the next phase:** a hermetic E2E run with a fake model server and network (TH2); tests for the
WPF-side logic — Now Playing's state, the shortcut recorder, the other glue (TM6–TM8); the crawler's skipped
Shortcuts section and a single pass (TM9); the E2E run since the Phase 1 fixes (TM10, the owner's); Dependabot's
two lock files (TM11); a release rehearsal with a draft and a smoke run (TM12); coverage with a floor (TM13); the
five agents without tests and the failure paths (TM14); fixtures built on the wall clock (TL4); E2E names taken
from tooltips (TL8); and the owner's: a LICENSE and private vulnerability reporting.

## Phase 3, 2 Oct: Ask, upgraded

From the roadmap's third phase (no review round; left uncommitted for the owner to try first):

| What | How |
|---|---|
| 3.1 Tables and maths | `AnswerMarkdown` parses answers into blocks and spans — headings, nested and task lists, quotes, rules, code, tables, display maths — for `MarkdownView`. A Markdown table is a native WPF table; LaTeX (`$…$`, `\(…\)`, `$$…$$`, `\[…\]`, `\begin{align}`) is drawn with XAML-Math 2.1.0 (decision 4; MIT, fonts OFL), inline formulae on the baseline, after `MathText` rewrites what models write into the LaTeX it knows and turns away formulae nested deeply enough to overflow its parser. Prices aren't maths ("$5 and $10"), and the symbol tidy-up (`20$^\circ$C` → 20°C) now leaves code and formulae alone. Copying keeps the Markdown (a table copies as a Markdown table, a formula as its LaTeX); read-aloud skips table rules and pipes. |
| 3.2 Notes for each chat | `ChatNotes`: facts, decisions and open questions kept with the chat and read into every answer's context ahead of anything that can be trimmed. The model keeps them with `update_notes` (six changes an answer, 60 notes, repeats refused unless a number changed); the header's notes button opens a panel beside the chat with them as text you can edit, and an edit keeps what Aqua added meanwhile. Separate from Memory; in the export; deleted with the chat. Notes Aqua wrote count as untrusted text. |
| 3.3 Compress a long chat | `ChatCompressor`: *Compress* in the context popup, or *Compress long chats on their own* (Settings › Ask Aqua, on) after an answer once messages would be left out or the context is 80 % full. Whole exchanges but the last two become a summary under fixed headings that keeps names, numbers, dates, decisions and sources (and must come out shorter). A line above the first message still sent in full says "N earlier messages go to the model as a summary · about X → Y tokens", with *Show summary* and *Undo*; the messages above it stay, dimmed. A fingerprint drops the summary when a message it covers is edited or asked again; the planner sees it too. |
| 3.4 Pictures in Ask chats | The screenshots Ask takes, the pictures it reads and the ones it looks at to find what you described show as thumbnails in the answer (matches first, eight at most). A click opens one — a screenshot in a viewer, a picture on the PC with its app — and pictures you attached open from their chip. Each chat has a folder in `chat-media`, deleted with it; pictures on the PC are linked, with only a 320-pixel thumbnail kept. Settings › Privacy › *Keep pictures with chats* (decision 8: on by default; off deletes them all), and SECURITY.md says so. |
| Verified | 114 new unit tests; 845 pass (23 live ones skipped). Three more fail only on the Linux machine this was built on, as before the phase — Windows paths and a battery reading (`LocalFileTests.ExpandsFolderTokens`, `CitationCheckTests.AFileNamedInTheAnswerBecomesAChip`, `PcHealthTests.BatteryWear`) — and pass on Windows. The new tests cover the Markdown parser (tables, prices against maths, streaming, nesting, hostile input), about 45 model-style formulae normalised and run through XAML-Math's own parser, notes and `update_notes`, the agent reading notes and a summary (fake Ollama), compression (how much, when, fingerprints, the request, the clean-up, a run through the fake model), the picture store and its file names, an answer's pictures (each once, eight at most, the match first) and saving them with a chat. The solution builds with no warnings, the locked restore passes, and the E2E suite compiles. Not run here: the app — WPF doesn't run on Linux — so tables, formulae (inline baseline alignment), the notes panel, the compression line, the thumbnails and the picture viewer are the owner's to look at, as is the E2E suite. |

## Phase 3 on Windows, 3 Oct: the failing CI run, a review and fixes

Phase 3 was built on Linux and merged into `main` from `phase3-test`; its CI run (a75d21b) failed.

**Why CI failed.** The locked restore of the solution stopped with NU1004: Phase 3's restore had added a
`net10.0/win-x64` section to AquaHub.Core's lock file that a restore on Windows doesn't produce, so the row above's
"the locked restore passes" held on Linux only. The step still passed: it ran two restores in one PowerShell script,
and a step reports only its last command's exit code, so the second restore's success hid the first's failure. The
build step did the same, and the test step then had nothing to run. Fixed: the lock file is 1.2.0's again, and each
restore and build is a step of its own, so a failing command fails the run where it happens; the test step has a
5-minute hang timeout.

**Checked on Windows**, on a scratch build and profile seeded with a test chat and driven by UI Automation: tables,
formulae inline and on their own lines, the notes panel, the compression line and its summary, the thumbnails and
the picture viewer. Deeply nested formulae were turned away on the UI thread's stack, but line breaks weren't limited:
the review below found a formula that ends the app, so "none crash" didn't hold. Found and fixed:

| Finding | Change |
|---|---|
| `\text{…}` in a sub- or superscript (`Q_{\text{out}}`) was drawn full size, as XAML-Math draws it | It becomes upright `\mathrm`, spaces kept. Tests. |
| `pmatrix` had square brackets, and a case's condition ran into its value | Round brackets, and a quad between the columns of `cases`, as amsmath sets it. Tests (every rewritten formula is also run through XAML-Math's own parser). |
| `update_notes`' remove took the first note containing the text, so "the" or "budget" could remove the wrong one | A phrase removes a note only as whole words, and only when one note holds it. Test. |
| A picture attached to a question opened only with the mouse | Its chip is a button (Enter or Space; "Open …" for screen readers). |
| *Compress the chat* had no accessible name (its tooltip stood in) | Named. |
| *Show summary* showed the summary's Markdown (`## What was asked`) as raw text | It's laid out like an answer. |
| "Aqua last added one 33m" | "… 33m ago". |
| The picture viewer's title bar stayed light in dark mode | It follows the theme. |

**Verified** (both phases' fixes together): 952 unit tests, 929 passing and 23 live ones skipped (none failing), in Debug and Release: 195 more than at the
Phase 2 review. The locked restores of the solution and the UI tests pass on Windows, both configurations build
with no warnings, and the E2E suite compiles (running it is the owner's). A UI Automation probe of a scratch build
in the sandbox went through the Phase 3 chat above with the fixes in it.

**Not done then:** Phase 3's own Sonnet review and test-pipeline round (below).

## After the review copy, 3 Oct: notes from what you say, feeds that keep failing

Two changes made after the review agents' copy was frozen, so neither agent saw them:

| What | How |
|---|---|
| Notes from what you say | Live on `qwen3.5:9b`, the answer model never called `update_notes` by itself in five messages that gave a size, a budget, a new budget and a decision. Now the plan lists the facts and decisions a message gives and the notes it overturns (`AskPlanner.ReadNotes`), and a plain chat, which isn't planned, gets one small call after the answer at background priority (`AskPlanner.NotesRequest`). With the same rules, live, the model noted the size, place, budget and choice, replaced the budget when it changed, and noted nothing for general questions. Tests with the fake model; a live run in the app is still to do. |
| Feeds that keep failing | The owner's log showed one feed's "404 Not Found" on every run. `SourceHealth` logs a failing feed when it starts failing, when its error changes and then once a day; one that answers "not found" (404 or 410) three runs in a row is fetched only every six hours until it works again, with one alert that names it and points at its row in Settings, which says what's wrong and since when. Tests. |

## Phase 3 review (7.0) and test-pipeline audit (6.1), 3–4 Oct: what changed

Two Sonnet agents worked read-only on a frozen copy of the tree checked above (a75d21b plus the Windows fixes). The
reviewer scored functionality 7.5, implementation 7, security and privacy 6, usability and accessibility 7.5 and docs
7: 7.0 overall, under the bar. The test-pipeline auditor scored unit coverage 6.0, test quality and determinism 7.0,
the E2E suite 5.0, CI and scripts 7.0 and the Phase 2 carry-overs 5.5: 6.1 (its 124 planted faults: 71 caught). Both
were cut off by a usage limit and resumed from their notes. Everything below was acted on; under the one-round rule
there's no re-score.

| # | Finding | Change |
|---|---|---|
| H1 | A formula with about 720 line breaks (`\\`) passed the guard and overflowed the stack in XAML-Math's parser, ending the app; saved in a chat, it ended it again whenever the chat was opened | Line breaks are limited too (100), and the length is checked before the rewrite, so a formula over a limit is shown as text (`MathText.ForDrawing`). The limits are tested against the real parser on a 512 KB stack, and `MathView` draws on a thread of its own with 32 MB. A scratch build opened a chat holding a 740-break formula: shown as text, the app stayed up. |
| M1 | A chat's notes and summary carried private facts into later answers past the web gate | A chat that has read your files, screen, attachments or holdings is marked so, and saved that way (`ReadPrivate`): its later answers ask before reaching the internet ("This chat has used …"). Names and numbers in the chat's notes (an order number, a sum) count as private details for searches and links. Tests; SECURITY.md's leak row says so. |
| M2 | Deleting a chat mid-answer brought it back, with its notes and pictures | A deleted chat stays deleted: the store won't save it again, its answer and any compression stop, and nothing more is written. Tests, the database included. |
| M3 | A folder taken back with × came back through *Retry*, *Dig deeper* or *Edit*, and its path was saved | Folders are never saved with a chat (left out of the stored options; paths saved before are removed once at start-up), and every question takes the chat's folders as they are then. Tests. |
| M4 | Switching *Keep pictures with chats* off left the picture rows (paths, what each showed) | The rows go too, in every chat, at once and again at start-up while the setting is off; Settings shows how much space the pictures take. Tests. |
| M5 | The screenshot Allow card said "only for this answer" while the screenshot is kept | When it will be kept, the card says so and where to switch that off. Test. |
| M6 | Quadratic regexes on the UI thread on every streaming re-render | The answer parser's patterns are linear (an opener can't contain another, a link's brackets must balance) and give up after 250 ms, leaving plain text. Found while fixing: linking an answer of 20,000 links took 34 s; now it doesn't. Timing tests at sizes where the old patterns took seconds. |
| M7 | "Update all my documents" or "run all the tests" refreshed the whole hub | "All" or "everything" means every job only on its own or with Aqua's things ("refresh everything", "update all the agents"); "first refresh the news" and "update the app, then refresh the news" are heard. The probe phrases are test rows. |
| L1–L4 | Links with brackets were cut; `\\server` lost a backslash; a mismatched `\begin…\end` swallowed the rest of the answer; `$50%$`, inline triple backticks and `<br>` misrendered | Brackets balance in links (`[[1]](…)` too); a network path is shown as written, as text; an environment stops at its own `\end` or a blank line, and words after it are a paragraph; `%` becomes `\%`, triple backticks are code and `<br>` a line break. Tests, and the scratch build showed each. |
| L5 | Read-aloud spoke LaTeX | Formulae are spoken in words ("E equals mc squared"; a long one or a matrix is "a formula"). Tests. |
| L6 | Chats over 300 messages lost their compression and orphaned pictures when reloaded | Trimming to 300 moves the summary to match and tidies pictures no message uses. Tests. |
| L7 | Notes: numbers compared out of order, "9000" ≠ "9,000", a note removed while you edited came back, 60 notes kept silently | Numbers compared in order, separators normalised; removed notes stay removed; the status line says the first 60 were kept and how many lines were left out. Tests. Notes from an answer you then replaced still stay (open). |
| L8 | "Sure! Here's a summary of the chat:" stayed in summaries; an automatic compression delayed the next question and couldn't be stopped | The preamble is stripped; your question stops an automatic compression and goes first, and the line says so. Tests for the clean-up. |
| L9 | Fence marks in the text weren't defused, so a page could close the data fence, through a summary for good | `Prompts.Fenced` defuses them; the planner's view of the earlier turns and the summary is fenced too. Tests. |
| L10 | Test gaps for the guards | See H1, M6 and the auditor's TH1–TH3: the chat decisions moved from `AskSession` into Core (deletion, folders, pictures, the turn rule) and are tested there. |
| L11 | Accessibility: a picture's match and note only in its tooltip; the notes button carried no state; nothing was announced | Help text on picture tiles; the notes button says whether the panel is open and how many notes there are; a polite live region announces new notes, a compression and folder changes. Tables are still read without row and column structure (a FlowDocument limit; copying keeps the Markdown). |
| L12 | A junction inside Documents leading to `.ssh` or AppData could be attached | An attached folder is judged by where it leads too (`LocalFiles.IsSensitiveRoot`), in Ask and in Settings. `LocalFiles.LongPath` is gone: .NET's `GetFullPath` already spells out short names, so "found while fixing" above was closed by the runtime; the test stays, on the combined behaviour. Tests with real junctions. |
| L13 | File checks on the UI thread on every re-render; a summary viewer in every message; pictures re-encoded on the UI thread; storage without a size; the notes panel squeezed the chat | Path checks are cached for a minute; the summary viewer exists only where a message has a summary; pictures are written off the UI thread before the chat is saved; Settings shows their size; the notes panel narrows below 900 px and the history closes sooner while it's open. Table columns and formulae per answer aren't capped yet. |
| L14 | `update_notes` could remove notes you wrote | It can't: the model is told the note is yours. Test. |

**The eight doc claims that didn't hold** are true now or corrected: the formula guard, folders not saved, the × taking
a folder back, switching pictures off, the screenshot card and the leak row by the changes above; a picture attached
from a file is kept as a thumbnail, lists nest ten levels, and a linked picture's path from the database opens only
from a drive and, like any file link, only as a document, picture or media file. The "none crash" line above is
corrected.

**The auditor's findings:**

| # | Finding | Change |
|---|---|---|
| TH1 | `MathText.Drawable`, the crash guard, had no test | Tests, including the worst formulae run through XAML-Math's parser on a 512 KB thread. |
| TH2 | "Notes or a summary Aqua wrote mark the answer as untrusted" had no test | A theory: Aqua's notes and summary mark it, yours don't. |
| TH3 | Nothing automated covers the Phase 3 interface | Partly: its decisions moved into Core and are tested (above), and a UI Automation probe of a scratch build went through the review's cases. A WPF test project and a seeded chat for the E2E suite are carried. |
| TM1 | CI went green when no tests ran | No tests is an error in CI, the release and `build.ps1`, and CI fails if fewer than 1,000 ran. |
| TM2 | A quadratic regex the hostile-input test couldn't see | M6, with timing tests. |
| TM3 | Deleting a chat was tested only in memory | Tests against the database. |
| TM4 | *Undo* on a compression wasn't checked to survive a restart | Round-trip test. |
| TM5 | The maths tests mostly asserted "doesn't throw" | Every rewrite in the tables is checked to apply, Unicode symbols to become their commands, and rows assert the output. |
| TM6 | Four tests couldn't fail | A brief request no run took up is withdrawn (with an agent that never takes it); `LongPath` is gone and its test guards the combined behaviour; the link tests skip with a reason when links can't be made (`[LinkFact]`), and the survey skipping a junction and reading through one failing closed are tested; the picture cap is the literal 8. |
| TM7 | Wiring and defaults without a test | One each: a thumbnail's name reopens it, the planner gets the summary, a message with a folder runs nothing first, the context limit sets when to compress, notes are in the export, the two defaults, "agenda" means events, and the runtime marking a run that changed nothing (O1). |
| TM8 | "12.345" was read as 12,345 in French, Swedish, Russian and Polish | The thousands mark comes from the culture; rows for all four. |
| TM9 | Dependabot's fix was unproven and its pull request 2 failed | The UI tests' lock file is gone (versions stay pinned in `Directory.Packages.props`), CI restores them unlocked, and Dependabot watches the root only. Proven only by its next pull request; PR 2 should be recreated. |
| TM10 | The structural items from Phase 2 | Carried (below). |
| TL1 | `TimeText.Ago` used the current culture; the culture runs stopped at Gregorian calendars | Invariant, and so is the calendar fixture; CI runs the suite again as on a Thai and a Saudi PC. Both found real bugs: a Thai date in an English prompt, and a date before 1900 throwing under the Um al-Qura calendar. String comparisons the Thai culture got wrong (it ignores punctuation) are ordinal now. |
| TL2 | `TempDir`'s cleanup was untested | Tested, and it removes links before folders (a recursive delete failed on junctions). |
| TL3 | `build.ps1` skipped the UI tests | It restores and builds them as CI does. Locked restores everywhere by default aren't adopted: Dependabot has to regenerate lock files, and CI, the release and `build.ps1` already restore locked. |
| TL4 | The release had no hang timeout, kept no results and didn't audit packages | All three, as in CI. Publishing straight away rather than as a checked draft is carried. |
| TL5 | The file-open allow-list sat untested in the WPF project | It's in Core with a 19-row test (programs, scripts, shortcuts, installers, web pages and macro-enabled files are only shown in Explorer). The `OnKey` source check stays: it fails loudly. |

One planted fault can't be caught: emphasis can't nest deeper than four levels (each kind's text excludes its own
marker), so the inline depth guard behind it is never reached. A test pins the four-level chain and a bounded tree.

**Verified:** 1,142 unit tests, 1,119 passing and 23 live ones skipped (none failing), in Debug and Release and again
under Thai and Arabic settings: 190 more than at the review. The solution and the UI tests build with no warnings, and
a UI Automation probe of a scratch build went through the review's cases: the app stayed up with the 740-break
formula, every case rendered, the compression line has one toggle that opens the summary, and the notes button and
the live region carry their names and states.

**Carried to Phase 4:** a WPF test project and a seeded chat for the E2E suite (TH3); notes from a replaced answer
(L7); caps on table columns and formulae per answer (L13); tables' structure for screen readers (L11); the Phase 2
structural items — a hermetic E2E run, tests for the WPF glue, the crawler's list, a release rehearsal with a draft, a
coverage floor, the five agents without tests, fixtures on the wall clock (TM10); and the owner's: an E2E run,
recreating Dependabot's pull request 2, private vulnerability reporting and a LICENSE.

## Phase 4 backend, 4 Oct: Aqua's own model runtime

Backend only — the screens, the review and every test are Astra's from this phase; the handoff is
[docs/handoff/phase-4.md](handoff/phase-4.md).

- **Built:** a llama.cpp runtime installed on request (one pinned build, picked from the card and driver), a model library
  over Aqua's downloads and Ollama's store (GGUF headers parsed for fit, thinking, tools, vision and MTP), Hugging Face
  search and verified, resumable downloads, a memory-fit estimate with the context chosen for the card, one server per
  model in a kill-on-close job with a key per start, GPU and processor lanes, MTP kept only when it measures faster, and
  llama.cpp's timings on every result. `Hub.LocalAi` is the one API the screens use.
- **Live, 4 Oct:** qwen3.5:9b from Ollama's store through Aqua's runtime (Ollama's bundled llama-server as the server):
  first answer in 13 s including the load, both notes taken from a message with facts, no server left after quitting.
  Earlier, on the RTX 4070 SUPER: 75.7 tokens/s against Ollama's ~66; Qwen3.8 27B IQ2_S at 32K, 36.6 tokens/s with 64 of
  65 layers on the card.
- **Notes from what you say, live (4 Oct):** with a JSON schema, qwen3.5:9b answered an empty list for messages that gave
  facts. A first `said` field (what the message says about you, in a few words) fixed it at temperatures 0 and 0.1 (6 of
  6), and a near-copy of an existing note is now merged.
- **Not yet:** the official pinned build hasn't run here (it needs the owner's OK to download it: about 655 MB for CUDA
  12.4); models split across files aren't downloaded; llama.cpp's router mode isn't used.
- Unit suite: 1,116 passed, 26 skipped, none failed.
