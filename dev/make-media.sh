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
