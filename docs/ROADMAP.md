# TvStation roadmap

Working plan for scheduling and curation features. Phase 1 is the agreed next three steps, in order.
Later phases are candidates, roughly ordered by payoff per effort; reorder freely.

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
suggester covers it). Revisit when someone asks.

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
