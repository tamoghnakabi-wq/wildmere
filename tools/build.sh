#!/bin/bash
# Build players. usage: tools/build.sh [mac|win|all]   (default mac)
cd "$(dirname "$0")/.." || exit 1
case "${1:-mac}" in
  mac) exec ./tools/unity.sh BuildScript.BuildMac ;;
  win) exec ./tools/unity.sh BuildScript.BuildWindows ;;
  all) exec ./tools/unity.sh BuildScript.BuildAll ;;
  *) echo "usage: $0 [mac|win|all]"; exit 2 ;;
esac
