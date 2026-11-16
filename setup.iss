; Inno Setup script for DayManager.
;
; Build:
;   "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" setup.iss
;
; Requires publish\app to exist first - run tools\package.ps1 to produce it.
; That script also writes the shared token, so NEVER point this at a hand-made
; folder: an app folder whose seed.json and appsettings.Local.json do not match
; will answer 401 to every request and just look "broken".
;
; Why this file sits in the repo root instead of tools\: Inno resolves relative
; paths against the folder the script lives in, and {#SourcePath} is NOT
; expanded when nested inside a #define - "Source: {#MyPayload}" comes out as
; the literal text "F:\DayManager\{#SourcePath}publish\app\*" and finds nothing.
; So use {#SourcePath} directly in the sections, and keep this script next to
; publish\ so the paths stay short.
;
; Why no Chinese language file: the Inno Setup package ships no
; ChineseSimplified.isl, so the wizard stays in English. It is three buttons.

#define MyAppName "DayManager"
#define MyAppVersion "1.1"
#define MyAppExeName "DayManager.App.exe"

[Setup]
; Fixed GUID: without it, installing twice creates two entries in
; "Add or remove programs" instead of upgrading the first one.
AppId={{2F6B1E8C-7A4D-4E1B-9C55-3D6A0F1B7E21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=DayManager

; Per-user install: no UAC prompt, nothing written outside the user's profile.
DefaultDirName={localappdata}\Programs\DayManager
PrivilegesRequired=lowest
; "dialog" matters for the folder choice: if the user picks somewhere the
; account cannot write (C:\Program Files, D:\ root on some setups), Inno
; restarts itself elevated instead of failing halfway through.
PrivilegesRequiredOverridesAllowed=dialog

; Explicit, because this is the whole point of the page: let the user put it
; wherever they want. It is on by default, but stating it here means a future
; edit to WizardStyle / PrivilegesRequired cannot quietly take it away.
DisableDirPage=no
DisableReadyPage=no

DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
; No SetupIconFile: the icon we build is a PNG-compressed .ico, and Inno's icon
; replacer rejects it ("path not found" while updating Setup.e32). Skipping it
; only affects the installer's own icon - shortcuts get their icon from
; DayManager.App.exe, which is the one that actually matters.
; To fix later: regenerate appicon.ico as plain BMP/DIB frames.

; The backend lives under {app} and stays running after the window is closed.
; Without this, uninstall fails on "file in use".
CloseApplications=yes
RestartApplications=no

; The payload is ~360 MB of runtime files; refuse rather than half-install.
ExtraDiskSpaceRequired=600

; WinUI 3 hard requirement: builds below 17763 (1809) crash on launch with
; NOTHING shown - no window, no error, nothing in Task Manager. Refusing the
; install with a clear message beats "installs fine, then does nothing".
MinVersion=10.0.17763

OutputBaseFilename=DayManager-Setup
OutputDir={#SourcePath}publish
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; Why overriding English strings instead of shipping ChineseSimplified.isl:
; the Inno package has no Chinese language file and the one on the net is not
; reachable from this machine. But [Messages] can override any single string of
; whatever language file is in use, so the handful of lines the user actually
; reads (pick a folder / ready / done / buttons) get rewritten in Chinese while
; everything we never see stays English. No dependency on a missing file.
[Messages]
WinVersionTooLowError=这个程序需要 %1 或更新版本的 Windows（%2 及以上）才能运行，当前系统版本太低了。
SetupWindowTitle=安装 - {#MyAppName}
SetupAppTitle={#MyAppName} 安装向导
WizardSelectDir=选择安装位置
SelectDirLabel3=把 {#MyAppName} 装到下面这个文件夹。想换地方就点「浏览…」自己挑一个，然后点「下一步」。
ButtonBrowse=浏览…
ButtonNext=下一步 >
ButtonBack=< 上一步
ButtonInstall=安装
ButtonFinish=完成
ButtonCancel=取消
WizardReady=准备安装
ReadyLabel1=现在可以开始把 {#MyAppName} 装到你的电脑上了。
ReadyLabel2a=点「安装」继续。装到一半想反悔就点「取消」。
FinishedHeadingLabel={#MyAppName} 装好了
FinishedLabel={#MyAppName} 已经装好了。开始菜单里有它，桌面快捷方式取决于你前面有没有勾。
ClickFinish=点「完成」退出安装程序。

[Tasks]
Name: "desktopicon"; Description: "在桌面上放一个快捷方式"; GroupDescription: "附加图标："; Flags: unchecked

[Files]
Source: "{#SourcePath}publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "现在打开 {#MyAppName}"; Flags: nowait postinstall skipifsilent
