// 轻量 fetch 封装：自动带 JWT、统一 401 处理、错误信息提取。
let authToken: string | null = localStorage.getItem('bs_token')
let onUnauthorized: (() => void) | null = null

export function setAuthToken(t: string | null) {
  authToken = t
  if (t) localStorage.setItem('bs_token', t)
  else localStorage.removeItem('bs_token')
}
export function getAuthToken() {
  return authToken
}
export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(path: string, opts: RequestInit = {}): Promise<T> {
  const headers = new Headers(opts.headers)
  if (authToken) headers.set('Authorization', `Bearer ${authToken}`)
  if (opts.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')

  const res = await fetch(path, { ...opts, headers })

  if (res.status === 401) {
    // 登录接口的 401 是"凭据错误"而非"登录态失效"：不触发全局登出处理（登录页会闪跳），
    // 文案也不能是"未登录或登录已过期"——密码输错看到这句只会一头雾水
    if (path.startsWith('/api/auth/login')) throw new ApiError(401, '用户名或密码错误')
    onUnauthorized?.()
    throw new ApiError(401, '未登录或登录已过期')
  }
  if (!res.ok) {
    // 423/429 后端不带 body（登录连续失败锁定 / IP 限速），默认文案要能让人看懂而不是"请求失败（423）"
    let msg =
      res.status === 423 ? '连续失败次数过多，账号已临时锁定，请稍后再试'
      : res.status === 429 ? '操作过于频繁，请稍后再试'
      : `请求失败（${res.status}）`
    try {
      const j = await res.json()
      msg = j.error || j.message || msg
    } catch {
      /* 非 JSON 错误体 */
    }
    throw new ApiError(res.status, msg)
  }
  if (res.status === 204) return undefined as T
  const ct = res.headers.get('content-type') || ''
  return (ct.includes('json') ? await res.json() : await res.text()) as T
}

export const http = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body !== undefined ? JSON.stringify(body) : undefined }),
  patch: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PATCH', body: body !== undefined ? JSON.stringify(body) : undefined }),
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
}
