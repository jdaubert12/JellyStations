# Example stations

Station definitions exported from a real server (see the library inventory in the project notes).
Import them from the dashboard (**Import**) or with `POST /TvStation/Stations/Import`.

Genre, tag and library sources resolve by name. Hand-picked movies, series, seasons and episodes
carry the original server's item ids, so on another server they are matched by title; what cannot
be matched is dropped and listed in the import result. `all-stations.json` holds the whole set.
