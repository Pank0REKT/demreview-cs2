Radar images / Изображения радаров

EN
This folder ships with CS2 radar images (1024x1024) for:
  de_mirage, de_inferno, de_dust2, de_nuke, de_ancient, de_anubis, de_train, de_overpass,
  de_vertigo, de_cache
The file name must be the map name in lower case: <map>.png.
To add or update a map, take game/csgo/panorama/images/overheadmaps/<map>_radar_psd.png
from the game files and rename it to <map>.png. A map also needs its coordinates in
src/DemReview.Infrastructure/Maps/maps.json (pos_x, pos_y, scale from game/csgo/resource/overviews/<map>.txt).
Without an image the radar is drawn on a grid; everything else works.
The images are Valve's game assets (see THIRD-PARTY-NOTICES.md).

RU
В папке лежат радары CS2 (1024x1024) для карт выше; имя файла — имя карты в нижнем регистре: <карта>.png.
Чтобы добавить или обновить карту, возьмите из файлов игры
game/csgo/panorama/images/overheadmaps/<карта>_radar_psd.png и переименуйте в <карта>.png.
Для новой карты нужны и её координаты в src/DemReview.Infrastructure/Maps/maps.json
(pos_x, pos_y, scale из game/csgo/resource/overviews/<карта>.txt).
Без изображения радар рисуется с сеткой, остальное работает.
Изображения — ассеты игры Valve (см. THIRD-PARTY-NOTICES.md).
