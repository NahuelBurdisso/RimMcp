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
import time
import urllib.error
import urllib.request

SERVER_INFO = {"name": "rimworld", "version": "0.2.0"}
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
         "priority": {"type": "integer", "minimum": 0, "maximum": 4},
         "manual": {"type": "boolean", "description": "true turns on manual priorities (1-4) for the whole colony, like the Work tab checkbox"}},
         "required": ["name", "work", "priority"]}},
    {"name": "message", "read": False,
     "description": "Show a short message on the player's screen, prefixed with [Claude].",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}},
    {"name": "focus", "read": False,
     "description": "Move the camera to a colonist and select them.",
     "inputSchema": {"type": "object", "properties": {"name": NAME}, "required": ["name"]}},
]
RECT = {"x": {"type": "integer"}, "z": {"type": "integer"},
        "x2": {"type": "integer", "description": "opposite corner (inclusive); or use w/h"},
        "z2": {"type": "integer"}, "w": {"type": "integer"}, "h": {"type": "integer"}}
NAMES = {"names": {"type": "array", "items": {"type": "string"}, "description": "colonist names; omit or ['all'] for everyone"}}


def obj(props, required=()):
    return {"type": "object", "properties": props, "required": list(required)}


TOOLS += [
    {"name": "map_view", "read": True,
     "description": "The current map as a text grid (north up) with a legend, plus a list of buildings, items and "
                    "pawns with coordinates. No x/z = whole map (use step 4 or 5 for an overview). layers: main "
                    "(things, zones, terrain), roof, fertility. Unexplored cells show as '?'.",
     "inputSchema": obj({**RECT, "step": {"type": "integer", "default": 1},
                         "layers": {"type": "array", "items": {"type": "string", "enum": ["main", "roof", "fertility"]}},
                         "objects": {"type": "boolean"}, "maxObjects": {"type": "integer"}})},
    {"name": "things", "read": True,
     "description": "Find things on the map: by query (defName or label part), kind (item, building, plant, pawn, "
                    "animal, hostile, blueprint) and optional rectangle. Gives id, position, forbidden/stored, growth.",
     "inputSchema": obj({"query": {"type": "string"}, "kind": {"type": "string"}, "limit": {"type": "integer"}, **RECT})},
    {"name": "designate", "read": False,
     "description": "Apply a designation to every valid cell of a rectangle, exactly like dragging the tool in game: "
                    "mine, cut, chop, harvest, hunt, tame, slaughter, deconstruct, uninstall, cancel, haul, smooth, "
                    "remove_floor, claim, strip, forbid, unforbid, roof_build, roof_remove, home_add, home_remove.",
     "inputSchema": obj({"type": {"type": "string"}, **RECT}, ["type", "x", "z"])},
    {"name": "buildable", "read": True,
     "description": "What can be built now (research and tech gates applied). No args: categories with counts. "
                    "category (Structure, Furniture, Production, Power, Security, Joy, Temperature, Misc, Floors, "
                    "Ship, Biotech...) or query: defs with cost, size, stuff options in stock, rotation.",
     "inputSchema": obj({"category": {"type": "string"}, "query": {"type": "string"}})},
    {"name": "build", "read": False,
     "description": "Place blueprints of a buildable def (ThingDef or floor TerrainDef). 1x1 defs fill the rectangle "
                    "(outline=true: only its border, for walls of a room); multi-cell defs place one at x,z (center). "
                    "rot: north/east/south/west. stuff: material defName (WoodLog, Steel, BlocksGranite...).",
     "inputSchema": obj({"def": {"type": "string"}, **RECT, "rot": {"type": "string"}, "stuff": {"type": "string"},
                         "outline": {"type": "boolean"}}, ["def", "x", "z"])},
    {"name": "zones", "read": True,
     "description": "All zones (growing with plant, stockpiles with priority, bounds) and areas (home, roof, allowed).",
     "inputSchema": obj({})},
    {"name": "zone", "read": False,
     "description": "Zones and allowed areas. action: grow (plant defName, default Plant_Potato) | stockpile | dumping "
                    "(optional only/allow/disallow lists of ThingCategoryDef or ThingDef, priority) | set_plant (zone, plant) "
                    "| set_filter (zone, only/allow/disallow, priority) | delete (zone) | remove_cells (rect) | area_new "
                    "(label, rect) | area_add / area_remove (area, rect) | area_delete (area). Home area and roofs: designate.",
     "inputSchema": obj({"action": {"type": "string"}, **RECT, "zone": {"type": "string"}, "plant": {"type": "string"},
                         "label": {"type": "string"}, "area": {"type": "string"},
                         "only": {"type": "array", "items": {"type": "string"}},
                         "allow": {"type": "array", "items": {"type": "string"}},
                         "disallow": {"type": "array", "items": {"type": "string"}},
                         "priority": {"type": "string", "enum": ["Low", "Normal", "Preferred", "Important", "Critical"]}},
                        ["action"])},
    {"name": "research", "read": False,
     "description": "No args: current project and every project that can start now (cost, tech level, bench needed). "
                    "set: defName or label to research. finished=true also lists finished projects.",
     "inputSchema": obj({"set": {"type": "string"}, "finished": {"type": "boolean"}})},
    {"name": "bills", "read": False,
     "description": "Workbench bills. action list (no bench = all benches), add (recipe; target = do until you have X, "
                    "or count, or forever), remove/suspend/resume/set (index). bench = id or defName, or x,z of the bench.",
     "inputSchema": obj({"action": {"type": "string"}, "bench": {"type": "string"}, "x": {"type": "integer"},
                         "z": {"type": "integer"}, "recipe": {"type": "string"}, "target": {"type": "integer"},
                         "count": {"type": "integer"}, "forever": {"type": "boolean"}, "index": {"type": "integer"}})},
    {"name": "forbid", "read": False,
     "description": "Forbid (forbidden=true) or allow (false) things: by id, or all forbiddable things in a rectangle "
                    "(optional query filter).",
     "inputSchema": obj({"forbidden": {"type": "boolean", "default": True}, "id": {"type": "string"},
                         "query": {"type": "string"}, **RECT})},
    {"name": "allow_area", "read": False,
     "description": "Restrict colonists to an allowed area (label from zones) or 'none' to unrestrict.",
     "inputSchema": obj({**NAMES, "area": {"type": "string"}}, ["area"])},
    {"name": "schedule", "read": False,
     "description": "Read or set daily schedules. schedule = 24 chars for hours 0..23: S sleep, W work, J joy, "
                    "A anything, M meditate. E.g. SSSSSSAWWWWWWWWWWWWJJJSS.",
     "inputSchema": obj({**NAMES, "schedule": {"type": "string"}})},
    {"name": "pawn_settings", "read": False,
     "description": "Read or set per-colonist settings: hostility (Ignore/Attack/Flee), medicine (NoCare, NoMeds, "
                    "HerbalOrWorse, NormalOrWorse, Best), selfTend. Also shows area and schedule.",
     "inputSchema": obj({**NAMES, "hostility": {"type": "string"}, "medicine": {"type": "string"}, "selfTend": {"type": "boolean"}})},
    {"name": "drafted_move", "read": False,
     "description": "Draft colonists (if needed) and order them to a cell; several colonists spread over nearby cells. "
                    "Drafted colonists with ranged weapons shoot enemies in range by themselves.",
     "inputSchema": obj({**NAMES, "x": {"type": "integer"}, "z": {"type": "integer"}}, ["x", "z"])},
    {"name": "orders", "read": False,
     "description": "The right-click menu of one colonist at a cell: without choose, lists the options (attack, rescue, "
                    "tend, equip, prioritize building/hauling, trade with, arrest...); choose = index or text part.",
     "inputSchema": obj({"name": NAME, "x": {"type": "integer"}, "z": {"type": "integer"}, "choose": {"type": "string"}},
                        ["name", "x", "z"])},
    {"name": "run", "read": False,
     "description": "Unpause for some in-game hours at a speed, then pause again. Stops early on threat/death/negative "
                    "letters, choice letters or dialogs. Waits up to wait_seconds (real time) and returns the status, "
                    "why it stopped, new alerts and open decisions.",
     "inputSchema": obj({"hours": {"type": "number", "default": 2}, "speed": {"type": "string", "enum": ["normal", "fast", "superfast"]},
                         "wait_seconds": {"type": "number", "default": 60}})},
    {"name": "save", "read": False,
     "description": "Save the game (a normal save file, like the save menu). name optional.",
     "inputSchema": obj({"name": {"type": "string"}})},
    {"name": "dialogs", "read": True,
     "description": "Everything waiting for a decision: open dialogs with their options, letters (with options when "
                    "they are choice letters), quest offers with rewards and expiry, ongoing quests.",
     "inputSchema": obj({})},
    {"name": "choose", "read": False,
     "description": "Answer a decision from dialogs: window + option (index or text; message boxes A/B/C; 'close'), "
                    "letter + option (index/text, 'open', 'dismiss'), or quest + option accept/dismiss (+ reward index, accepter).",
     "inputSchema": obj({"window": {"type": "integer"}, "letter": {"type": "integer"}, "quest": {"type": "integer"},
                         "option": {"type": "string"}, "reward": {"type": "integer"}, "accepter": {"type": "string"}})},
    {"name": "trade", "read": False,
     "description": "The open trade screen. action list (query filter), set (index, count: >0 buy, <0 sell), reset, "
                    "execute, cancel.",
     "inputSchema": obj({"action": {"type": "string"}, "index": {"type": "integer"}, "count": {"type": "integer"},
                         "query": {"type": "string"}})},
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


def run_and_wait(args):
    """run: unpause in the game, then poll until it pauses again (or wait_seconds pass)."""
    wait = float(args.pop("wait_seconds", 60) or 60)
    reply = call_game("run", args)
    if not reply.get("ok"):
        return reply
    t0 = time.time()
    status = None
    while time.time() - t0 < wait:
        time.sleep(1.0)
        st = call_game("status", {})
        if st.get("ok") and st["result"].get("paused"):
            status = st["result"]
            break
    stopped = None
    if status is None:
        call_game("set_speed", {"speed": "pause"})
        status = call_game("status", {}).get("result")
        stopped = f"still running after {wait:.0f}s real time; paused it"
    dialogs = call_game("dialogs", {}).get("result") or {}
    alerts = call_game("alerts", {}).get("result") or {}
    return {"ok": True, "result": {
        "stoppedBecause": stopped or dialogs.get("lastAutoPause") or "paused",
        "status": status,
        "alerts": [f"{a['alert']} ({a['priority']})" for a in alerts.get("alerts", [])],
        "messages": alerts.get("messages", []),
        "decisions": {k: dialogs.get(k) for k in ("windows", "questOffers") if dialogs.get(k)},
        "letters": [f"{l['id']}: {l['label']} [{l['type']}]" for l in dialogs.get("letters", [])],
    }}


def run_tool(name, args):
    if name not in TOOL_NAMES:
        return f"unknown tool {name}", True
    try:
        reply = run_and_wait(dict(args or {})) if name == "run" else call_game(name, args or {})
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


def cli(argv):
    """python rimmcp_server.py call <tool> ['{json args}']: one call, prints the result."""
    tool = argv[0]
    args = json.loads(argv[1]) if len(argv) > 1 else {}
    text, is_error = run_tool(tool, args)
    sys.stdout.reconfigure(encoding="utf-8")
    print(("ERROR: " if is_error else "") + text)
    return 1 if is_error else 0


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "call":
        sys.exit(cli(sys.argv[2:]))
    main()
