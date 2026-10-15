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

- **Clearer failure diagnostics.** Distinguish permission denied from not found when a file
  cannot be opened; surface the last encoder error on the station row. Small.
- **Health report.** Nightly summary on the dashboard: sources that expanded to zero items,
  stations under one day of unique content, excluded counts by reason. Small.
- **Now and next on the station row.** Next two programs and time to switch. Small.
- **Richer guide entries.** Confirm thumbnails, season/episode numbers, year, rating flow into the
  guide; add a "New" tag for recently added items. Small.
- **Filler gate and block-level dayparting.** Extend Step 2 to slots mode if needed.

---

## Phase 5 — broadcast feel and reach

From a 2026-10-15 survey of ErsatzTV, Tunarr, dizqueTV, PseudoTV Live and Airwave. We already
match their core (shuffle with episodes in order, marathons, time blocks, filler, dayparts,
seasonal channels, watermark, suggestions, native guide). These are the gaps worth closing, in
order; the first three together turn a station into a broadcast, the fourth lets clients outside
Jellyfin tune in.

1. **Pre-roll and post-roll bumpers.** Per station: an ident before every program and/or a clip
   after it, chosen from the bumper sources (optionally separate pre/post lists), on top of the
   existing gap fill. Pre/post time comes out of the reserved break so the grid still holds.
   Small; builds on `BumperFill`.
2. **Mid-roll breaks at chapters.** Per station: break every N minutes or at Jellyfin chapter
   marks (whichever the user picks), minimum break length, bumpers fill the break. The
   restreamer plays the program as segments (seek to the chapter, same clock), the schedule
   entry stays one guide item. Medium; the risky part is the segment handover in
   `FfmpegRestreamer`, verify on the dev container with the stream test.
3. **Cooldown.** Per pool: a movie or series may not repeat within N hours. Implemented in
   `PoolOrdering` as a soft rule after series spacing (swap forward until the gap is met, give up
   quietly like the other soft rules). Matters most on small movie stations (Ghibli, Holiday).
   Small.
4. **M3U and XMLTV export.** `GET /TvStation/Channels.m3u` and `GET /TvStation/Guide.xml` with a
   per-plugin access token in the URL, serving the same MPEG-TS stream the Live TV path uses and
   the same schedule as XMLTV, so VLC, Kodi, Tivimate, Plex or Emby can tune in. Document the
   token in Settings. Medium.

Considered and parked: off-air hours (a daypart rule approximates it), date ranges on blocks,
custom guide titles per block, studio and people sources, smart (search) collections, multi-part
episode grouping, rerun blocks, channel redirects, on-demand channels (contradicts the always-on
clock), DVR (files are already in the library), music videos, AI lineup builder (the rule-based
suggester covers it). Revisit when someone asks.

---

## Open questions

- Rating ladder for non-US ratings (BBFC "12A", "15"; German FSK). Start with the two US ladders
  and treat unknown strings as unrated; add mappings when someone needs them.
- Whether a daypart rule that excludes everything should fall back to a slate or to the least
  restrictive allowed item. Plan says slate; revisit after seeing a real schedule.
- Preview item cap for very large libraries (10k+ episodes). 5000 with paging is a guess.
