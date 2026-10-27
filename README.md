# JellyStations for Jellyfin

Turn your Jellyfin library into always-on TV stations. Build any number of stations from movies,
series, collections, playlists or genres, choose how each one is scheduled, and every Jellyfin
client (web, mobile, Android TV, Roku, ...) tunes in through the built-in **Live TV** guide,
joining whatever is airing right now, exactly like broadcast television.

**Contents**

- [How it works](#how-it-works)
- [Scheduling modes](#scheduling-modes)
- [Station features](#station-features): bumpers and cards, logo, "Up next" card, content filters,
  tags, family hours, seasonal stations, padding, block repeats, export and import
- [Weather channel](#weather-channel): screens, forecasts, music, music packs
- [HDR channels](#hdr-channels): an HDR10 station, what it needs, what clients get
- [Requirements](#requirements)
- [Install](#install)
- [Using it](#using-it): the dashboard and editor, keeping stations fresh, station health, the Status page
- [Settings](#settings)
- [Troubleshooting](#troubleshooting)
- [HTTP API](#http-api)
- [Development](#development)
- [License](#license)

## How it works

- Stations are exposed as native Live TV channels, so the standard Jellyfin guide, channel list,
  and player are used everywhere. No extra apps or custom players.
- Every station has one deterministic schedule. All viewers see the same program at the same
  moment, and the guide shows up to 14 days ahead.
- When someone tunes in, the plugin starts a single ffmpeg process for that station that plays the
  current program from the correct offset in real time and hands it to Jellyfin as a continuous
  MPEG-TS stream. Additional viewers share the same encoder. The encoder stops shortly after the
  last viewer leaves.

## Scheduling modes

Every station has one mode, chosen when it is created and changeable later.

| Mode | What it does |
|---|---|
| **Pool** ("Shuffle a collection" in the dashboard) | Pick shows, movies, genres, collections or playlists. The station plays through the pool in shuffled or sequential cycles, or shuffled with each series' episodes kept in order (a random show comes up, but it is always that show's next episode). Shuffle avoids the same series or the same movie back to back, also across the seam between two cycles. A pool can also **rest** a show or film for N hours before it plays again, which keeps a film from coming straight back when one round ends and the next begins (a gentle rule: when nothing rested is available the station plays on; stations with time blocks are not covered yet). Optional **time blocks** take over at fixed local times, say a movie every evening from 18:00 to 21:00 on a cartoon station, and the pool carries on where it left off afterwards. When the block's content ends early, the pool fills the rest and its last program may finish past the block end rather than being cut, and a block can start up to N minutes late so the program already playing finishes first, keeping its full duration (both per-block options, on for new blocks). |
| **Slots** ("Weekly schedule" in the dashboard) | Recurring blocks in the station's time zone, e.g. weekdays 20:00-21:00 = Seinfeld, Saturday 08:00-12:00 = cartoons. A filler pool plays between blocks. Sequential blocks pick up where they left off each day. |
| **Lineup** ("Play a fixed list" in the dashboard) | A hand-ordered list that loops forever from the station's anchor time. |
| **Weather** ("Weather channel" in the dashboard) | No library items: the plugin draws the screens itself. Current conditions, the next twelve hours and the week ahead for up to eight places, cycling over music, with a live clock. See [Weather channel](#weather-channel). |

## Station features

These apply to the library-based modes (Pool, Slots and Lineup). Everything here is optional and
set per station in the editor.

### Bumpers and station cards

**Bumpers** are optional short clips (idents, trailers) that fill the gaps between programs. Pick any
sources, cap the clip length, and optionally reserve a break of a few minutes after every program so
there is always room for one. When bumpers are off, or no clip fits a gap, the **station card** is shown
instead; its text is configurable per station and defaults to "We'll be right back". Clips never appear
in the guide. Two fixed positions can be switched on as well: a **pre-roll** ident that ends every break
right before the next program, and a **post-roll** clip that opens the break after one. Each picks from
its own sources, or from the bumper sources when it has none; both come out of the existing break, so
programs still start on the grid, and a position is skipped quietly when no clip fits.

**Local weather in the breaks** puts a short run of the [weather channel](#weather-channel)'s screens
(title card, current conditions, radar, forecasts…) into every break that has at least 45 seconds of
room, up to a length you set (1 to 10 minutes), either at the end of the break right before the next
program (or its pre-roll ident) or at the start right after the program (or its post-roll clip). It
stretches into whatever time the clips leave, so a break shorter than the length still gets weather,
and clips and the card take the rest. The places, look and music come from the station's **Weather**
section, which the editor shows while this is on; like a clip, weather in a break is not a guide entry.

### Logo

A station **logo** appears in the guide and on station cards. It can also be drawn over the video as a
corner bug, with a choice of corner, size and opacity. That is off by default because the overlay
filter costs encoder CPU.

### "Up next" card

An **"Up next" card** can be drawn over the last seconds of every program: a translucent panel in a
corner with the next program's title, its poster or episode thumbnail, the season and episode or the
year, and the time it starts and ends in the station's time zone (12 or 24-hour clock). Pick the
corner, the times before the end it appears (one or more, e.g. 1h, 30m, 5m, 30s), how long it stays,
its height and opacity, whether to show the artwork and the times, and how it arrives and leaves: a
fade (the default), a slide in from the nearest side, both, or neither. The card is burned into the
stream by the plugin's own ffmpeg, so every viewer sees it, it stays in step with the picture whatever
the client's HLS delay, and no client support is needed; it cannot be dismissed. Bumper clips and
station breaks are skipped when choosing what to announce, programs too short to carry the card do
without it, and a card that ffmpeg cannot draw is dropped for the session with a note on the station
row. Off by default: like the logo it costs encoder CPU for the whole program, and the two should sit
in different corners.

### Content sources and filters

Every content source can be tuned from its chip (the ✎ button): series sources take a **season range**
and a **skip specials** switch; genre, playlist, collection and library sources can be limited to
**movies only** or **shows only**; every expanding source can take a **max rating** ("PG / TV-PG" and
below, with an "allow unrated" switch) and a **year range**; and any source gets a **weight** (×1 to
×10) that controls how often it appears compared with the other sources. Episodes without their own
rating use the series rating, and the year filter uses the show's year for episodes. Pools can also
run **marathons**: play 2 to N episodes of a show back to back before switching.

Every source also has an **Audio** and a **Subtitles** choice, so an anime series can play in
Japanese with English subtitles burned into the picture while the rest of the station keeps the
station's language (the encoding profile's audio language preference). The lists show the languages
actually found on the source's files with file counts. Subtitles can be limited to **only when the
audio language is missing** from a file: ask for English audio with English subtitles "if needed" and
dubbed episodes play dubbed while the others play in the original language with subtitles. Text
tracks (SRT, ASS, MP4 text, WebVTT, and external subtitle files Jellyfin has found) are rendered by
libass after scaling and tone mapping; PGS and DVD bitmap tracks are overlaid as pictures. Embedded
text tracks are extracted once into `data/JellyStations/subtitles` (the next program's track is
extracted while the current one plays, so program starts do not wait on it). Full subtitles win
over forced-only and hearing-impaired tracks in the same language. Press **Refresh content** after
changing a source's tracks; the server needs at least one font installed for libass to draw with.

The editor's **Content** section lists everything a station expands to, with a text filter, totals,
and an **Exclude** switch per item for the one episode or film that does not belong. Exclusions are
saved with the station and survive recompiles. A station can also **skip items shorter than** a
number of minutes, which keeps shorts and stubs out of pools and blocks.

Films split across files (Blu-ray `part1` / `part2`, which Jellyfin stacks into one movie) are
scheduled at their full combined length and play straight through, one guide entry. Items without a
known runtime are skipped and listed in the editor; refresh their metadata in Jellyfin and press
**Refresh content**.

### Tags

Library **tags** work both ways. A **Tag** source plays everything carrying a tag (for example
"christmas", as TMDB tags holiday films), including every episode of a tagged series. **Excluded
tags**, per station or plugin-wide in Settings, keep tagged items off a station when a genre, library,
playlist, collection or series source expands to them. Hand-picked movies and episodes, lineups, and a
Tag source for that same tag are never affected, whatever order the sources are in, so a seasonal
holiday station keeps its films while "christmas" stays off every regular channel all year. Items
dropped by a tag rule appear in the Content section marked "by tag".

### Family hours

Pools can carry **family hours** (daytime rating rules) such as "nothing above PG between 06:00 and
20:00" (station time zone). The schedule moves items that would overlap a window to a later slot; if
nothing fits, that slot shows a station card. Items can only be moved within their cycle, so a pool
whose full cycle is shorter than the restricted window will show cards rather than defer; with a few
hours of content this is never an issue. When the pool has time blocks, an item a rule keeps off the
air is skipped and comes up later in the pass instead. Blocks are already time-bound and are not gated.

### Seasonal stations

A station can be **seasonal**: give it one or more yearly date ranges (for example December 1 to
January 6, or pick a preset such as Halloween or Holiday season) and it is only listed in the guide
during those dates, in the station's time zone. Off season it stays in the dashboard with an "Off
season until …" badge and comes back by itself.

### Padding and the schedule anchor

Optional **padding grid** (5/15/30/60 minutes) rounds every program up to the grid like real TV,
filling the gap with a station card. **By length** padding instead gives each program a slot sized to
it: anything up to an hour fills half hours (a 22-minute episode gets 30, a 44-minute one 60) and
anything longer fills whole hours (a 95-minute film gets 2 hours). The bumper break counts toward
the length. The schedule anchor can be set to "now" from the editor, and a saved station has a
**Start schedule from now** button on its row.

### Time block repeats and templates

A block repeats every week unless its **Repeats** setting says otherwise: every N weeks counted from a
chosen date, certain weeks of the month (1st to 4th, or last; weeks run Monday to Sunday and belong to
the month of their Monday, so a weekend stays together), or only on listed dates. A "Harry Potter
weekend" is a Saturday and Sunday block with the collection as its source, set to the first week of the
month or to the dates you want; list it above any regular block it should take over from, since the
earlier block in the list wins when two coincide. Sequential blocks continue where the previous
occurrence stopped, however far apart the occurrences are.

Any block can also **repeat every N minutes** (5 or more) from its start time until a time of day or
midnight, each repeat with the full duration; an earlier block in the list still wins where a repeat
lands on another block.

A block's **What plays** can be **Local weather** instead of programs: the occurrence is one run of the
[weather channel](#weather-channel)'s screens, a real guide entry titled from the block name (or the
Weather section's guide title), with the places, look and music from the station's **Weather**
section, which the editor shows while such a block exists. "The weather at the top of every hour" is a
5-minute weather block at 06:00 repeating every 60 minutes until 23:00.

A time block can be saved as a **template** (bookmark button on the block) and added to any station
from the dropdown next to "Add block". Templates are managed in Settings.

### Export and import

**Export** downloads every station as a JSON file; **Import** creates or updates stations from such a
file (matched by id, then by name), which makes station sets easy to back up, share, or hand-edit.
Hand-picked items (movies, series, seasons, episodes, collections, playlists, libraries) are matched
by title when their ids are unknown on the importing server, so a station file from another server,
or one of the examples under `examples/stations/`, imports cleanly; anything that cannot be matched
is dropped and listed.

## Weather channel

A **Weather channel** station is in the spirit of the cable weather channel's "Local on the 8s": no
voice, no library items, just forecasts drawn as full-screen graphics over music. Pick up to eight
places (a search box finds towns and cities and fills in the coordinates and time zone) and the
channel cycles through them.

### Screens and cycle

For each place the channel shows **current conditions** (big temperature, condition, feels like,
high and low, humidity, wind, gusts, pressure, cloud cover, sunrise and sunset), the **next 12
hours** (hourly icons, temperatures, the chance of rain, snow, a mix or ice, and a temperature
curve) and the **7-day forecast**, then a **travel cities** screen with every place at a glance.
Each place opens with a short **title card** (its name and region over a faint icon of its
conditions; 3 seconds by default, 1 to 10, or off) so the eye knows the channel has moved on, and
while a place has a weather alert in force an **alerts** screen comes before its current conditions
(see below). Screens cross-fade; each stays a configurable number of seconds (default 10). With **On the 8s** a
full pass starts at :08, :18, :28... and the rest of each ten minutes repeats current conditions,
like the real format. Three colour themes (classic blue, dark, light), °F or °C with matching wind
and pressure units, a 12 or 24-hour clock, and the station logo on every screen. The lower band
carries the **local time in that place's own zone** as a live clock, the music credit, and "Weather
data by Open-Meteo.com".

The guide shows one entry per hour (configurable, 15 minutes to 4 hours) titled from a template,
default "Local weather: Boston, Denver, Tokyo", so the channel reads like a channel and not like a
hundred three-minute programs. The cycle runs from the station clock, so every viewer sees the same
screen at the same moment and tuning in lands mid-cycle like real TV.

### Forecasts

**Forecasts** come from [Open-Meteo](https://open-meteo.com) (free for non-commercial use, no account
or key, data under CC BY 4.0, hence the attribution on every screen). This is the only part of the
plugin that talks to the internet; it is per station, and the only thing ever sent is a latitude and
longitude. Each place is polled every 15 minutes while the station is enabled and in season (failures
back off up to 15 minutes), reports are cached under `data/JellyStations/weather/` so a restart has
something to show, and when a report is older than the station allows (default 3 hours) the screens
say so plainly instead of showing old numbers as current. The editor's **Forecast status** table shows
the last fetch and any error per place, **Fetch now** polls at once, and **Preview screen** renders
any screen for any place as it will air. Rendered screens are PNGs under `data/JellyStations/weather/screens/`,
regenerated only when the report, the clock hour or the look changes.

Screens are drawn with SkiaSharp, which the Jellyfin server already ships; the Inter font (SIL Open
Font License) is embedded in the plugin.

### Radar

With **Show the local radar screen** (on by default) each place's current conditions are followed by
a **local radar** screen: a loop of the last three quarters of an hour of precipitation radar over a
map of the area, with nearby towns, the place marked, a light-to-heavy legend and the clock of the
frame on air. Four map areas: metro (about 250 miles across), state (500, the default), regional
(1,000) and wide (2,000). Short screens play the loop once; longer ones repeat it about every eight
seconds, holding the newest frame a moment each time.

Two sources, picked per place: **NOAA NEXRAD** through the [Iowa Environmental Mesonet](https://mesonet.agron.iastate.edu/ogc/)'s
time-enabled WMS for places in the contiguous United States (one small image per frame, five-minute
steps), and [RainViewer](https://www.rainviewer.com/api.html) everywhere else (ten-minute steps, tiles
stitched by the plugin; free for non-commercial use with the credit the screen shows). Frames are
fetched every five minutes while the station is enabled and in season, only the frames not already on
disk, and kept under `data/JellyStations/weather/radar/` so a restart has a loop to show. A place no
source covers simply skips the screen. The map underneath (coasts, lakes, country and state borders,
towns) is [Natural Earth](https://www.naturalearthdata.com/) 1:10m data (public domain), simplified
and packed into the plugin (`dev/basemap/` has the build script), so nothing but the radar itself is
fetched. The editor's forecast status shows the radar source, frame count and newest frame per place.

### Forecast radar

With **Show the forecast radar screen** (on by default) the local radar is followed by a **forecast
radar** screen on the same map: where the model expects the rain and snow to go, from now out to a
configurable **look-ahead** (1 to 18 hours, default 3) at a configurable **step** (15 or 30 minutes or
an hour, default 15). The loop is kept to 25 frames, so a long look-ahead at a short step plays at
the next step up (12 hours at 15 minutes plays at 30). The header says which time each frame is a
forecast for and the map credits the model run it comes from.

The frames are NOAA's **HRRR** model (High-Resolution Rapid Refresh: a new run every hour, forecast
reflectivity every 15 minutes out to 18 hours), served as map tiles by the
[Iowa Environmental Mesonet](https://mesonet.agron.iastate.edu/ogc/), so the screen is for places in
the contiguous United States only; places elsewhere skip it. The plugin finds the newest published run
by probing the last few hours, fetches the frames from now to the look-ahead plus two hours (so the loop
stays whole until the next run is up), tops the window up as the clock moves, and keeps them under
`data/JellyStations/weather/radar/<place>/forecast/`. The editor's forecast status shows the run, the
frame count and how far out the frames reach; **Preview screen** shows the furthest frame.

### Alerts

With **Show an alerts screen** (on by default) a place that has a watch, warning, advisory or
statement in force gets an **alerts** screen right after its title card: each alert in a panel with
a colour bar for its severity (the NWS's extreme/severe/moderate/minor ramp), when it runs in the
place's own time ("Until 4:00 PM", "From Thu 7:00 AM until Fri 1:00 AM"), the area it covers and as
much of its text as fits, with the "WHAT... WHERE... WHEN..." bullets turned into headings. Up to
three alerts fit a screen; any more are counted. The current, hourly and 7-day screens carry a
one-line strip meanwhile ("Winter Storm Warning until Thu 4:00 PM · 1 more"). A place with nothing
in force simply skips the screen, so most of the time the channel looks as it did.

Alerts come from the [US National Weather Service API](https://www.weather.gov/documentation/services-web-api)
(free, no key, public domain), which covers the United States and its territories; places elsewhere
skip the screen, and the editor's forecast status says "not covered". Each place is checked every
five minutes while the station is enabled and in season (failures back off up to 15 minutes), test
and exercise messages and cancellations are dropped, and the last answer is kept under
`data/JellyStations/weather/alerts/` so a restart has it. The status table shows the strongest alert
and how many more; **Preview screen** can show the alerts screen and the title card.

### Music

**Music** plays underneath from two kinds of source, which can be combined: items from your Jellyfin
music library (playlists, albums, artists, music genres or single tracks, through the same picker as
the other modes) and every audio file in a **folder** on the server (a name means a folder under
`data/JellyStations/music/`; an absolute path works too; mp3, flac, ogg, opus, m4a, wav and more, tagged
with ffprobe at compile time). Tracks play back to back in a seeded shuffle that only repeats once
every track has played, the same for every viewer and continuous across the plugin's internal segment
boundaries, at a configurable level, with "Now playing: title – artist" drawn on the screens (switchable).
Several folders can be listed, separated by `;`. New files in a folder are picked up when the
station is recompiled (nightly, or **Refresh content**); with music off the channel is silent.

**Level the music** (on by default) plays every track at the same loudness: each file is measured once
in the background with ffmpeg's `loudnorm` analysis (a few seconds per track, one at a time, at low
priority) and the answer is kept in `data/JellyStations/music-loudness.json` keyed by path, size and
modification time; the track is then played with the gain that brings it to −18 LUFS, never pushing
its true peak past −1 dB (a quiet, dynamic recording is raised only as far as its peak allows). Until a
track has been measured it plays as recorded, so music starts at once and evens out as the
measurements come in; the forecast status in the editor says how far they have got. The music level
setting applies on top. Where one track ends and the next begins they fade out and in over half a
second; the plugin's internal segment cuts stay seamless.

### Music packs

The plugin bundles **no audio**, but the editor offers **music packs**: curated instrumental sets
under licences that allow playing and passing them on. **Get it** fetches each track from the
artist's own site into `data/JellyStations/music/<pack>/`, checks it against its published SHA-256,
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
  BT.709 by the plugin, so by default every channel is a plain 8-bit SDR H.264 stream that
  clients play without re-encoding. This needs `zscale` in ffmpeg (`jellyfin-ffmpeg` has it).
  A station can instead be an [HDR channel](#hdr-channels), which needs an HEVC encoder.

## HDR channels

By default a station is SDR: HDR programs are tone mapped and the whole channel is 8-bit BT.709
H.264, which every client direct plays. Set a profile's **Output** to **HDR10** (Settings >
Default encoding, or the station's own encoding override) and that channel becomes 10-bit
BT.2020 PQ **HEVC Main 10** instead:

- **HDR10 and HDR10+ programs** are scaled in 10 bits and keep their range and mastering
  metadata; nothing is tone mapped. **HLG** is converted to PQ. Dolby Vision plays from its HDR10
  base layer, so profile 5 (no base layer) stays unsupported.
- **SDR programs, slates, "Up next" cards, the logo and weather screens** are converted into the
  PQ container at SDR brightness (100 nits reference white), so the picture looks as it did on an
  SDR channel and the stream never changes format at a program boundary. A live MPEG-TS cannot
  switch codec or colour space mid-stream without players stalling, which is why the whole
  station is HDR or none of it.
- **An HEVC encoder is required**: `libx265` (software, several times the CPU of libx264),
  `hevc_qsv`, `hevc_nvenc`, `hevc_vaapi`, `hevc_amf` or `hevc_videotoolbox`. The Hardware
  section lists the HEVC encoders in your ffmpeg build, and **Test** with the station runs the
  real 10-bit chain so you can check the speed before switching. Intel UHD 630 and later encode
  Main 10 fine (an i5-8500T does 1080p30 at about 5x real time, and the full 4K HDR decode, scale
  and encode chain at about 3x). The profile's preset applies to `hevc_qsv` and `hevc_nvenc` too:
  on that i5 the SDR-to-PQ chain runs at 2x with Quick Sync's default `medium` and 3.5x with
  `veryfast`.
- **In a virtual machine** pass the host CPU through (QEMU/KVM, Proxmox: CPU type `host`). The
  PQ conversion runs in `zscale`, which is 40x slower without AVX2; a VM with a generic CPU model
  hides it and the Hardware Test reports well under real time with the CPU pegged.
- **Clients**: Android TV, Apple TV, Roku, Kodi and most TV apps direct play HEVC HDR10 and the
  display switches to HDR mode. Browsers mostly do not, so Jellyfin transcodes the channel for
  them. Turn on **tone mapping** under Dashboard > Playback > Transcoding, or those transcodes
  come out washed out. Jellyfin remuxes the channel for direct-play clients exactly as it does an
  SDR one.
- The guide and Live TV channel report `hevc`, and the stream is declared to Jellyfin as HEVC
  Main 10 BT.2020 PQ. Jellyfin still probes the stream once per output format (it caches a live
  channel's probe for good under the media source id, which is therefore derived from the codec, resolution
  and range), so a station switched to or from HDR10 takes the new format at its next tune-in after
  a probe of a few seconds; viewers already on it should change channel and back. `ffprobe` on the
  stream URL should show `hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084)`.

## Install

### From the plugin repository

1. **Dashboard > Plugins > Repositories**, press **+**, give it any name (say "JellyStations") and the URL
   `https://raw.githubusercontent.com/jdaubert12/JellyStations/main/manifest.json`, and save.
2. **Dashboard > Plugins > Catalog**: JellyStations is listed under Live TV. Install it and restart Jellyfin
   when asked.
3. New versions then show up in the catalog and install like any other plugin update.

The manifest lists every release with its download URL and MD5, and is updated by the packaging
script with each release.

### By hand

1. Download `JellyStations_1.16.1.0.zip` from the [releases page](https://github.com/jdaubert12/JellyStations/releases)
   and unzip it, or build `Jellyfin.Plugin.JellyStations.dll` yourself.
2. Copy the DLL (and `meta.json` from the zip) into a folder named `JellyStations_1.16.1.0` inside Jellyfin's plugins directory:
   - Docker: `/config/plugins/JellyStations_1.16.1.0/` (inside the config volume)
   - Linux: `/var/lib/jellyfin/plugins/JellyStations_1.16.1.0/`
   - Windows: `%LOCALAPPDATA%\jellyfin\plugins\JellyStations_1.16.1.0\` or `%ProgramData%\Jellyfin\Server\plugins\JellyStations_1.16.1.0\`
3. Restart Jellyfin. **Dashboard > Plugins** should list *JellyStations* as Active and the dashboard
   menu gains a **JellyStations** entry.

Either way, station data lives in `<data>/JellyStations/` (`/config/data/JellyStations/` in Docker) and
survives plugin upgrades.

**Upgrading from TvStation** (the plugin's name up to 1.14.2.0): the id is unchanged, so the catalog
offers JellyStations as an update and your stations carry over; the data folder is moved from
`<data>/TvStation/` to `<data>/JellyStations/` on first start and Live TV channels keep their ids.
Jellyfin retires older plugin versions by name, though, so an old `TvStation_<version>` folder is not
replaced automatically: JellyStations marks it for removal when it starts and logs a warning, and one
more restart clears it. Installing by hand, delete the old folder yourself and skip the second restart.
API routes moved from `/TvStation/...` to `/JellyStations/...`.

The folder must be readable and writable by the user Jellyfin runs as (on Linux packages
`chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/JellyStations_1.16.1.0`); with wrong ownership the
server sits on "Starting" and never finishes. For Docker, `docker cp Jellyfin.Plugin.JellyStations.dll <container>:/config/plugins/JellyStations_1.16.1.0/`
followed by `docker restart <container>` is enough.

## Using it

### Your first station

With no stations yet, the dashboard opens on **what your library could support**: tick the suggested
channels and press **Create selected**. **New station** starts from a preset (Kids channel, Movie night,
Comfort reruns, Holiday channel, One show start to finish, Weekly schedule, Play a fixed list, Weather
channel, or a plain shuffle); the preset fills in the schedule settings, you add the shows and movies,
and everything can be changed later.

1. Open **Dashboard > JellyStations > New station**.
2. Give it a name and channel number, pick a mode, add content with the picker, and use
   **Preview** to see the next day of programming.
3. **Save & refresh guide**. Within a few seconds the station appears under **Live TV**.
4. Tune in from any client. The stream starts at the live position of the current program.

### The editor

The editor walks through three steps: **Name and look** (name, number, a logo made from the name in a
color of your choice, with a background pattern, and optionally the color, seeded from the name,
number and what the channel plays), **What plays** (shows, films and how they play) and **When**
(family hours, time blocks, the dates the channel is listed, and a preview). Time blocks are painted
onto a seven-day grid: click or drag to add one, drag sideways to cover several days, click a block to
edit it. The rarely needed settings (call sign, padding grid, schedule anchor, card text, encoding) sit
under **Advanced** at the bottom of every step. **All settings** shows the whole form on one page.
Step 2 also has a **Leave out** section: tags to exclude, the minimum length, and whether the
plugin-wide excluded tags from Settings apply to this station.

### Keeping stations fresh

Edits that change the schedule (content, mode, anchor, padding, time zone) bump the station's
*revision*, which re-rolls the future schedule deterministically and refreshes the guide. Cosmetic
edits (name, number, logo) do not. A nightly task re-scans the library for stations with
*nightly refresh* enabled so new episodes join the rotation. Added a batch of films and don't want to
wait for the night? **Refresh content** on the dashboard re-scans every station at once and refreshes
the guide if anything changed; **Refresh content** in a station's editor does the same for one.

### Station rows and health

Each station row shows what is on now with the time left, the next two programs, and badges for
anything the last compile flagged: a source that matched nothing, under a day of unique content,
or items that had to be skipped (hover the excluded count for the reasons). If streaming hit a
problem, the row keeps the last error in red until you dismiss it: a file that could not be opened,
with permission denied told apart from a missing file or an unmounted folder, or an encoder that
quit early, with ffmpeg's last log line. **Status** has a Health section listing every station
that needs attention.

### Status page and hardware

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
try a decoder before putting it on a profile.

### Filling out the lineup

**Suggest stations** on the dashboard turns the library analysis into ready-made channels: genres and
series with enough hours, a kids channel and family movies by rating, franchise marathons and a
"Franchise Nights" slots station from titles that share a prefix, collections, decades, and seasonal
channels from holiday tags. Tick the ones you want and they are created with the next free channel
numbers, editable like any other station.

**Not on a channel** on the dashboard lists the movies and shows no channel plays, from the compiled
content of every enabled station (clips used as bumpers or idents count as on it). Tick titles and
either start a new channel with them or add them to a channel you already have: its pool, its filler
for a weekly schedule, or its list for a fixed list. A show with only some episodes on a channel is
listed with a badge; adding it again gives the channel every episode. A filter box narrows the list
by title, genre, rating or library.

## Settings

- **Default encoding**: resolution, frame rate, bitrates, encoder, output range, preset, GOP,
  hardware decoding. All programs on a station are normalized to one profile so clients never
  need to re-initialize at a program boundary. H.264 High 4.1 + AAC, tagged SDR BT.709,
  direct-streams on nearly every client, so Jellyfin only remuxes. A station can override the
  profile, which is the place for per-channel choices:
  - *Output*: SDR (default) or HDR10, see [HDR channels](#hdr-channels). HDR10 needs an HEVC
    encoder; the H.264 profile and level fields apply to libx264 only.
  - *Preset*: the encoder's speed/quality trade-off, `veryfast` by default. libx264, libx265 and
    Quick Sync take the x264 words (Quick Sync knows `veryfast` to `veryslow`; `ultrafast` and
    `superfast` become `veryfast`), NVENC takes `p1` to `p7` and x264 words are translated
    (`veryfast` is `p2`). VA-API, AMF and VideoToolbox have no preset. Blank leaves the encoder's
    own default, which for Quick Sync is `medium` and noticeably slower.
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
- **Excluded tags** (plugin-wide) and **block templates** are managed here too; see
  [Tags](#tags) and [Time block repeats and templates](#time-block-repeats-and-templates).

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
  relative to `data/JellyStations/music/` unless absolute, and the Jellyfin user must be able to read
  the files.
- *Channel has no programs*: check the station is enabled and has content; press
  **Refresh guide**. Jellyfin's own guide refresh runs every 24 h.
- *Playback fails to start*: open **Dashboard > JellyStations > Status**. It shows the ffmpeg path,
  active sessions and the last encoder log lines. Common causes are a wrong ffmpeg path, or media
  paths that are not visible from the server (e.g. missing Docker volume).
- *Picture is a station card*: the file for the current program is missing or ffmpeg could not
  decode it; the plugin shows a card until the next program. The station row on the dashboard
  says which: permission denied, file not found, folder not found (share or volume not mounted),
  or the ffmpeg exit code with its last log line.
- *Playback Info says "Transcoding" with reason "video range type"*: on an SDR station the
  stream carried HDR tags. Since 1.11.0 every segment is tone mapped and tagged BT.709; if a
  station was compiled by an older version, **Recompile all** (or a restart) records which items
  are HDR. `ffprobe` on the stream URL should show `yuv420p(tv, bt709)` with no mastering-display
  side data. On an HDR10 station that reason is expected from a client that cannot play HDR (a
  browser, most often): Jellyfin tone maps the channel for it, if tone mapping is on under
  Dashboard > Playback > Transcoding.
- *HDR10 station plays washed out or grey on a phone or browser*: Jellyfin is transcoding the
  channel without tone mapping. Turn on tone mapping under Dashboard > Playback > Transcoding.
- *HDR10 station will not start, "Encoding with hevc_qsv failed"*: the encoder cannot take 10-bit
  input on this GPU or driver (Intel needs Kaby Lake or later for Main 10). Try `libx265` on a
  720p profile, or go back to SDR output.
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

## HTTP API

Everything the dashboard does goes through the plugin's own endpoints under `/JellyStations`, with the
usual Jellyfin API key or session token. The ones worth calling by hand:

| Endpoint | What it returns or does |
|---|---|
| `GET /JellyStations/Library/Analysis` | One-pass summary of the library: hours per genre, every series with episode count and rating, collections, franchises detected from shared title prefixes, decades, ratings, tags and libraries. The basis for station suggestions and the health report. |
| `GET /JellyStations/Health` | Every station that needs attention, as the Status page lists them. |
| `GET /JellyStations/Hardware` | Jellyfin's transcoding setting, what the default profile resolves to, the `/dev/dri` nodes and this ffmpeg build's hwaccels, encoders and filters. |
| `POST /JellyStations/Hardware/Test` | Runs a station's current program through the real chain at full speed and reports the decoder and the speed relative to real time. |
| `GET /JellyStations/Weather/Search?q=` | Place search (Open-Meteo geocoding). |
| `GET /JellyStations/Weather/Status/{stationId}` | Last fetch and any error per place. |
| `POST /JellyStations/Weather/Refresh/{stationId}` | Polls every place now. |
| `GET /JellyStations/Weather/Preview/{stationId}?screen=Current\|Hourly\|Daily\|Travel&location=0&width=1280` | A screen as it will air, as PNG. |
| `GET /JellyStations/MusicPacks` | The pack catalogue with install state. |
| `POST /JellyStations/MusicPacks/{id}/Install`, `POST /JellyStations/MusicPacks/{id}/Cancel` | Start or stop a pack download. |

## Development

```powershell
.\scripts\build.ps1            # build + unit tests
.\scripts\deploy-local.ps1     # build, install into dev/ test container, restart it
.\scripts\package.ps1          # zip + meta.json in dist/
```

See `dev/README.md` for the local Jellyfin 12.2 test container. The scheduling engine
(`src/.../Scheduling`) and stream engine (`src/.../Streaming`) have no Jellyfin dependencies and
are covered by xunit tests under `tests/`.

**Releasing**: bump the version in the csproj, `build.yaml`, both scripts and the READMEs, add the
changelog line to `build.yaml` and `package.ps1`, build Release, run `package.ps1` (it writes the zip
and records the version, checksum and download URL in `manifest.json`), commit, tag `v<version>`,
push, then `gh release create v<version> dist\JellyStations_<version>.zip`. The manifest entry points at
that release asset, so the zip uploaded must be the one the script hashed.

## License

GPL-3.0-only, matching the Jellyfin.Controller and Jellyfin.Model packages the plugin links against.
The Inter font is under the SIL Open Font License 1.1 (`src/.../Assets/Inter-LICENSE.txt`); SkiaSharp (MIT)
is used from the server's own copy and not redistributed. No music ships with the plugin.
