#!/bin/sh
# Regenerates keepawake.mp4 in wwwroot: a 1-second, 16x16, black, H.264/yuv420p,
# faststart-muxed video used only to keep the display machine awake in browsers
# without the Wake Lock API. Run from anywhere with ffmpeg on PATH:
# `sh tools/generate-keepawake.sh`.
#
# Lives in tools/, not src/EventPhotoBot/wwwroot, so it is a dev utility only —
# wwwroot is published into the container image and served as static files, and
# this script served at /generate-keepawake.sh served no purpose there.
set -eu
cd "$(dirname "$0")/../src/EventPhotoBot/wwwroot"
ffmpeg -y -f lavfi -i color=black:s=16x16:d=1 -c:v libx264 -pix_fmt yuv420p \
  -movflags +faststart keepawake.mp4
