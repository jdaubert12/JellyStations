#!/usr/bin/env bash
# Tunes into a station channel on the local test server, reads the raw TS for N seconds, and reports
# program-boundary behaviour (early ends, slates, timestamp jumps). Usage: bash dev/stream-test.sh <channelItemId> [seconds]
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
B="${JF_URL:-http://localhost:8096}"
T=$(cat "$HERE/.token")
H="Authorization: MediaBrowser Token=\"$T\", Client=\"dev\", Device=\"dev\", DeviceId=\"dev1\", Version=\"1\""
CH="$1"; SECS="${2:-120}"
W=$(cygpath -w /tmp 2>/dev/null || echo /tmp)
UID_=$(curl -s "$B/Users/Me" -H "$H" | python -c "import sys,json;print(json.load(sys.stdin)['Id'])")
cat > /tmp/pb_test.json <<EOF
{"UserId":"$UID_","AutoOpenLiveStream":true,"EnableDirectPlay":true,"EnableDirectStream":true,"EnableTranscoding":true,"MaxStreamingBitrate":40000000,
 "DeviceProfile":{"Name":"test","MaxStreamingBitrate":40000000,
   "DirectPlayProfiles":[{"Container":"ts,mpegts,mp4,mkv","Type":"Video","VideoCodec":"h264","AudioCodec":"aac"}],
   "TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac","Protocol":"hls","Context":"Streaming"}],
   "CodecProfiles":[],"SubtitleProfiles":[],"ContainerProfiles":[]}}
EOF
curl -s -X POST "$B/Items/$CH/PlaybackInfo" -H "$H" -H 'Content-Type: application/json' -d @/tmp/pb_test.json -o /tmp/pb_test_out.json -w "open HTTP %{http_code}\n"
UNIQ=$(curl -s "$B/TvStation/Status" -H "$H" | grep -o 'tvs[0-9a-f]*' | head -1)
echo "session $UNIQ, reading for ${SECS}s"
H0=$(date +%s.%N); curl -s -m "$SECS" "$B/LiveTv/LiveStreamFiles/$UNIQ/stream.ts" -o /tmp/stream_test.ts; H1=$(date +%s.%N)
echo "--- restreamer log ---"
docker logs --since "$((SECS + 30))s" tvstation-jf 2>&1 | grep -E 'playing|slate|exited|ended early|overran' | sed -E 's/^\[([0-9:]+)\].*TvStation \[[^]]*\]: /\1 /' | cut -c1-160
python - "$W" "$H0" "$H1" <<'PY'
import sys, os
d = open(os.path.join(sys.argv[1], 'stream_test.ts'), 'rb').read()
wall = float(sys.argv[3]) - float(sys.argv[2])
ps = []
for i in range(0, len(d) - 187, 188):
    p = d[i:i + 188]
    if p[0] == 0x47 and ((p[3] >> 4) & 2) and p[4] >= 7 and (p[5] & 0x10):
        ps.append(((p[6] << 25) | (p[7] << 17) | (p[8] << 9) | (p[9] << 1) | (p[10] >> 7)) / 90000.0)
jumps = [(round(ps[i - 1], 1), round(ps[i], 1)) for i in range(1, len(ps)) if abs(ps[i] - ps[i - 1]) > 1.0]
back = [j for j in jumps if j[1] < j[0]]
fwd = [j for j in jumps if j[1] >= j[0]]
print('host wall %.1fs, %.1f MB, PCR span %.1fs, forward jumps %s, BACKWARD jumps %s' % (wall, len(d) / 1e6, ps[-1] - ps[0] if ps else 0, fwd[:6], back[:6]))
PY
LS=$(python -c "import json,sys;print(json.load(open(sys.argv[1]+'/pb_test_out.json'))['MediaSources'][0]['LiveStreamId'])" "$W")
curl -s -X POST "$B/LiveStreams/Close?liveStreamId=$LS" -H "$H" -o /dev/null -w "closed HTTP %{http_code}\n"
