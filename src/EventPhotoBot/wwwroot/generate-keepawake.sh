#!/bin/sh
# Regenerates keepawake.mp4 next to this script: a 1-second, 16x16, black,
# H.264/yuv420p, faststart-muxed video used only to keep the display machine
# awake in browsers without the Wake Lock API. Run from anywhere with ffmpeg
# on PATH: `sh src/EventPhotoBot/wwwroot/generate-keepawake.sh`.
set -eu
cd "$(dirname "$0")"
ffmpeg -y -f lavfi -i color=black:s=16x16:d=1 -c:v libx264 -pix_fmt yuv420p \
  -movflags +faststart keepawake.mp4
