#!/usr/bin/env bash
set -euo pipefail
# BoltPreload — 本地开发一键启动 / 重启脚本 (macOS / Linux)
#
# 用法:
#   ./dev.sh            # 启动整套 (数据库 + 后端 + 前端)
#   ./dev.sh up         # 同上
#   ./dev.sh down       # 停止前后端 (保留数据库容器)
#   ./dev.sh restart    # 干净重启前后端 (先停旧实例再按固定端口拉起)
#   ./dev.sh status     # 查看三层运行状态
#   ./dev.sh logs       # 跟踪前后端日志 (Ctrl-C 退出)
#
# 固定端口: 数据库 5432 / 后端 5085 / 前端 5173 (与 web/vite.config.ts 的 proxy 对齐)
# 进程日志: logs/backend.log , logs/frontend.log (均被 .gitignore 忽略)

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="${REPO_ROOT}/deploy/docker-compose.yml"
API_DIR="${REPO_ROOT}/src/BoltPreload.Api"
WEB_DIR="${REPO_ROOT}/web"
LOG_DIR="${REPO_ROOT}/logs"

BACKEND_PORT=5085
FRONTEND_PORT=5173

mkdir -p "${LOG_DIR}"

c_red()    { printf "\033[31m%s\033[0m\n" "$1"; }
c_green()  { printf "\033[32m%s\033[0m\n" "$1"; }
c_yellow() { printf "\033[33m%s\033[0m\n" "$1"; }
c_cyan()   { printf "\033[36m%s\033[0m\n" "$1"; }

# 停掉监听指定端口的进程 (优雅 kill, 必要时强杀)
stop_port() {
    local port="$1"
    local label="$2"
    local pids
    pids="$(lsof -nP -tiTCP:"${port}" -sTCP:LISTEN 2>/dev/null || true)"
    if [[ -z "${pids}" ]]; then
        c_cyan "  ${label} 未在运行 (端口 ${port})"
        return 0
    fi
    local list
    list="$(echo "${pids}" | tr '\n' ' ')"
    c_yellow "  停止 ${label} (端口 ${port}, PID ${list})"
    kill ${pids} 2>/dev/null || true
    local n=10
    while [[ ${n} -gt 0 ]]; do
        pids="$(lsof -nP -tiTCP:"${port}" -sTCP:LISTEN 2>/dev/null || true)"
        [[ -z "${pids}" ]] && return 0
        sleep 0.5
        n=$((n - 1))
    done
    pids="$(lsof -nP -tiTCP:"${port}" -sTCP:LISTEN 2>/dev/null || true)"
    [[ -n "${pids}" ]] && kill -9 ${pids} 2>/dev/null || true
    return 0
}

# 停掉 pid 文件记录的父进程 (如 dotnet run / npm 包装进程)
stop_recorded() {
    local pidfile="$1"
    local pid
    [[ -f "${pidfile}" ]] || return 0
    pid="$(cat "${pidfile}" 2>/dev/null || true)"
    if [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null; then
        kill "${pid}" 2>/dev/null || true
        sleep 0.5
        kill -9 "${pid}" 2>/dev/null || true
    fi
    rm -f "${pidfile}"
}

ensure_dev_config() {
    local cfg="${API_DIR}/appsettings.Development.json"
    if [[ ! -f "${cfg}" ]]; then
        c_yellow "  缺少 appsettings.Development.json, 从 .example 生成"
        cp "${API_DIR}/appsettings.Development.json.example" "${cfg}"
    fi
}

start_db() {
    c_cyan "==> 数据库 PostgreSQL + TimescaleDB"
    if ! command -v docker >/dev/null 2>&1; then
        c_red "  未安装 Docker, 见 README 安装 Docker Desktop"
        exit 1
    fi
    if ! docker info >/dev/null 2>&1; then
        c_red "  Docker daemon 未运行, 请先启动 Docker Desktop"
        exit 1
    fi
    docker compose -f "${COMPOSE_FILE}" up -d postgres >/dev/null
    c_cyan "  等待数据库就绪 ..."
    local n=30
    while [[ ${n} -gt 0 ]]; do
        if docker compose -f "${COMPOSE_FILE}" exec -T postgres pg_isready -U boltpreload -d boltpreload >/dev/null 2>&1; then
            c_green "  数据库就绪 (5432)"
            return 0
        fi
        sleep 1
        n=$((n - 1))
    done
    c_red "  数据库就绪超时, 看日志: docker compose -f \"${COMPOSE_FILE}\" logs postgres"
    exit 1
}

start_backend() {
    c_cyan "==> 后端 API (端口 ${BACKEND_PORT})"
    ensure_dev_config
    stop_port "${BACKEND_PORT}" "旧后端"
    cd "${API_DIR}"
    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:${BACKEND_PORT}" \
        nohup dotnet run --no-launch-profile > "${LOG_DIR}/backend.log" 2>&1 &
    echo $! > "${LOG_DIR}/backend.pid"
    cd "${REPO_ROOT}"
    c_cyan "  编译并启动中 (首次较慢), 等待 /api/health ..."
    local n=120
    while [[ ${n} -gt 0 ]]; do
        if curl -s -m 2 "http://localhost:${BACKEND_PORT}/api/health" >/dev/null 2>&1; then
            c_green "  后端就绪 http://localhost:${BACKEND_PORT} (Swagger: /swagger)"
            return 0
        fi
        if ! kill -0 "$(cat "${LOG_DIR}/backend.pid" 2>/dev/null)" 2>/dev/null; then
            c_red "  后端进程已退出, 末尾日志:"
            tail -n 30 "${LOG_DIR}/backend.log" 2>/dev/null || true
            exit 1
        fi
        sleep 1
        n=$((n - 1))
    done
    c_red "  后端启动超时, 看日志: ${LOG_DIR}/backend.log"
    exit 1
}

start_frontend() {
    c_cyan "==> 前端 Vite (端口 ${FRONTEND_PORT})"
    stop_port "${FRONTEND_PORT}" "旧前端"
    if [[ ! -d "${WEB_DIR}/node_modules" ]]; then
        c_yellow "  缺少 node_modules, 执行 npm install"
        ( cd "${WEB_DIR}" && npm install )
    fi
    cd "${WEB_DIR}"
    nohup npm run dev > "${LOG_DIR}/frontend.log" 2>&1 &
    echo $! > "${LOG_DIR}/frontend.pid"
    cd "${REPO_ROOT}"
    local n=30
    while [[ ${n} -gt 0 ]]; do
        if curl -s -o /dev/null -m 2 "http://localhost:${FRONTEND_PORT}/" 2>/dev/null; then
            c_green "  前端就绪 http://localhost:${FRONTEND_PORT}"
            return 0
        fi
        sleep 1
        n=$((n - 1))
    done
    c_yellow "  前端未在 ${FRONTEND_PORT} 响应 (可能端口被占导致漂移), 看日志: ${LOG_DIR}/frontend.log"
}

stop_all() {
    c_cyan "==> 停止前后端"
    stop_port "${BACKEND_PORT}" "后端"
    stop_recorded "${LOG_DIR}/backend.pid"
    stop_port "${FRONTEND_PORT}" "前端"
    stop_recorded "${LOG_DIR}/frontend.pid"
    c_green "  已停止 (数据库容器保留; 如需停库: docker compose -f \"${COMPOSE_FILE}\" down)"
}

status() {
    c_cyan "==> 运行状态"
    if docker compose -f "${COMPOSE_FILE}" exec -T postgres pg_isready -U boltpreload -d boltpreload >/dev/null 2>&1; then
        c_green "  数据库   : 运行中 (5432)"
    else
        c_red   "  数据库   : 未运行"
    fi
    if curl -s -m 2 "http://localhost:${BACKEND_PORT}/api/health" >/dev/null 2>&1; then
        c_green "  后端 API : 运行中  http://localhost:${BACKEND_PORT}"
    else
        c_red   "  后端 API : 未运行 (端口 ${BACKEND_PORT})"
    fi
    if curl -s -o /dev/null -m 2 "http://localhost:${FRONTEND_PORT}/" 2>/dev/null; then
        c_green "  前端     : 运行中  http://localhost:${FRONTEND_PORT}"
    else
        c_red   "  前端     : 未运行 (端口 ${FRONTEND_PORT})"
    fi
}

up() {
    start_db
    start_backend
    start_frontend
    echo
    c_green "==> 全部就绪"
    echo    "  前端  http://localhost:${FRONTEND_PORT}   (登录 admin / admin123)"
    echo    "  后端  http://localhost:${BACKEND_PORT}/swagger"
    echo    "  日志  ./dev.sh logs   |   停止  ./dev.sh down"
}

cmd="${1:-up}"
case "${cmd}" in
    up|"")   up ;;
    down)    stop_all ;;
    restart) stop_all; echo; up ;;
    status)  status ;;
    logs)    tail -n 50 -f "${LOG_DIR}/backend.log" "${LOG_DIR}/frontend.log" ;;
    *)       echo "用法: ./dev.sh [up|down|restart|status|logs]"; exit 1 ;;
esac
