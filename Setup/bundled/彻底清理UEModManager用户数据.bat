@echo off
setlocal EnableExtensions EnableDelayedExpansion
REM ============================================================
REM  ENCODING WARNING - keep this file in GBK (codepage 936).
REM  Do NOT re-save it as UTF-8. cmd.exe miscounts line offsets in
REM  a batch file that mixes "chcp 65001" with non-ASCII text and
REM  starts executing the tail of a line as a command. It is not
REM  cosmetic: it truncated the SET statements below to empty
REM  strings, so the script deleted nothing at all.
REM  See the commit that introduced this banner for the repro.
REM ============================================================
chcp 936 >nul

title 彻底清理 UEModManager 用户数据

echo ============================================================
echo  爱酱MOD管理器 - 卸载后彻底清理脚本
echo ============================================================
echo.
echo 本脚本会清理当前 Windows 用户下的 UEModManager 数据：
echo   1. %APPDATA%\UEModManager
echo   2. %LOCALAPPDATA%\UEModManager
echo   3. 安装目录里 v2.0.5 以前留下的 Data\ / Backups\ / config.json / 日志
echo   4. 开始菜单/桌面快捷方式残留
echo   5. 开机自启注册表项
echo.
echo 说明：本脚本可以在卸载前直接运行，它会先结束正在运行的管理器。
echo 若想卸载之后再运行，请先把本文件复制到桌面——卸载会把安装目录里的
echo 它一起删掉。
echo 本脚本不会删除游戏目录里的 MOD 文件，也不会删除你在设置里
echo 自定义到其它磁盘的仓库/生成物/备份目录——那些位置记在
echo %APPDATA%\UEModManager\ui_config.json 里，本脚本删掉它之后就无从得知，
echo 如有请自行删除。
echo.

REM v2.0.5 起运行期数据统一在 %LOCALAPPDATA%\UEModManager；
REM 在那之前它们写在 exe 旁边，而 Inno 卸载器只删自己装过的文件，
REM 运行时生成的 Data\ / Backups\ / config.json / console*.log 一律留在原地。
REM 因此老机器上新旧两处都可能有残留，两处都要清。
set "ROAMING_DIR=%APPDATA%\UEModManager"
set "LOCAL_DIR=%LOCALAPPDATA%\UEModManager"
set "START_MENU_DIR=%APPDATA%\Microsoft\Windows\Start Menu\Programs\爱酱MOD管理器"
set "DESKTOP_LINK=%USERPROFILE%\Desktop\爱酱MOD管理器.lnk"
set "QUICK_LINK=%APPDATA%\Microsoft\Internet Explorer\Quick Launch\爱酱MOD管理器.lnk"

REM 脚本自身所在目录去掉结尾反斜杠，否则拼出的路径会带一个多余的 \
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

echo 将要清理：
echo   "%ROAMING_DIR%"
echo   "%LOCAL_DIR%"
echo   安装目录下的旧版残留（Data\ Backups\ UserData\ config.json 日志）
echo   "%START_MENU_DIR%"
echo   "%DESKTOP_LINK%"
echo   "%QUICK_LINK%"
echo.
choice /C YN /N /M "确认清理？输入 Y 继续，输入 N 取消："
if errorlevel 2 goto CANCEL

echo.
echo [1/6] 尝试关闭正在运行的 UEModManager...
taskkill /IM UEModManager.exe /F >nul 2>nul

echo [2/6] 删除用户数据目录...
call :DeleteDir "%ROAMING_DIR%"
call :DeleteDir "%LOCAL_DIR%"

echo [3/6] 删除安装目录里的旧版残留...
REM 只在能确认是安装目录时才动手：脚本自身目录必须还有 UEModManager.exe
REM （即在卸载前运行），否则脚本被复制到桌面时会把桌面上同名的 Data\ 一起删掉。
REM 其余三条是安装向导提供过的固定路径，路径本身已无歧义，不需要额外证据。
call :CleanInstallDir "%SCRIPT_DIR%" require-exe
call :CleanInstallDir "%LOCALAPPDATA%\Programs\UEModManager"
call :CleanInstallDir "%ProgramFiles%\UEModManager"
call :CleanInstallDir "%ProgramFiles(x86)%\UEModManager"

echo [4/6] 删除快捷方式残留...
call :DeleteDir "%START_MENU_DIR%"
call :DeleteFile "%DESKTOP_LINK%"
call :DeleteFile "%QUICK_LINK%"

echo [5/6] 删除开机自启注册表项...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "UEModManager" /f >nul 2>nul

echo [6/6] 清理完成。
echo.
echo 已完成 UEModManager 用户数据清理。
echo 若你当初把程序装在了上面没列到的自定义目录，请顺手删掉那里的
echo Data\、Backups\、UserData\、config.json 与 console*.log。
echo 现在可以重新安装新版安装包。
echo.
pause
exit /b 0

:CANCEL
echo.
echo 已取消，未清理任何文件。
pause
exit /b 1

:CleanInstallDir
REM %~1 = 候选安装目录，%~2 = "require-exe" 时要求目录内确实有 UEModManager.exe。
REM 只删精确命名的运行期产物，绝不整目录删：候选路径可能是用户的其它目录，
REM 而且卸载前运行时目录里还有程序文件，端掉整个目录只会把卸载器一并干掉。
set "APPDIR=%~1"
if "%APPDIR%"=="" exit /b 0
if not exist "%APPDIR%\" exit /b 0
if /I "%~2"=="require-exe" if not exist "%APPDIR%\UEModManager.exe" exit /b 0

echo   安装目录："%APPDIR%"
call :DeleteDir  "%APPDIR%\Data"
call :DeleteDir  "%APPDIR%\Backups"
call :DeleteDir  "%APPDIR%\UserData"
call :DeleteFile "%APPDIR%\config.json"
call :DeleteFile "%APPDIR%\console.log"
call :DeleteFile "%APPDIR%\XamlErrorTracking.log"
REM 轮转日志有几十份，逐个报告只会淹没上面几行，静默批删即可
del /F /Q "%APPDIR%\console_*.log" >nul 2>nul
exit /b 0

:DeleteDir
set "TARGET=%~1"
if exist "%TARGET%" (
    echo   删除目录："%TARGET%"
    rmdir /S /Q "%TARGET%" >nul 2>nul
    if exist "%TARGET%" (
        echo   [失败] 目录仍存在，可能被占用："%TARGET%"
    ) else (
        echo   [完成] 已删除
    )
) else (
    echo   [跳过] 不存在："%TARGET%"
)
exit /b 0

:DeleteFile
set "TARGET=%~1"
if exist "%TARGET%" (
    echo   删除文件："%TARGET%"
    del /F /Q "%TARGET%" >nul 2>nul
    if exist "%TARGET%" (
        echo   [失败] 文件仍存在，可能被占用："%TARGET%"
    ) else (
        echo   [完成] 已删除
    )
) else (
    echo   [跳过] 不存在："%TARGET%"
)
exit /b 0
