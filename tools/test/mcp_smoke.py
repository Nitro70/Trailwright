"""Smoke test for the Trailwright AI port (MCP over streamable HTTP, stateless).
Usage: python mcp_smoke.py [url]   (default http://127.0.0.1:7341/mcp)"""
import json, sys, urllib.request

URL = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:7341/mcp"
_id = 0


def rpc(method, params=None):
    global _id
    _id += 1
    body = json.dumps({"jsonrpc": "2.0", "id": _id, "method": method, "params": params or {}}).encode()
    req = urllib.request.Request(URL, body, {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    raw = urllib.request.urlopen(req, timeout=30).read().decode()
    if raw.lstrip().startswith("{"):
        msg = json.loads(raw)
    else:  # SSE: take the data line
        msg = json.loads([l[5:] for l in raw.splitlines() if l.startswith("data:")][-1])
    if "error" in msg:
        raise RuntimeError(msg["error"])
    return msg["result"]


def call(_tool, **args):
    r = rpc("tools/call", {"name": _tool, "arguments": args})
    text = r["content"][0]["text"]
    return json.loads(text)


init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
print("server:", init["serverInfo"], "| instructions:", len(init.get("instructions", "")), "chars")
tools = rpc("tools/list")["tools"]
print("tools:", len(tools), [t["name"] for t in tools])
print(call("new_build", name="Smoke test car", discard_changes=True))
seats = call("list_block_types", category="SEAT", limit=5)
print("seats:", [(t["name"], t["size"]) for t in seats["types"]])
frame = call("block_type_info", type="Block 2x2x2")
print("frame:", frame["type"]["size"], "attach points:", len(frame["attachPoints"]))
# a 4x2x8 floor of 2x2x2 blocks
items = [{"type": "Block 2x2x2", "x": x, "y": 0, "z": z} for x in (0, 2) for z in (0, 2, 4, 6)]
r = call("place_blocks", blocks=items, allow_floating=False)
print("floor:", r if "error" in r else [(p["id"], p["connections"]) for p in r["placed"]])
seat = seats["types"][0]["name"]
print("check seat:", call("check_placement", type=seat, x=1, y=2, z=2))
r = call("place_block", type=seat, x=1, y=2, z=2)
print("seat:", r.get("error") or (r["placed"], r["connections"]))
print(call("layer_map", y=0)["map"])
spots = call("find_attach_spots", id=1)
print("free spots on #1:", len(spots["free"]), spots["free"][:3])
props = call("list_block_types", query="Propeller", limit=3)["types"]
print("propellers:", [(t["name"], t.get("settings")) for t in props])
wheels = call("list_block_types", category="WHEEL", limit=3)["types"]
print("wheels:", [(t["name"], t["size"]) for t in wheels])
hinge = call("list_block_types", query="Steering Hinge", limit=1)["types"][0]
# put a hinge on the floor's edge and turn its speed past the limit
spot = next(s for s in spots["free"] if "left" in s["facing"])
c = spot["emptyCell"]
fits = call("fit_block", type=hinge["name"], x=c[0], y=c[1], z=c[2])
print("hinge fits:", fits["count"], fits["options"][:2])
o = fits["options"][0]
r = call("place_block", type=hinge["name"], x=o["x"], y=o["y"], z=o["z"], rx=o["rx"], ry=o["ry"], rz=o["rz"])
print("hinge:", r.get("error") or (r["placed"], r["connections"]))
if "placed" in r:
    print("setting:", call("set_block_setting", id=r["placed"], setting="Speed", value=50))
w = call("fit_block", type=wheels[1]["name"], x=-1, y=0, z=6)
print("wheel fits:", w["count"], w["options"][:1])
if w["count"]:
    o = w["options"][0]
    r = call("place_block", type=wheels[1]["name"], x=o["x"], y=o["y"], z=o["z"], rx=o["rx"], ry=o["ry"], rz=o["rz"])
    print("wheel:", r.get("error") or (r["placed"], r["connections"], r["block"].get("settings")))
print(call("paint_blocks", ids=[1, 2, 3], color="red"))
print(call("set_block_keys", id=1, positive="StructureForward", negative="StructureBack").get("keys"))
print("undo:", call("undo"), "redo:", call("redo"))
print("status:", call("builder_status"))
print("save:", call("save_build", as_new=True))
