# RimMcp - Claude bridge

Lets Claude Code (or any MCP client) read and play a running RimWorld 1.6 game.

```
Claude Code --stdio--> Mcp/rimmcp_server.py --HTTP 127.0.0.1--> RimMcp mod (in game)
```

- **Mod** (`Source/RimMcp`): local HTTP endpoint on `127.0.0.1:47863` (change with the
  `-rimmcp-port=<n>` command-line argument). Every request needs the random token the mod
  writes to `<save data folder>/RimMcp/endpoint.json` at startup. Requests are queued and run
  on the game's main thread.
- **MCP server** (`Mcp/rimmcp_server.py`): Python standard library only. Finds the endpoint
  file in the default save data folder plus `RIMMCP_DATA_DIRS`, reconnects when the game
  restarts, and reports "not running" instead of failing when there is no game.

## Tools

| Tool | Does |
|---|---|
| `status` | running?, colony, date, hour, speed, weather, temperature, wealth, storyteller |
| `colonists` | mood vs break threshold, mental state, health, bleeding, drafted, current job |
| `colonist` | traits, skills/passions, health conditions, mood thoughts, needs, work priorities, gear |
| `resources` | stored items, loose items, forbidden loose items, edible nutrition, approx. food days |
| `alerts` | active alerts with explanations, letters, on-screen messages |
| `threats` | hostiles by faction/kind, fires, active conditions per map |
| `set_speed` | pause / normal / fast / superfast |
| `draft` | draft or undraft some or all colonists |
| `set_work_priority` | 0-4 (does not switch the game's manual-priorities mode) |
| `message` | show a `[Claude]` message on screen |
| `focus` | jump the camera to a colonist and select them |
| `map_view` | map as a text grid (main / roof / fertility layers, `step` for overviews) + objects with coordinates |
| `things` | find things by def/label, kind and rectangle |
| `designate` | mine, cut, chop, harvest, hunt, tame, slaughter, deconstruct, uninstall, cancel, haul, smooth, remove_floor, claim, strip, forbid, unforbid, roof_build, roof_remove, home_add, home_remove over a rectangle |
| `buildable` / `build` | what can be built now; place blueprints (rotation, material, room outlines) |
| `zones` / `zone` | list zones and areas; growing zones, stockpiles with filters/priority, allowed areas |
| `research` | available projects; choose the current one |
| `bills` | list/add/remove/suspend workbench bills (count, until you have X, forever) |
| `forbid`, `allow_area`, `schedule`, `pawn_settings` | forbid/allow, area restriction, 24-hour schedules, hostility/medicine |
| `drafted_move`, `orders` | move drafted colonists; any right-click order of a colonist at a cell |
| `run` | unpause for N in-game hours, auto-pause on threats, deaths, choice letters and dialogs |
| `save` | save the game |
| `dialogs` / `choose` | open dialogs, letters and quest offers; answer them |
| `trade` | the open trade screen: list, set counts, execute, cancel |

Every action uses the game's own checks (designators, `GenConstruct`, research gates) and
returns an error saying why instead of failing silently. The MCP server also works as a CLI:
`python Mcp/rimmcp_server.py call <tool> '{"arg": 1}'`.

## Use

1. Enable the mod in the game's mod list.
2. Register the server, e.g. a project `.mcp.json`:

```json
{"mcpServers": {"rimworld": {"command": "python", "args": ["<mod folder>/Mcp/rimmcp_server.py"]}}}
```

3. Start a game and ask Claude about your colony.

## Build

`rw build RimMcp` from the D:/rimworld workspace (net472, references the installed game).
