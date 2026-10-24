# Example stations

Eleven station definitions that show the plugin's modes and options: a kids channel, anime, sci-fi
series, animated films, family movies by genre, action, sci-fi and fantasy movies, comedy, a seasonal
holiday channel, a retro movies pool drawn from a whole library, and a weekly **Franchise Nights**
schedule with a different film series each evening and a comedy and adventure filler between blocks.

Hand-picked movies and series carry placeholder titles ("Holiday Classic 3 (1999)", "Franchise A
Part 2 (2002)") and made-up item ids. Import a station for its structure, then swap in your own
titles in the editor; the placeholders match nothing, are dropped by the import and listed in its
result. Genre, tag and library sources resolve by name and work as they are.

Import from the dashboard (**Import**) or with `POST /JellyStations/Stations/Import`. `all-stations.json`
holds the whole set in one file; `logos/` has a generated logo for each station, which the editor
can replace with one made from the name.
