# TvStation roadmap

Working plan for scheduling and curation features. Phase 1 is the agreed next three steps, in order.
Later phases are candidates, roughly ordered by payoff per effort; reorder freely.
Phase 7 (smarter stations: embeddings, watch history, optional local model) is the current plan.
Phase 8 (weather channel) steps 1-6 shipped in 1.14.0.0 (2026-10-23); step 7 polish is open.

Conventions for every step:

- Pure logic lives in `Scheduling/` or `Library/` and gets xUnit tests in `tests/`.
- Jellyfin-facing code (resolver queries, API, hosted service) is verified on the dev container
  (`dev/`, Jellyfin 12.2) through the preview or stations API before the zip is rebuilt.
- Existing stations must keep producing the same schedule after an upgrade unless the user edits
  them. New settings default to "off" and are excluded from the schedule fingerprint when they do
  not change the timeline.
- Station JSON in `examples/stations/` is the source of truth for the real server (and doubles as
  shipped examples); changes there go in via Import.

## Already done (October 2026)

Pool / slots / lineup modes, shuffle with episodes-in-order, marathons, per-source weight, season
range and specials toggle, movies/shows filter on container sources, seasonal windows, import and
export, station logos, Jellyfin 12.2 / .NET 10. Multi-part (stacked) movies play at full length as
consecutive segments on one clock (2026-10-15), which is also the groundwork for mid-roll breaks.
Stream output is SDR BT.709 end to end since 1.11.0.0 (2026-10-20): HDR sources are tone mapped and stripped of HDR side data at the plugin, hardware decoding follows Jellyfin's setting, h264_vaapi works.
Stream health since 1.12.0.0 (2026-10-20): Status shows per session whether the hardware decoder is really in use, CPU, real-time margin and problems; Hardware section with a decode/encode test.
Weather channel since 1.14.0.0 (2026-10-23): `ScheduleMode.Weather`, Open-Meteo provider, Skia-drawn screens with a live per-place clock, xfade cycle on the station clock, hourly guide entries, music timeline from Jellyfin music and a folder, poller with disk cache and staleness, editor with place search and previews.

---

## Phase 1 — remove the reasons to hand-pick lists

### Step 1. Rating and year filters on sources — DONE 2026-10-14

**Goal.** Genre, playlist, collection, and library sources (and series sources for year) can be
limited by official rating and production year, so channels like Family Movies and Retro Movies
can be genre-driven and pick up new library additions automatically.

**Model.** On `ContentSource`:

- `Ratings: List<string>` — allowed official ratings, empty = any. Matched case-insensitively
  against `OfficialRating` ("G", "PG", "TV-Y7", ...). Unrated items match only when `"NR"` is in
  the list (treat null/empty/"Not Rated"/"Unrated"/"NR"/"Approved"/"Passed" as the `NR` bucket).
- `YearFrom`, `YearTo: int?` — inclusive production year range; items without a year are dropped
  when either bound is set.
- `MaxRating: string?` — alternative to listing ratings: "at most PG-13" using the two ladders
  below. Pick one of `Ratings` or `MaxRating` in the UI; the compiler honours both if both are set.

Rating ladders (used by this step and by Step 2):

- Movies: G < PG < PG-13 < R < NC-17
- TV: TV-Y < TV-Y7 < TV-G < TV-PG < TV-14 < TV-MA
- A movie rating and a TV rating are comparable by rank position (G ≈ TV-G, PG ≈ TV-PG,
  PG-13 ≈ TV-14, R ≈ TV-MA). `NR` is above everything unless the user allows it explicitly.

**Code.**

- `Library/RatingLadder.cs` (new, pure): `Rank(string? rating)`, `IsAtMost(rating, max)`,
  `IsUnrated(rating)`. Tests cover both ladders, cross-ladder comparison, unknown strings.
- `LibraryContentResolver.ExpandSource`: apply `FilterRating`/`FilterYear` after the existing kind
  and season filters, for every source kind except `Movie`/`Episode` (an explicit pick is always
  included).
- `StationService.NormalizeSources`: trim/upper-case ratings, swap inverted years.
- Dashboard chip editor: "Max rating" dropdown (ladder values plus "any"), "Allow unrated"
  checkbox, "Years from / to" number inputs. Badge shows e.g. `≤ PG · 1980–1999`.
- README: one paragraph under source tuning.

**Verification.** Unit tests for the ladder and the two filters (construct `Movie`/`Episode`
entities as in `SourceFilterTests`). On the dev container, tag test items with ratings/years and
preview a genre source with each filter. Then rebuild Family Movies and Retro Movies in
`examples/stations/` as genre sources with filters and compare item counts against the hand-picked lists.

**Size.** Small. One day including UI.

### Step 2. Time-of-day rating gate — DONE 2026-10-14

**Goal.** A pool-level rule such as "nothing above PG before 20:00" or "TV-MA only after 22:00",
so mixed channels (Anime, Action, Comedy) are safe during the day without building slot schedules.

**Model.** On `PoolConfig`:

- `DaypartRules: List<DaypartRule>` where `DaypartRule { StartLocal "HH:mm", EndLocal "HH:mm",
  MaxRating string, AllowUnrated bool }`. Times are in the station time zone; a rule may wrap
  midnight. Empty = no gate. Several rules may overlap; the strictest applies.

**Scheduling design.** Cycles are laid out in advance, so eligibility is checked while building
the order, not at play time:

- `CyclicScheduler.BuildCycle` already knows the cycle's start instant and each slot's offset, so
  it can compute each slot's local start time as it walks the order.
- New pass `DaypartGate.Apply(order, items, cycleStartUtc, padded, rules, tz)`: walk positions
  left to right; if the item at position *i* is not allowed at the slot's local time, swap it with
  the first later item that is allowed there and whose own move keeps it allowed at position *i*'s
  time (greedy, deterministic). Items that cannot be placed anywhere stay where they are and the
  compiler reports them as `Excluded` with reason `Dayparted` so the user sees why.
- Runs for marathons: the gate operates on runs (use the run's first slot time) so marathons stay
  intact. Episodes-in-order is re-applied afterwards (same trick as today).
- Slot blocks: not gated in this step; blocks are already time-bound by definition. Filler gets
  the same gate as pools in a follow-up if wanted.
- Slate fallback: if a cycle ends up with a stretch where nothing is allowed, the gap is already a
  slate; no new behaviour needed.

**Code.** `Scheduling/DaypartGate.cs` (pure), hook in `CyclicScheduler`, `ScheduleInput` carries
the rules, `StationService` validation (times parse, rating in ladder), `PlayableItem` already has
`OfficialRating`. UI: a "Daytime rules" list under the pool options, rows of from/to time pickers
and a max-rating dropdown. Compile result shows a "dayparted" count next to excluded.

**Verification.** Unit tests: an R-rated item never starts before 20:00 local across 7 days of
enumeration in `America/New_York`; a cycle with only R-rated items and a 24-hour PG rule yields all
items excluded; marathons survive the gate. Dev container: preview an Anime-like pool across a
day boundary.

**Size.** Medium. The greedy placement and its interaction with marathons is the risky part;
budget two days, most of it tests.

### Step 3. Content preview with exclusions — DONE 2026-10-14

**Goal.** See exactly what a station will play and remove individual items without touching the
sources.

**Model.** On `Station`: `ExcludedItemIds: List<Guid>`. Applied in `StationCompiler` after
expansion for pools, blocks, and filler; lineups ignore it (they are explicit already). Excluded
items are reported in the compile result with reason `UserExcluded` so the count stays honest.

**API.**

- `GET /TvStation/Stations/{id}/Items?offset&limit&sort` — compiled items of the saved station
  with title, episode title, season/episode, year, rating, runtime, genres, source label, and
  whether excluded. Served from the compiled snapshot, so it is cheap.
- `POST /TvStation/Schedule/PreviewItems` — same for an unsaved draft (compiles in memory, like
  schedule preview; capped at 5000 items).
- Exclusion toggling is just the station PUT with the updated list; recompile happens as today.

**UI.** A "Content" tab in the station editor: table with sort, a text filter, counts per source,
and an "exclude" toggle per row. Excluded rows stay visible, greyed, with an "include" toggle.
Totals line: items, hours of unique content, hours per cycle (pool).

**Verification.** Compiler unit test: excluded id removed from pool/blocks/filler, kept in lineup,
reported once. Dev container: exclude an episode, confirm the preview no longer schedules it and
the station row's excluded count rises by one.

**Size.** Small to medium; mostly UI. One day.

---

## Phase 2 — make it feel like a channel

- **Bumpers and interstitials — DONE 2026-10-14.** Per-station toggle, clip sources, max clip
  length, optional reserved break after every program; gaps fill with seeded clips and fall back to
  the station card, whose text is now configurable per station.
- **Logo overlay on video — DONE 2026-10-15.** ffmpeg `overlay` filter with the station logo in a
  chosen corner, per-station toggle, opacity and size; default off. If the filter fails the stream
  carries on without it for that session.
- **Re-anchor from now — DONE 2026-10-14.** "Set to now" in the editor and "Start schedule from
  now" on the station row, rounded down to the padding grid. No "start with this item" pick yet.
- **Holiday calendar presets — DONE 2026-10-14.** Preset dropdown in the season block.
- **Time blocks on pools — DONE 2026-10-15.** A Pool station can carry weekly blocks (the same
  editor as Slots mode); the pool plays between them with its shuffle, series spacing, marathons
  and daytime rules intact, resuming where it left off. Slots-mode filler is unchanged.
- **Refresh guide after logo upload — already in place** (`SaveLogo` triggers the guide refresh).

## Phase 3 — curation help

Order: import by name first (it makes the station files shippable as examples), then the
analyzer, then suggestion rules and the dashboard view on top of it.

- **Resolve import sources by name — DONE 2026-10-15.** `StationImportResolver` looks up every
  item reference whose id is unknown here by kind and display name (year breaks ties for movies,
  episodes fall back to series + S/E numbers, seasons to the number); unmatched references are
  dropped and listed in the import result. Real-server set moved to `examples/stations/`.
- **Library analysis — DONE 2026-10-15.** `LibraryAnalysisService` collects `LibraryFact`s per
  library (episode rating/genres/tags inherited from the series) and `LibraryAnalyzer` aggregates
  them: totals, kids/family/unrated hours, genres, series, collections, franchises
  (`FranchiseDetector`, title prefixes), decades, ratings, tags, libraries. Cached 15 minutes,
  `GET /TvStation/Library/Analysis?refresh=true` recomputes. The health report reads the same data.
- **Suggest stations — DONE 2026-10-15.** `StationSuggester` (pure, tested) over the analysis:
  genres ≥ 8 h, series ≥ 20 episodes, Kids TV (libraries capped at G) and Family Movies (PG),
  franchise marathon lineups (≥ 3 titles) plus a "Franchise Nights" slots station, collections,
  decades ≥ 8 h, seasonal channels from holiday tags with their guide window. "Suggest stations"
  view on the dashboard; `GET /TvStation/Suggestions`, `POST /TvStation/Suggestions/Create`.
- **Franchise detection — DONE 2026-10-15** as part of the analyzer (`FranchiseDetector`). A
  "pick a franchise" source type can build on it later.
- **Block recurrence — DONE 2026-10-16.** `SlotBlock.Recurrence`: weekly (default), every N
  weeks from a start date, chosen weeks of the month (Monday-based weeks, 5 = last), or listed
  dates. `RecurrenceRule` decides per local date; the weekly scheduler asks it when listing a
  week's occurrences, so cursors still carry across sparse occurrences. Built for "a Harry
  Potter weekend on some weekends". Still open: a custom guide title per block.
- **Block templates — DONE 2026-10-15.** Bookmark button on a block saves it under its name
  (`/TvStation/BlockTemplates`, stored in settings); the dropdown next to "Add block" starts a new
  block from a template; Settings lists and deletes them.
- **Exclusion by tag — DONE 2026-10-14.** Per-station and global excluded tags (hand picks and
  Tag sources for the same tag exempt), a Tag source kind, `GET /TvStation/Library/Tags`, and
  "by tag" rows in the content view. Changing the global list recompiles every station.
- **Other rule exclusions — DONE 2026-10-15.** "Skip items shorter than N minutes" per station
  (pool, filler, blocks; reason `TooShort` in the content view). Items without a runtime were
  already skipped.

## Phase 4 — operations and polish

- **Clearer failure diagnostics — DONE 2026-10-19.** `FileProbe` tells permission denied, file
  not found and folder not found (unmounted share) apart before a program is skipped. Every
  streaming fault (unopenable file, early ffmpeg exit with its last stderr line, overlay or slate
  failure, ffmpeg not starting) is kept per station in `StreamSessionManager` after the session
  ends and shown on the station row until dismissed (`POST Stations/{id}/Stream/ClearError`).
- **Health report — DONE 2026-10-19.** The compiler records what every source found and kept
  (`CompiledStation.Sources`); `StationHealth` (pure, tested) turns the last compile into
  warnings: sources that matched nothing or contributed nothing, under a day of unique content,
  items skipped for no runtime / path / file. `GET /TvStation/Health` feeds a Health section on
  the Status view; the station row carries "empty source" and "N h unique" badges and a
  by-reason tooltip on the excluded count. It reflects the nightly recompile; no extra task.
- **Same movie twice in a row — FIXED 2026-10-19.** The back-to-back rule only compared series ids,
  so a film could follow itself across the seam between two shuffle cycles (seen with Dogma on
  Comedy). `PoolOrdering.KeyOf` now keys the rule on the series, or the movie's title and year
  (two library entries of one film count as one); the cyclic scheduler re-checks the seam after
  the daypart gate against the last program the previous cycle actually airs. Stations whose
  shuffle never produced a repeat keep their timeline; the rest change only at those seams.
- **Now and next on the station row — DONE 2026-10-19.** Time left in the current program and
  the next two programs with start times (bumper clips skipped; a slate only when nothing else
  follows); the list refreshes itself every minute while it is on screen.
- **Richer guide entries.** Thumbnails, season/episode numbers, year and rating confirmed flowing
  into the guide on the dev server 2026-10-16 (movie poster, episode thumb, series-poster fallback
  all served; channel logo too). Image paths are captured at compile time, so artwork added later
  shows after the nightly recompile. Still open: a "New" tag for recently added items. Small.
- **Filler gate and block-level dayparting.** Extend Step 2 to slots mode if needed.

---

## Phase 5 — broadcast feel and reach

From a 2026-10-15 survey of ErsatzTV, Tunarr, dizqueTV, PseudoTV Live and Airwave. We already
match their core (shuffle with episodes in order, marathons, time blocks, filler, dayparts,
seasonal channels, watermark, suggestions, native guide). These are the gaps worth closing, in
order; together they turn a station into a broadcast.

1. **Pre-roll and post-roll bumpers — DONE 2026-10-16.** `BumperConfig.PreRoll` / `PostRoll`
   (enabled flag plus optional own sources; empty = the main bumper sources). `BumperFill.Plan`
   opens each break with the post-roll clip and closes it with the pre-roll ident, so programs
   stay on the grid; the shuffled gap fill runs between them and a position is skipped when no
   clip fits or no program is adjacent (gated slots, filler-only slates). Clips carry a
   "Pre-roll" / "Post-roll" label in the preview and content view. Verified on the dev
   container (schedule API and stream).
2. **Mid-roll breaks at chapters.** Per station: break every N minutes or at Jellyfin chapter
   marks (whichever the user picks), minimum break length, bumpers fill the break. The
   restreamer plays the program as segments (seek to the chapter, same clock), the schedule
   entry stays one guide item. Medium; the risky part is the segment handover in
   `FfmpegRestreamer`, verify on the dev container with the stream test.
3. **Cooldown — DONE 2026-10-19.** `PoolConfig.CooldownHours` (0 = off, clamped to a week). The
   cyclic scheduler applies it on the timeline after the daypart gate (`ApplyCooldown`): with a
   cooldown each round is built from the one before as it actually aired (a chain from the anchor,
   cached), a series or movie that airs too soon is swapped with a later or earlier run when that
   leaves fewer violations, keeps neighbours distinct and puts nothing new behind the gate; keys
   that cannot possibly rest enough in one round are left alone, and the search has a budget.
   Covers the seam between rounds and weighted repeats. Not applied by the weekly scheduler
   (stations with time blocks); the Movie night preset sets 8 h. Dashboard field under the pool
   options.
4. **"Up next" card — DONE 2026-10-21.** `Station.UpNextCard` (enabled, corner, lead times before
   the end (a list, e.g. 1h/30m/5m), show seconds, height %, opacity, thumbnail, times, 24-hour clock, animation
   (none/fade/slide/both, default fade); off by default). Slide is the overlay's x expression in `t`; fade turns
   the still into a frame-per-tick stream inside the graph (`loop` + `setpts`, `trim`med at the last window so
   it never outlives the program) and drives a `colorchannelmixer@fade` alpha with `sendcmd` `[expr]` commands
   (`TI`, `1-TI`), one pair per window. Looping the PNG as a second demuxed input (`-loop 1 -framerate`) drew
   the card ~8 s late on the live stream (never in a standalone run of the same command line), so the card
   stream is generated in-graph; the still-image overlay (none/slide) was always on time.
   Burned into the picture, since Jellyfin clients give a plugin no way to raise a toast during
   playback, which also keeps it in step with the picture under HLS latency. Per program the
   restreamer finds the next `Program` entry (bumpers and slates skipped, 6 h lookahead), has
   ffmpeg render a PNG card (`FfmpegArgsBuilder.ForUpNextCard`: translucent `color` panel, the
   artwork scaled into a left box, `drawtext` lines, no image library) into `data/TvStation/cards`,
   and overlays it with `enable='between(t,from,to)'` on the segment that ends the program
   (`UpNextCard.Window`; multi-part items only on the last part or when cut). Falls back to a card
   without artwork, then disables the card for the session, each with a station fault. Unit tests
   cover the lookup, window, times, layout and both ffmpeg argument lists.

Considered and parked: off-air hours (a daypart rule approximates it),
custom guide titles per block, studio and people sources, smart (search) collections, multi-part
episode grouping, rerun blocks, channel redirects, on-demand channels (contradicts the always-on
clock), DVR (files are already in the library), music videos, AI lineup builder (the rule-based
suggester covers it; see Phase 7 for what a model is and is not allowed to do). Revisit when someone asks.

---

## Phase 6 — easy for non-technical people

From a 2026-10-19 review of the dashboard: the base is solid but speaks engineer. In payoff order.

1. **Guided start and presets — DONE 2026-10-19.** With no stations, the list sends the person to
   the suggestions with a welcome banner ("Create selected" or "Start from a preset"). "New
   station" opens a preset page (Shuffle a collection, Kids channel, Movie night, Comfort reruns,
   Holiday channel, One show start to finish, Weekly schedule, Play a fixed list); a preset fills
   in mode, order, marathons, padding, family hours, season window and tag behaviour, and can cap
   the rating or item kind of every source added afterwards (`PRESETS` in tvstation.js).
2. **Plain words — DONE 2026-10-19.** Modes read "Shuffle a collection" / "Weekly schedule" /
   "Play a fixed list" with examples; "Recompile" is "Refresh content" everywhere; daytime rules
   are "Family hours"; the revision is no longer shown. Call sign, padding grid, anchor, card
   text, excluded tags, minimum length and encoding sit under an **Advanced** fold (excluded
   tags and minimum length moved to a "Leave out" section on step 2 on 2026-10-19: too hidden
   for something about content). Health
   warnings carry "Refresh content" / "Add content" buttons and the row's warning badges open the
   station. Checked in Chrome on the dev server 2026-10-19: also fixed the schedule DTO sending the
   combined "Series - Episode" title (rows and previews doubled the episode name), added a Save
   button to the editor toolbar, and reworded the remaining compile / pool / slot wording.
3. **Three-step editor — DONE 2026-10-19.** A step bar on the editor: Name and look (General,
   Logo with "Make a logo from the name": an 800x450 card drawn on a canvas, color suggested from
   the name, uploaded right after the first save for a new station; since 1.10.3.0 the background is one of six
   patterns drawn from a seeded PRNG over the name, number, sources and an "Another look" counter; 1.10.4.0 added "Pick the color too", which draws the color from the same seed), What plays (mode, sources,
   order, bumpers, content list), When (family hours, time blocks, season, preview). Sections carry
   `data-steps`; "All settings" is the whole form and is remembered per browser. A refused save
   lands on the step with the problem.
4. **Paint the week — DONE 2026-10-19.** `renderWeekGrid` draws seven day columns of 30-minute rows
   above the block cards (blocks crossing midnight continue in the next column, non-weekly ones
   are outlined); pointer events paint a selection and `addBlockFromPaint` turns it into a block
   (a click gives an hour, a drag the rows and days covered, the template select applies),
   clicking a block expands its card. Cards stay the editing surface; moving or resizing on the
   grid is not supported.
5. **Not on a channel — DONE 2026-10-19.** `GET /TvStation/Library/Coverage` compares the library
   facts behind the analysis (now cached with it) against the compiled content of every enabled
   station (`includeDisabled=true` counts paused ones; bumper and ident clips count) and returns the movies
   and series left out, series with only some episodes covered carrying the covered count
   (`LibraryCoverage`, pure). The dashboard page lists both with a filter and tick boxes; "New
   channel with selected" opens the editor with the titles as pool sources, "Add selected to
   channel" appends them to an existing station's pool, filler or lineup by mode.
   1.10.2.0 fixed the page's filter box: an `is="emby-input"` element must not carry the
   `emby-input` class itself, or Jellyfin's upgrade throws and every later element stays plain.
6. **Smaller wins.** Duplicate station, "Pause" instead of a Disabled badge, generated logos, a
   one-line plain summary per row, a read-only "What's on" page for non-admins.

---

## Phase 7 — smarter stations

From the 2026-10-23 discussion and the `prototypes/TvStation.Brain` experiment (LLamaSharp + Qwen2.5
1.5B on the CPU, results in `prototypes/TvStation.Brain/RESULTS.md`). What the prototype settled:
a 1.5B model referring to titles by id over the whole library produces valid-looking garbage; by
exact title under a grammar over ~30 candidates it ranks acceptably; it cannot do times, durations
or de-duplication (0/5 valid stations) and a repair round only helps on short prompts; without a
grammar it invents titles. Cost is ~1.2 GB RSS, 1 GB on disk, 16 s to read a 160-title library on a
fast desktop. So: every decision about *when* stays in code, models only ever rank real items or
write text, and the plugin never asks a model for ids. Everything below is offline; nothing talks to
the internet.

In payoff order. Steps 1-3 need no language model at all.

1. **Embedding index and "Describe the channel".** A small sentence-embedding model (bge-small or
   nomic-embed-text, 30-130 MB GGUF, through `LLamaEmbedder` in LLamaSharp, CPU only, ~10-50 ms
   per item) embeds every movie and series once (name, year, genres, tags, first sentence of the
   overview) into `data/TvStation/embeddings.bin`, refreshed with the library analysis for changed
   items only. Features on top of it:
   - A text box on the station preset page and on the sources step: "Describe what should play"
     returns the 30 nearest titles with tick boxes and a similarity bar; "Use these" adds them as
     pool sources (or a lineup). Output is always a ranked list of real items, so nothing can be
     invented and the editor stays the editing surface.
   - A `SourceKind.SimilarTo` content source: seeds (item ids) plus a count or a similarity floor,
     expanded at compile time to the nearest items; recompiles when the index changes.
   - The suggester gets **theme channels**: cluster the embeddings (and tag co-occurrence) and
     propose a station for every cluster with enough hours, named after its most frequent tags
     ("Heists", "Time travel", "Space westerns"). Not-on-a-channel leftovers are grouped the same
     way so "New channel with selected" can offer coherent groups instead of one pile.
   The native runtime ships in the plugin zip (linux-x64 and win-x64 CPU backends, ~30-60 MB);
   `meta.json` `assemblies` lists only the managed DLLs so a Windows host never loads a native
   `.dll` as an assembly. The model file is a one-time download from the plugin's own GitHub
   release with a checksum, or a file the person drops in the data folder; until it is there the
   features are shown greyed with a one-line explanation. Pure parts (`Library/Embeddings/`:
   cosine search, clustering, naming) get xUnit tests with fixed vectors; the model call is behind
   an interface so tests never load a model. Index a 2,000-movie library inside a scheduled task,
   never in a web request. Medium-large.
2. **Watch-history awareness.** Jellyfin's per-user played state and favorites, read through
   `IUserDataManager` for the users the admin ticks in Settings ("Our household"). Per station, all
   off by default:
   - "Skip what we finished in the last N days" (cooldown keyed on real playback, not airing).
   - "Favorites play more often" (weight multiplier on favorited items).
   - "Follow our progress": a series source plays from the first unwatched episode and advances as
     the household watches, in `ShuffleEpisodesInOrder` and `Sequential` orders; the schedule stays
     deterministic because progress is sampled at compile time and recorded in the compiled
     station (recompile when it changes, like the library refresh).
   Pure logic in `Scheduling/WatchHistory*.cs` with tests; the Jellyfin reads live in
   `Library/`. Fingerprint: these change the timeline, so they are part of it. Medium.
3. **Day shape preset and "why this is playing".**
   - One control on the When step, "Shape the day like a channel": a dropdown of shapes (Kids
     daytime, Family evening, Grown-up nights, Weekend movie marathons) that writes the daypart
     rules and, for pools with blocks, a feature block at prime time, instead of asking for six
     rules. Shapes are data in `tvstation.js` like `PRESETS`.
   - The compiler already knows why every item is on the timeline (source, gate, cooldown, run);
     keep that trace per entry and show it on the Now and next row and in the content preview:
     "Action genre · PG-13 allowed after 20:00 · 9 days since it last aired". Small-medium.
4. **People, studios and keywords as sources** (parked in Phase 5, unparked here because step 1's
   candidate lists need them): `SourceKind.Person` (actor or director, with the role), `Studio`,
   and `Tag` already exists. Resolver queries via `ILibraryManager` with `PersonIds` / `StudioIds`;
   name matching on import like the other kinds. Small.
5. **Local model endpoint for station authoring (optional, advanced).** A Settings field "Local
   language model URL" for an OpenAI-compatible server on the LAN (Ollama, LM Studio,
   llama-server), with model name and a test button; nothing ships in the zip for this. When set,
   the preset page shows "Make a channel from a sentence": the request goes to the model with the
   embedding search as a tool (`find_titles(query, filters)`), the existing station shapes as the
   output schema, titles referenced by exact name, and a validator that checks ids, ratings against
   dayparts, runtime versus block length, duplicates, and feeds problems back once. The result opens
   as an unsaved draft in the editor; nothing is saved without the person. Requires a 7B+ model to
   be useful; the dashboard says so next to the field. Pure parts (prompt assembly, schema,
   validator, repair) in `Library/Authoring/` with tests against recorded model replies. Medium.
6. **In-process tiny model for text only (defer; maybe never).** Qwen2.5 1.5B or Qwen3 0.6B at Q4
   through the same LLamaSharp runtime as step 1, used solely for prose: channel descriptions, block
   names, up next blurbs, the one-line plain summary per station row. Grammar-constrained picking
   from a step-1 candidate list is possible (prototype: most picks sensible) but adds 1 GB and a
   second model download for a small gain over ranking by similarity, so it waits for someone to
   ask. If it comes: on-demand load, thread cap, unload after the job, run inside a scheduled task,
   refuse unknown model files, and the KV prefix cache from the prototype (157 MB per 5.7k tokens,
   0.07 s reload) if whole-library prompts are ever wanted.

Not planned: any cloud model, or sending library data anywhere. The design-time path (export a
library digest, have any large model write station JSON, import by name) already works through the
Import page and stays the power-user route; a "Copy library digest" button on the Import page is a
small add if someone asks.

---

## Phase 8 — Weather channel — steps 1-6 DONE 2026-10-23 (1.14.0.0)

Shipped as planned below with these differences. The **starter pack** is not a zip on the plugin's
release but a manifest (`Assets/music-packs.json`) of thirty Kevin MacLeod pieces (CC BY 4.0) with
URLs on incompetech.com and SHA-256 checksums; `MusicPackService` fetches them into
`data/TvStation/music/lounge/` on request and writes CREDITS.txt, so nothing is re-hosted and the
private repo is not in the path. A second pack (HoliznaCC0, CC0) is "manual": the Free Music
Archive needs a login, so the editor gives links and the folder. The music folder setting takes
several folders separated by ";". No title card between
places and no alerts screen yet (step 7). The clock is `drawtext` with an offset per place rather than
a re-rendered PNG, so it ticks every minute at no render cost; the screens themselves are re-rendered
when the report, the hour or the look changes. Per-track volume is a plain `volume` filter, not
`loudnorm`. Verified on the dev container: a 150 s tune-in ran one 4-minute segment of 31 screens
and 6 music pieces with no PCR jump, the clock advanced in each place's zone, credits drawn.

From the 2026-10-23 request: a station in the spirit of The Weather Channel's "Local on the 8s".
No voice. It cycles through a few locations, shows current conditions and forecasts as full-screen
graphics, and plays royalty-free music underneath, with a way to add more music. This is the first
feature that talks to the internet; it is per station, opt-in, and the only thing it ever sends is a
latitude and longitude.

**Data.** Default provider is [Open-Meteo](https://open-meteo.com): no account, no key, global,
current conditions plus hourly and 7-16 day daily forecasts in one call, with a geocoding endpoint
for turning "Boston" into coordinates. Its free tier is for non-commercial use under 10,000 calls a
day (a household with five locations polling every 15 minutes makes ~500) and the data is CC BY 4.0,
so every weather screen carries "Weather data by Open-Meteo.com" in small type. Behind an
`IWeatherProvider` interface (`Weather/`) so the US National Weather Service (`api.weather.gov`:
free, no key, has **alerts**, needs a User-Agent) can be added as a second provider for US locations
and alerts, and a keyed provider later if anyone asks. Everything in the plugin speaks one neutral
`WeatherReport` model (observed at, current, hourly[48], daily[7], alerts[], units converted in code
from the provider's metric values).

**Rendering.** Screens are PNGs drawn with **SkiaSharp**, which the Jellyfin server ships for its own
image pipeline. Verified on the dev container (Jellyfin 12.2 image) on 2026-10-23: `/jellyfin` holds
`SkiaSharp.dll` 3.119.4 with `libSkiaSharp.so`, plus HarfBuzzSharp; a plugin that references the
`SkiaSharp` package with `ExcludeAssets="runtime"` (as it does `Jellyfin.Controller`) resolves the
server's copy from the default load context and draws text to a PNG without shipping any native
library (probe: 640x360, DejaVu Sans, 12 KB PNG). The host image has 41 font families (DejaVu, Noto
CJK, IPA, UnFonts) but no modern UI sans, so the zip ships one open-licensed font (Inter or Noto
Sans, OFL) loaded with `SKTypeface.FromFile`, with DejaVu Sans as the fallback, and an open-licensed
weather icon set rendered to PNG at two sizes, keyed by WMO weather code and day/night. The layout is
a pure model (`Weather/Screens/`: given a `WeatherReport`, the location, the clock and the picture
size, produce text and icon placements; xUnit tests check placements and measured-text fits, never
pixels); a thin Skia painter turns it into a PNG. Two or three colour themes (the classic blue
gradient, dark, light) and the station logo in the corner. Version coupling: the plugin follows the
server's SkiaSharp major, pinned with the Jellyfin version it targets. The ffmpeg in the image
(8.1.3-Jellyfin) has `drawtext`, `xfade`, `fade`, `overlay`, `loudnorm`, `afade` and `acrossfade`,
so the slideshow and the music mix need nothing extra.

**Screens per location**, each a fixed number of seconds (default 8-12, configurable), in order:

1. Current conditions: big temperature, icon, condition text, feels like, humidity, wind with
   direction, pressure trend, visibility, sunrise and sunset, "as of 3:47 PM".
2. Next 12 hours: a strip of hourly icon, temperature and rain chance.
3. 7-day extended forecast: columns with day name, icon, high, low, rain chance.
4. Alerts, only when the provider has one for the location (headline and until-when; the full text
   scrolls if it fits the screen time).
5. Optional: "Travel cities", every configured location on one screen; "Almanac" (today's normal
   high and low, record, moon phase) when the provider offers it.

A lower band on every screen carries the station name, the location, the local time in that
location's zone (not the station's), and the music credit ("♫ Lobby Time · Kevin MacLeod"). Screens
within a location cross-fade; locations are separated by a short title card ("Denver, CO"). The whole
pass over all locations is the **cycle**; a cycle for five locations with four screens each at ten
seconds runs about 3.5 minutes.

**Schedule.** A new `ScheduleMode.Weather` (the station's content is the cycle, not library items).
The compiler emits one `EntryKind.Program` per hour (or per padding grid) titled from a template,
default "Local weather: Boston, Denver, Austin", so the Live TV guide reads like a channel and not
like 400 three-minute programs; the cycle repeats inside the entry from the station clock, so every
viewer sees the same screen at the same moment and tune-in lands mid-cycle like real TV. Option
"Start a pass on the 8s": the cycle restarts at :08, :18, :28..., and fills the time before the next
eight with the travel-cities screen and the current conditions of the first location. Pure and
deterministic (`Scheduling/WeatherCycle.cs`: given the clock and the config, which screen of which
location is on and how long it has left), tested like the other schedulers.

**Playback.** The restreamer treats a Weather entry as a run of short ffmpeg segments on the one
clock, the same handover the multi-part movies use: each segment is one location pass (or one
screen when a report changed mid-pass), built by `FfmpegArgsBuilder.ForWeather`: the screen PNGs as
inputs with `-loop 1 -t`, `xfade` between them, the audio from the music timeline, the usual
`-output_ts_offset` and pacing. Rendered PNGs live in `data/TvStation/weather/{station}/` and are
regenerated only when the report or the theme changes (never inside the segment loop). When the
provider is unreachable the last report keeps showing with its real "as of" time; after a configurable
staleness (default 3 h) the screen says so plainly ("Forecast unavailable since 2:10 PM") instead of
showing old numbers as current. Startup with no report yet shows the title card and music until the
first fetch lands (seconds).

**Fetching.** A hosted poller (`Weather/WeatherService.cs`, `IHttpClientFactory`, timeouts, one
retry, jittered schedule): current conditions every 15 minutes, forecast hourly, only for stations
that are enabled and only while they are listed in the guide (an inactive season window stops the
polling). Reports are cached on disk so a restart has something to show. The Status page shows the
last fetch time and error per station, and a station fault is raised after repeated failures. Honest
User-Agent with the plugin name and version.

**Music.** The audio is its own deterministic timeline: a seeded shuffle of the station's tracks
played back to back from the anchor (`Scheduling/MusicTimeline.cs`, pure; `TrackAt(utc)` gives the
track and offset, so any segment can `-ss` into the right track and, when a track ends mid-segment,
the next one follows through the `concat` demuxer with `inpoint`). Tracks come from two places and
the station can use both:

- **Jellyfin music**: `ContentSource` kinds for music (`Playlist` already exists; add `Album`,
  `Artist`, `MusicGenre`), resolved to audio files through the existing resolver. This is "add more":
  drop albums in the music library, tick them on the station.
- **A folder**: `data/TvStation/music/<station or shared>/` scanned at compile time (mp3, flac, m4a,
  ogg), for people without a Jellyfin music library.

A **starter pack** is downloaded on demand from the plugin's own GitHub release with a checksum (like
the Phase 7 model file), never bundled in the zip: eight to twelve instrumental tracks in the smooth
jazz and light electronic register the format is known for, every one under CC0 or CC BY 4.0, with
a `CREDITS.txt` carrying title, artist, source URL and licence, and the on-screen credit line drawn
from the file's tags. Only licences that allow redistribution go in the pack (CC0 from the Free Music
Archive; Kevin MacLeod's catalogue is CC BY 4.0 and widely redistributed with credit, but confirm
incompetech's own licence page before including any track; Pixabay and YouTube Audio Library forbid
redistribution and are left for people to download themselves). "Royalty free" and "free to
redistribute" are different things, and the pack respects the second. Volume normalisation with
`loudnorm` at compile time or `-af volume`, so a quiet flac and a hot mp3 sit at the same level; a
0.5 s crossfade between tracks is a later polish.

**Editor.** The "New station" preset page gets "Weather channel". The editor for a Weather station
has three steps like the others: Name and look (theme, logo, units: °F/°C, mph/km/h, 12/24 h, on the
8s), Locations (a search box backed by the geocoding endpoint through the plugin's API, so the browser
never calls the provider; results as pick-one rows; up to eight locations, drag to order, a per-
location display name), Music (sources, folder, "Get the starter pack" button with progress, track
list with play length, total hours). The content preview shows the cycle as a list of screens with
times, and the guide shows the hourly entries.

**Steps, in order.**

1. `WeatherReport` model, Open-Meteo provider with recorded JSON fixtures, WMO code to icon and
   text mapping, unit conversion. Pure and tested. Small.
2. Screen layouts, the Skia painter and the `ForWeather` argument builder; render the four screens
   for one fixture report to PNG on the dev container and eyeball them at 720p and 1080p. Font and
   icons in the zip. Medium.
3. `ScheduleMode.Weather`, the cycle and the hourly guide entries; restreamer plays the cycle with
   silence. Verify with the stream test (mid-cycle tune-in lands on the right screen). Medium.
4. Music timeline, music sources and folder, segment audio with concat and inpoint; verify no gap or
   jump at a track boundary and across a segment handover. Medium; the risky part is audio
   continuity across ffmpeg processes, which the video path already solves with the PCR offset.
5. Poller, disk cache, staleness screen, Status page row, faults. Small-medium.
6. Editor: preset, locations step with geocoding, music step, starter pack download. Medium.
7. Polish: alerts screen (and the NWS provider for US alerts), travel cities, almanac, crossfades,
   themes, "Local weather at the top of the hour" as a two-minute block type on ordinary stations
   (the cycle runs as a bumper-length program, which is the real Weather Channel's place in a cable
   lineup). Only after 1-6 are on the real server.

Not planned: speech or a synthesized voice (the user asked for none; a local TTS could be a later
option under Phase 7's rules), radar imagery (needs tiles from a map provider and a licence check),
and any provider that needs the person's own paid key before the free ones have shown their limits.

---

## Backlog

Dropped from the plan on 2026-10-19; here in case it is ever wanted again.

- **M3U and XMLTV export.** `GET /TvStation/Channels.m3u` and `GET /TvStation/Guide.xml` with a
  per-plugin access token in the URL, serving the same MPEG-TS stream the Live TV path uses and
  the same schedule as XMLTV, so VLC, Kodi, Tivimate, Plex or Emby could tune in. Not needed
  while every viewer uses a Jellyfin client. Medium.

---

## Open questions

- Rating ladder for non-US ratings (BBFC "12A", "15"; German FSK). Start with the two US ladders
  and treat unknown strings as unrated; add mappings when someone needs them.
- Whether a daypart rule that excludes everything should fall back to a slate or to the least
  restrictive allowed item. Plan says slate; revisit after seeing a real schedule.
- Preview item cap for very large libraries (10k+ episodes). 5000 with paging is a guess.
