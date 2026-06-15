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
    onUnauthorized?.()
    throw new ApiError(401, '未登录或登录已过期')
  }
  if (!res.ok) {
    let msg = `请求失败（${res.status}）`
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
}
