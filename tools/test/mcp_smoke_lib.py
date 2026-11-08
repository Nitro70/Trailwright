"""Shared client for the smoke tests: for the Trailwright AI port (MCP over streamable HTTP, stateless).
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


