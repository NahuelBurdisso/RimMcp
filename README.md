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
| `dialogs` / `choose` | open dialogs, letters and quest offers; answer them (ritual and bestowing-ceremony start windows: roles, quality, begin/cancel) |
| `gizmos` | a thing's command buttons as the player sees them (no dev gizmos); click one, with a map target if it needs one |
| `trade` | the open trade screen: list, set counts, execute, cancel |
| `governor` | in-game combat reflexes + event stream (see below): status, enable, disable, reload, set_stations, events |

Every action uses the game's own checks (designators, `GenConstruct`, research gates) and
returns an error saying why instead of failing silently. The MCP server also works as a CLI:
`python Mcp/rimmcp_server.py call <tool> '{"arg": 1}'`.

## Governor (in-game reflexes)

Claude acts with 10-20 s between tool calls; mad animals and raids do not wait. The governor
(`Source/RimMcp/Governor/`, a `GameComponent`) runs inside the game, only when the policy
enables it, and does only what a player could: draft, move, ordered jobs, forbid flags.

- **Threat** = visible hostile pawns that are an active threat to a home map (raiders,
  manhunters, mechanoids; not dormant, not your own berserkers or prisoners, not factions in
  `ignoreFactions`). Checked every 60 ticks; the heavy logic runs every 120 ticks while needed.
- **Fighters** (player-controlled, violent, healthy) are drafted and sent to policy `stations`
  or, without stations, to automatic positions near home facing the enemy with the best vanilla
  cover (`CoverUtility`), one cell apart. Melee fighters hold behind the shooters and engage
  enemies that get within `engageMeleeRadius` of one. Positions are re-planned when the enemy
  moves a lot or a station is flanked, keeping good stations (hysteresis).
- **Retreat** below `retreatHealthPct` or above `retreatBleedRate`: undraft, go to bed.
  **Shelter**: non-combatants outdoors go to a roofed room. **Rescue** downed colonists when no
  enemy is within 10 cells. **Overwhelming** enemy (`defendIndoorsIfRatioAbove`): indoor
  positions and a `decision_needed` event.
- **End** after `undraftAfterQuietTicks` without a threat: undraft the pawns it drafted,
  re-equip dropped weapons, unforbid loot near base, capture downed enemies (free prisoner bed).
- A colonist Claude drafts or orders (draft, drafted_move, orders, gizmos) or the player
  undrafts during a fight is never touched again until the fight ends.
- Policy `<save data folder>/RimMcp/governor.json` (created with defaults, disabled; re-read
  when it changes):

```json
{"enabled": false,
 "combat": {"autoDraft": true, "retreatHealthPct": 0.5, "retreatBleedRate": 0.5,
            "stations": {"home": [[120, 98], [122, 98]]}, "homeCenter": null,
            "engageMeleeRadius": 5, "undraftAfterQuietTicks": 600,
            "defendIndoorsIfRatioAbove": 2.5, "ignoreFactions": []},
 "rescue": {"downedColonists": true, "captureDowned": true},
 "nonCombatants": {"shelter": true},
 "events": {"maxFileKb": 512}}
```

- Events `<save data folder>/RimMcp/events.jsonl` (rotated to `events.1.jsonl`), one JSON
  object per line with `seq`, tick, date, hour, map, `type`: governor_active, threat_start,
  governor_deploy, retreat, rescued, captured, player_override, decision_needed, threat_end,
  colonist_downed, colonist_died, mental_break, letter_received, quest_offer, alert_new,
  alert_cleared, fire_in_home, fire_out, governor_error. Read them with
  `governor {action: "events", since: <next>}`.
- Test harness: `mods/RimMcpGovTest` (quicktest scenarios: raid, manhunters, retreat + rescue,
  capture, manual-draft respect).

## Use

1. Enable the mod in the game's mod list.
2. Register the server, e.g. a project `.mcp.json`:

```json
{"mcpServers": {"rimworld": {"command": "python", "args": ["<mod folder>/Mcp/rimmcp_server.py"]}}}
```

3. Start a game and ask Claude about your colony.

## Build

`rw build RimMcp` from the D:/rimworld workspace (net472, references the installed game).
