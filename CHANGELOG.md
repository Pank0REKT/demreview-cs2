# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org).

## [Unreleased]

### Features
- New interface: a dark theme with a section bar (*Radar*, *Stats*, *Profile*, *Settings*) and a player panel with
  both teams, K / D / A, the kill-death difference and a nickname search. The top menu is gone; the hotkeys stay.
- Radar: filters above the map, +/− and reset-view buttons, a legend, a timeline with a play button and speed picker,
  round cards with the first zones of the route.
- Statistics: summary tiles (rounds, wins, damage per round, first contact — overall and per side), side and buy
  switches on the page, buy and grenade badges in the table.
- Player profile: a predictability ring, role cards with key metrics, a T vs CT comparison, category filter for patterns.
- Settings: analysis thresholds can be edited in the app (no need to edit `settings.json`), cache fill level.
- Library and the profile across many demos: add a folder with demos (subfolders included) and DemReview parses each
  demo once and keeps the analysis in a separate library (`library.db`, no 50-demo limit, not cleared with the cache).
  Pick a player and a map, tick the demos — the combined profile (patterns, roles, radar, heatmaps) is built from the
  library in seconds, even if the .dem files are gone. *Rescan* adds new matches from the folders.
- Map filter: the combined profile is built per map; the map list shows how many demos the player has on each.
- My profile: every opened demo where the main player plays is saved to the library automatically (can be switched
  off), so your own profile grows with each match.
- Pistol rounds are detected at the start of every demo in a combined profile.

## [0.1.2]

### Features
- Main player: pick yourself in the first opened demo (or enter a SteamID64, profile link, [U:1:…] or STEAM_0:…
  in *Settings → Main player*). You are then selected automatically and marked *You* in the player list.
  The SteamID is stored only locally.
- Radars for 13 more maps: cs_office, cs_italy, cs_shelter, de_dust, de_ancient_night, de_boulder, de_debris,
  de_eldorado, de_fachwerk, de_poseidon, ar_baggage, ar_shoots, ar_shoots_night.
- de_train: the lower level is detected by height, like on Nuke and Vertigo.

## [0.1.1]

### Features
- Radar images and coordinates for de_vertigo (two levels) and de_cache.
- Anonymous usage statistics via Aptabase: app start and demo load (map, cache or parse, parse time).
  No file names, nicknames or persistent user ID. Can be turned off in *Settings → Usage statistics*.

## [0.1.0] — first public release

### Features
- Open CS2 demos (`.dem`): file dialog, Ctrl+O, drag and drop, command-line argument ("Open with").
  Background parsing with progress and cancellation.
- Radar with map background: routes, grenades (throw → detonation, smoke and fire areas), deaths,
  heatmaps of positions or deaths; zoom and pan.
- Round timeline with playback: all players with view direction, nickname on hover, game-style kill feed,
  damage pop-ups, grenade damage, moment context (players alive, money, buys, bomb, score).
- Filters by round, side, team buy and grenade type.
- Statistics: per-round situations, "conditions → actions" frequency tables, match rounds with buys.
- Player profile: role per side with reasoning, stable behavior patterns with frequency and confidence
  (Wilson interval), side summary with ADR, predictability index; example rounds on the radar.
- Profile export to Markdown and JSON.
- Parsed demo cache in SQLite.
- Russian and English interface (chosen on first start, switchable in Settings).
- Settings tab: language, cache size and clearing, data and map folders, update check.
- Optional check for new releases on GitHub.
