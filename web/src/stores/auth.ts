import { defineStore } from 'pinia'
import { authApi } from '../api'
import { getAuthToken, setAuthToken } from '../api/client'

/**
 * M6：解析 JWT 的 exp 判断令牌是否仍有效（前端预判过期，不必等某次 API 返回 401 才登出）。
 * 解析失败一律视为无效（保守）。payload 是 base64url，需把 -/_ 还原为 +//。
 */
function isJwtValid(token: string | null): boolean {
  if (!token) return false
  try {
    const part = token.split('.')[1]?.replace(/-/g, '+').replace(/_/g, '/')
    if (!part) return false
    const payload = JSON.parse(atob(part)) as { exp?: number }
    return typeof payload.exp === 'number' && payload.exp * 1000 > Date.now()
  } catch {
    return false
  }
}

export const useAuth = defineStore('auth', {
  state: () => ({
    token: getAuthToken(),
    userId: Number(localStorage.getItem('bs_uid') || 0) || null,
    username: localStorage.getItem('bs_user'),
    role: localStorage.getItem('bs_role'),
  }),
  getters: {
    isAuthed: (s) => isJwtValid(s.token),
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
      this.token = r.accessToken
      this.userId = r.userId
      this.username = r.username
      this.role = r.role
      setAuthToken(r.accessToken)
      localStorage.setItem('bs_uid', String(r.userId))
      localStorage.setItem('bs_user', r.username)
      localStorage.setItem('bs_role', r.role)
    },
    logout() {
      this.token = null
      this.userId = null
      this.username = null
      this.role = null
      setAuthToken(null)
      localStorage.removeItem('bs_uid')
      localStorage.removeItem('bs_user')
      localStorage.removeItem('bs_role')
    },
  },
})
