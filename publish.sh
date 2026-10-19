#!/usr/bin/env bash
# Публикация DemReview: self-contained single-file сборки для win-x64 и linux-x64.
# Использование:  ./publish.sh             # обе платформы
#                 ./publish.sh linux-x64   # только Linux
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$root/src/DemReview.Desktop/DemReview.Desktop.csproj"
rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(win-x64 linux-x64)

for rid in "${rids[@]}"; do
    out="$root/artifacts/$rid"
    echo "==> Публикация $rid в $out"
    rm -rf "$out"

    dotnet publish "$project" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=none \
        -o "$out"

    [ "$rid" = "linux-x64" ] && chmod +x "$out/DemReview"
    echo "==> Готово: $out"
done
