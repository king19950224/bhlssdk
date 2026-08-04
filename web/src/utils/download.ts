// 文件下载的统一入口。各处原本都在重复两件事：拼 <a download> 那套 DOM 动作，
// 以及决定存盘用什么名字。后者以前只有点位表一处认服务端给的名字，其余各自另编，
// 结果后端 ReportEndpoints.DownloadName 里的标题清洗与长度截断在主下载路径上被绕过。
import { getAuthToken } from '../api/client'

/**
 * 从 Content-Disposition 取文件名。
 * 名字带中文时 ASP.NET Core 会同时下发两个：filename= 是 ASCII 降级版（中文被换成下划线），
 * filename*=UTF-8'' 才是真名。必须先认后者，只匹配 filename= 会存出一串下划线。
 */
function fileNameFromDisposition(cd: string | null): string | null {
  if (!cd) return null
  const star = cd.match(/filename\*=UTF-8''([^;]+)/i)
  if (star) {
    try {
      return decodeURIComponent(star[1])
    } catch {
      // 编码坏了就退回 ASCII 那份，总比不给名字强
    }
  }
  return cd.match(/filename="?([^";]+)/)?.[1] ?? null
}

/**
 * 带登录态取回文件并存盘，名字优先用服务端给的那个。
 * fallbackName 只在服务端没给名字时兜底，别拿它当主用名：
 * 服务端那份才做了标题清洗、非法字符替换和长度截断。
 */
export async function downloadFile(url: string, fallbackName: string): Promise<void> {
  const headers = new Headers()
  const token = getAuthToken()
  if (token) headers.set('Authorization', `Bearer ${token}`)

  const res = await fetch(url, { headers })
  if (!res.ok) {
    // 后端会带具体原因（404 文件缺失、400 完整性校验失败等），透传给调用方而不是一律报"下载失败"
    let msg = `下载失败（HTTP ${res.status}）`
    try {
      const body = await res.json() as { error?: string }
      if (body?.error) msg = body.error
    } catch { /* 非 JSON 响应 */ }
    throw new Error(msg)
  }

  const objectUrl = URL.createObjectURL(await res.blob())
  const a = document.createElement('a')
  a.href = objectUrl
  a.download = fileNameFromDisposition(res.headers.get('content-disposition')) ?? fallbackName
  a.click()
  URL.revokeObjectURL(objectUrl)
}
