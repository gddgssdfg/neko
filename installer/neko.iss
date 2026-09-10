; Neko 桌宠安装脚本（v0.1.42 Inno Setup 6.x）
; 设计：用户级安装（{userappdata}）—— 无 UAC 摩擦；卸载保留 data/ 让用户配置可恢复
; 编译：iscc.exe neko.iss（输出 installer/NekoSetup-0.1.42.exe）

#define MyAppName "Neko 桌宠"
#define MyAppVersion "0.1.42"
#define MyAppPublisher "灯灯"
#define MyAppURL "https://github.com/lantern/neko"
#define MyAppExeName "Neko.exe"
#define SourceDir "..\publish\v0.1.42"
#define OutputDir "..\installer"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-1234-567890ABCDEF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={userappdata}\{#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=NekoSetup-{#MyAppVersion}
; ultra64 压缩最高（单文件 170MB 压到 ~80MB），慢但是一次性
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=..\neko-assets\tray.ico
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 中文界面
[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

; 拷贝整个 publish 输出（含 Neko.exe / native deps / neko-assets / plugins / data）
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 注意：Neko.exe 是 self-contained 单文件聚合（~170MB），无需额外运行时

; 快捷方式
[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autostartmenu}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"

; 可选任务
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "autostart"; Description: "开机自启动 Neko"; GroupDescription: "其他："; Flags: unchecked

; 自启动注册表（HKCU 不需 UAC；卸载时一并清理）
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NekoDeskPet"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

; 安装完成提示
[Messages]
BeveledLabel={#MyAppName} v{#MyAppVersion}

[Code]
// 首次安装后给个友好提示：v0.1.41 起 Neko 启动时会弹首启引导（猫名+称呼）
// 这里给个贴心兜底，避免用户以为"装完没反应"
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MsgBox(
      'Neko 桌宠已安装到：' + ExpandConstant('{app}') + #13#10#13#10 +
      '首次启动会要求你给猫取个名字 + 告诉它怎么称呼你（一次性设置）。' + #13#10 +
      '也可以稍后在「设置 → 猫咪 → 外观」里修改。' + #13#10#13#10 +
      '现在去体验：',
      mbInformation, MB_OK);
  end;
end;
