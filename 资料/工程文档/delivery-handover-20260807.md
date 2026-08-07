# 商用交付交接单 · v1.2.1（2026-08-07）

> 用途：随交付包移交。列清"软件侧已闭环的"与"必须你方/业主拍板或动手的"，逐条有归属。
> 软件侧状态见 `delivery-master-plan.md` §4/§5 与 `m4-real-regression-20260807.md`。

## 1. 需要业主/商务侧拍板的口径（未定就上线 = 默认值生效）

| 事项 | 默认值 | 说明 |
|---|---|---|
| 测量精度对外口径 | **±5%（GB/T 43232 单波法）** | 产品说明书 V1.03 的 ±3% 必须改掉（文档在仓库外，商务侧执行）。双波法为硬件升级项：需厂家在 model=3 补横波支持 + 换复合探头，查证链 `sdk-api-facts.md` §13 |
| UncertaintyMode（不确定度进告警判定） | `off`（只用实测保持率） | 可选 guardband（更难触发）/ sensitive（更早触发）。**须由业主结构工程师签字定档**并写进验收记录，配置项 `Alarm:UncertaintyMode` |
| 告警阈值（0.70/0.72、1.10/1.08 等） | 出厂兜底值 | 库内 alarm_rules 三级覆盖 + 两步异人复核。现场核准值由业主结构工程师书面确认后录入 |
| 通知 webhook 指向内网 | 禁止（防 SSRF） | 业主接收器在风场内网时改 `Notifications:AllowPrivateWebhookTargets=true`，责任留痕在配置 |
| 报警免责条款 | 已上登录页 | 三种不产生预紧力告警的情形 + 不参与安全联锁声明，须同步进合同与用户手册首页 |

## 2. 需要你方动手的事（软件侧做不了）

1. **git 远端**：仓库至今无 remote，全部历史只在开发机 + C 盘 bundle（`C:\Users\lenovo\bhlssdk-backup\`）。
   给一个 Gitee 私有库或内网 Git 地址，`git remote add origin <url> && git push -u origin main --tags` 一条完事。
2. **正式授权密钥对**：`LicenseSigner keygen` 生成正式钥，私钥离线保管（丢了全部现场授权作废重签）。
   特别注意：本轮出包用的是**彩排公钥**（`C:\Users\lenovo\bhlssdk-backup\rehearsal-keys\`）——正式出货必须换正式公钥重新组包。
3. **备份异地**：本机日备 30 份 + 月备 12 份挡不住整机损毁。定一个 NAS/业主存储位置，
   任务计划 robocopy 同步 `C:\BoltPreload\backups`（deployment-guide §7.1）。
4. **干净虚机装机彩排**：`BoltPreload-Setup-1.2.1.exe` 双击装 → 编排跑完 → 打出机器码（installer README 清单）。
5. **现场物理项回归**（m4 记录 §7）：插拔探头、载荷标定全流程、48 路满载、断网长跑、告警声音听感。

## 3. 软件侧已闭环（有据可查）

- 集成测试 **508/508 通过、跳过 0**（CI 门禁：跳过数必须为 0）
- 真机链路（DEV_LAB_001 @192.168.0.7）：连接、校时、MultiMeas、自动建档、验收拦截闭环
- DB 故障演练：降级可见（/ready degraded + 前端全局横幅）→ WAL 全量重放 → system_degraded 审计留痕
- 恢复演练：restore-postgres.ps1 对真机库 0 错误
- 安装包：Windows 首次编译出包；部署脚本 PowerShell 5.1 解析缺陷（BOM）修复
- 七道代码门禁全过；审计日志 append-only（0030 触发器）

## 4. 已知边界（合同/验收时如实陈述）

- **DB 故障窗口内 warn 级预紧力告警不评估**，仅恢复后由 WAL 重放补 critical 档；期间系统降级态全程可见
  （/ready 503 + 页面横幅 + 恢复后审计留痕）。彻底修（告警评估脱离入库依赖）排交付后维护版。
- 单实例架构：告警状态机在进程内存，不支持双机热备/多实例。
- 波形文件保留 30 天（测量元数据 5 年在库）；长期 A-Scan 溯源需业主明确要求后另行方案。
- PG 前置为交互式安装（静默装需预设超级口令，v1 不做）。
