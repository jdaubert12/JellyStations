# TvStation for Jellyfin

Turn your Jellyfin library into always-on TV stations. Build any number of stations from movies,
series, collections, playlists or genres, choose how each one is scheduled, and every Jellyfin
client (web, mobile, Android TV, Roku, ...) tunes in through the built-in **Live TV** guide,
joining whatever is airing right now, exactly like broadcast television.

## How it works

- Stations are exposed as native Live TV channels, so the standard Jellyfin guide, channel list,
  and player are used everywhere. No extra apps or custom players.
- Every station has one deterministic schedule. All viewers see the same program at the same
  moment, and the guide shows up to 14 days ahead.
- When someone tunes in, the plugin starts a single ffmpeg process for that station that plays the
  current program from the correct offset in real time and hands it to Jellyfin as a continuous
  MPEG-TS stream. Additional viewers share the same encoder. The encoder stops shortly after the
  last viewer leaves.

### Scheduling modes

| Mode | What it does |
|---|---|
| **Pool** ("Shuffle a collection" in the dashboard) | Pick shows, movies, genres, collections or playlists. The station plays through the pool in shuffled or sequential cycles, or shuffled with each series' episodes kept in order (a random show comes up, but it is always that show's next episode). Shuffle avoids the same series or the same movie back to back, also across the seam between two cycles. Optional **time blocks** take over at fixed local times, say a movie every evening from 18:00 to 21:00 on a cartoon station, and the pool carries on where it left off afterwards. When the block's content ends early, the pool fills the rest and its last program may finish past the block end rather than being cut, and a block can start up to N minutes late so the program already playing finishes first, keeping its full duration (both per-block options, on for new blocks). |
| **Slots** ("Weekly schedule" in the dashboard) | Recurring blocks in the station's time zone, e.g. weekdays 20:00-21:00 = Seinfeld, Saturday 08:00-12:00 = cartoons. A filler pool plays between blocks. Sequential blocks pick up where they left off each day. |
| **Lineup** ("Play a fixed list" in the dashboard) | A hand-ordered list that loops forever from the station's anchor time. |

**Bumpers** are optional short clips (idents, trailers) that fill the gaps between programs. Pick any
sources, cap the clip length, and optionally reserve a break of a few minutes after every program so
there is always room for one. When bumpers are off, or no clip fits a gap, the **station card** is shown
instead; its text is configurable per station and defaults to "We'll be right back". Clips never appear
in the guide. Two fixed positions can be switched on as well: a **pre-roll** ident that ends every break
right before the next program, and a **post-roll** clip that opens the break after one. Each picks from
its own sources, or from the bumper sources when it has none; both come out of the existing break, so
programs still start on the grid, and a position is skipped quietly when no clip fits.

A station **logo** appears in the guide and on station cards. It can also be drawn over the video as a
corner bug, with a choice of corner, size and opacity. That is off by default because the overlay
filter costs encoder CPU.

The editor's **Content** section lists everything a station expands to, with a text filter, totals,
and an **Exclude** switch per item for the one episode or film that does not belong. Exclusions are
saved with the station and survive recompiles.

Library **tags** work both ways. A **Tag** source plays everything carrying a tag (for example
"christmas", as TMDB tags holiday films), including every episode of a tagged series. **Excluded
tags**, per station or plugin-wide in Settings, keep tagged items off a station when a genre, library,
playlist, collection or series source expands to them. Hand-picked movies and episodes, lineups, and a
Tag source for that same tag are never affected, so a seasonal holiday station keeps its films while
"christmas" stays off every regular channel all year. Items dropped by a tag rule appear in the
Content section marked "by tag".

Pools can carry **family hours** (daytime rating rules) such as "nothing above PG between 06:00 and 20:00" (station time
zone). The schedule moves items that would overlap a window to a later slot; if nothing fits, that
slot shows a station card. Items can only be moved within their cycle, so a pool whose full cycle is
shorter than the restricted window will show cards rather than defer; with a few hours of content this
is never an issue. When the pool has time blocks, an item a rule keeps off the air is skipped and comes up
later in the pass instead. Blocks are already time-bound and are not gated.

A station can be **seasonal**: give it one or more yearly date ranges (for example December 1 to
January 6, or pick a preset such as Halloween or Holiday season) and it is only listed in the guide
during those dates, in the station's time zone. Off
season it stays in the dashboard with an "Off season until …" badge and comes back by itself.

**Export** downloads every station as a JSON file; **Import** creates or updates stations from such a
file (matched by id, then by name), which makes station sets easy to back up, share, or hand-edit. Hand-picked items (movies, series, seasons, episodes, collections, playlists, libraries) are
matched by title when their ids are unknown on the importing server, so a station file from another
server, or one of the examples under `examples/stations/`, imports cleanly; anything that cannot be
matched is dropped and listed.

Every content source can be tuned from its chip (the ✎ button): series sources take a **season range**
and a **skip specials** switch; genre, playlist, collection and library sources can be limited to
**movies only** or **shows only**; every expanding source can take a **max rating** ("PG / TV-PG" and
below, with an "allow unrated" switch) and a **year range**; and any source gets a **weight** (×1 to
×10) that controls how often it appears compared with the other sources. Episodes without their own
rating use the series rating, and the year filter uses the show's year for episodes. Pools can also run **marathons**: play 2 to N episodes of a
show back to back before switching.

Optional **padding grid** (5/15/30/60 minutes) rounds every program up to the grid like real TV,
filling the gap with a station card. **By length** padding instead gives each program a slot sized to
it: anything up to an hour fills half hours (a 22-minute episode gets 30, a 44-minute one 60) and
anything longer fills whole hours (a 95-minute film gets 2 hours). The bumper break counts toward
the length. The schedule anchor can be set to "now" from the editor, and a
saved station has a **Start schedule from now** button on its row.

## Requirements

- Jellyfin **12.x** (the plugin targets ABI 12.0.0.0 and .NET 10). Jellyfin 10.11 is not supported.
- ffmpeg reachable by the server. The official Docker image and standard installers bundle
  `jellyfin-ffmpeg`; the plugin uses the server's configured encoder path by default.
- CPU budget: roughly one core per *actively watched* station at the default 720p profile.
  Hardware encoders (`h264_nvenc`, `h264_qsv`, `h264_vaapi`) can be configured in Settings.

## Install

1. Build or download `Jellyfin.Plugin.TvStation.dll`.
2. Copy it into a folder named `TvStation_1.6.0.0` inside Jellyfin's plugins directory:
   - Docker: `/config/plugins/TvStation_1.6.0.0/` (inside the config volume)
   - Linux: `/var/lib/jellyfin/plugins/TvStation_1.6.0.0/`
   - Windows: `%LOCALAPPDATA%\jellyfin\plugins\TvStation_1.6.0.0\` or `%ProgramData%\Jellyfin\Server\plugins\TvStation_1.6.0.0\`
3. Restart Jellyfin. **Dashboard > Plugins** should list *TvStation* as Active and the dashboard
   menu gains a **TV Stations** entry.

The folder must be readable and writable by the user Jellyfin runs as (on Linux packages
`chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/TvStation_1.6.0.0`); with wrong ownership the
server sits on "Starting" and never finishes. For Docker, `docker cp Jellyfin.Plugin.TvStation.dll <container>:/config/plugins/TvStation_1.6.0.0/`
followed by `docker restart <container>` is enough. Station data lives in
`<data>/TvStation/` (`/config/data/TvStation/` in Docker) and survives plugin upgrades.

## Using it

With no stations yet, the dashboard opens on **what your library could support**: tick the suggested
channels and press **Create selected**. **New station** starts from a preset (Kids channel, Movie night,
Comfort reruns, Holiday channel, One show start to finish, Weekly schedule, Play a fixed list, or a plain
shuffle); the preset fills in the schedule settings, you add the shows and movies, and everything can be
changed later. The rarely needed settings (call sign, padding grid, schedule anchor, card text, excluded
tags, minimum length, encoding) sit under **Advanced** in the editor.

1. Open **Dashboard > TV Stations > New station**.
2. Give it a name and channel number, pick a mode, add content with the picker, and use
   **Preview** to see the next day of programming.
3. **Save & refresh guide**. Within a few seconds the station appears under **Live TV**.
4. Tune in from any client. The stream starts at the live position of the current program.

Edits that change the schedule (content, mode, anchor, padding, time zone) bump the station's
*revision*, which re-rolls the future schedule deterministically and refreshes the guide. Cosmetic
edits (name, number, logo) do not. A nightly task re-scans the library for stations with
*nightly refresh* enabled so new episodes join the rotation. Added a batch of films and don't want to
wait for the night? **Refresh content** on the dashboard re-scans every station at once and refreshes
the guide if anything changed; **Refresh content** in a station's editor does the same for one.

Each station row shows what is on now with the time left, the next two programs, and badges for
anything the last compile flagged: a source that matched nothing, under a day of unique content,
or items that had to be skipped (hover the excluded count for the reasons). If streaming hit a
problem, the row keeps the last error in red until you dismiss it: a file that could not be opened,
with permission denied told apart from a missing file or an unmounted folder, or an encoder that
quit early, with ffmpeg's last log line. **Status** has a Health section listing every station
that needs attention.

Films split across files (Blu-ray `part1` / `part2`, which Jellyfin stacks into one movie) are
scheduled at their full combined length and play straight through, one guide entry. Items without a
known runtime are skipped and listed in the editor; refresh their metadata in
Jellyfin and press **Refresh content**.

**Suggest stations** on the dashboard turns the library analysis into ready-made channels: genres and
series with enough hours, a kids channel and family movies by rating, franchise marathons and a
"Franchise Nights" slots station from titles that share a prefix, collections, decades, and seasonal
channels from holiday tags. Tick the ones you want and they are created with the next free channel
numbers, editable like any other station.

A block repeats every week unless its **Repeats** setting says otherwise: every N weeks counted from a
chosen date, certain weeks of the month (1st to 4th, or last; weeks run Monday to Sunday and belong to
the month of their Monday, so a weekend stays together), or only on listed dates. A "Harry Potter
weekend" is a Saturday and Sunday block with the collection as its source, set to the first week of the
month or to the dates you want; list it above any regular block it should take over from, since the
earlier block in the list wins when two coincide. Sequential blocks continue where the previous
occurrence stopped, however far apart the occurrences are.

A time block can be saved as a **template** (bookmark button on the block) and added to any station
from the dropdown next to "Add block". Templates are managed in Settings. A station can also **skip
items shorter than** a number of minutes, which keeps shorts and stubs out of pools and blocks.

`GET /TvStation/Library/Analysis` returns a one-pass summary of the library: hours per genre, every
series with episode count and rating, collections, franchises detected from shared title prefixes,
decades, ratings, tags and libraries. It is the basis for station suggestions and the health report.

## Settings

- **Default encoding**: resolution, frame rate, bitrates, encoder, preset, GOP. All programs on a
  station are normalized to one profile so clients never need to re-initialize at a program
  boundary. H.264 High 4.1 + AAC direct-streams on nearly every client, so Jellyfin usually
  only remuxes.
- **Linger**: seconds the encoder keeps running after the last viewer leaves (quick re-tunes
  reuse it).
- **Max concurrent sessions**: cap on simultaneously encoded stations.
- **Slate**: colour and whether to draw text / the station logo on break cards.

## Troubleshooting

- *Channel has no programs*: check the station is enabled and has content; press
  **Refresh guide**. Jellyfin's own guide refresh runs every 24 h.
- *Playback fails to start*: open **Dashboard > TV Stations > Status**. It shows the ffmpeg path,
  active sessions and the last encoder log lines. Common causes are a wrong ffmpeg path, or media
  paths that are not visible from the server (e.g. missing Docker volume).
- *Picture is a station card*: the file for the current program is missing or ffmpeg could not
  decode it; the plugin shows a card until the next program. The station row on the dashboard
  says which: permission denied, file not found, folder not found (share or volume not mounted),
  or the ffmpeg exit code with its last log line.
- Live HLS adds 10-30 s of latency behind the encoder; the position shown in the guide is the
  server's.

## Development

```powershell
.\scripts\build.ps1            # build + unit tests
.\scripts\deploy-local.ps1     # build, install into dev/ test container, restart it
.\scripts\package.ps1          # zip + meta.json in dist/
```

See `dev/README.md` for the local Jellyfin 12.2 test container. The scheduling engine
(`src/.../Scheduling`) and stream engine (`src/.../Streaming`) have no Jellyfin dependencies and
are covered by xunit tests under `tests/`.

## License

GPL-3.0-only, matching the Jellyfin server it links against.
