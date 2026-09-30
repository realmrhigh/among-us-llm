#!/usr/bin/env python3
"""
Reads the wall / obstacle collision shapes of every Among Us ship out of YOUR OWN installed copy of the game and
writes them as small JSON files that the LLM bots use to path around walls.

The output is derived from the game's assets, so it is git-ignored (see .gitignore) and is not meant to be
redistributed. Each person who wants wall-aware bots runs this once against their own install.

    pip install UnityPy
    python tools/extract_collision.py                       # finds the Steam install, writes ./collision-data
    python tools/extract_collision.py --game "D:\\Games\\Among Us" --out collision-data

Output: collision-data/<ship>.json  ->  {"ship", "solids": [{"name","layer","closed","points":[[x,y],...]}, ...]}
Coordinates are Unity world units, the same units the game (and Impostor) use for player positions.
"""
import argparse
import json
import math
import os
import sys

try:
    import UnityPy
except ImportError:
    sys.exit("UnityPy is required: pip install UnityPy")

SHIPS = {
    "SkeldShip": "skeld",
    "MiraShip": "mira",
    "PolusShip": "polus",
    "Airship": "airship",
    "FungleShip": "fungle",
    "AprilShip": "april",
}

# Unity layers that block walking in Among Us: 9 = Ship (walls), 10 = room walls, 12 = short objects (tables, beds...).
SOLID_LAYERS = {9, 10, 12}

STEAM_DEFAULTS = [
    r"C:\Program Files (x86)\Steam\steamapps\common\Among Us",
    r"C:\Program Files\Steam\steamapps\common\Among Us",
    os.path.expanduser("~/.steam/steam/steamapps/common/Among Us"),
]


def find_game(explicit):
    candidates = [explicit] if explicit else STEAM_DEFAULTS
    for c in candidates:
        if c and os.path.isdir(os.path.join(c, "Among Us_Data")):
            return c
    sys.exit("Could not find the game. Pass --game <folder containing 'Among Us_Data'>.")


def quat_z(q):
    return math.atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.y * q.y + q.z * q.z))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", help="Among Us install folder")
    ap.add_argument("--out", default="collision-data")
    ap.add_argument("--circle-segments", type=int, default=12)
    args = ap.parse_args()

    game = find_game(args.game)
    bundle = os.path.join(game, "Among Us_Data", "StreamingAssets", "aa", "Steam", "StandaloneWindows", "initialmaps_assets_all.bundle")
    if not os.path.isfile(bundle):
        sys.exit(f"Map bundle not found: {bundle}")

    env = UnityPy.load(bundle)
    objs = {o.path_id: o for o in env.objects}

    tf, go = {}, {}
    for o in env.objects:
        t = o.type.name
        if t in ("Transform", "RectTransform"):
            x = o.read()
            tf[o.path_id] = dict(
                go=x.m_GameObject.path_id,
                parent=x.m_Father.path_id,
                kids=[c.path_id for c in x.m_Children],
                pos=(x.m_LocalPosition.x, x.m_LocalPosition.y),
                rot=quat_z(x.m_LocalRotation),
                scl=(x.m_LocalScale.x, x.m_LocalScale.y),
            )
        elif t == "GameObject":
            g = o.read()
            comps = []
            for c in g.m_Components:
                comps.append(c.path_id if hasattr(c, "path_id") else c[1].path_id)
            go[o.path_id] = dict(name=g.m_Name, layer=g.m_Layer, active=g.m_IsActive, comps=comps)

    def world(t, pt):
        x, y = pt
        while t:
            n = tf[t]
            x, y = x * n["scl"][0], y * n["scl"][1]
            c, s = math.cos(n["rot"]), math.sin(n["rot"])
            x, y = x * c - y * s, x * s + y * c
            x, y = x + n["pos"][0], y + n["pos"][1]
            t = n["parent"] if n["parent"] in tf else 0
        return [round(x, 4), round(y, 4)]

    os.makedirs(args.out, exist_ok=True)

    for root_name, short in SHIPS.items():
        roots = [t for t, v in tf.items() if v["parent"] == 0 and go[v["go"]]["name"] == root_name]
        if not roots:
            print(f"  {short}: root '{root_name}' not found, skipped")
            continue

        solids = []
        # Walk the hierarchy, skipping inactive branches.
        stack = [(roots[0], True)]
        while stack:
            t, parent_active = stack.pop()
            g = go[tf[t]["go"]]
            active = parent_active and g["active"]
            for k in tf[t]["kids"]:
                if k in tf:
                    stack.append((k, active))
            if not active or g["layer"] not in SOLID_LAYERS:
                continue
            for cid in g["comps"]:
                o = objs.get(cid)
                if o is None or "Collider2D" not in o.type.name:
                    continue
                c = o.read()
                if getattr(c, "m_IsTrigger", False) or not getattr(c, "m_Enabled", 1):
                    continue
                off = (c.m_Offset.x, c.m_Offset.y)
                shapes = []  # (points, closed)
                if o.type.name == "EdgeCollider2D":
                    shapes.append(([(p.x + off[0], p.y + off[1]) for p in c.m_Points], False))
                elif o.type.name == "PolygonCollider2D":
                    for path in c.m_Points.m_Paths:
                        shapes.append(([(p.x + off[0], p.y + off[1]) for p in path], True))
                elif o.type.name == "BoxCollider2D":
                    hx, hy = c.m_Size.x / 2, c.m_Size.y / 2
                    shapes.append(([(off[0] - hx, off[1] - hy), (off[0] + hx, off[1] - hy), (off[0] + hx, off[1] + hy), (off[0] - hx, off[1] + hy)], True))
                elif o.type.name == "CircleCollider2D":
                    r = c.m_Radius
                    n = args.circle_segments
                    shapes.append(([(off[0] + r * math.cos(2 * math.pi * i / n), off[1] + r * math.sin(2 * math.pi * i / n)) for i in range(n)], True))
                for pts, closed in shapes:
                    if len(pts) >= 2:
                        solids.append(dict(name=g["name"], layer=g["layer"], closed=closed, points=[world(t, p) for p in pts]))

        path = os.path.join(args.out, f"{short}.json")
        with open(path, "w") as f:
            json.dump(dict(ship=short, solids=solids), f, separators=(",", ":"))
        print(f"  {short}: {len(solids)} solid shapes -> {path}")


if __name__ == "__main__":
    main()
