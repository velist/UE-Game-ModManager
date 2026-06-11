@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

title 彻底清理 UEModManager 用户数据

echo ============================================================
echo  爱酱MOD管理器 - 卸载后彻底清理脚本
echo ============================================================
echo.
echo 本脚本会清理当前 Windows 用户下的 UEModManager 数据：
echo   1. %APPDATA%\UEModManager
echo   2. %LOCALAPPDATA%\UEModManager
echo   3. 开始菜单/桌面快捷方式残留
echo   4. 开机自启注册表项
echo.
echo 注意：请先在“应用和功能”里卸载爱酱MOD管理器，再运行本脚本。
echo 本脚本不会删除游戏目录里的 MOD 文件、备份目录或 Steam 游戏文件。
echo.

set "ROAMING_DIR=%APPDATA%\UEModManager"
set "LOCAL_DIR=%LOCALAPPDATA%\UEModManager"
set "START_MENU_DIR=%APPDATA%\Microsoft\Windows\Start Menu\Programs\爱酱MOD管理器"
set "DESKTOP_LINK=%USERPROFILE%\Desktop\爱酱MOD管理器.lnk"
set "QUICK_LINK=%APPDATA%\Microsoft\Internet Explorer\Quick Launch\爱酱MOD管理器.lnk"

echo 将要清理：
echo   "%ROAMING_DIR%"
echo   "%LOCAL_DIR%"
echo   "%START_MENU_DIR%"
echo   "%DESKTOP_LINK%"
echo   "%QUICK_LINK%"
echo.
choice /C YN /N /M "确认清理？输入 Y 继续，输入 N 取消："
if errorlevel 2 goto CANCEL

echo.
echo [1/5] 尝试关闭正在运行的 UEModManager...
taskkill /IM UEModManager.exe /F >nul 2>nul

echo [2/5] 删除用户数据目录...
call :DeleteDir "%ROAMING_DIR%"
call :DeleteDir "%LOCAL_DIR%"

echo [3/5] 删除快捷方式残留...
call :DeleteDir "%START_MENU_DIR%"
call :DeleteFile "%DESKTOP_LINK%"
call :DeleteFile "%QUICK_LINK%"

echo [4/5] 删除开机自启注册表项...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "UEModManager" /f >nul 2>nul

echo [5/5] 清理完成。
echo.
echo 已完成 UEModManager 用户数据清理。
echo 现在可以重新安装新版安装包。
echo.
pause
exit /b 0

:CANCEL
echo.
echo 已取消，未清理任何文件。
pause
exit /b 1

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
