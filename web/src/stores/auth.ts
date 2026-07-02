import { defineStore } from 'pinia'
import { authApi } from '../api'
import { getAuthToken, setAuthToken } from '../api/client'

/**
 * 身份信息一律以【JWT 签名载荷】为唯一事实源，不再信任可被 DevTools 篡改的 localStorage 值。
 * 令牌本身仍存 localStorage（免刷新丢失登录态）；但 role/userId/username 从 token 解析而来，
 * 篡改 localStorage 里的角色字符串不再能骗过前端路由/按钮（后端也始终按 JWT claims 做 403 兜底）。
 */
interface JwtClaims {
  userId: number | null
  username: string | null
  role: string | null
  exp: number | null
}

// base64url → UTF-8 JSON（补 padding + TextDecoder 正确解码中文用户名）
function decodePayload(part: string): Record<string, unknown> {
  const b64 = part.replace(/-/g, '+').replace(/_/g, '/')
  const pad = b64.length % 4 ? '='.repeat(4 - (b64.length % 4)) : ''
  const bin = atob(b64 + pad)
  const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0))
  return JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>
}

// 兼容 JwtSecurityTokenHandler 的出站 claim 映射：同时探测短名与 SOAP/长 URI 形式。
const ROLE_KEYS = ['role', 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role']
const NAME_KEYS = ['unique_name', 'name', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name']
const ID_KEYS = ['sub', 'nameid', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier']

function pick(p: Record<string, unknown>, keys: string[]): string | null {
  for (const k of keys) {
    const v = p[k]
    if (typeof v === 'string' && v) return v
    if (typeof v === 'number') return String(v)
  }
  return null
}

function parseJwt(token: string | null): JwtClaims {
  const empty: JwtClaims = { userId: null, username: null, role: null, exp: null }
  if (!token) return empty
  try {
    const part = token.split('.')[1]
    if (!part) return empty
    const p = decodePayload(part)
    const idStr = pick(p, ID_KEYS)
    const uid = idStr != null ? Number(idStr) : NaN
    return {
      userId: Number.isFinite(uid) ? uid : null,
      username: pick(p, NAME_KEYS),
      role: pick(p, ROLE_KEYS),
      exp: typeof p.exp === 'number' ? p.exp : null,
    }
  } catch {
    return empty
  }
}

function claimsFromStorage(): JwtClaims & { token: string | null } {
  const token = getAuthToken()
  return { token, ...parseJwt(token) }
}

export const useAuth = defineStore('auth', {
  state: () => {
    const c = claimsFromStorage()
    return { token: c.token, userId: c.userId, username: c.username, role: c.role }
  },
  getters: {
    // 令牌未过期即视为已认证（exp 来自签名载荷，无法伪造）
    isAuthed: (s) => {
      const exp = parseJwt(s.token).exp
      return exp != null && exp * 1000 > Date.now()
    },
    isReviewer: (s) => s.role === 'reviewer' || s.role === 'admin',
    isAdmin: (s) => s.role === 'admin',
    roleLabel: (s) =>
      ({ admin: '管理员', reviewer: '复核员', operator: '操作员' } as Record<string, string>)[s.role || ''] ||
      s.role ||
      '—',
    // 按角色默认落地：操作员→资产监测,复核员→参数审批,管理员→总览
    homeName: (s) => (s.role === 'operator' ? 'assets' : s.role === 'reviewer' ? 'configChanges' : 'dashboard'),
  },
  actions: {
    async login(username: string, password: string) {
      const r = await authApi.login(username, password)
      setAuthToken(r.accessToken)
      // 身份从签名令牌解析（唯一事实源）；令牌缺失某 claim 时回退到登录响应值。
      const c = parseJwt(r.accessToken)
      this.token = r.accessToken
      this.userId = c.userId ?? r.userId
      this.username = c.username ?? r.username
      this.role = c.role ?? r.role
    },
    logout() {
      this.token = null
      this.userId = null
      this.username = null
      this.role = null
      setAuthToken(null)
    },
  },
})
