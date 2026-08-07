#!/usr/bin/env bash
# ============================================================================
# 真机联调整套栈一键起停：Postgres(docker) + API(:5085, Fx:Mode=Real) + 前端(:5173)
#
# 前提：
#   1. src/BoltPreload.Api/appsettings.Development.json 已存在（Real 模式 + 本地 PG +
#      开发免 License + dev JWT；参见 appsettings.Development.json.example）。
#   2. 真机 DEV_LAB_001 网络可达 192.168.0.7:23。注意：若开着 Clash/VPN 的 TUN，
#      192.168.0.x 可能被劫持进隧道导致连不上——用直连网卡(如 en5)或给 192.168.0.0/16 加 DIRECT。
#      自检：dotnet run --project tools/DeviceSmokeTest -- 192.168.0.7:23 10 false "1,2,7,8,11,14,17,20" single
#
# 用法： deploy/dev-real.sh {up|down|status|logs}
# ============================================================================
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN="$ROOT/.dev-real"; mkdir -p "$RUN"
API_URL="http://localhost:5085"
WEB_URL="http://localhost:5173"
COMPOSE="docker compose -f $ROOT/deploy/docker-compose.yml"

pg_health() { docker inspect -f '{{.State.Health.Status}}' boltpreload-pg 2>/dev/null || echo absent; }
http_up()   { curl -sf -o /dev/null "$1" 2>/dev/null && echo up || echo down; }

case "${1:-status}" in
  up)
    echo "[1/3] Postgres 容器 ..."
    $COMPOSE up -d
    for _ in $(seq 1 30); do [ "$(pg_health)" = healthy ] && break; sleep 2; done
    echo "      PG health=$(pg_health)"

    echo "[2/3] API ($API_URL, Fx:Mode=Real) ..."
    ( cd "$ROOT" && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$API_URL" \
        nohup dotnet run --no-launch-profile --project src/BoltPreload.Api >"$RUN/api.log" 2>&1 & echo $! >"$RUN/api.pid" )

    echo "[3/3] 前端 ($WEB_URL) ..."
    ( cd "$ROOT/web" && nohup npm run dev >"$RUN/web.log" 2>&1 & echo $! >"$RUN/web.pid" )

    echo "启动中；约 20s 后打开 $WEB_URL （admin / admin123）。日志：$RUN/{api,web}.log"
    ;;

  down)
    pkill -f "BoltPreload.Api" 2>/dev/null || true
    [ -f "$RUN/web.pid" ] && kill "$(cat "$RUN/web.pid")" 2>/dev/null || true
    pkill -f "vite" 2>/dev/null || true
    $COMPOSE stop
    rm -f "$RUN/api.pid" "$RUN/web.pid"
    echo "已停：API + 前端 + Postgres 容器（DB 数据保留在 .docker-data/）"
    ;;

  status)
    echo "PG  : $(pg_health)"
    echo "API : $(http_up "$API_URL/swagger/index.html")   ($API_URL)"
    echo "Web : $(http_up "$WEB_URL")   ($WEB_URL)"
    ;;

  logs)
    tail -n 40 -F "$RUN/api.log" "$RUN/web.log"
    ;;

  *)
    echo "用法: $0 {up|down|status|logs}"; exit 1
    ;;
esac
