"""Builds the plugin's embedded base map from Natural Earth 10m GeoJSON (public domain):
land polygons, lakes, country borders, state/province borders and populated places, simplified and
packed into a small deflated binary (see BaseMap.cs for the reader).

Usage: python make_basemap.py <ne-dir> <out.bin>
"""
import gc, json, math, struct, sys, zlib, os, time

NE = sys.argv[1]
OUT = sys.argv[2]
UNIT = 1000.0  # coordinates stored in 1e-3 degrees (about 110 m, a third of a pixel at the closest zoom)
TOL = 0.004     # simplification tolerance in degrees (~400 m)

LAND, LAKES, COUNTRY_LINES, STATE_LINES = 1, 2, 3, 4


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def load(name):
    with open(os.path.join(NE, name + '.geojson'), encoding='utf-8') as f:
        return json.load(f)


def decimate(points, step):
    """Drops points closer than step to the last kept one (keeps the ends)."""
    if len(points) < 3:
        return points
    out = [points[0]]
    lx, ly = points[0]
    s2 = step * step
    for p in points[1:-1]:
        dx, dy = p[0] - lx, p[1] - ly
        if dx * dx + dy * dy >= s2:
            out.append(p)
            lx, ly = p
    out.append(points[-1])
    return out


def dp(points, tol):
    """Douglas-Peucker, iterative, on (lon, lat) pairs; tolerance in degrees."""
    n = len(points)
    if n < 3:
        return points
    keep = bytearray(n)
    keep[0] = keep[-1] = 1
    stack = [(0, n - 1)]
    tol2 = tol * tol
    while stack:
        a, b = stack.pop()
        if b - a < 2:
            continue
        ax, ay = points[a]
        bx, by = points[b]
        dx, dy = bx - ax, by - ay
        L2 = dx * dx + dy * dy
        best, besti = 0.0, -1
        for i in range(a + 1, b):
            px, py = points[i]
            if L2 == 0:
                d2 = (px - ax) ** 2 + (py - ay) ** 2
            else:
                t = ((px - ax) * dx + (py - ay) * dy) / L2
                if t < 0:
                    t = 0.0
                elif t > 1:
                    t = 1.0
                qx, qy = ax + t * dx, ay + t * dy
                d2 = (px - qx) ** 2 + (py - qy) ** 2
            if d2 > best:
                best, besti = d2, i
        if best > tol2:
            keep[besti] = 1
            stack.append((a, besti))
            stack.append((besti, b))
    return [p for p, k in zip(points, keep) if k]


def simplify(points, tol):
    return dp(decimate(points, tol / 2), tol)


def rings_of(geom):
    if not geom:
        return []
    t = geom['type']
    if t == 'Polygon':
        return [geom['coordinates']]
    if t == 'MultiPolygon':
        return geom['coordinates']
    return []


def lines_of(geom):
    if not geom:
        return []
    t = geom['type']
    if t == 'LineString':
        return [geom['coordinates']]
    if t == 'MultiLineString':
        return geom['coordinates']
    return []


def extent(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return max(xs) - min(xs), max(ys) - min(ys)


out = bytearray()


def varint(n):
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return


def zigzag(n):
    varint((n << 1) ^ (n >> 63))


def write_polylines(layer, polylines, closed):
    varint(layer)
    varint(len(polylines))
    total = 0
    for pts in polylines:
        if closed and pts[0] == pts[-1]:
            pts = pts[:-1]
        varint(len(pts))
        px = py = 0
        for lon, lat in pts:
            x, y = int(round(lon * UNIT)), int(round(lat * UNIT))
            zigzag(x - px)
            zigzag(y - py)
            px, py = x, y
        total += len(pts)
    return total


def polygons(name, tol, min_extent):
    rings = []
    data = load(name)
    for f in data['features']:
        for poly in rings_of(f['geometry']):
            for ring in poly:  # outer ring and holes alike: drawn with even-odd fill
                pts = [(float(p[0]), float(p[1])) for p in ring]
                w, h = extent(pts)
                if max(w, h) < min_extent:
                    continue
                s = simplify(pts, tol)
                if len(s) >= 4:
                    rings.append(s)
    del data
    gc.collect()
    return rings


def lines(name, tol):
    result = []
    data = load(name)
    for f in data['features']:
        for line in lines_of(f['geometry']):
            pts = [(float(p[0]), float(p[1])) for p in line]
            s = simplify(pts, tol)
            if len(s) >= 2:
                result.append(s)
    del data
    gc.collect()
    return result


out += b'JSMAP2'
n = write_polylines(LAND, polygons('ne_10m_land', TOL, 0.01), True)
log('land points', n, 'bytes', len(out))
n = write_polylines(LAKES, polygons('ne_10m_lakes', TOL, 0.05), True)
log('lakes points', n, 'bytes', len(out))
n = write_polylines(COUNTRY_LINES, lines('ne_10m_admin_0_boundary_lines_land', TOL), False)
log('country lines points', n, 'bytes', len(out))
n = write_polylines(STATE_LINES, lines('ne_10m_admin_1_states_provinces_lines', TOL), False)
log('state lines points', n, 'bytes', len(out))

places = []
for f in load('ne_10m_populated_places_simple')['features']:
    p = f['properties']
    lon, lat = f['geometry']['coordinates']
    pop = max(0, int(p.get('pop_max') or 0))  # -99 marks "unknown" in Natural Earth
    rank = min(255, max(0, int(p.get('scalerank') if p.get('scalerank') is not None else 10)))
    name = p.get('name') or ''
    if not name or (pop < 15000 and rank > 7):
        continue
    places.append((name, float(lat), float(lon), rank, pop))
varint(5)
varint(len(places))
for name, lat, lon, rank, pop in places:
    b = name.encode('utf-8')
    varint(len(b))
    out += b
    out += struct.pack('<ii', int(round(lat * UNIT)), int(round(lon * UNIT)))
    out.append(min(255, rank))
    varint(pop)
log('places', len(places), 'bytes', len(out))

packed = zlib.compress(bytes(out), 9)[2:-4]  # raw deflate for System.IO.Compression.DeflateStream
with open(OUT, 'wb') as f:
    f.write(packed)
log('raw', len(out), 'deflated', len(packed))
