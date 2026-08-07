; ============================================================================
; BoltPreload 离线安装包（Inno Setup 6）
; ----------------------------------------------------------------------------
; 定位：把交付物摆到位 + 缺前置时拉起前置安装器 + 装完弹管理员 PowerShell 跑
; install-all.ps1（建库/迁移/服务/HTTPS/备份任务全在那边编排）。安装逻辑不写在
; 安装器里——手工部署和一键安装走同一条 PowerShell 编排，只维护一份。
;
; 组包与编译步骤见同目录 README.md（payload\ 目录布局、iscc 命令）。
; 本工程尚未在 Windows 上编译彩排过，首次使用照 README 的验证清单走。
; ============================================================================

[Setup]
AppId={{7E1B7C1E-BP12-4B7A-9C60-B0LTPRELOAD1}
AppName=BoltPreload 螺栓预紧力在线监测系统
AppVersion=1.2.0
AppPublisher=BoltPreload
DefaultDirName=C:\BoltPreload
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=BoltPreload-Setup-1.2.0
OutputDir=output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
; 简体中文是非官方翻译，不随 Inno 安装器分发——语言文件收进本目录（kira-96 维护版），
; 出包不依赖每台机器的 Inno 装了哪些翻译
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Files]
; 应用本体（自包含发布，目标机免装 .NET）
Source: "payload\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; 迁移与运维脚本
Source: "payload\deploy\*"; DestDir: "{app}\deploy"; Flags: recursesubdirs ignoreversion
; NSSM 与授权公钥
Source: "payload\nssm.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "payload\license-public.pem"; DestDir: "{app}"; Flags: ignoreversion
; 现场手册
Source: "payload\docs\*"; DestDir: "{app}\docs"; Flags: recursesubdirs ignoreversion
; 前置安装器（可选放入 payload\prereq\，没放就要求现场已装好）
Source: "payload\prereq\*"; DestDir: "{tmp}\prereq"; Flags: recursesubdirs ignoreversion skipifsourcedoesntexist

[Run]
; PostgreSQL 缺失且包里带了安装器 → 交互式拉起（静默装需要预设超级口令，v1 不做，
; 装完 TimescaleDB 安装器同理；两个都装完了本步自动跳过）
Filename: "{tmp}\prereq\postgresql-16-setup.exe"; Description: "安装 PostgreSQL 16（检测到未安装）"; \
    Flags: waituntilterminated; Check: PgMissing and PgInstallerBundled
Filename: "{tmp}\prereq\timescaledb-setup.exe"; Description: "安装 TimescaleDB 扩展"; \
    Flags: waituntilterminated; Check: PgMissing and TsInstallerBundled
; 主编排：管理员 PowerShell 窗口里跑 install-all.ps1，交互补口令，窗口保留看结果与机器码
Filename: "powershell.exe"; \
    Parameters: "-ExecutionPolicy Bypass -NoExit -File ""{app}\deploy\windows\install-all.ps1"" -InstallDir ""{app}"""; \
    Description: "现在执行装机编排（建库/迁移/服务/HTTPS/备份任务）"; \
    Flags: postinstall shellexec waituntilterminated

[UninstallRun]
; 卸载只停删服务；数据库、data\（WAL/波形/报告）、logs\ 全部保留——卸载不许顺手删监测数据
Filename: "powershell.exe"; \
    Parameters: "-ExecutionPolicy Bypass -File ""{app}\deploy\windows\uninstall-service.ps1"""; \
    Flags: waituntilterminated; RunOnceId: "StopService"

[Code]
function PgMissing(): Boolean;
begin
  // EDB 安装器会写这个键；TimescaleDB 自带 PG 的场合同样能探到
  Result := not RegKeyExists(HKLM, 'SOFTWARE\PostgreSQL\Installations');
end;

function PgInstallerBundled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\prereq\postgresql-16-setup.exe'));
end;

function TsInstallerBundled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\prereq\timescaledb-setup.exe'));
end;
