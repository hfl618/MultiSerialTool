; MultiSerialTool Inno Setup 安装脚本
; 版本号由 build_release.ps1 通过 /DAppVersion=x.y.z 传入，无需手动修改
; 手动编译示例：ISCC.exe /DAppVersion=1.0.3 MultiSerialTool.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "MultiSerialTool"
#define AppPublisher "hfl"
; exe 文件名固定，版本号体现在文件属性与安装包文件名中
#define AppExeName "MultiSerialTool-hfl.exe"
#define SourceDir "..\publish\app"
#define OutputDir "..\publish"

[Setup]
; AppId 固定不变，Inno Setup 据此识别同一产品并执行升级安装
AppId={{93CD65A3-4D28-450A-8F66-54DCD735C9B6}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; 安装到当前用户目录，免管理员权限（UAC），串口上位机无需提权
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Setup_MultiSerialTool_{#AppVersion}
; 暂无 ico 资源，使用默认图标；将 app.ico 放入 Assets\Icons 后取消下一行注释即可
; SetupIconFile=..\Assets\Icons\app.ico
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern
; 安装前检测并提示关闭正在运行的程序，避免 exe 占用导致替换失败
CloseApplications=yes

[Languages]
; 语言文件随项目维护（安装包默认不含非官方语言），{#SourcePath} 为本 iss 所在目录
Name: "chinesesimp"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; 清理早期版本遗留的带版本号命名 exe（如 MultiSerialTool-1.0.3-hfl.exe）
[InstallDelete]
Type: files; Name: "{app}\MultiSerialTool-*-hfl.exe"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent
