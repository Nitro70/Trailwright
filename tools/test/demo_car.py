"""Builds a small demo car through the AI port (used for the README screenshot and as an end-to-end test).
Usage: python demo_car.py [url]"""
import sys

sys.argv = sys.argv[:2]
from mcp_smoke_lib import call  # noqa: E402

print(call("new_build", name="Demo buggy", discard_changes=True))
# chassis: 3 x 6 blocks of 2x2x2 -> 6 wide, 12 long
items = [{"type": "Block 2x2x2", "x": x, "y": 0, "z": z, "color": "blue"} for x in (-2, 0, 2) for z in range(0, 12, 2)]
r = call("place_blocks", blocks=items)
print("chassis", "error" in r and r["error"] or len(r["placed"]))
seat = call("fit_block", type="Car Seat", x=0, y=2, z=5)
o = seat["options"][0]
print("seat", call("place_block", type="Car Seat", x=o["x"], y=o["y"], z=o["z"], rx=o["rx"], ry=o["ry"], rz=o["rz"], color="black").get("placed"))
# wheels on both sides near the front and back
wheel = "Drag Racing Wheel"
for side_x in (-3, 4):
    for z in (1, 10):
        f = call("fit_block", type=wheel, x=side_x, y=0, z=z)
        if not f["count"]:
            print("no wheel fit at", side_x, z)
            continue
        o = f["options"][0]
        print("wheel", call("place_block", type=wheel, x=o["x"], y=o["y"], z=o["z"], rx=o["rx"], ry=o["ry"], rz=o["rz"], color="black").get("placed"))
# a spoiler bar at the back
for x in (-2, 0, 2):
    f = call("fit_block", type="Block 1x2", x=x, y=2, z=0)
    if f["count"]:
        o = f["options"][0]
        call("place_block", type="Block 1x2", x=o["x"], y=o["y"], z=o["z"], rx=o["rx"], ry=o["ry"], rz=o["rz"], color="orange")
print(call("builder_status"))
