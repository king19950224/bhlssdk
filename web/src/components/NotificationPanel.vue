<script setup lang="ts">
// 出站通知面板（Alarms 页"通知"标签）：渠道、规则、发送记录三块。
// 渠道/规则是 Admin 的事（后端 403 兜底，这里只是不给按钮）；发送记录 reviewer 可见——
// 值班复核要能看见"通知到底发出去没有"，dead 行修好渠道后可手工重发。
// 发送语义是至少一次：极端时机（发送成功与记账之间进程被杀）会重发一条，宁可重复不可丢。
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  notificationApi, alarmRuleApi,
  type NotificationChannel, type NotificationRule, type NotificationOutboxItem, type AlarmRuleTargets,
} from '../api'
import { alarmTypeLabel } from '../utils/labels'
import { useAuth } from '../stores/auth'

const auth = useAuth()
const channels = ref<NotificationChannel[]>([])
const rules = ref<NotificationRule[]>([])
const outbox = ref<NotificationOutboxItem[]>([])
const targets = ref<AlarmRuleTargets>({ groups: [], bolts: [] })
const loadError = ref<string | null>(null)
const outboxStatus = ref('')
let timer: number | undefined

const FORMAT_LABELS: Record<string, string> = { raw: '通用 JSON', wecom: '企业微信', dingtalk: '钉钉' }
const SEVERITY_LABELS: Record<string, string> = { info: '提示', warn: '预警', critical: '严重' }
const STATUS_META: Record<string, { label: string; cls: string }> = {
  pending: { label: '待发', cls: 'st-pending' },
  sent: { label: '已发', cls: 'st-sent' },
  dead: { label: '失败搁置', cls: 'st-dead' },
}
// 与后端 KnownAlarmTypes 同源的可订阅类型（no_data 是历史词条不是告警类型，不列；
// clock_drift 由 TimeSyncService 真实产生，后端白名单/词表都有，之前漏列导致无法显式订阅时钟漂移）
const TYPE_OPTIONS = ['preload_low', 'preload_high', 'preload_loss', 'signal_lost', 'wiring_suspect', 'clock_drift']

async function load() {
  try {
    if (auth.isAdmin) {
      const [ch, ru, tg] = await Promise.all([
        notificationApi.channels(), notificationApi.rules(), alarmRuleApi.targets(),
      ])
      channels.value = ch.items
      rules.value = ru.items
      targets.value = tg
    }
    outbox.value = (await notificationApi.outbox(80, outboxStatus.value || undefined)).items
    loadError.value = null
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : '通知配置加载失败'
  }
}
onMounted(() => {
  load()
  timer = window.setInterval(() => { if (!document.hidden) load() }, 15000)
})
onUnmounted(() => { if (timer) clearInterval(timer) })

function setOutboxStatus(s: string) {
  outboxStatus.value = s
  load()
}

function fmtTime(iso: string | null) {
  if (!iso) return '—'
  const d = new Date(iso)
  const p = (n: number) => String(n).padStart(2, '0')
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

// ---- 渠道编辑 ----
const chDlg = reactive({
  visible: false, saving: false, id: null as number | null, hasSecret: false,
  name: '', format: 'wecom', url: '', secret: '', minIntervalSeconds: 60, enabled: true,
  // 显式"清除已保存密钥"开关：后端支持空串=清除，但"留空=保留"的默认映射让清除操作在 UI 上无路可走，
  // secret 配错后只能替换或删渠道重建
  clearSecret: false,
})
function openChannel(c?: NotificationChannel) {
  chDlg.id = c?.id ?? null
  chDlg.name = c?.name ?? ''
  chDlg.format = c?.format ?? 'wecom'
  chDlg.url = c?.url ?? ''
  chDlg.secret = ''
  chDlg.hasSecret = c?.hasSecret ?? false
  chDlg.minIntervalSeconds = c?.minIntervalSeconds ?? 60
  chDlg.enabled = c?.enabled ?? true
  chDlg.clearSecret = false
  chDlg.visible = true
}
async function saveChannel() {
  if (!chDlg.name.trim()) return ElMessage.warning('渠道名必填')
  if (!/^https?:\/\//i.test(chDlg.url)) return ElMessage.warning('Url 必须是 http(s) 地址')
  chDlg.saving = true
  try {
    // secret 三态（后端语义：null 保留 / 空串清除 / 有值替换）：
    // 勾了"清除"发空串；否则留空=保留现有，填了=替换
    const secret = chDlg.id != null
      ? (chDlg.clearSecret ? '' : (chDlg.secret === '' ? null : chDlg.secret))
      : chDlg.secret
    const body = {
      name: chDlg.name.trim(), kind: 'webhook', url: chDlg.url.trim(), format: chDlg.format,
      secret,
      minIntervalSeconds: chDlg.minIntervalSeconds, enabled: chDlg.enabled,
    }
    if (chDlg.id == null) await notificationApi.createChannel(body)
    else await notificationApi.updateChannel(chDlg.id, body)
    ElMessage.success('已保存')
    chDlg.visible = false
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    chDlg.saving = false
  }
}
const testing = ref<Record<number, boolean>>({})
async function testChannel(c: NotificationChannel) {
  testing.value[c.id] = true
  try {
    await notificationApi.testChannel(c.id)
    ElMessage.success(`测试消息已发到 '${c.name}'，去群里看一眼`)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '测试发送失败')
  } finally {
    testing.value[c.id] = false
  }
}
async function removeChannel(c: NotificationChannel) {
  try {
    await ElMessageBox.confirm(
      `删除渠道 '${c.name}'？名下 ${c.ruleCount} 条规则一并删除。已有发送记录的渠道删不掉（历史要留），请改用停用。`,
      '删除通知渠道', { type: 'warning' })
  } catch { return }
  try {
    await notificationApi.deleteChannel(c.id)
    ElMessage.success('已删除')
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '删除失败')
  }
}

// ---- 规则编辑 ----
const ruleDlg = reactive({
  visible: false, saving: false, id: null as number | null,
  channelId: null as number | null, minSeverity: 'warn', alarmTypes: [] as string[],
  groupId: null as number | null, notifyOnClear: false,
  quietStart: null as string | null, quietEnd: null as string | null, enabled: true,
})
function openRule(r?: NotificationRule) {
  ruleDlg.id = r?.id ?? null
  ruleDlg.channelId = r?.channelId ?? (channels.value[0]?.id ?? null)
  ruleDlg.minSeverity = r?.minSeverity ?? 'warn'
  ruleDlg.alarmTypes = r?.alarmTypes ? [...r.alarmTypes] : []
  ruleDlg.groupId = r?.groupId ?? null
  ruleDlg.notifyOnClear = r?.notifyOnClear ?? false
  ruleDlg.quietStart = r?.quietStart ?? null
  ruleDlg.quietEnd = r?.quietEnd ?? null
  ruleDlg.enabled = r?.enabled ?? true
  ruleDlg.visible = true
}
async function saveRule() {
  if (ruleDlg.channelId == null) return ElMessage.warning('请选择渠道')
  if ((ruleDlg.quietStart == null) !== (ruleDlg.quietEnd == null))
    return ElMessage.warning('静默窗要么两头都给，要么都不给')
  ruleDlg.saving = true
  try {
    const body = {
      channelId: ruleDlg.channelId, minSeverity: ruleDlg.minSeverity,
      alarmTypes: ruleDlg.alarmTypes.length ? ruleDlg.alarmTypes : null,
      groupId: ruleDlg.groupId, notifyOnClear: ruleDlg.notifyOnClear,
      quietStart: ruleDlg.quietStart || null, quietEnd: ruleDlg.quietEnd || null,
      enabled: ruleDlg.enabled,
    }
    if (ruleDlg.id == null) await notificationApi.createRule(body)
    else await notificationApi.updateRule(ruleDlg.id, body)
    ElMessage.success('已保存')
    ruleDlg.visible = false
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    ruleDlg.saving = false
  }
}
async function removeRule(r: NotificationRule) {
  try {
    await ElMessageBox.confirm(`删除渠道 '${r.channelName}' 的这条规则？`, '删除通知规则', { type: 'warning' })
  } catch { return }
  try {
    await notificationApi.deleteRule(r.id)
    ElMessage.success('已删除')
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '删除失败')
  }
}

// ---- 发件箱 ----
async function retry(o: NotificationOutboxItem) {
  try {
    await notificationApi.retryOutbox(o.id)
    ElMessage.success('已重新入队，稍候自动发送')
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '重发失败')
  }
}
function ruleSummary(r: NotificationRule) {
  const parts = [`≥${SEVERITY_LABELS[r.minSeverity] ?? r.minSeverity}`]
  parts.push(r.alarmTypes?.length ? r.alarmTypes.map(alarmTypeLabel).join('/') : '全部类型')
  parts.push(r.groupCode ? `部位 ${r.groupCode}` : '全站')
  if (r.notifyOnClear) parts.push('含恢复')
  if (r.quietStart) parts.push(`静默 ${r.quietStart}–${r.quietEnd}`)
  return parts.join(' · ')
}
const deadCount = computed(() => outbox.value.filter((o) => o.status === 'dead').length)
</script>

<template>
  <div class="notify">
    <div v-if="loadError" class="load-error">
      通知数据加载失败：{{ loadError }}
      <el-button size="small" text type="primary" @click="load">重试</el-button>
    </div>

    <!-- 渠道（Admin） -->
    <div class="panel" v-if="auth.isAdmin">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />通知渠道</span>
        <span class="hd-sub">webhook：企业微信 / 钉钉群机器人，或客户系统的通用 JSON 接口</span>
        <span class="hd-actions"><el-button size="small" type="primary" @click="openChannel()">新建渠道</el-button></span>
      </div>
      <div class="tbl">
        <div class="tr th ch">
          <span>名称</span><span>报文</span><span>地址</span><span>限流</span><span>规则数</span><span>状态</span><span>操作</span>
        </div>
        <div v-for="c in channels" :key="c.id" class="tr ch" :class="{ dim: !c.enabled }">
          <span class="mono strong">{{ c.name }}</span>
          <span>{{ FORMAT_LABELS[c.format ?? ''] ?? c.format }}<i v-if="c.hasSecret" class="sec">已配密钥</i></span>
          <span class="mono url" :title="c.url ?? ''">{{ c.url }}</span>
          <span class="mono">{{ c.minIntervalSeconds }}s</span>
          <span class="mono">{{ c.ruleCount }}</span>
          <span><span class="st" :class="c.enabled ? 'st-sent' : 'st-dead'">{{ c.enabled ? '启用' : '停用' }}</span></span>
          <span class="ops">
            <el-button size="small" text :loading="testing[c.id]" @click="testChannel(c)">测试</el-button>
            <el-button size="small" text @click="openChannel(c)">编辑</el-button>
            <el-button size="small" text type="danger" @click="removeChannel(c)">删除</el-button>
          </span>
        </div>
        <div v-if="!channels.length" class="tbl-empty">尚无渠道：告警只有页面和提示音两个出口，值班不盯屏幕就收不到任何东西</div>
      </div>
    </div>

    <!-- 规则（Admin） -->
    <div class="panel" v-if="auth.isAdmin">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />通知规则</span>
        <span class="hd-sub">级别 / 类型 / 部位过滤 + 静默窗；严重（critical）无视静默窗</span>
        <span class="hd-actions">
          <el-button size="small" type="primary" :disabled="!channels.length" @click="openRule()">新建规则</el-button>
        </span>
      </div>
      <div class="tbl">
        <div class="tr th ru">
          <span>渠道</span><span>匹配条件</span><span>状态</span><span>操作</span>
        </div>
        <div v-for="r in rules" :key="r.id" class="tr ru" :class="{ dim: !r.enabled }">
          <span class="mono strong">{{ r.channelName }}</span>
          <span class="cond">{{ ruleSummary(r) }}</span>
          <span><span class="st" :class="r.enabled ? 'st-sent' : 'st-dead'">{{ r.enabled ? '启用' : '停用' }}</span></span>
          <span class="ops">
            <el-button size="small" text @click="openRule(r)">编辑</el-button>
            <el-button size="small" text type="danger" @click="removeRule(r)">删除</el-button>
          </span>
        </div>
        <div v-if="!rules.length" class="tbl-empty">尚无规则：渠道建好后要配规则才会有消息进发件箱</div>
      </div>
    </div>
    <p v-if="!auth.isAdmin" class="hint">渠道与规则的配置需要管理员权限；下方发送记录对复核员可见。</p>

    <!-- 发送记录（Reviewer+） -->
    <div class="panel">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" :class="deadCount ? 'hd-mark--crit' : ''" />发送记录</span>
        <span class="hd-sub">语义为至少一次：极端时机会重复，绝不静默丢；失败退避 1/5/15/60 分钟，连败 6 次搁置待人工重发</span>
        <span class="hd-actions seg">
          <span class="seg-tab" :class="{ active: outboxStatus === '' }" @click="setOutboxStatus('')">全部</span>
          <span class="seg-tab" :class="{ active: outboxStatus === 'pending' }" @click="setOutboxStatus('pending')">待发</span>
          <span class="seg-tab" :class="{ active: outboxStatus === 'dead' }" @click="setOutboxStatus('dead')">失败搁置</span>
        </span>
      </div>
      <div class="tbl">
        <div class="tr th ob">
          <span>时间</span><span>渠道</span><span>事件</span><span>螺栓</span><span>内容</span><span>状态</span><span>重试</span><span>失败原因</span><span>操作</span>
        </div>
        <div v-for="o in outbox" :key="o.id" class="tr ob">
          <span class="mono time"><b>{{ fmtTime(o.createdAt) }}</b><i v-if="o.sentAt">发出 {{ fmtTime(o.sentAt) }}</i></span>
          <span class="mono">{{ o.channelName }}</span>
          <span>{{ o.eventKind === 'clear' ? '恢复' : '触发' }}</span>
          <span class="mono">{{ o.boltCode ?? '—' }}</span>
          <span class="msg" :title="o.message ?? ''">{{ o.message ?? '—' }}</span>
          <span><span class="st" :class="STATUS_META[o.status]?.cls">{{ STATUS_META[o.status]?.label ?? o.status }}</span></span>
          <span class="mono">{{ o.attempts }}</span>
          <span class="msg err" :title="o.lastError ?? ''">{{ o.lastError ?? '—' }}</span>
          <span>
            <el-button v-if="o.status === 'dead'" size="small" text type="warning" @click="retry(o)">重发</el-button>
            <span v-else class="none">—</span>
          </span>
        </div>
        <div v-if="!outbox.length" class="tbl-empty">暂无发送记录</div>
      </div>
    </div>

    <!-- 渠道编辑对话框 -->
    <el-dialog v-model="chDlg.visible" :title="chDlg.id == null ? '新建通知渠道' : '编辑通知渠道'" width="520px">
      <el-form label-width="110px" label-position="left">
        <el-form-item label="名称"><el-input v-model="chDlg.name" placeholder="如：值班群机器人" maxlength="64" /></el-form-item>
        <el-form-item label="报文格式">
          <el-radio-group v-model="chDlg.format">
            <el-radio value="wecom">企业微信</el-radio>
            <el-radio value="dingtalk">钉钉</el-radio>
            <el-radio value="raw">通用 JSON</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="Webhook 地址">
          <el-input v-model="chDlg.url" placeholder="群机器人的 webhook 地址（含 key）" />
        </el-form-item>
        <el-form-item label="密钥">
          <el-input v-model="chDlg.secret" type="password" show-password :disabled="chDlg.clearSecret"
            :placeholder="chDlg.hasSecret ? '已配置（留空保留现有）' : '钉钉加签密钥 / 通用格式的请求头令牌，可不填'" />
          <!-- 后端支持"空串=清除"，但输入框留空默认映射为"保留"——配错的密钥必须有显式清除入口 -->
          <el-checkbox v-if="chDlg.id != null && chDlg.hasSecret" v-model="chDlg.clearSecret" style="margin-top: 4px">
            清除已保存的密钥（保存后此渠道不再带签名/令牌发送）
          </el-checkbox>
        </el-form-item>
        <el-form-item label="最小间隔 (秒)">
          <el-input-number v-model="chDlg.minIntervalSeconds" :min="0" :max="3600" :step="10" controls-position="right" />
          <span class="unit">两次发送的最小间隔（风暴合并之外的第二道闸）</span>
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="chDlg.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="chDlg.visible = false">取消</el-button>
        <el-button type="primary" :loading="chDlg.saving" @click="saveChannel">保存</el-button>
      </template>
    </el-dialog>

    <!-- 规则编辑对话框 -->
    <el-dialog v-model="ruleDlg.visible" :title="ruleDlg.id == null ? '新建通知规则' : '编辑通知规则'" width="520px">
      <el-form label-width="110px" label-position="left">
        <el-form-item label="渠道">
          <el-select v-model="ruleDlg.channelId" style="width: 100%">
            <el-option v-for="c in channels" :key="c.id" :label="c.name" :value="c.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="最低级别">
          <el-radio-group v-model="ruleDlg.minSeverity">
            <el-radio value="info">提示及以上</el-radio>
            <el-radio value="warn">预警及以上</el-radio>
            <el-radio value="critical">仅严重</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="告警类型">
          <el-select v-model="ruleDlg.alarmTypes" multiple clearable placeholder="不选 = 全部类型" style="width: 100%">
            <el-option v-for="t in TYPE_OPTIONS" :key="t" :label="alarmTypeLabel(t)" :value="t" />
          </el-select>
        </el-form-item>
        <el-form-item label="监测部位">
          <el-select v-model="ruleDlg.groupId" clearable filterable placeholder="不选 = 全站" style="width: 100%">
            <el-option v-for="g in targets.groups" :key="g.id" :label="`${g.code} ${g.name}`" :value="g.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="恢复也通知"><el-switch v-model="ruleDlg.notifyOnClear" /></el-form-item>
        <el-form-item label="静默窗">
          <div class="quiet-row">
            <el-time-select v-model="ruleDlg.quietStart" start="00:00" step="00:30" end="23:30" clearable placeholder="开始" style="width: 130px" />
            <span class="dash">—</span>
            <el-time-select v-model="ruleDlg.quietEnd" start="00:00" step="00:30" end="23:30" clearable placeholder="结束" style="width: 130px" />
          </div>
          <span class="unit">服务器本地时间，可跨零点（如 22:00–06:00）；严重告警无视静默</span>
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="ruleDlg.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="ruleDlg.visible = false">取消</el-button>
        <el-button type="primary" :loading="ruleDlg.saving" @click="saveRule">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.notify { display: flex; flex-direction: column; gap: 14px; }
.panel-hd { display: flex; align-items: center; gap: 12px; }
.hd-actions { margin-left: auto; display: flex; align-items: center; gap: 8px; }
.hint { font-size: 12.5px; color: var(--text-3); margin: 0; padding: 0 4px; }
.tbl { padding: 4px 12px 14px; overflow-x: auto; }
.tr { display: grid; align-items: center; padding: 8px; border-radius: 8px; font-size: 12.5px; gap: 6px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.06em; }
.tr.dim { opacity: 0.55; }
.tr.ch { grid-template-columns: 150px 130px 1fr 70px 64px 80px 170px; min-width: 900px; }
.tr.ru { grid-template-columns: 150px 1fr 80px 120px; min-width: 700px; }
.tr.ob { grid-template-columns: 128px 110px 52px 96px 1fr 78px 48px 200px 70px; min-width: 1050px; }
.strong { font-weight: 600; }
.url { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-3); }
.sec { font-style: normal; font-size: 10.5px; color: var(--text-3); margin-left: 6px; }
.cond { color: var(--text-2); }
.msg { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.msg.err { color: var(--warn); }
.time { display: flex; flex-direction: column; line-height: 1.25; }
.time b { font-weight: 500; font-size: 12px; }
.time i { font-style: normal; font-size: 10.5px; color: var(--text-3); }
.st { font-size: 11px; padding: 2px 9px; border-radius: 10px; white-space: nowrap; }
.st-pending { color: var(--warn); border: 1px solid var(--warn); }
.st-sent { color: var(--ok, #34d399); border: 1px solid currentColor; }
.st-dead { color: var(--crit, #f43f5e); border: 1px solid currentColor; }
.ops { display: flex; gap: 2px; }
.none { color: var(--text-3); }
.tbl-empty { text-align: center; padding: 24px; color: var(--text-3); font-size: 12.5px; }
.load-error { display: flex; align-items: center; gap: 8px; padding: 10px 18px; color: var(--warn); font-size: 12.5px; }
.unit { display: block; font-size: 11.5px; color: var(--text-3); line-height: 1.5; margin-top: 2px; }
.quiet-row { display: flex; align-items: center; gap: 8px; }
.dash { color: var(--text-3); }
.seg { display: flex; gap: 4px; background: var(--panel); border: 1px solid var(--panel-border); border-radius: 9px; padding: 3px; }
.seg-tab { font-size: 12px; padding: 4px 12px; border-radius: 6px; cursor: pointer; color: var(--text-3); }
.seg-tab:hover { color: var(--text-1); }
.seg-tab.active { color: var(--accent); background: rgba(34, 211, 238, 0.1); font-weight: 600; }
</style>
