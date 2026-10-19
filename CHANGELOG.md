# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org).

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
