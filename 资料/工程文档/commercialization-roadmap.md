# 商业化工业软件路线图

本文件是 `commercial_roadmap` 的落地执行版，用于指导后续开发，不替代总体方案。

## 当前产品级别

当前状态：实验室 MVP → 商业试用版底座过渡。

已具备：

- 真机采集、EchoTime、预紧力计算。
- 扭矩阶梯实验和 torque_fit active calibration。
- Watchdog 自动恢复。
- Vue Dashboard。
- 基础告警。

仍不具备：

- 多设备运行时完整管理。
- 参数变更复核流程。
- 正式载荷标定中心。
- 波形诊断中心完整 UI。
- Windows 离线安装包。
- License 授权。

## 已经落地的商业化底座

1. `DeviceConnectorManager`
   - 已把单例 connector 构造逻辑收口。
   - 当前只有 primary runtime，但 API 和数据结构已按多设备设计。

2. `ChannelConfigService`
   - 可查看 DB 主配置。
   - 可对比 DB 与设备最近读回参数。

3. 参数变更请求表
   - `channel_config_change_requests`
   - `configure-channel` 已写入请求、执行、失败记录。

4. 波形管理一期
   - `wave_features`
   - `reference_waves`
   - 最近波形特征查询。
   - 设置参考波形。

5. 标定中心一期
   - `torque_fit` 支持截距。
   - 标定中心 summary 按 method/trustLevel 统计。

6. 工业部署底座
   - Windows Service 支持。
   - 安装/卸载脚本。
   - PostgreSQL 备份脚本。
   - 诊断包脚本。

7. License 授权底座
   - 机器码接口 `/api/license/machine-code`。
   - 授权状态接口 `/api/license/status`。
   - 离线 `license.json` 模板。
   - runtime 接口暴露当前设备数/通道数与授权上限。
   - Phase 1 只做格式和机器码校验，Phase 2 换 RSA/ECDSA 签名校验。

## 下一轮建议

优先级：

1. 多设备动态加载：从 `devices` 表加载多个 runtime。
2. 参数变更审批：requested → approved → executed → verified。
3. 波形诊断 UI：A-Scan、阈值、零交叉、SNR。
4. 标定中心 UI：标定点、拟合线、报告导出。
5. License：机器码、通道数、到期时间。

## 商业风险提醒

- `torque_fit` 仍是扭矩法临时拟合，不能作为正式安全验收。
- 正式商用必须至少支持 `load_calibrated` 或符合 GB/T 43232 的标定流程。
- 参数修改、标定应用、告警确认必须可审计、可追责。
