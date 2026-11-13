; QR Quick Lite 安装程序（Inno Setup 6）
; 编译：ISCC.exe setup\QRQuickLite.iss
; 目标：当前用户安装到 %LocalAppData%\Programs\QRQuickLite，不需要管理员权限。

#define AppName "QR Quick Lite"
#define AppVersion "1.0.0"
#define AppPublisher "QR Quick"
#define AppExeName "QRQuickLite.exe"

[Setup]
AppId={{6C1E4B72-9F3A-4C58-8D21-5A7B0E93F4D6}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion=1.0.0.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
DefaultDirName={localappdata}\Programs\QRQuickLite
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=QRQuickLite-{#AppVersion}-Setup
SetupIconFile=..\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
DisableWelcomePage=no
; 按系统界面语言自动选语言，不弹「选择安装语言」对话框，少一次点击
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.ShellMenuTask=在资源管理器右键菜单中加入“二维码识别”
chinesesimp.IntegrationGroup=系统集成（可稍后在设置里改）
chinesesimp.LaunchApp=立即运行 %1
chinesesimp.UninstallHint=卸载会同时删除配置与日志（%%AppData%%\QRQuickLite）。
english.ShellMenuTask=Add "Scan QR Code" to the Explorer context menu
english.IntegrationGroup=System integration (change later in Settings)
english.LaunchApp=Launch %1 now
english.UninstallHint=Uninstall also removes settings and logs (%%AppData%%\QRQuickLite).

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "shellmenu"; Description: "{cm:ShellMenuTask}"; GroupDescription: "{cm:IntegrationGroup}"

[Files]
Source: "..\publish\QRQuickLite.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 走程序自己的开关，保证注册表与配置文件一致（只写注册表的话下次启动会被配置回滚）
Filename: "{app}\{#AppExeName}"; Parameters: "--enable-context-menu"; Flags: runhidden waituntilterminated; Tasks: shellmenu
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--disable-context-menu"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveShellMenu"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\settings.json"
Type: filesandordirs; Name: "{userappdata}\QRQuickLite"
