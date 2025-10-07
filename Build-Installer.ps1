Write-Host "=== 虚幻引擎MOD管理器 v1.7.38 安装程序构建 ===" -ForegroundColor Green

# 检查Inno Setup
$innoPath = "D:\安装\ISCC.exe"
if (!(Test-Path $innoPath)) {
    $innoPath = "D:\安装\Compil32.exe"
}

if (!(Test-Path $innoPath)) {
    Write-Host "❌ 错误：找不到Inno Setup" -ForegroundColor Red
    Write-Host "请安装 Inno Setup 6: https://jrsoftware.org/isdl.php"
    exit 1
}

Write-Host "✅ 找到Inno Setup: $innoPath" -ForegroundColor Green

# 检查必要文件
$exePath = "UEModManager\bin\Release\net8.0-windows\UEModManager.exe"
if (!(Test-Path $exePath)) {
    Write-Host "❌ 错误：找不到Release版本的exe文件" -ForegroundColor Red
    Write-Host "路径: $exePath"
    exit 1
}

Write-Host "✅ 找到主程序文件" -ForegroundColor Green

# 检查安装脚本
$issPath = "installer_clean.iss"
if (!(Test-Path $issPath)) {
    Write-Host "❌ 错误：找不到安装脚本文件: $issPath" -ForegroundColor Red
    exit 1
}

Write-Host "✅ 找到安装脚本" -ForegroundColor Green

# 创建输出目录
if (!(Test-Path "installer_output")) {
    New-Item -ItemType Directory -Path "installer_output" | Out-Null
    Write-Host "✅ 创建输出目录" -ForegroundColor Green
}

# 清理并复制最新编译的Release文件
$stagingDir = "installer_output\UEModManager_v1.7.38"
if (Test-Path $stagingDir) {
    Remove-Item -Path "$stagingDir\*" -Recurse -Force | Out-Null
    Write-Host "🧹 清理旧的打包文件" -ForegroundColor Yellow
} else {
    New-Item -ItemType Directory -Path $stagingDir | Out-Null
    Write-Host "✅ 创建打包目录" -ForegroundColor Green
}

Write-Host "📋 复制Release编译文件..." -ForegroundColor Yellow
Copy-Item -Path "UEModManager\bin\Release\net8.0-windows\*" -Destination $stagingDir -Recurse -Force | Out-Null
Write-Host "✅ 文件复制完成" -ForegroundColor Green

# 构建安装程序
Write-Host "🔨 开始构建安装程序..." -ForegroundColor Yellow
$process = Start-Process -FilePath $innoPath -ArgumentList $issPath -Wait -PassThru -NoNewWindow

if ($process.ExitCode -eq 0) {
    $installer = "installer_output\UEModManager_v1.7.38_Setup_Clean.exe"
    if (Test-Path $installer) {
        $size = [math]::Round((Get-Item $installer).Length / 1MB, 2)
        Write-Host "🎉 构建成功！" -ForegroundColor Green
        Write-Host "文件: $installer" -ForegroundColor Cyan
        Write-Host "大小: $size MB" -ForegroundColor Cyan
        
        # 显示文件信息
        $fileInfo = Get-ItemProperty $installer
        Write-Host "创建时间: $($fileInfo.CreationTime)" -ForegroundColor Gray
        Write-Host ""
        Write-Host "📋 安装程序特性:" -ForegroundColor Yellow
        Write-Host "  • 干净安装，程序文件与数据分离" -ForegroundColor White
        Write-Host "  • 自动显示捐赠引导图片" -ForegroundColor White
        Write-Host "  • 支持桌面图标和开机启动选项" -ForegroundColor White
        Write-Host "  • 数据保存在用户目录，避免权限问题" -ForegroundColor White
    } else {
        Write-Host "❌ 构建失败：找不到输出文件" -ForegroundColor Red
    }
} else {
    Write-Host "❌ 构建失败，退出代码: $($process.ExitCode)" -ForegroundColor Red
}

