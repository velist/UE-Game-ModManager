@echo off
REM ============================================================
REM  ENCODING WARNING - keep this file in GBK (codepage 936).
REM  Do NOT re-save it as UTF-8. cmd.exe miscounts line offsets in
REM  a batch file that mixes "chcp 65001" with non-ASCII text and
REM  starts executing the tail of a line as a command.
REM  See the sibling cleanup script for the same warning.
REM ============================================================
chcp 936 > nul
setlocal EnableExtensions EnableDelayedExpansion

REM ============================================================
REM   UE Mod Manager - 一键迁移老版本数据
REM ============================================================
REM   做什么：
REM   1) 把三处用户数据里"丢了就回不来"的部分复制一份留底
REM   2) 启动管理器（有老数据时会自动完成迁移）
REM   3) 失败时备份保留，可随时还原
REM ============================================================
REM
REM   设计说明（改这个脚本前先读）
REM
REM   一、时间戳怎么取
REM   老版本用 `wmic OS Get localdatetime`。wmic 在 Windows 11 24H2 起已被移除，
REM   取不到值时变量为空，备份目录名会变成含冒号的非法串，xcopy 当场失败——
REM   也就是说这个脚本在新机器上根本跑不起来。
REM   现在按三级降级取时间：
REM     1. powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"
REM        首选它，是因为输出格式由自己指定，不受系统区域设置影响，拿到就能直接
REM        当目录名用。代价是启动约 0.3 秒，整个脚本只调两次，可以接受。
REM     2. DATE / TIME 环境变量。不作首选：格式随区域设置变（2026/07/28 与
REM        28-07-2026 都可能），小时数小于 10 时 TIME 前面还是空格，直接拼会拿到
REM        含空格甚至含斜杠的目录名。所以这里只把其中的数字抠出来用，顺序未必是
REM        年月日，但唯一性够用，真实时间另记在备份目录的说明文件里。
REM     3. 前两条都不成时用固定后缀 no-timestamp。
REM   无论走哪条，拼好的后缀都要再过一遍白名单（只允许数字字母连字符），
REM   然后检查目录是否已存在，存在就往后加 -2 / -3，
REM   所以同一秒内跑两次也不会互相覆盖。取时间失败绝不中止脚本——
REM   留底的目录名难看一点，也比没有留底强。
REM
REM   二、备份哪些、不备份哪些
REM   用户数据现在分布在三处（对应 AppPaths / AppDataLayout）：
REM     A. LOCALAPPDATA\UEModManager   新位置：config.json、Data\、Logs\
REM     B. APPDATA\UEModManager        漫游：ui_config.json、Avatars\、
REM                                    Backgrounds\、config\、local.db
REM     C. 安装目录                    老版本残留：Data\、UserData\、config.json
REM   三处都备份，但一律跳过 Repository\ Overwrites\ Backups\ 这三类目录：
REM   它们装的是 MOD 包本体和文件副本，常见几十 GB，用户还可能把它们指到别的盘
REM   （位置记在 ui_config.json 的 RepositoryRoot / OverwritesRoot / BackupsRoot）。
REM   无脑全量复制会让"留底"变成塞满磁盘、跑几小时的事故。
REM   判据是"丢了还能不能拿回来"：包本体可以重新导入，备份副本可以重新备份，
REM   而 MOD 清单、方案、分类、索引、配置丢了就真没了——后者才是留底要保护的，
REM   体积通常只有几十 MB。被跳过的目录的当前位置会写进备份里的说明文件，
REM   免得用户还原完不知道该去哪找。
REM
REM   三、几个容易踩的坑
REM   - 往文件里 echo 时，`>>` 前面必须留一个空格。否则一旦这行末尾是数字，
REM     cmd 会把它当成重定向句柄号吃掉（echo abc1>>f 写进去的是 abc）。
REM   - 排除目录用 robocopy 的 /XD，不用 xcopy 的 /EXCLUDE：后者只接受一个清单
REM     文件，而那个参数不支持带空格的路径，用户名带空格就废了。
REM   - robocopy 返回码 0-7 都算成功，8 起才是真出错，不能照 xcopy 判 errorlevel 1。
REM ============================================================

title UE Mod Manager - 一键迁移老版本数据

echo.
echo ============================================================
echo   UE Mod Manager - 一键迁移老版本数据
echo ============================================================
echo.
echo   这个脚本会做两件事：
echo.
echo     1. 把你的设置、MOD 清单、方案、分类复制一份留底
echo     2. 启动管理器，让它把老数据搬到新位置
echo.
echo   全程不会动你的游戏目录，也不会删任何东西。
echo   万一迁移出问题，留底还在，照着说明文件放回去就行。
echo.
echo ============================================================
echo.

REM ── 找出三处数据在哪（只读，不改任何东西） ──────────
set "ROAMING_DIR=%APPDATA%\UEModManager"
set "LOCAL_DIR=%LOCALAPPDATA%\UEModManager"

set "SCRIPT_DIR=%~dp0"
if "!SCRIPT_DIR:~-1!"=="\" set "SCRIPT_DIR=!SCRIPT_DIR:~0,-1!"

REM 老版本把数据写在 exe 旁边。安装目录可能在好几个位置，逐个试。
set "INSTALL_DIR="
call :TryInstallDir "!SCRIPT_DIR!" require-exe
call :TryInstallDir "%LOCALAPPDATA%\Programs\UEModManager"
call :TryInstallDir "%ProgramFiles%\UEModManager"
call :TryInstallDir "%ProgramFiles(x86)%\UEModManager"

set "HAS_ANY="
if exist "!ROAMING_DIR!\" set "HAS_ANY=1"
if exist "!LOCAL_DIR!\" set "HAS_ANY=1"
if defined INSTALL_DIR set "HAS_ANY=1"
if not defined HAS_ANY goto NOTHING_TO_BACKUP

REM ── 读出用户自定义的大目录位置（只读，不改） ────────
set "UI_CONFIG=!ROAMING_DIR!\ui_config.json"
call :ReadJsonString "!UI_CONFIG!" RepositoryRoot CUSTOM_REPO
call :ReadJsonString "!UI_CONFIG!" OverwritesRoot CUSTOM_OVER
call :ReadJsonString "!UI_CONFIG!" BackupsRoot CUSTOM_BACKUPS

call :ResolveBigRoot "!CUSTOM_REPO!" Repository REPO_PATH
call :ResolveBigRoot "!CUSTOM_OVER!" Overwrites OVER_PATH
call :ResolveBigRoot "!CUSTOM_BACKUPS!" Backups BACKUPS_PATH

REM ── 决定备份放哪 ────────────────────────────────
call :ResolveStamp
set "BACKUP_BASE=%USERPROFILE%\UEModManager-backup-!STAMP!"
call :MakeUniquePath

REM ── 把打算做的事摊开给用户看 ────────────────────
echo [步骤 1/3] 准备留底。
echo.
echo   会备份（体积小，丢了就回不来）：
if exist "!LOCAL_DIR!\" echo     - !LOCAL_DIR!
if exist "!ROAMING_DIR!\" echo     - !ROAMING_DIR!
if defined INSTALL_DIR echo     - !INSTALL_DIR! 里的 Data\ UserData\ config.json
echo.
echo   不会备份（体积可能几十 GB，丢了还能重新导入或重新备份）：
echo     - MOD 包本体：     !REPO_PATH!
echo     - 部署生成物：     !OVER_PATH!
echo     - MOD 与部署备份： !BACKUPS_PATH!
if defined INSTALL_DIR if exist "!INSTALL_DIR!\Backups\" echo     - 旧版备份：       !INSTALL_DIR!\Backups
echo.
echo     这几个位置会记进备份目录里的说明文件，需要时照着找即可。
echo.
echo   留底放在：
echo     !BACKUP_BASE!
echo.
choice /c YN /n /m "确认开始？(Y=继续 / N=取消): "
if errorlevel 2 goto USER_CANCEL

REM ── 管理器还开着的话，local.db 之类的文件复制不出来 ──
tasklist /FI "IMAGENAME eq UEModManager.exe" 2>nul | findstr /I "UEModManager.exe" >nul
if errorlevel 1 goto NOT_RUNNING
echo.
echo [提醒] 管理器正在运行，它占着的文件复制不出来。
choice /c YN /n /m "现在关掉它再继续吗？(Y=关掉 / N=不关，可能有文件备份不全): "
if errorlevel 2 goto NOT_RUNNING
taskkill /IM UEModManager.exe /F >nul 2>nul
REM 进程退出后文件句柄释放要一点时间，等两秒再复制
ping -n 3 127.0.0.1 >nul 2>nul
echo        已关闭。
:NOT_RUNNING

REM ── 空间够不够 ──────────────────────────────────
call :CheckFreeSpace
if defined SPACE_ABORT goto USER_CANCEL

REM ── 开始复制 ────────────────────────────────────
echo.
echo [步骤 2/3] 正在复制，请稍候...
echo.

md "!BACKUP_BASE!" >nul 2>nul
if exist "!BACKUP_BASE!\" goto BACKUP_DIR_OK
echo [失败] 建不出备份目录：
echo        !BACKUP_BASE!
echo.
echo 可能是磁盘满了或者没有写入权限。原数据没有任何变动，
echo 处理完再跑一次就行。
echo.
pause
exit /b 1
:BACKUP_DIR_OK

set "LOGFILE=!BACKUP_BASE!\备份日志.txt"
echo UEModManager 备份日志 > "!LOGFILE!"

set "COPY_FAILED="
call :CopyTree "!LOCAL_DIR!" "!BACKUP_BASE!\Local" "本机数据（配置与 MOD 清单）"
call :CopyTree "!ROAMING_DIR!" "!BACKUP_BASE!\Roaming" "漫游数据（界面设置与账号）"
if not defined INSTALL_DIR goto INSTALL_COPIED
call :CopyTree "!INSTALL_DIR!\Data" "!BACKUP_BASE!\Install\Data" "旧版 MOD 清单"
call :CopyTree "!INSTALL_DIR!\UserData" "!BACKUP_BASE!\Install\UserData" "旧版头像等"
call :CopyFile "!INSTALL_DIR!\config.json" "!BACKUP_BASE!\Install" "旧版主配置"
:INSTALL_COPIED

call :WriteReadme

echo.
if defined COPY_FAILED goto COPY_PARTIAL
echo [完成] 留底做好了：
echo        !BACKUP_BASE!
echo.
echo        怎么放回去，写在里面的"请先读我.txt"。
goto BACKUP_FINISHED

:COPY_PARTIAL
echo [注意] 有内容没能复制完整，详情见：
echo        !LOGFILE!
echo.
echo        常见原因是文件被别的程序占着，或者磁盘空间不够。
echo        你的原数据仍然完好、没有被改动，管理器照常能用。
echo        建议先处理掉原因再跑一次本脚本，确认留底完整了再迁移。
echo.
choice /c YN /n /m "仍然继续启动管理器开始迁移吗？(Y=继续 / N=先不迁移): "
if errorlevel 2 goto STOP_AFTER_PARTIAL
goto BACKUP_FINISHED

:STOP_AFTER_PARTIAL
echo.
echo 已经停在这里，没有启动管理器，原数据也没有任何变动。
echo 不完整的留底留在：!BACKUP_BASE!
echo.
pause
exit /b 1

:BACKUP_FINISHED
set "BACKUP_DONE=1"
REM 备份目录在用户主文件夹里，不主动弹一下没几个人找得到
start "" explorer "!BACKUP_BASE!"
goto LAUNCH

:NOTHING_TO_BACKUP
echo [信息] 没有找到任何存档，说明你是第一次用，不需要迁移。
echo.
echo        找过这些位置：
echo          !ROAMING_DIR!
echo          !LOCAL_DIR!
echo          程序安装目录
echo.
echo        如果你确定装过老版本，把这个脚本复制到老版本的安装目录里
echo        再双击一次，它就能找到了。
echo.
pause
goto LAUNCH

:USER_CANCEL
echo.
echo 已取消，什么都没动。
echo.
pause
exit /b 0

REM ── 启动管理器 ──────────────────────────────────
:LAUNCH
echo.
if defined BACKUP_DONE echo [步骤 3/3] 启动 UE Mod Manager...
if not defined BACKUP_DONE echo [启动] 启动 UE Mod Manager...
echo.
echo         管理器启动时会自己把老数据搬到新位置，你不用管，照常用就行。
echo.

set "EXE="
if defined INSTALL_DIR if exist "!INSTALL_DIR!\UEModManager.exe" set "EXE=!INSTALL_DIR!\UEModManager.exe"
if not defined EXE if exist "!SCRIPT_DIR!\UEModManager.exe" set "EXE=!SCRIPT_DIR!\UEModManager.exe"
if not defined EXE if exist "%LOCALAPPDATA%\Programs\UEModManager\UEModManager.exe" set "EXE=%LOCALAPPDATA%\Programs\UEModManager\UEModManager.exe"
if not defined EXE if exist "%ProgramFiles%\UEModManager\UEModManager.exe" set "EXE=%ProgramFiles%\UEModManager\UEModManager.exe"
if not defined EXE if exist "%ProgramFiles(x86)%\UEModManager\UEModManager.exe" set "EXE=%ProgramFiles(x86)%\UEModManager\UEModManager.exe"
if defined EXE goto LAUNCH_EXE

echo [找不到] UEModManager.exe
echo.
echo 从开始菜单或桌面快捷方式启动管理器就行，效果一样。
if defined BACKUP_DONE echo 留底已经做好了，放在：!BACKUP_BASE!
echo.
pause
exit /b 0

:LAUNCH_EXE
echo 找到主程序：!EXE!
start "" "!EXE!"

echo.
echo ============================================================
echo   一切就绪
echo ============================================================
echo.
if defined BACKUP_DONE echo   - 留底位置：!BACKUP_BASE!
if defined BACKUP_DONE echo   - 确认用了一周没问题，可以手动删掉它释放空间
if not defined BACKUP_DONE echo   - 这次没有需要留底的数据
echo   - 管理器已经启动，正常用即可
echo.
echo   出问题？打开留底目录里的"请先读我.txt"照着放回去，
echo   或者联系开发者：mr.xzuo@foxmail.com
echo.
pause
exit /b 0


REM ============================================================
REM   子过程
REM ============================================================

:TryInstallDir
REM %~1 = 候选安装目录；%~2 = require-exe 时要求目录里确实有 UEModManager.exe。
REM 只认第一个命中的候选。
if defined INSTALL_DIR exit /b 0
set "CAND=%~1"
if "!CAND!"=="" exit /b 0
if not exist "!CAND!\" exit /b 0
REM 本脚本随安装包分发，也常被复制到桌面。桌面上恰好有个 Data 文件夹的人不在少数，
REM 所以拿脚本自身所在目录当安装目录时，必须看到 UEModManager.exe 才作数。
if /I "%~2"=="require-exe" if not exist "!CAND!\UEModManager.exe" exit /b 0
set "HIT="
if exist "!CAND!\Data\" set "HIT=1"
if exist "!CAND!\config.json" set "HIT=1"
if exist "!CAND!\UserData\" set "HIT=1"
if not defined HIT exit /b 0
set "INSTALL_DIR=!CAND!"
exit /b 0

:ResolveStamp
REM 三级降级，详见文件头的设计说明。
set "STAMP="
for /f "usebackq delims=" %%t in (`powershell -NoProfile -NonInteractive -Command "Get-Date -Format yyyyMMdd-HHmmss" 2^>nul`) do set "STAMP=%%t"
REM PowerShell 在受限环境里可能吐出一行提示而不是时间，格式对不上就当没拿到。
if defined STAMP echo(!STAMP!| findstr /r /c:"^[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]$" >nul || set "STAMP="
if defined STAMP exit /b 0

REM 退路 1：把 DATE / TIME 里的数字抠出来。顺序未必是年月日，唯一性够用。
set "RAW=%DATE%%TIME%"
set "CLEAN="
for /l %%i in (0,1,39) do (
    set "CH=!RAW:~%%i,1!"
    if not "!CH!"=="" for %%d in (0 1 2 3 4 5 6 7 8 9) do if "!CH!"=="%%d" set "CLEAN=!CLEAN!%%d"
)
REM 这一句不能省：变量没定义时 !CLEAN:~13,1! 展开出来的不是空串，而是字面量
REM ~13,1，下面两条长度判断会全部判反，最后拼出一个带冒号的非法目录名——
REM 和老版本 wmic 取不到值时栽的是同一个坑。
if not defined CLEAN goto NoStamp
if not "!CLEAN:~13,1!"=="" set "STAMP=!CLEAN:~0,8!-!CLEAN:~8,6!" & goto CheckStamp
if not "!CLEAN:~7,1!"=="" set "STAMP=!CLEAN!" & goto CheckStamp

REM 退路 2：连日期都取不到。目录重名由 MakeUniquePath 兜住。
:NoStamp
set "STAMP=no-timestamp"
exit /b 0

:CheckStamp
REM 最后再验一道：只放行数字、字母和连字符。上面任何一步出意外，也不能让
REM 冒号之类的非法字符进到目录名里去。
echo(!STAMP!| findstr /r /c:"^[-0-9A-Za-z][-0-9A-Za-z]*$" >nul || set "STAMP=no-timestamp"
exit /b 0

:MakeUniquePath
REM 目标已存在就往后加序号，绝不覆盖已有的留底。
set "UNIQ_BASE=!BACKUP_BASE!"
set /a UNIQ_N=1
:MU_LOOP
if not exist "!BACKUP_BASE!" exit /b 0
set /a UNIQ_N+=1
if !UNIQ_N! GTR 99 set "BACKUP_BASE=!UNIQ_BASE!-!RANDOM!" & exit /b 0
set "BACKUP_BASE=!UNIQ_BASE!-!UNIQ_N!"
goto :MU_LOOP

:ReadJsonString
REM %~1 = json 文件，%~2 = 键名，%~3 = 输出变量名。
REM ui_config.json 是缩进过的，一行一个键，够简单，不值得为它引一个 json 解析器。
REM 读不出来不当错误：调用方会退回默认位置。
set "%~3="
if not exist "%~1" exit /b 0
set "JV="
for /f "usebackq tokens=1,* delims=:" %%a in (`findstr /c:"%~2" "%~1" 2^>nul`) do set "JV=%%b"
if not defined JV exit /b 0
REM 去掉前导空格
for /l %%i in (1,1,8) do if defined JV if "!JV:~0,1!"==" " set "JV=!JV:~1!"
if not defined JV exit /b 0
REM 去掉行尾逗号
if "!JV:~-1!"=="," set "JV=!JV:~0,-1!"
if not defined JV exit /b 0
if /i "!JV!"=="null" exit /b 0
REM 剥掉包裹的引号。用 for 的 ~ 修饰符而不是字符串替换：路径里可能有 & 之类的
REM 字符，替换要写成不带引号的 set，那种写法一遇到 & 就语法错误。
for %%v in (!JV!) do set "JV=%%~v"
if not defined JV exit /b 0
REM json 里的反斜杠是转义过的
set "JV=!JV:\\=\!"
set "%~3=!JV!"
exit /b 0

:ResolveBigRoot
REM %~1 = 用户自定义值（可空），%~2 = 目录名，%~3 = 输出变量名。
REM 没自定义时按"新位置优先、其次老位置"报告，两处都没有就说明还没用到。
if not "%~1"=="" set "%~3=%~1" & exit /b 0
if exist "!LOCAL_DIR!\%~2\" set "%~3=!LOCAL_DIR!\%~2" & exit /b 0
if exist "!ROAMING_DIR!\%~2\" set "%~3=!ROAMING_DIR!\%~2" & exit /b 0
set "%~3=还没用到（默认会建在 !LOCAL_DIR!\%~2）"
exit /b 0

:CheckFreeSpace
REM 只是提个醒：跳过大目录之后备份通常只有几十 MB，空间几乎不可能不够。
REM 查不到就不查，别为了一个提示把脚本卡住。
set "SPACE_ABORT="
set "FREE_MB="
set "DRV=!BACKUP_BASE:~0,1!"
for /f "usebackq delims=" %%f in (`powershell -NoProfile -NonInteractive -Command "[int]((Get-PSDrive -Name '!DRV!').Free/1MB)" 2^>nul`) do set "FREE_MB=%%f"
if not defined FREE_MB exit /b 0
echo(!FREE_MB!| findstr /r /c:"^[0-9][0-9]*$" >nul || exit /b 0
echo   备份所在磁盘剩余 !FREE_MB! MB。
if !FREE_MB! GEQ 512 exit /b 0
echo.
echo   [提醒] 剩余空间偏少，备份可能做不完整。
choice /c YN /n /m "仍要继续吗？(Y=继续 / N=取消): "
if errorlevel 2 set "SPACE_ABORT=1"
exit /b 0

:CopyTree
REM %~1 = 源目录，%~2 = 目标目录，%~3 = 给用户看的名字。
if not exist "%~1\" echo   [跳过] 没有这个目录：%~1& exit /b 0
echo   正在复制 %~3 ...
robocopy "%~1" "%~2" /E /XJ /XD Repository Overwrites Backups /R:1 /W:2 /NFL /NDL /NJH /NJS /NP >> "!LOGFILE!" 2>&1
if errorlevel 8 set "COPY_FAILED=1" & echo   [失败] %~3& exit /b 0
echo   [完成] %~3
exit /b 0

:CopyFile
REM %~1 = 源文件，%~2 = 目标目录，%~3 = 给用户看的名字。
if not exist "%~1" echo   [跳过] 没有这个文件：%~1& exit /b 0
if not exist "%~2\" md "%~2" >nul 2>nul
copy /Y "%~1" "%~2\" >> "!LOGFILE!" 2>&1
if errorlevel 1 set "COPY_FAILED=1" & echo   [失败] %~3& exit /b 0
echo   [完成] %~3
exit /b 0

:WriteReadme
REM 注意：下面每个 >> 前面的空格都是必须的，否则行尾是数字时会被当成重定向句柄。
set "RM=!BACKUP_BASE!\请先读我.txt"
set "NOW="
for /f "usebackq delims=" %%t in (`powershell -NoProfile -NonInteractive -Command "Get-Date -Format 'yyyy-MM-dd HH:mm:ss'" 2^>nul`) do set "NOW=%%t"
if not defined NOW set "NOW=%DATE% %TIME%"

echo UE Mod Manager 数据留底 > "!RM!"
echo ============================================================ >> "!RM!"
echo. >> "!RM!"
echo 备份时间：!NOW! >> "!RM!"
echo 电脑名称：%COMPUTERNAME% >> "!RM!"
echo Windows 用户：%USERNAME% >> "!RM!"
echo. >> "!RM!"
echo 【备份了什么】 >> "!RM!"
echo 这里面是你的设置、MOD 清单、方案、分类、账号信息和日志， >> "!RM!"
echo 都是丢了就没法自动恢复的东西。对应关系： >> "!RM!"
echo. >> "!RM!"
echo   Local\    来自  !LOCAL_DIR! >> "!RM!"
echo   Roaming\  来自  !ROAMING_DIR! >> "!RM!"
if defined INSTALL_DIR echo   Install\  来自  !INSTALL_DIR!  ^(只取 Data\ UserData\ config.json^) >> "!RM!"
echo. >> "!RM!"
echo 【没备份什么，它们现在在哪】 >> "!RM!"
echo 下面这些是 MOD 包本体和文件副本，动辄几十 GB，复制一遍要跑很久、 >> "!RM!"
echo 还可能把磁盘塞满，所以没有放进这份留底。它们也不太需要留底： >> "!RM!"
echo 包本体重新导入就有，备份副本重新备份就有。 >> "!RM!"
echo 本脚本没有删过、动过它们，就在原地： >> "!RM!"
echo. >> "!RM!"
echo   MOD 包本体：     !REPO_PATH! >> "!RM!"
echo   部署生成物：     !OVER_PATH! >> "!RM!"
echo   MOD 与部署备份： !BACKUPS_PATH! >> "!RM!"
if defined INSTALL_DIR if exist "!INSTALL_DIR!\Backups\" echo   旧版备份：       !INSTALL_DIR!\Backups >> "!RM!"
echo. >> "!RM!"
echo 【怎么放回去】 >> "!RM!"
echo 1. 先完全退出 UE Mod Manager。 >> "!RM!"
echo 2. 把下面的目录整个复制回去，遇到同名文件选覆盖： >> "!RM!"
echo      本目录\Local\    复制回  !LOCAL_DIR! >> "!RM!"
echo      本目录\Roaming\  复制回  !ROAMING_DIR! >> "!RM!"
if defined INSTALL_DIR echo      本目录\Install\  复制回  !INSTALL_DIR! >> "!RM!"
echo 3. 重新打开管理器。 >> "!RM!"
echo. >> "!RM!"
echo 放回去之前想再留个后手的话，先把上面那几个目录改个名字。 >> "!RM!"
echo. >> "!RM!"
echo 有问题联系开发者：mr.xzuo@foxmail.com >> "!RM!"
exit /b 0
