import { onUnmounted, ref, type Ref } from 'vue'

/**
 * 返回一个每 intervalMs 自动前进的「当前时间(ms)」ref。
 * 让依赖「距今多久」的 computed / 模板随时间刷新——否则 Date.now() 不是响应式，
 * 状态灯（nodata/stale）与「N 分钟前」在无新数据时会一直卡在首次渲染的值。
 */
export function useNow(intervalMs = 30000): Ref<number> {
  const now = ref(Date.now())
  const id = window.setInterval(() => {
    now.value = Date.now()
  }, intervalMs)
  onUnmounted(() => window.clearInterval(id))
  return now
}
