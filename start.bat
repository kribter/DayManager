@echo off
setlocal

rem ============================================================
rem  DayManager 启动脚本
rem
rem  用法:
rem      start.bat            编译最新代码，然后启动后端和 Windows 客户端
rem      start.bat nobuild    跳过编译，直接启动上一次的构建结果
rem
rem  为什么要这个脚本:
rem    1. 真正的 exe 藏在 bin\Debug\net10.0-windows...\win-x64\ 深处，手点太麻烦
rem    2. 数据库位置由程序自己固定在项目下的 data\，不依赖任何环境变量。
rem       所以不管你怎么启动，用的都是同一个库
rem    3. AI 的拆解功能依赖本机后端，脚本会一并拉起来 ——
rem       单独记着「先开后端」这件事太容易忘，忘了的表现是界面上写着「连不上后端」，
rem       而你会以为是程序坏了
rem    4. 启动前自动关掉旧实例 —— 否则旧进程占着文件，编译会失败
rem
rem  【这个文件必须存成 GBK（ANSI）编码，不能存成 UTF-8】
rem  cmd 按字节读取批处理文件，遇到 UTF-8 的中文时，有些字会被它当成命令分隔符，
rem  于是注释行被拆成几段去执行，表现就是一堆「xxx 不是内部或外部命令」，
rem  而且报错内容夹杂乱码。存成 GBK 之后，中文注释既是中文，也不会被解析。
rem ============================================================

rem /d 保证切到脚本自己所在的盘符和目录，这样从任何地方双击都能正常工作
cd /d "%~dp0"

set "TFM=net10.0-windows10.0.19041.0"
set "APP_PROJ=src\DayManager.App\DayManager.App.csproj"
set "API_PROJ=src\DayManager.Api\DayManager.Api.csproj"
set "EXE=src\DayManager.App\bin\Debug\%TFM%\win-x64\DayManager.App.exe"
set "API_PORT=5080"

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

rem 后端也要一并关掉，否则它的 dll 被占用，编译会撞上「文件被另一个进程使用」而失败。
rem 这个错误第一次遇到时很费解 —— 报的是复制失败，看不出是忘了关后端。
rem
rem 【为什么不能只写 taskkill /IM DayManager.Api.exe】
rem 后端是用 dotnet 跑起来的（dotnet run / dotnet xxx.dll），进程名一律叫 dotnet.exe，
rem 按名字杀杀不到它。所以这里按「命令行里有没有 DayManager.Api」来找，
rem 这也是唯一能稳定命中它的办法：
rem   只看进程名 -> 杀不掉（叫 dotnet.exe）
rem   只看窗口标题 -> 客户端自己拉起的那个没有窗口
rem 找到就停掉，找不到 PowerShell 也只是安静地什么都不做。
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter 'Name=''dotnet.exe''' | ForEach-Object { if ($_.CommandLine -like '*DayManager.Api*') { Stop-Process -Id $_.ProcessId -Force } }" >nul 2>&1

rem 等 2 秒让被杀掉的进程彻底释放文件句柄。
rem 用 ping 而不是 timeout.exe 是有意的：timeout.exe 在标准输入被重定向时会直接报错退出，
rem 而 ping 不依赖标准输入，任何调用方式下都能稳定等待（-n 3 之间正好是 2 个 1 秒间隔）。
ping -n 3 127.0.0.1 >nul

rem 杀完确认一次端口有没有放开。
rem 【为什么要多这一步】
rem 万一上面没杀掉（比如进程是别的账号启动的），编译会以「复制 dll 失败」告终，
rem 报错里半个字都没提端口。提前把占着端口的进程号打出来，
rem 总比让人对着 MSB3027 猜要强。
netstat -ano | findstr ":%API_PORT%" | findstr "LISTENING" >nul
if not errorlevel 1 (
    echo [提示] 端口 %API_PORT% 还被占着，正在等待它释放……
    netstat -ano | findstr ":%API_PORT%" | findstr "LISTENING"
    taskkill /F /PID 0 >nul 2>&1
    echo        上面的 PID 就是还没退干净的后端，手工 taskkill /F /PID 那个号 也能解决。
)

echo 正在编译后端...
"%DOTNET%" build "%API_PROJ%" -v q --nologo
if errorlevel 1 (
    echo.
    echo [错误] 后端编译失败，上面就是编译器的报错信息，修好后再运行本脚本。
    echo        如果报错里写着「无法复制到 ... DayManager.Api.dll / 文件已被使用」，
    echo        说明旧后端还占着文件：关掉那个最小化的「DayManager 后端」窗口，
    echo        或者跑一次 taskkill /F /IM dotnet.exe，再重新运行本脚本。
    pause
    exit /b 1
)

echo 正在编译 Windows 客户端...
"%DOTNET%" build "%APP_PROJ%" -f %TFM% -v q --nologo
if errorlevel 1 (
    echo.
    echo [错误] 客户端编译失败，上面就是编译器的报错信息，修好后再运行本脚本。
    pause
    exit /b 1
)

:launch

rem ---- 后端：已经在跑就复用，没跑就拉起来 ----
rem 用 netstat 探测而不是无脑 start 一个新实例：
rem 重复启动会因为端口被占而立刻退出，留下一个一闪而过的黑窗口，什么都看不清。
netstat -ano | findstr ":%API_PORT%" | findstr "LISTENING" >nul
if not errorlevel 1 (
    echo 后端已在运行。
    goto :run_app
)

echo 正在启动后端服务（AI 拆解需要它）...
rem 用 --project 而不是直接跑编译好的 dll，是因为后者的工作目录是 bin\...，
rem 而 appsettings.Local.json（里面存着 AI 密钥）不在那个目录下，
rem 结果就是后端能起来但读不到密钥，界面上显示「AI 还没配好」。
rem --project 会把项目目录作为工作目录，配置文件才能被正确找到。
start "DayManager 后端" /MIN "%DOTNET%" run --project "%API_PROJ%" --no-build --no-launch-profile

rem 给后端几秒钟启动完，否则客户端一进去就显示「连不上后端」，白白吓人一跳
ping -n 9 127.0.0.1 >nul

:run_app
if not exist "%EXE%" (
    echo [错误] 找不到 %EXE%
    echo        请不带参数重新运行本脚本，先完整编译一次。
    pause
    exit /b 1
)

echo 正在启动 DayManager...
echo 数据目录: 项目下的 data\（程序内固定，不随启动方式改变）
start "" "%EXE%"

echo.
echo 已启动。
echo 那个最小化的「DayManager 后端」窗口别关，关了 AI 拆解就用不了了。
echo 不需要 AI 的时候，关掉它软件照样能用，只是拆解功能会提示连不上。
ping -n 5 127.0.0.1 >nul
endlocal
