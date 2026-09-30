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
| Notifications | Clicking a Windows notification opens the alert it showed (before, the last one shown) and marks it read; opening an alert from the bell or an in-app banner marks just that one read, closes the list and dismisses the banner; a summary notification opens the bell's list; the list follows alerts that arrive while it's open. |
| 128K context | Settings › Ask Aqua and the Ask context popup offer 128K; automatic grows through 64K and 96K to 128K when a chat needs it. |
| Start with Windows | An installed copy takes over the entry from a build run from source; a source build never takes it back; uninstalling removes it. |
| Privacy and security sweep | No names, e-mail addresses, keys, tokens or personal paths in what would be committed; the E2E suite no longer points at a developer's temp folder or types a real name; screenshots with a name, what was playing or press photos moved to a git-ignored folder, and the README's Today screenshot has the greeting's name painted out. Code scan: no certificate bypasses, unsafe serializers or string-built SQL; files from answers open only as documents and media. CI pins the target framework (a newer SDK on the runner would break the locked restore) and runs the E2E job on demand. |
| Verified | 29 new unit tests (538 pass): the release address, the check schedule, update alerts and notes, one alert read at a time, alerts raised once per key, the Start with Windows hand-over, 128K growth and validation. The app starts through the new entry point (a settings snapshot run), and the E2E project compiles with new checks for *Check now* and for opening an alert lowering the unread count. Not run here: the E2E suite and a real release (they need the owner's desktop and GitHub repository). |
