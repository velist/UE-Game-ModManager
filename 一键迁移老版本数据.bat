@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

REM ============================================================
REM   UE Mod Manager - 一键迁移老版本数据
REM ============================================================
REM   做什么：
REM   1) 自动备份 %APPDATA%\UEModManager 整个目录
REM   2) 启动管理器（如有老数据会自动弹"迁移向导"）
REM   3) 失败时备份保留，可随时还原
REM ============================================================

title UE Mod Manager - 一键迁移老版本数据

echo.
echo ============================================================
echo   UE Mod Manager - 一键迁移老版本数据
echo ============================================================
echo.
echo   这个脚本会做两件事：
echo.
echo     1. 把你现在的存档完整复制一份留底
echo     2. 启动管理器，让它自动检测并迁移老数据
echo.
echo   全程不会动你的游戏目录、不会删除任何老文件。
echo   即使迁移失败，备份仍在，可随时手工还原。
echo.
echo ============================================================
echo.

set "USERDATA=%APPDATA%\UEModManager"
for /f "tokens=2 delims==" %%a in ('wmic OS Get localdatetime /value 2^>nul') do set "DT=%%a"
set "STAMP=%DT:~0,8%-%DT:~8,6%"
set "BACKUP=%APPDATA%\UEModManager-backup-%STAMP%"

REM ── 步骤 1: 检查存档是否存在 ──────────────────────────
if not exist "%USERDATA%\" (
    echo [信息] 没有发现老存档目录：
    echo        %USERDATA%
    echo        说明你是首次使用，无需迁移。直接启动管理器即可。
    echo.
    pause
    goto LAUNCH
)

echo [步骤 1/3] 准备备份你的存档目录...
echo           源目录: %USERDATA%
echo           备份到: %BACKUP%
echo.
choice /c YN /n /m "确认开始？(Y=继续 / N=取消): "
if errorlevel 2 (
    echo.
    echo 已取消，未做任何操作。
    pause
    exit /b 0
)

REM ── 步骤 2: 复制备份 ────────────────────────────────
echo.
echo [步骤 2/3] 正在复制备份，请稍候...
xcopy "%USERDATA%" "%BACKUP%\" /E /I /H /Y /Q > "%TEMP%\uemm-migrate.log" 2>&1
if errorlevel 1 (
    echo.
    echo [失败] 备份过程出错，详细日志：
    echo        %TEMP%\uemm-migrate.log
    echo.
    echo 已中止。原数据未变动，可重试或联系开发者。
    pause
    exit /b 1
)

echo [完成] 备份成功。
echo.
echo        如需还原，把以下两个目录交换名字即可：
echo          %USERDATA%
echo          %BACKUP%
echo.

REM ── 步骤 3: 启动管理器 ───────────────────────────────
:LAUNCH
echo [步骤 3/3] 启动 UE Mod Manager...
echo.
echo           如果检测到老数据，程序会自动弹出"迁移向导"，按提示操作即可。
echo           5 步全自动：扫描 → 整理 → 迁移 → 校验 → 完成。
echo.

REM 尝试在多个可能位置启动主程序
set "EXE="
if exist "%~dp0UEModManager.exe" set "EXE=%~dp0UEModManager.exe"
if "%EXE%"=="" if exist "%ProgramFiles%\UEModManager\UEModManager.exe" set "EXE=%ProgramFiles%\UEModManager\UEModManager.exe"
if "%EXE%"=="" if exist "%ProgramFiles(x86)%\UEModManager\UEModManager.exe" set "EXE=%ProgramFiles(x86)%\UEModManager\UEModManager.exe"
if "%EXE%"=="" if exist "%LocalAppData%\Programs\UEModManager\UEModManager.exe" set "EXE=%LocalAppData%\Programs\UEModManager\UEModManager.exe"

if "%EXE%"=="" (
    echo [找不到] UEModManager.exe
    echo.
    echo 请把这个 .bat 文件放到管理器安装目录下再双击运行，
    echo 或手动启动管理器（开始菜单 / 桌面快捷方式）。
    echo.
    echo 提示：备份已完成，启动管理器后会自动弹迁移向导。
    pause
    exit /b 0
)

echo 找到主程序: %EXE%
echo.
start "" "%EXE%"

echo.
echo ============================================================
echo   一切就绪
echo ============================================================
echo.
echo   - 备份位置: %BACKUP%
echo   - 程序已启动，按提示完成迁移即可
echo   - 迁移成功一周后可手动删除备份目录释放空间
echo.
echo   出问题？把备份目录还原回 %USERDATA%
echo   或联系开发者：mr.xzuo@foxmail.com
echo.
pause
