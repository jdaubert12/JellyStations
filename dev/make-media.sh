#!/usr/bin/env bash
# Generates small synthetic test clips using the ffmpeg bundled in the Jellyfin image.
# Usage: bash dev/make-media.sh   (run from the repository root; needs Docker)
set -euo pipefail
M="$(cd "$(dirname "$0")" && pwd)/media"
mkdir -p "$M/tv/Test Show/Season 01" "$M/tv/Other Show/Season 01" "$M/movies/Movie Alpha (2020)" "$M/movies/Movie Beta (2021)"

gen() {
  local out="$1" dur="$2" size="$3" freq="$4"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$M:/media" --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg jellyfin/jellyfin:12.2 \
    -y -loglevel error -f lavfi -i "testsrc=size=$size:rate=30" -f lavfi -i "sine=frequency=$freq:sample_rate=48000" \
    -t "$dur" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -b:a 96k -movflags +faststart "/media/$out"
  echo "made $out"
}

gen "tv/Test Show/Season 01/Test Show S01E01.mp4" 150 1280x720 440
gen "tv/Test Show/Season 01/Test Show S01E02.mp4" 150 1280x720 523
gen "tv/Test Show/Season 01/Test Show S01E03.mp4" 150 1280x720 659
gen "tv/Test Show/Season 01/Test Show S01E04.mp4" 150 1280x720 784
gen "tv/Other Show/Season 01/Other Show S01E01.mp4" 90 854x480 330
gen "tv/Other Show/Season 01/Other Show S01E02.mp4" 90 854x480 392
gen "movies/Movie Alpha (2020)/Movie Alpha (2020).mp4" 300 1920x1080 220
gen "movies/Movie Beta (2021)/Movie Beta (2021).mp4" 240 1280x720 294

# A stacked (multi-part) movie, as Jellyfin sees a Blu-ray split into part1/part2: cut from Movie Alpha.
mkdir -p "$M/movies/Movie Gamma (2022)"
cut() {
  local out="$1" start="$2" dur="$3"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$M:/media" --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg jellyfin/jellyfin:12.2     -y -loglevel error -ss "$start" -i "/media/movies/Movie Alpha (2020)/Movie Alpha (2020).mp4" -t "$dur" -c copy -movflags +faststart "/media/$out"
  echo "made $out"
}
cut "movies/Movie Gamma (2022)/Movie Gamma (2022) - part1.mp4" 0 60
cut "movies/Movie Gamma (2022)/Movie Gamma (2022) - part2.mp4" 60 45

# Short clips for bumpers / pre-roll / post-roll tests, filed as movies so the library picks them up.
mkdir -p "$M/movies/Ident A (2024)" "$M/movies/Ident B (2024)" "$M/movies/Outro A (2024)"
gen "movies/Ident A (2024)/Ident A (2024).mp4" 10 640x360 880
gen "movies/Ident B (2024)/Ident B (2024).mp4" 15 640x360 988
gen "movies/Outro A (2024)/Outro A (2024).mp4" 20 640x360 1047

# A folder of bumps on its own, added as a "Home videos" library named Bumps: plain videos, which the library
# analysis recognises as a clip library and the suggestions offer as a bumper source.
mkdir -p "$M/bumps"
gen "bumps/Station ident 1.mp4" 8 640x360 660
gen "bumps/Station ident 2.mp4" 12 640x360 740
gen "bumps/Coming up next.mp4" 20 640x360 830
gen "bumps/We will be right back.mp4" 30 640x360 932

# Interlacing baked into progressive frames, for the picture check: a DVD-sized cartoon with 3:2 pulldown woven
# in (top field first), video-rate fields woven in (bottom field first), the same drawings clean, and a starved
# progressive encode that idet alone would call interlaced. Every file is flagged progressive.
mkdir -p "$M/tv/Combed Show/Season 01"
combed() {
  local out="$1" rate="$2" step="$3" filter="$4" video="$5"
  local toon="color=c=0x2a6fb0:s=720x480:r=$rate[bg];color=c=0xf0c020:s=160x200:r=$rate[b1];color=c=0xd03030:s=90x90:r=$rate[b2];[bg][b1]overlay=x='mod(floor(n/$step)*14,720)':y=120[t];[t][b2]overlay=x='600-mod(floor(n/$step)*9,600)':y=300,format=yuv420p[out0]"
  MSYS_NO_PATHCONV=1 docker run --rm -v "$M:/media" --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg jellyfin/jellyfin:12.2 \
    -y -loglevel error -f lavfi -i "$toon" -f lavfi -i "sine=frequency=500:sample_rate=48000" -t 150 \
    -vf "$filter" $video -c:a aac -b:a 96k "/media/tv/Combed Show/Season 01/$out"
  echo "made $out"
}
X265="-c:v libx265 -preset veryfast -crf 24 -x265-params log-level=error"
combed "Combed Show S01E01.mkv" 24000/1001 2 "telecine=first_field=top:pattern=23" "$X265"
combed "Combed Show S01E02.mkv" 60000/1001 1 "interlace=scan=bff:lowpass=off,setfield=prog" "-r 30000/1001 $X265"
combed "Combed Show S01E03.mkv" 30000/1001 3 "null" "$X265"
MSYS_NO_PATHCONV=1 docker run --rm -v "$M:/media" --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg jellyfin/jellyfin:12.2 \
  -y -loglevel error -f lavfi -i "testsrc2=s=720x480:r=30000/1001" -f lavfi -i "sine=frequency=600:sample_rate=48000" -t 150 \
  -c:v libx264 -preset veryfast -b:v 150k -pix_fmt yuv420p -c:a aac -b:a 96k "/media/tv/Combed Show/Season 01/Combed Show S01E04.mkv"
echo "made Combed Show S01E04.mkv"
