#!/usr/bin/env bash
# ============================================================================
# 螺栓预紧力 — 正式载荷标定助手
# ----------------------------------------------------------------------------
# 把现场逐点 (已知载荷 F[N], 温补后声时差 Δt[ns]) 喂进系统，跑通：
#   登录 → (可选)重标定基线 → 提交载荷标定(load-calibrate) → 异人会签(countersign)
# 详见 docs/calibration-field-guide.md。所有写操作要求 reviewer+ 账号。
#
# 依赖：curl, jq（均为常见预装；本仓库 macOS/CI 环境已具备）。
#
# 用法：
#   export BASE=http://127.0.0.1:5080          # 默认值，可省
#
#   # 1) 登录拿 token
#   TOKEN=$(./calibrate-load.sh login --user admin --pass admin123)
#
#   # 2) (可选) 螺栓置零载后重设基线 t0（用物理通道号 channel_no）
#   ./calibrate-load.sh recalibrate --token "$TOKEN" --device 1 --channel-no 14
#
#   # 3) 提交正式载荷标定（用 channels.id；points 为 "F:Δt,F:Δt,..."，≥4 点）
#   ./calibrate-load.sh load --token "$TOKEN" --channel-id 22 \
#        --reftime 32600 --reftemp 29.9 \
#        --points "20000:6.1,40000:12.3,60000:18.4,80000:24.6" \
#        --loading-device "拉伸机XX" --cert "证书号YY"
#   # → 打印 calibrationId（reviewState=pending）
#
#   # 4) 另一名 reviewer 会签激活（提交人不能自签）
#   TOKEN2=$(./calibrate-load.sh login --user reviewer2 --pass ******)
#   ./calibrate-load.sh countersign --token "$TOKEN2" --channel-id 22 --calib-id <GUID>
# ============================================================================
set -euo pipefail

BASE="${BASE:-http://127.0.0.1:5080}"

die() { echo "错误: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "缺少依赖: $1"; }
need curl; need jq

cmd="${1:-}"; shift || true

# 通用 flag 解析（bash 3.2 兼容：存到 ARG_<key> 变量，连字符转下划线）
while [[ $# -gt 0 ]]; do
  case "$1" in
    --*) _k="ARG_${1#--}"; _k="${_k//-/_}"; printf -v "$_k" '%s' "${2:-}"; shift 2;;
    *) die "无法识别的参数: $1";;
  esac
done

arg() { local _n="ARG_${1//-/_}"; printf '%s' "${!_n:-}"; }
require_arg() { local _n="ARG_${1//-/_}"; [[ -n "${!_n:-}" ]] || die "缺少 --$1"; }

api() {
  # api METHOD PATH [JSON_BODY]; 用 --token 注入 Authorization
  local method="$1" path="$2" body="${3:-}"
  local -a curl_args=(-sS -X "$method" "$BASE$path")
  local tok; tok="$(arg token)"
  [[ -n "$tok" ]] && curl_args+=(-H "Authorization: Bearer $tok")
  if [[ -n "$body" ]]; then curl_args+=(-H 'Content-Type: application/json' -d "$body"); fi
  curl "${curl_args[@]}"
}

case "$cmd" in
  login)
    require_arg user; require_arg pass
    body=$(jq -nc --arg u "$(arg user)" --arg p "$(arg pass)" '{username:$u,password:$p}')
    resp=$(curl -sS -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' -d "$body")
    tok=$(echo "$resp" | jq -r '.accessToken // empty')
    [[ -n "$tok" ]] || die "登录失败: $resp"
    echo "$tok"
    ;;

  recalibrate)
    require_arg token; require_arg device; require_arg channel-no
    # 不带 reftime/reftemp = 用设备当前实测 echo/temp 作基线（推荐）；带则显式覆盖
    body=$(jq -nc --arg rt "$(arg reftime)" --arg rp "$(arg reftemp)" \
      '{} + (if $rt != "" then {refTimeNs:($rt|tonumber)} else {} end)
          + (if $rp != "" then {refTempC:($rp|tonumber)} else {} end)')
    api POST "/api/devices/$(arg device)/channels/$(arg channel-no)/recalibrate" "$body" | jq .
    ;;

  load)
    require_arg token; require_arg channel-id; require_arg reftime; require_arg reftemp; require_arg points
    # points "F:dt,F:dt,..." → [{forceN,deltaTofNs}]
    points_json=$(echo "$(arg points)" | jq -Rc 'split(",") | map(split(":") | {forceN:(.[0]|tonumber), deltaTofNs:(.[1]|tonumber)})')
    n=$(echo "$points_json" | jq 'length')
    [[ "$n" -ge 4 ]] || die "正式载荷标定至少需要 4 个点，当前 $n"
    body=$(jq -nc \
      --argjson pts "$points_json" \
      --argjson rt "$(arg reftime)" \
      --argjson rtemp "$(arg reftemp)" \
      --arg ld "$(arg loading-device)" \
      --arg cert "$(arg cert)" \
      --arg note "$(arg note)" \
      '{points:$pts, refTimeNs:$rt, refTempC:$rtemp}
       + (if $ld  != "" then {loadingDevice:$ld} else {} end)
       + (if $cert!= "" then {loadingDeviceCert:$cert} else {} end)
       + (if $note!= "" then {note:$note} else {} end)')
    resp=$(api POST "/api/channels/$(arg channel-id)/load-calibrate" "$body")
    echo "$resp" | jq .
    cid=$(echo "$resp" | jq -r '.calibrationId // empty')
    [[ -n "$cid" ]] && echo ">> calibrationId=$cid （reviewState=pending，需异人会签）" >&2
    ;;

  countersign)
    require_arg token; require_arg channel-id; require_arg calib-id
    api POST "/api/channels/$(arg channel-id)/calibrations/$(arg calib-id)/countersign" '' | jq .
    ;;

  *)
    cat >&2 <<'EOF'
用法: calibrate-load.sh <command> [--flags]
  login        --user --pass
  recalibrate  --token --device --channel-no [--reftime --reftemp]
  load         --token --channel-id --reftime --reftemp --points "F:dt,F:dt,F:dt,F:dt" [--loading-device --cert --note]
  countersign  --token --channel-id --calib-id
环境: BASE（默认 http://127.0.0.1:5080）
详见 docs/calibration-field-guide.md
EOF
    exit 2
    ;;
esac
