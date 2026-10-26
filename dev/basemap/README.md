# Base map for the radar screen

`src/Jellyfin.Plugin.JellyStations/Assets/basemap.bin` is the map under the weather channel's radar:
Natural Earth 1:10m land, lakes, country borders, state/province borders and populated places
(public domain), simplified to about 400 m and packed as delta-coded varints in thousandths of a
degree, deflated. `BaseMap.cs` reads it.

To rebuild it (only needed if Natural Earth changes or the simplification should change):

```
mkdir ne && cd ne
for f in ne_10m_land ne_10m_lakes ne_10m_admin_0_boundary_lines_land ne_10m_admin_1_states_provinces_lines ne_10m_populated_places_simple; do
  curl -sLO "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/$f.geojson"
done
cd ..
python make_basemap.py ne ../../src/Jellyfin.Plugin.JellyStations/Assets/basemap.bin
```

The script needs only the Python standard library and runs in a few seconds. The downloaded GeoJSON
(about 45 MB) is not kept in the repository.
