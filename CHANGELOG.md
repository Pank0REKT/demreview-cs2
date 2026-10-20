# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org).

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
