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
- Station JSON in `stations/` is the source of truth for the real server; changes there go in via
  Import.

## Already done (October 2026)

Pool / slots / lineup modes, shuffle with episodes-in-order, marathons, per-source weight, season
range and specials toggle, movies/shows filter on container sources, seasonal windows, import and
export, station logos, Jellyfin 12.2 / .NET 10.

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
`stations/` as genre sources with filters and compare item counts against the hand-picked lists.

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
- **Logo overlay on video.** ffmpeg `overlay` filter with the station logo in a corner, per-station
  toggle and opacity. Small; cost is encoder CPU, so default off.
- **Re-anchor from now — DONE 2026-10-14.** "Set to now" in the editor and "Start schedule from
  now" on the station row, rounded down to the padding grid. No "start with this item" pick yet.
- **Holiday calendar presets — DONE 2026-10-14.** Preset dropdown in the season block.
- **Time blocks on pools — DONE 2026-10-15.** A Pool station can carry weekly blocks (the same
  editor as Slots mode); the pool plays between them with its shuffle, series spacing, marathons
  and daytime rules intact, resuming where it left off. Slots-mode filler is unchanged.
- **Refresh guide after logo upload — already in place** (`SaveLogo` triggers the guide refresh).

## Phase 3 — curation help

Order: import by name first (it makes `stations/` shippable as examples), then the analyzer,
then suggestion rules and the dashboard view on top of it.

- **Resolve import sources by name.** Sources already carry `DisplayName` beside `ItemId`. On
  import, when the id is unknown on this server, look the item up by kind and name (series,
  movie, collection, playlist, library; genres and tags already match by name) and report what
  could not be matched instead of silently dropping it. Move the real-server set to
  `examples/stations/` with a note that hand-picked sources resolve by title. Small.
- **Library analysis.** One pass over the library producing the facts suggestions are built
  from: hours per genre, episodes and rating per series, titles per collection, franchises from
  shared title prefixes, counts per decade, kids content by rating, holiday content by tag.
  Pure `Library/` code with tests, exposed as `GET /TvStation/Library/Analysis`; the Phase 4
  health report reads the same data. Small to medium.
- **Suggest stations.** Rule set over the analysis: genre channels with ≥ N hours, series with
  ≥ M episodes, franchise lineups from collection names or title prefixes, decade channels, a
  kids channel from TV-Y/TV-Y7/G content, a holiday station from tags. Each rule yields a ready
  station definition (sources, mode, blocks where it fits, suggested name). Presented as a
  checklist with hours of content per suggestion that creates the ticked stations via the
  existing create path, channel numbers assigned in order. Medium.
- **Franchise detection.** Shared helper for the analyzer and for a "pick a franchise" source type.
  Small once the analyzer exists.
- **Block templates.** Save a slot block's day/time/sources as a template, reuse across stations.
  Small.
- **Exclusion by tag — DONE 2026-10-14.** Per-station and global excluded tags (hand picks and
  Tag sources for the same tag exempt), a Tag source kind, `GET /TvStation/Library/Tags`, and
  "by tag" rows in the content view. Changing the global list recompiles every station.
- **Other rule exclusions.** "Skip episodes under 5 minutes", "skip items without a runtime" as
  pool-level switches. Small.

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

## Open questions

- Rating ladder for non-US ratings (BBFC "12A", "15"; German FSK). Start with the two US ladders
  and treat unknown strings as unrated; add mappings when someone needs them.
- Whether a daypart rule that excludes everything should fall back to a slate or to the least
  restrictive allowed item. Plan says slate; revisit after seeing a real schedule.
- Preview item cap for very large libraries (10k+ episodes). 5000 with paging is a guess.
