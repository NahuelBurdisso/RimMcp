"""MCP server (stdio) for the RimMcp mod: exposes a running RimWorld game as tools.

Standard library only. Register with Claude Code, e.g. in a project .mcp.json:

    {"mcpServers": {"rimworld": {"command": "python", "args": ["<path>/rimmcp_server.py"]}}}

The mod writes <save data folder>/RimMcp/endpoint.json (port + token) when the game
starts. This server looks for it in RIMMCP_ENDPOINT (a file), RIMMCP_DATA_DIRS
(os.pathsep-separated save data folders) and the default RimWorld save data folder,
and talks to whichever endpoint answers. The game can start or stop at any time.
"""
import glob
import json
import os
import sys
import urllib.error
import urllib.request

SERVER_INFO = {"name": "rimworld", "version": "0.1.0"}
PROTOCOLS = ["2025-06-18", "2025-03-26", "2024-11-05"]

INSTRUCTIONS = (
    "Tools to read and control a running RimWorld game through the RimMcp mod. "
    "Call status first. Reads are cheap; actions change the player's game, so only "
    "draft, change speed or edit work priorities when the user asked for it."
)

NAME = {"type": "string", "description": "Colonist short name (as shown in game) or part of the full name"}

TOOLS = [
    {"name": "status", "read": True,
     "description": "Whether RimWorld is running and a game is loaded; colony name, date, hour, speed, "
                    "weather, temperature, wealth, storyteller, number of colonists.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "colonists", "read": True,
     "description": "All free colonists: mood % and minor-break threshold %, mental state, health %, "
                    "bleeding, downed, drafted, current job, position.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "colonist", "read": True,
     "description": "One colonist in detail: traits, skills and passions, health conditions, mood "
                    "thoughts with their mood effect, needs, work priorities, weapon, apparel, inventory.",
     "inputSchema": {"type": "object", "properties": {"name": NAME}, "required": ["name"]}},
    {"name": "resources", "read": True,
     "description": "Stored resources on the current map (top 80 by value), total edible nutrition and "
                    "an approximate number of food days.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "alerts", "read": True,
     "description": "Active alerts (with explanations), unread letters and on-screen messages.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "threats", "read": True,
     "description": "Per map: hostile pawns grouped by faction and kind, number of fires, active "
                    "conditions (heat wave, toxic fallout...).",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "set_speed", "read": False,
     "description": "Set game speed: pause, normal, fast or superfast.",
     "inputSchema": {"type": "object", "properties": {
         "speed": {"type": "string", "enum": ["pause", "normal", "fast", "superfast"]}},
         "required": ["speed"]}},
    {"name": "draft", "read": False,
     "description": "Draft or undraft colonists. Omit names to affect every colonist who can be drafted.",
     "inputSchema": {"type": "object", "properties": {
         "names": {"type": "array", "items": {"type": "string"}},
         "drafted": {"type": "boolean", "default": True}}}},
    {"name": "set_work_priority", "read": False,
     "description": "Set a colonist's work priority: 0 = off, 1 = highest ... 4 = lowest. Work is a "
                    "work type defName or label (Doctor, Cooking, Growing, Hauling...).",
     "inputSchema": {"type": "object", "properties": {
         "name": NAME, "work": {"type": "string"},
         "priority": {"type": "integer", "minimum": 0, "maximum": 4}},
         "required": ["name", "work", "priority"]}},
    {"name": "message", "read": False,
     "description": "Show a short message on the player's screen, prefixed with [Claude].",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}},
    {"name": "focus", "read": False,
     "description": "Move the camera to a colonist and select them.",
     "inputSchema": {"type": "object", "properties": {"name": NAME}, "required": ["name"]}},
]
TOOL_NAMES = {t["name"] for t in TOOLS}


class GameDown(Exception):
    pass


class StaleEndpoint(Exception):
    """The game answered but rejected our token: it restarted and wrote a new one."""


def candidate_files():
    files = []
    if os.environ.get("RIMMCP_ENDPOINT"):
        files.append(os.environ["RIMMCP_ENDPOINT"])
    dirs = [d for d in os.environ.get("RIMMCP_DATA_DIRS", "").split(os.pathsep) if d]
    local_low = os.path.join(os.environ.get("USERPROFILE", ""), "AppData", "LocalLow")
    dirs.append(os.path.join(local_low, "Ludeon Studios", "RimWorld by Ludeon Studios"))
    for d in dirs:
        files += glob.glob(os.path.join(d, "RimMcp", "endpoint.json"))
    existing = [f for f in dict.fromkeys(files) if os.path.isfile(f)]
    return sorted(existing, key=os.path.getmtime, reverse=True)


def http(ep, method, path, body=None, timeout=15):
    req = urllib.request.Request(
        f"http://127.0.0.1:{ep['port']}{path}", method=method,
        data=None if body is None else json.dumps(body).encode("utf-8"),
        headers={"Authorization": f"Bearer {ep['token']}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        if e.code == 401:
            raise StaleEndpoint() from e
        return json.loads(e.read().decode("utf-8"))


_endpoint = None


def endpoint():
    """The endpoint of a game that answers right now (cached until it stops answering)."""
    global _endpoint
    if _endpoint is not None:
        return _endpoint
    for f in candidate_files():
        try:
            with open(f, encoding="utf-8") as fh:
                ep = json.load(fh)
            if http(ep, "GET", "/health", timeout=2).get("ok"):
                _endpoint = ep
                return ep
        except (OSError, ValueError, urllib.error.URLError, TimeoutError, StaleEndpoint):
            continue
    raise GameDown("RimWorld is not running with the RimMcp mod active (no endpoint answered).")


def call_game(tool, args):
    global _endpoint
    for attempt in (1, 2):
        ep = endpoint()
        try:
            return http(ep, "POST", "/call", {"tool": tool, "args": args})
        except (urllib.error.URLError, TimeoutError, ConnectionError, StaleEndpoint):
            _endpoint = None          # game restarted on another token/port, or closed
            if attempt == 2:
                raise GameDown("RimWorld stopped answering.")


def run_tool(name, args):
    if name not in TOOL_NAMES:
        return f"unknown tool {name}", True
    try:
        reply = call_game(name, args or {})
    except GameDown as e:
        if name == "status":
            return json.dumps({"running": False, "detail": str(e)}), False
        return str(e), True
    if not reply.get("ok"):
        return reply.get("error", "unknown error"), True
    result = reply.get("result")
    if name == "status":
        result = {"running": True, **(result or {})}
    return json.dumps(result, ensure_ascii=False, indent=1), False


def handle(msg):
    method, mid = msg.get("method"), msg.get("id")
    if mid is None:                      # notification: nothing to answer
        return None
    if method == "initialize":
        asked = (msg.get("params") or {}).get("protocolVersion")
        result = {"protocolVersion": asked if asked in PROTOCOLS else PROTOCOLS[0],
                  "capabilities": {"tools": {"listChanged": False}},
                  "serverInfo": SERVER_INFO, "instructions": INSTRUCTIONS}
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = {"tools": [{"name": t["name"], "description": t["description"],
                             "inputSchema": t["inputSchema"],
                             "annotations": {"readOnlyHint": t["read"], "destructiveHint": False,
                                             "openWorldHint": False}} for t in TOOLS]}
    elif method == "tools/call":
        p = msg.get("params") or {}
        text, is_error = run_tool(p.get("name"), p.get("arguments"))
        result = {"content": [{"type": "text", "text": text}], "isError": is_error}
    else:
        return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": f"method not found: {method}"}}
    return {"jsonrpc": "2.0", "id": mid, "result": result}


def main():
    stdin = open(sys.stdin.fileno(), encoding="utf-8", closefd=False)
    stdout = open(sys.stdout.fileno(), "w", encoding="utf-8", closefd=False, newline="\n")
    for line in stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            reply = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "parse error"}}
        else:
            try:
                reply = handle(msg)
            except Exception as e:       # never die on one bad request
                reply = {"jsonrpc": "2.0", "id": msg.get("id"), "error": {"code": -32603, "message": str(e)}}
        if reply is not None:
            stdout.write(json.dumps(reply, ensure_ascii=False) + "\n")
            stdout.flush()


if __name__ == "__main__":
    main()
