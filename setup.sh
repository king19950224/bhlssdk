#!/usr/bin/env bash
set -euo pipefail
# BoltPreload — 本地开发环境一键准备脚本（macOS / Linux）
#
# 前置条件：
#   - .NET 8 SDK 已装  (检查 `dotnet --version` 应输出 8.0.xxx)
#   - Docker Desktop 已启动 (检查 `docker --version`)
#
# 用法：
#   bash setup.sh           # 完整流程（restore + 数据库 + build + test）
#   bash setup.sh up        # 只启动数据库
#   bash setup.sh down      # 只停数据库
#   bash setup.sh reset     # 重置数据库（清空所有数据）
#   bash setup.sh test      # 只跑测试

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$REPO_ROOT/deploy/docker-compose.yml"

color_red() { printf "\033[31m%s\033[0m\n" "$1"; }
color_green() { printf "\033[32m%s\033[0m\n" "$1"; }
color_yellow() { printf "\033[33m%s\033[0m\n" "$1"; }
color_cyan() { printf "\033[36m%s\033[0m\n" "$1"; }

check_prerequisites() {
    color_cyan "==> 检查前置工具"
    local missing=0

    if ! command -v dotnet >/dev/null 2>&1; then
        color_red "  缺少：.NET SDK"
        color_yellow "  装：https://dotnet.microsoft.com/en-us/download/dotnet/8.0"
        missing=1
    else
        local ver
        ver=$(dotnet --version)
        if [[ ! "$ver" =~ ^8\. ]]; then
            color_yellow "  .NET 版本: $ver（需要 8.x，可能仍能工作）"
        else
            color_green "  .NET SDK: $ver"
        fi
    fi

    if ! command -v docker >/dev/null 2>&1; then
        color_red "  缺少：Docker"
        color_yellow "  装：https://www.docker.com/products/docker-desktop/"
        missing=1
    else
        if ! docker info >/dev/null 2>&1; then
            color_red "  Docker 已装但 daemon 未运行，启动 Docker Desktop 后再来"
            missing=1
        else
            color_green "  Docker: $(docker --version)"
        fi
    fi

    if [[ $missing -ne 0 ]]; then
        color_red "==> 前置工具不齐，退出"
        exit 1
    fi
}

start_database() {
    color_cyan "==> 启动 PostgreSQL + TimescaleDB（Docker）"
    docker compose -f "$COMPOSE_FILE" up -d postgres
    color_cyan "==> 等待数据库就绪..."
    local retries=30
    while [[ $retries -gt 0 ]]; do
        if docker compose -f "$COMPOSE_FILE" exec -T postgres pg_isready -U boltpreload -d boltpreload >/dev/null 2>&1; then
            color_green "  数据库就绪"
            break
        fi
        ((retries--))
        sleep 1
    done
    if [[ $retries -eq 0 ]]; then
        color_red "  数据库就绪超时，查看日志：docker compose -f $COMPOSE_FILE logs postgres"
        exit 1
    fi
}

stop_database() {
    color_cyan "==> 停止数据库"
    docker compose -f "$COMPOSE_FILE" down
}

reset_database() {
    color_yellow "==> 重置数据库（删除所有数据）"
    read -r -p "确认？(y/N) " yn
    if [[ "$yn" != "y" ]]; then
        echo "取消"
        exit 0
    fi
    docker compose -f "$COMPOSE_FILE" down -v
    rm -rf "$REPO_ROOT/.docker-data"
    color_green "  已清空"
}

dotnet_restore_build() {
    color_cyan "==> 还原 NuGet 依赖"
    cd "$REPO_ROOT"
    dotnet restore BoltPreload.sln

    color_cyan "==> 编译"
    dotnet build BoltPreload.sln --no-restore --configuration Debug
}

dotnet_test() {
    color_cyan "==> 跑单元测试"
    cd "$REPO_ROOT"
    dotnet test BoltPreload.sln --no-build --logger "console;verbosity=normal"
}

main_full() {
    check_prerequisites
    start_database
    dotnet_restore_build
    dotnet_test

    color_green ""
    color_green "==> setup 完成"
    color_cyan "下一步："
    echo "  cd src/BoltPreload.Api"
    echo "  dotnet run"
    echo "  打开 http://localhost:5000/swagger 看 API"
    echo "  curl http://localhost:5000/api/measurements/count   # 看入库进度"
    echo "  curl http://localhost:5000/api/measurements/latest  # 看最近数据"
}

cmd="${1:-full}"
case "$cmd" in
    full) main_full ;;
    up) check_prerequisites; start_database ;;
    down) stop_database ;;
    reset) reset_database ;;
    test) dotnet_test ;;
    *) echo "用法: bash setup.sh [full|up|down|reset|test]"; exit 1 ;;
esac
