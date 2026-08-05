# 离线安装包组包说明

一键安装器 = Inno Setup 壳（摆文件、拉前置）+ `deploy/windows/install-all.ps1` 编排（建库、
迁移、服务、HTTPS、备份任务）。安装逻辑全在 PowerShell 侧，手工部署和一键安装同一条编排，
只维护一份；安装器坏了照 `docs/deployment-guide.md` 手工走完全等价。

## 组包（在 Windows 上做）

1. 在本目录建 `payload\`，按下面布局摆料：

```
payload\
  app\                  ← 交付包的 app\ 内容（自包含发布：BoltPreload.Api.exe、wwwroot、appsettings.json…）
  deploy\
    sql\                ← 全部 0*.sql 迁移
    windows\            ← 全部 *.ps1（含 install-all.ps1）
  docs\                 ← deployment-guide.md / troubleshooting.md 等现场手册
  nssm.exe              ← nssm.cc 下载 2.24 的 win64 版
  license-public.pem    ← 授权公钥（供应商密钥对的公钥半）
  prereq\               ← 可选：postgresql-16-setup.exe、timescaledb-setup.exe
                          （放了就会在目标机缺 PG 时自动拉起；不放则要求现场先装好）
```

交付 zip（bhlssdk-delivery-vX.Y.Z.zip）解开即是 app/deploy/docs/公钥四样，补 nssm 和 prereq 即可。

2. 装 Inno Setup 6（jrsoftware.org，装一次以后离线可用），编译：

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
# 产物：output\BoltPreload-Setup-1.2.0.exe
```

3. 换版本号：改 `installer.iss` 的 `AppVersion` 与 `OutputBaseFilename`，和 `Directory.Build.props` 保持一致。

## 首次彩排验证清单

本工程还没在 Windows 上编译彩排过，首次出包按这个清单验：

- [ ] 干净虚机（无 PG、无 .NET）双击安装 → 前置拉起 → 编排跑完 → 打出机器码；
- [ ] 装完照 `docs/deployment-guide.md` §8 走 8 条自检（设备在线一条无真机可跳过）；
- [ ] 升级彩排：装上一版本 → 造数据 → 再跑新版安装器（迁移账本只补新增）→ 数据完好；
- [ ] 卸载：服务停删，数据库与 `data\`、`logs\` 全部保留（卸载不许顺手删监测数据）；
- [ ] 已知限制记录在案：PG 前置是交互式安装（静默装要预设超级口令，v1 不做）；
      降级不支持（迁移单向），回滚 = 恢复上一版程序目录 + 从备份恢复库。
