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
| **Pool** ("Shuffle a collection" in the dashboard) | Pick shows, movies, genres, collections or playlists. The station plays through the pool in shuffled or sequential cycles, or shuffled with each series' episodes kept in order (a random show comes up, but it is always that show's next episode). Shuffle avoids the same series or the same movie back to back, also across the seam between two cycles. A pool can also **rest** a show or film for N hours before it plays again, which keeps a film from coming straight back when one round ends and the next begins (a gentle rule: when nothing rested is available the station plays on; stations with time blocks are not covered yet). Optional **time blocks** take over at fixed local times, say a movie every evening from 18:00 to 21:00 on a cartoon station, and the pool carries on where it left off afterwards. When the block's content ends early, the pool fills the rest and its last program may finish past the block end rather than being cut, and a block can start up to N minutes late so the program already playing finishes first, keeping its full duration (both per-block options, on for new blocks). |
| **Slots** ("Weekly schedule" in the dashboard) | Recurring blocks in the station's time zone, e.g. weekdays 20:00-21:00 = Seinfeld, Saturday 08:00-12:00 = cartoons. A filler pool plays between blocks. Sequential blocks pick up where they left off each day. |
| **Lineup** ("Play a fixed list" in the dashboard) | A hand-ordered list that loops forever from the station's anchor time. |
| **Weather** ("Weather channel" in the dashboard) | No library items: the plugin draws the screens itself. Current conditions, the next twelve hours and the week ahead for up to eight places, cycling over music, with a live clock. See [Weather channel](#weather-channel). |

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

An **"Up next" card** can be drawn over the last seconds of every program: a translucent panel in a
corner with the next program's title, its poster or episode thumbnail, the season and episode or the
year, and the time it starts and ends in the station's time zone (12 or 24-hour clock). Pick the
corner, the times before the end it appears (one or more, e.g. 1h, 30m, 5m, 30s), how long it stays, its height and opacity,
whether to show the artwork and the times, and how it arrives and leaves: a fade (the default), a slide in
from the nearest side, both, or neither. The card is burned into the stream by the plugin's own
ffmpeg, so every viewer sees it, it stays in step with the picture whatever the client's HLS delay,
and no client support is needed; it cannot be dismissed. Bumper clips and station breaks are skipped
when choosing what to announce, programs too short to carry the card do without it, and a card that
ffmpeg cannot draw is dropped for the session with a note on the station row. Off by default: like
the logo it costs encoder CPU for the whole program, and the two should sit in different corners.

### Weather channel

A **Weather channel** station is in the spirit of the cable weather channel's "Local on the 8s": no
voice, no library items, just forecasts drawn as full-screen graphics over music. Pick up to eight
places (a search box finds towns and cities and fills in the coordinates and time zone), and the
channel cycles through them: for each place **current conditions** (big temperature, condition,
feels like, high and low, humidity, wind, gusts, pressure, cloud cover, sunrise and sunset), the
**next 12 hours** (hourly icons, temperatures, the chance of rain, snow, a mix or ice, and a temperature curve) and the **7-day
forecast**, then a **travel cities** screen with every place at a glance. Screens cross-fade; each
stays a configurable number of seconds (default 10). With **On the 8s** a full pass starts at :08,
:18, :28... and the rest of each ten minutes repeats current conditions, like the real format.
Three colour themes (classic blue, dark, light), °F or °C with matching wind and pressure units, a 12
or 24-hour clock, and the station logo on every screen. The lower band carries the **local time in
that place's own zone** as a live clock, the music credit, and "Weather data by Open-Meteo.com".

The guide shows one entry per hour (configurable, 15 minutes to 4 hours) titled from a template,
default "Local weather: Boston, Denver, Tokyo", so the channel reads like a channel and not like a
hundred three-minute programs. The cycle runs from the station clock, so every viewer sees the same
screen at the same moment and tuning in lands mid-cycle like real TV.

**Forecasts** come from [Open-Meteo](https://open-meteo.com) (free for non-commercial use, no account
or key, data under CC BY 4.0, hence the attribution on every screen). This is the only part of the
plugin that talks to the internet; it is per station, and the only thing ever sent is a latitude and
longitude. Each place is polled every 15 minutes while the station is enabled and in season (failures
back off up to 15 minutes), reports are cached under `data/TvStation/weather/` so a restart has
something to show, and when a report is older than the station allows (default 3 hours) the screens
say so plainly instead of showing old numbers as current. The editor's **Forecast status** table shows
the last fetch and any error per place, **Fetch now** polls at once, and **Preview screen** renders
any screen for any place as it will air. Rendered screens are PNGs under `data/TvStation/weather/screens/`,
regenerated only when the report, the clock hour or the look changes.

**Music** plays underneath from two kinds of source, which can be combined: items from your Jellyfin
music library (playlists, albums, artists, music genres or single tracks, through the same picker as
the other modes) and every audio file in a **folder** on the server (a name means a folder under
`data/TvStation/music/`; an absolute path works too; mp3, flac, ogg, opus, m4a, wav and more, tagged
with ffprobe at compile time). Tracks play back to back in a seeded shuffle that only repeats once
every track has played, the same for every viewer and continuous across the plugin's internal segment
boundaries, at a configurable level, with "Now playing: title – artist" drawn on the screens (switchable).
Several folders can be listed, separated by `;`. New files in a folder are picked up when the
station is recompiled (nightly, or **Refresh content**); with music off the channel is silent.

The plugin bundles **no audio**, but the editor offers **music packs**: curated instrumental sets
under licences that allow playing and passing them on. **Get it** fetches each track from the
artist's own site into `data/TvStation/music/<pack>/`, checks it against its published SHA-256,
writes a `CREDITS.txt` beside the files and recompiles the stations that use the folder; a run that
stops picks up where it left off. The starter pack, **Lounge and light jazz**, is thirty pieces by
Kevin MacLeod (CC BY 4.0, about 1 h 54 min, 240 MB) in the register the cable weather channel is
known for, "Local Forecast" included; the on-screen title and artist line is the credit the licence
asks for, so leave it on. A second, optional pack, **Lo-fi jazz guitar** by HoliznaCC0 (CC0, two
short albums), lives on the Free Music Archive, which needs a free account to download, so the
editor gives the links and the folder to drop the files into. **Use this folder** adds the pack's
folder to the station. "Royalty free" and "free to redistribute" are different things; the packs
only carry licences that allow both, and anything else you have the right to play goes in a folder of
your own.

API: `GET /TvStation/MusicPacks` (catalogue with install state), `POST /TvStation/MusicPacks/{id}/Install`,
`POST /TvStation/MusicPacks/{id}/Cancel`; `GET /TvStation/Weather/Search?q=` (place search), `GET /TvStation/Weather/Status/{stationId}`,
`POST /TvStation/Weather/Refresh/{stationId}`, `GET /TvStation/Weather/Preview/{stationId}?screen=Current|Hourly|Daily|Travel&location=0&width=1280` (PNG).
Screens are drawn with SkiaSharp, which the Jellyfin server already ships; the Inter font (SIL Open
Font License) is embedded in the plugin.

The editor's **Content** section lists everything a station expands to, with a text filter, totals,
and an **Exclude** switch per item for the one episode or film that does not belong. Exclusions are
saved with the station and survive recompiles.

Library **tags** work both ways. A **Tag** source plays everything carrying a tag (for example
"christmas", as TMDB tags holiday films), including every episode of a tagged series. **Excluded
tags**, per station or plugin-wide in Settings, keep tagged items off a station when a genre, library,
playlist, collection or series source expands to them. Hand-picked movies and episodes, lineups, and a
Tag source for that same tag are never affected, whatever order the sources are in, so a seasonal holiday station keeps its films while
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
  Hardware encoders (`h264_nvenc`, `h264_qsv`, `h264_vaapi`) can be configured in Settings, and
  hardware decoding follows Jellyfin's own transcoding setting by default (4K HEVC sources are
  the heavy part; decoding them on the GPU is what keeps a 4K movie station real time).
- Internet access from the server to `api.open-meteo.com` and `geocoding-api.open-meteo.com`, only
  for Weather channel stations, and to `incompetech.com` only while a music pack downloads.
  Everything else works offline.
- HDR sources (HDR10, HDR10+, Dolby Vision on an HDR10 base, HLG) are tone mapped to SDR
  BT.709 by the plugin, so every channel is a plain 8-bit SDR H.264 stream that clients play
  without re-encoding. This needs `zscale` in ffmpeg (`jellyfin-ffmpeg` has it).

## Install

1. Build or download `Jellyfin.Plugin.TvStation.dll`.
2. Copy it into a folder named `TvStation_1.14.2.0` inside Jellyfin's plugins directory:
   - Docker: `/config/plugins/TvStation_1.14.2.0/` (inside the config volume)
   - Linux: `/var/lib/jellyfin/plugins/TvStation_1.14.2.0/`
   - Windows: `%LOCALAPPDATA%\jellyfin\plugins\TvStation_1.14.2.0\` or `%ProgramData%\Jellyfin\Server\plugins\TvStation_1.14.2.0\`
3. Restart Jellyfin. **Dashboard > Plugins** should list *TvStation* as Active and the dashboard
   menu gains a **TV Stations** entry.

The folder must be readable and writable by the user Jellyfin runs as (on Linux packages
`chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/TvStation_1.14.2.0`); with wrong ownership the
server sits on "Starting" and never finishes. For Docker, `docker cp Jellyfin.Plugin.TvStation.dll <container>:/config/plugins/TvStation_1.14.2.0/`
followed by `docker restart <container>` is enough. Station data lives in
`<data>/TvStation/` (`/config/data/TvStation/` in Docker) and survives plugin upgrades.

## Using it

With no stations yet, the dashboard opens on **what your library could support**: tick the suggested
channels and press **Create selected**. **New station** starts from a preset (Kids channel, Movie night,
Comfort reruns, Holiday channel, One show start to finish, Weekly schedule, Play a fixed list, or a plain
shuffle); the preset fills in the schedule settings, you add the shows and movies, and everything can be
changed later. The editor walks through three steps: **Name and look** (name, number, a logo made from the name in
a color of your choice, with a background pattern, and optionally the color, seeded from the name, number and what the channel plays), **What plays** (shows, films and how they play) and **When** (family hours, time
blocks, the dates the channel is listed, and a preview). Time blocks are painted onto a seven-day grid:
click or drag to add one, drag sideways to cover several days, click a block to edit it. The rarely needed settings
(call sign, padding grid, schedule anchor, card text, encoding) sit under **Advanced** at the bottom of every step.
**All settings** shows the whole form on one page. Step 2 also has a **Leave out** section: tags to exclude, the minimum length, and
whether the plugin-wide excluded tags from Settings apply to this station.

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

**Status** also shows what each active encoder is really doing, refreshed every five seconds:
whether the hardware decoder is in use (on Linux the plugin checks that ffmpeg holds a GPU device
such as `/dev/dri/renderD128` open; a fallback to software, or a device that failed when the
stream started, is flagged in red with ffmpeg's own line), the encoder, HDR tone mapping, the CPU
cores the ffmpeg process is using, and how far ahead of real time it runs (about +1.5 s when it
keeps up; a negative number is what viewers see as stutter). Problems are spelled out under the
row. The Hardware section lists Jellyfin's transcoding setting, what the default profile resolves
to, the `/dev/dri` nodes the server can see, and the hwaccels, H.264 encoders and tone mapping
filters in this ffmpeg build, with notes on anything that does not line up. **Test** runs a few
seconds of a station's current program through the real decode, filter and encode chain at full
speed and reports which decoder ffmpeg attached and the speed relative to real time, so you can
try a decoder before putting it on a profile (`GET /TvStation/Hardware`,
`POST /TvStation/Hardware/Test`).

Films split across files (Blu-ray `part1` / `part2`, which Jellyfin stacks into one movie) are
scheduled at their full combined length and play straight through, one guide entry. Items without a
known runtime are skipped and listed in the editor; refresh their metadata in
Jellyfin and press **Refresh content**.

**Suggest stations** on the dashboard turns the library analysis into ready-made channels: genres and
series with enough hours, a kids channel and family movies by rating, franchise marathons and a
"Franchise Nights" slots station from titles that share a prefix, collections, decades, and seasonal
channels from holiday tags. Tick the ones you want and they are created with the next free channel
numbers, editable like any other station.

**Not on a channel** on the dashboard lists the movies and shows no channel plays, from the compiled
content of every enabled station (clips used as bumpers or idents count as on it). Tick titles and either start a new
channel with them or add them to a channel you already have: its pool, its filler for a weekly
schedule, or its list for a fixed list. A show with only some episodes on a channel is listed with a
badge; adding it again gives the channel every episode. A filter box narrows the list by title, genre,
rating or library.

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

- **Default encoding**: resolution, frame rate, bitrates, encoder, preset, GOP, hardware
  decoding. All programs on a station are normalized to one profile so clients never need to
  re-initialize at a program boundary. H.264 High 4.1 + AAC, tagged SDR BT.709, direct-streams
  on nearly every client, so Jellyfin only remuxes. A station can override the profile, which is
  the place for per-channel choices:
  - *Frame rate*: 24 for a film station (most movies are 23.976 fps and judder when converted
    to 30), 30 for series, 60 only for 60 fps sources. Keep one rate per station; the profile
    must not change between programs.
  - *Bitrate*: 3500 kbps is plenty for 720p and 8000 for 1080p. Higher only inflates every
    client's download and any transcode Jellyfin still has to do.
  - *Hardware decoding*: "Follow Jellyfin" (default) mirrors Dashboard > Playback > Transcoding
    (QSV, VA-API, NVDEC/CUDA, VideoToolbox or D3D11VA) and its device; or pick one and give the
    render node or adapter. Decoding is where 4K HEVC costs its CPU; scaling, tone mapping and
    the logo stay in software. ffmpeg falls back to software decoding when the device cannot
    handle a file, and the plugin drops to software for the session if the device will not open.
    The GPU decoders themselves never fall back, so the plugin decides per file from the codec,
    profile and bit depth recorded at compile time: with "Follow Jellyfin" the files that go to the
    GPU are exactly those Jellyfin's "Enable hardware decoding for" checkboxes (and its 10-bit
    HEVC, VP9 and RExt toggles) allow; a hand-picked decoder takes everything GPUs can decode.
    10-bit H.264, DivX/Xvid and the like always play in software, and the Status page says so.
    QSV is set up as the `*_qsv` decoder for the file's codec (`hevc_qsv`, `h264_qsv`, ...)
    decoding to system memory, not as `-hwaccel qsv`, which in ffmpeg 7 leaves frames on GPU
    surfaces the software filters cannot read; a file whose codec was never recorded stays in
    software until the station's content is refreshed.
  - *VA-API encoding* (`h264_vaapi`) uses the same device and uploads frames itself.
  - *Key frames*: every GOP (2 s by default) starts with an IDR frame and its headers, which is
    where a client tuning in mid-program starts. libx264 and VA-API get forced key frames; QSV,
    NVENC and AMF set them by GOP alone, since their forced key frames are plain I frames that no
    demuxer treats as a key frame (a client would get audio and no video until the next program).
- **Linger**: seconds the encoder keeps running after the last viewer leaves (quick re-tunes
  reuse it).
- **Stop an unwatched stream after**: seconds a station may run with nobody reading it before
  the plugin stops it regardless of the viewer count (default 120; 0 turns it off). Jellyfin only
  reports a viewer gone when the client says so or its connection closes, so a killed app, a TV
  switched off on a channel or a dropped network can leave the count stuck at 1 and ffmpeg
  encoding for nobody; the reader count tells the truth within seconds.
- **Max concurrent sessions**: cap on simultaneously encoded stations.
- **Slate**: color and whether to draw text / the station logo on break cards.

## Troubleshooting

- *ffmpeg keeps running with nobody watching*: Status shows the session with Viewers 1 or more
  and Readers 0. Normal for the Linger seconds after a viewer leaves; beyond that the viewer
  never told Jellyfin it stopped, and the **Stop an unwatched stream after** setting ends it
  (a warning is logged). The Stop button on the Status row ends it at once.
- *Weather channel shows "Forecast unavailable"*: the server could not reach `api.open-meteo.com`
  for longer than the station's staleness limit, or no report has been fetched yet. The editor's
  Forecast status table has the error per place (DNS, time-out, HTTP status); **Fetch now** retries.
  A place with coordinates 0, 0 was typed rather than picked from the search; pick it again.
- *Weather channel is silent*: music is off, or the folder and sources expanded to nothing (the
  station row says "no music"; the Content section lists the tracks found). Folder names are
  relative to `data/TvStation/music/` unless absolute, and the Jellyfin user must be able to read
  the files.
- *Channel has no programs*: check the station is enabled and has content; press
  **Refresh guide**. Jellyfin's own guide refresh runs every 24 h.
- *Playback fails to start*: open **Dashboard > TV Stations > Status**. It shows the ffmpeg path,
  active sessions and the last encoder log lines. Common causes are a wrong ffmpeg path, or media
  paths that are not visible from the server (e.g. missing Docker volume).
- *Picture is a station card*: the file for the current program is missing or ffmpeg could not
  decode it; the plugin shows a card until the next program. The station row on the dashboard
  says which: permission denied, file not found, folder not found (share or volume not mounted),
  or the ffmpeg exit code with its last log line.
- *Playback Info says "Transcoding" with reason "video range type"*: the stream carried HDR tags.
  Since 1.11.0 every segment is tone mapped and tagged BT.709; if a station was compiled by an
  older version, **Recompile all** (or a restart) records which items are HDR. `ffprobe` on the
  stream URL should show `yuv420p(tv, bt709)` with no mastering-display side data.
- *Stutter on a 4K HEVC source*: the plugin's own ffmpeg is probably decoding in software and
  cannot keep real time. Turn on hardware decoding (Settings > Default encoding) or on the station.
- *Is the GPU really being used?* **Status** answers it: the session's Encoder column says
  "qsv decode ✓" only when ffmpeg holds the GPU device open, and turns red when ffmpeg fell back
  to software or the device failed. Press **Test** in the Hardware section to run a station's
  current file through the chain and read which decoder ffmpeg used and how many times real
  time it managed; under 1.0x means that file cannot play smoothly with that profile. The test
  confirms a QSV or CUVID decoder from ffmpeg's own stream mapping, and any other decoder from
  the GPU device node ffmpeg holds open while it runs (Linux, with a software encoder); when
  neither can tell, for example VA-API decode with `h264_vaapi` encode, it says "unverified"
  rather than flagging a problem, since ffmpeg logs a fallback whenever a hwaccel fails. Outside the
  plugin, `intel_gpu_top` (Intel) or `nvidia-smi` (NVIDIA) on the server shows the decoder busy
  while a station plays.
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
