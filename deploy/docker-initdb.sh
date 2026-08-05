#!/bin/bash
# ============================================================================
# 本地开发库初始化（仅 docker-compose 首次建库时由 postgres 官方 entrypoint 调用）
#
# 为什么不再把每个 .sql 逐个 mount 进 /docker-entrypoint-initdb.d：
# 那份清单会漂移。实测 docker-compose.yml 只挂到 0021，而 deploy/sql/ 已经有到 0024
# （0022 唯一索引、0023 状态回填、0024 告警原因改 text）——新机器 docker compose up
# 建出来的开发库比 CI 和生产少 3 个迁移，而且不会有任何报错提示，只会在跑到相关功能时
# 表现为莫名其妙的失败。改成按通配符执行后，新增迁移自动生效，不需要再改 compose。
#
# 生产（Windows 工控机）不走这里：只按序执行 deploy/sql/0*.sql，不含 dev 种子。
# ============================================================================
set -euo pipefail

run_sql() {
    echo "[initdb] $1 -> $(basename "$2")"
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -f "$2"
}

# 0) 迁移账本：与生产升级脚本（deploy/windows/apply-migrations.ps1）同一张表。
#    开发库虽然每次重建都全量跑，但记了账的库随时可以用升级脚本增量追平，语义一致。
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -q -c \
    "CREATE TABLE IF NOT EXISTS schema_migrations (filename text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"

# 1) schema 迁移：deploy/sql/0*.sql，按文件名序号顺序（与 CI、测试夹具、生产同一套脚本）
for f in /sql/0*.sql; do
    run_sql "迁移" "$f"
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -q -c \
        "INSERT INTO schema_migrations(filename) VALUES ('$(basename "$f")') ON CONFLICT DO NOTHING"
done

# 2) dev 种子：仅本地开发。生产部署不挂载本目录。
for f in /sql/dev/0*.sql; do
    [ -e "$f" ] || continue
    run_sql "dev 种子" "$f"
done

echo "[initdb] 完成"
