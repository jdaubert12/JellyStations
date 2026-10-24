# Local test server

A throwaway Jellyfin 12.2 instance for developing the plugin.

```powershell
# 1. Start Docker Desktop, then bring the server up (first run pulls the image)
docker compose -f dev/docker-compose.yml up -d

# 2. Build and install the plugin, restart the container, tail the log
.\scripts\deploy-local.ps1

# 3. Open http://localhost:8096, finish the setup wizard, add libraries:
#    Movies  -> /media/movies
#    Shows   -> /media/tv
```

`dev/media` holds synthetic clips (ffmpeg `testsrc`, which renders a running timecode so
join-in-progress offsets can be checked by eye). Regenerate them with the commands in
`dev/make-media.sh` if the folder is empty. `dev/config` and `dev/cache` are the container's
state and are git-ignored.

Plugin data on the test server: `dev/config/data/JellyStations/`. Plugin DLL: `dev/config/plugins/JellyStations_1.15.0.0/`.

## Stream test

```bash
bash dev/stream-test.sh <liveTvChannelItemId> 200   # tunes in, reads raw TS, reports boundaries/slates/PCR jumps
```
Get the channel item id from `GET /LiveTv/Channels`.

## Caveat: Docker Desktop clock skew

On this Windows machine the Docker Desktop (WSL2) VM clock runs several percent fast. ffmpeg's own
`-re` pacing is therefore unusable here, which is why the plugin paces output itself against the wall
clock using the MPEG-TS PCR. Timing on a real Linux host is unaffected.
