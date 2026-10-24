#!/usr/bin/env bash
# Completes the Jellyfin first-run wizard on the local test container, creates an admin user
# (admin / admin), adds the Movies and Shows libraries pointing at /media, and waits for the scan.
# Writes the admin API token to dev/.token for ad-hoc curl calls.
set -euo pipefail
BASE="${JF_URL:-http://localhost:8096}"
HERE="$(cd "$(dirname "$0")" && pwd)"
AUTH='Authorization: MediaBrowser Client="jellystations-dev", Device="dev", DeviceId="jellystations-dev-1", Version="1.0"'

echo "Waiting for $BASE ..."
for i in $(seq 1 90); do
  if curl -sf -m 3 "$BASE/System/Info/Public" >/dev/null 2>&1; then break; fi
  sleep 2
done
curl -sf -m 3 "$BASE/System/Info/Public" >/dev/null || { echo "server not reachable"; exit 1; }

wizard=$(curl -s "$BASE/System/Info/Public" | grep -o '"StartupWizardCompleted":[a-z]*' | cut -d: -f2)
if [ "$wizard" != "true" ]; then
  echo "Running startup wizard"
  curl -sf -X POST "$BASE/Startup/Configuration" -H "$AUTH" -H 'Content-Type: application/json' \
    -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
  curl -sf "$BASE/Startup/User" -H "$AUTH" >/dev/null
  curl -sf -X POST "$BASE/Startup/User" -H "$AUTH" -H 'Content-Type: application/json' -d '{"Name":"admin","Password":"admin"}'
  curl -sf -X POST "$BASE/Startup/RemoteAccess" -H "$AUTH" -H 'Content-Type: application/json' -d '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}'
  curl -sf -X POST "$BASE/Startup/Complete" -H "$AUTH"
  echo "Wizard complete"
fi

TOKEN=$(curl -sf -X POST "$BASE/Users/AuthenticateByName" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"Username":"admin","Pw":"admin"}' | grep -o '"AccessToken":"[^"]*"' | cut -d'"' -f4)
[ -n "$TOKEN" ] || { echo "login failed"; exit 1; }
echo "$TOKEN" > "$HERE/.token"
H="Authorization: MediaBrowser Client=\"jellystations-dev\", Device=\"dev\", DeviceId=\"jellystations-dev-1\", Version=\"1.0\", Token=\"$TOKEN\""

have=$(curl -sf "$BASE/Library/VirtualFolders" -H "$H")
opts='{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableChapterImageExtraction":false,"ExtractChapterImagesDuringLibraryScan":false,"EnableTrickplayImageExtraction":false,"SaveLocalMetadata":false,"TypeOptions":[{"Type":"Movie","MetadataFetchers":[],"ImageFetchers":[]},{"Type":"Series","MetadataFetchers":[],"ImageFetchers":[]},{"Type":"Season","MetadataFetchers":[],"ImageFetchers":[]},{"Type":"Episode","MetadataFetchers":[],"ImageFetchers":[]}]}}'
if ! echo "$have" | grep -q '"Name":"Movies"'; then
  curl -sf -X POST "$BASE/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false&paths=%2Fmedia%2Fmovies" -H "$H" -H 'Content-Type: application/json' -d "$opts"
  echo "Added Movies library"
fi
if ! echo "$have" | grep -q '"Name":"Shows"'; then
  curl -sf -X POST "$BASE/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=false&paths=%2Fmedia%2Ftv" -H "$H" -H 'Content-Type: application/json' -d "$opts"
  echo "Added Shows library"
fi

curl -sf -X POST "$BASE/Library/Refresh" -H "$H"
echo "Scanning library..."
for i in $(seq 1 60); do
  count=$(curl -sf "$BASE/Items?IncludeItemTypes=Movie,Episode&Recursive=true&Limit=0" -H "$H" | grep -o '"TotalRecordCount":[0-9]*' | cut -d: -f2)
  if [ "${count:-0}" -ge 8 ]; then echo "Library has $count videos"; break; fi
  sleep 3
done
echo "Token saved to dev/.token"
