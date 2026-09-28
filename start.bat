@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  DayManager 启动脚本
rem
rem  用法:
rem      start.bat            编译最新代码，然后启动
rem      start.bat nobuild    跳过编译，直接启动上一次的构建结果
rem
rem  为什么要这个脚本:
rem    1. 真正的 exe 藏在 bin\Debug\net10.0-windows...\win-x64\ 深处，手点太麻烦
rem    2. 双击 exe 时没有设置 DAYMANAGER_DATA_DIR，数据库会跑到系统默认位置，
rem       于是你会看到一份空的清单，以为数据丢了。这里统一把数据目录固定到项目下的 data\
rem    3. 启动前自动关掉旧实例 -- 否则旧进程占着 exe 文件，编译会失败
rem ============================================================

rem /d 保证切到脚本自己所在的盘符和目录，这样从任何地方双击都能正常工作
cd /d "%~dp0"

set "DATA_DIR=%~dp0data"
set "TFM=net10.0-windows10.0.19041.0"
set "PROJ=src\DayManager.App\DayManager.App.csproj"
set "EXE=src\DayManager.App\bin\Debug\%TFM%\win-x64\DayManager.App.exe"

rem ---- 找 dotnet ----
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if not errorlevel 1 goto :dotnet_ok
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
    goto :dotnet_ok
)
echo [错误] 找不到 dotnet，请先安装 .NET 10 SDK
pause
exit /b 1
:dotnet_ok

if /i "%~1"=="nobuild" goto :launch

echo 正在关闭正在运行的旧实例...
taskkill /F /IM DayManager.App.exe >nul 2>&1
rem 等 2 秒让被杀掉的进程彻底释放文件句柄，否则编译会撞上"文件被占用"。
rem 用 ping 而不是 timeout.exe 是有意的：timeout.exe 在标准输入被重定向时会直接报错退出，
rem 而 ping 不依赖标准输入，任何调用方式下都能稳定等待（-n 3 之间正好是 2 个 1 秒间隔）。
ping -n 3 127.0.0.1 >nul

echo 正在编译 Windows 客户端...
"%DOTNET%" build "%PROJ%" -f %TFM% -v q --nologo
if errorlevel 1 (
    echo.
    echo [错误] 编译失败，上面就是编译器的报错信息，修好后再运行本脚本。
    pause
    exit /b 1
)

:launch
if not exist "%EXE%" (
    echo [错误] 找不到 %EXE%
    echo        请不带参数重新运行本脚本，先完整编译一次。
    pause
    exit /b 1
)

if not exist "%DATA_DIR%" mkdir "%DATA_DIR%"

echo 正在启动 DayManager...
echo 数据目录: %DATA_DIR%
set "DAYMANAGER_DATA_DIR=%DATA_DIR%"
start "" "%EXE%"

echo 已启动。这个窗口可以关掉了。
ping -n 4 127.0.0.1 >nul
endlocal
