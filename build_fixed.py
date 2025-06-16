import os
import shutil
import zipfile
import subprocess
from pathlib import Path
import sys
from download_unrar import download_unrar_dll, download_unrar64_dll

def create_backup_dir():
    """创建备份目录"""
    backup_dir = os.path.join(os.getcwd(), "modbackup")
    if not os.path.exists(backup_dir):
        os.makedirs(backup_dir)
        print(f"创建modbackup目录: {backup_dir}")
    return backup_dir

def create_empty_config():
    """创建空的配置文件，确保它被包含在打包中"""
    config_file = os.path.join(os.getcwd(), "config.json")
    if not os.path.exists(config_file):
        with open(config_file, 'w', encoding='utf-8') as f:
            f.write('{}')
        print(f"创建空的配置文件: {config_file}")
    else:
        print(f"配置文件已存在: {config_file}")

def ensure_unrar_dll():
    """确保UnRAR.dll存在于libs目录中"""
    libs_dir = os.path.join(os.getcwd(), "libs")
    if not os.path.exists(libs_dir):
        os.makedirs(libs_dir)
        print(f"创建libs目录: {libs_dir}")
    
    # 确保32位UnRAR.dll存在
    unrar_dll_path = os.path.join(libs_dir, "UnRAR.dll")
    if not os.path.exists(unrar_dll_path):
        print("未找到UnRAR.dll，尝试下载...")
        if not download_unrar_dll():
            print("下载UnRAR.dll失败，请手动下载并放置在libs目录中")
            return False
    
    # 确保64位UnRAR.dll存在
    unrar64_dll_path = os.path.join(libs_dir, "UnRAR64.dll")
    if not os.path.exists(unrar64_dll_path):
        print("未找到UnRAR64.dll，尝试下载...")
        if not download_unrar64_dll():
            print("下载UnRAR64.dll失败，请手动下载并放置在libs目录中")
            # 不返回False，因为32位DLL已经存在
    
    return True

def build_exe():
    """使用PyInstaller构建可执行文件"""
    # 确保libs目录和UnRAR.dll存在
    if not ensure_unrar_dll():
        print("确保UnRAR.dll失败，构建中止")
        return False
    
    # 创建空的配置文件
    create_empty_config()
    
    # 检查图标文件
    icon_path = "icons/4.png"
    if not os.path.exists(icon_path):
        print(f"图标文件 {icon_path} 不存在，使用默认图标")
        icon_option = ""
    else:
        icon_option = f"--icon={icon_path}"
    
    # 构建命令
    cmd = [
        "pyinstaller",
        "--noconfirm",
        "--onefile",
        "--windowed",
    ]
    
    # 添加图标选项（如果存在）
    if icon_option:
        cmd.append(icon_option)
    
    # 添加其他选项
    cmd.extend([
        "--add-data=icons;icons",
        "--add-data=libs;libs",  # 添加libs目录
        "--add-data=ui/style.qss;ui",  # 添加样式表文件
        "--add-data=捐赠.png;.",  # 添加捐赠图片
        "--name=剑星MOD管理器_v1.6.3",
        "main.py"
    ])
    
    # 执行构建命令
    print("开始构建可执行文件...")
    process = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        shell=True,
        text=True
    )
    stdout, stderr = process.communicate()
    
    print(stdout)
    if stderr:
        print(f"构建错误: {stderr}")
    
    if process.returncode != 0:
        print("构建失败")
        return False
    
    # 复制可执行文件到release目录
    release_dir = os.path.join(os.getcwd(), "release")
    if not os.path.exists(release_dir):
        os.makedirs(release_dir)
    
    exe_path = os.path.join(os.getcwd(), "dist", "剑星MOD管理器_v1.6.3.exe")
    release_exe_path = os.path.join(release_dir, "剑星MOD管理器_v1.6.3.exe")
    
    if os.path.exists(exe_path):
        shutil.copy2(exe_path, release_exe_path)
        print(f"可执行文件已复制到: {release_exe_path}")
    else:
        print(f"可执行文件未找到: {exe_path}")
        return False
    
    print("保留build和dist文件夹，以便调试")
    
    return True

def create_source_package():
    """创建源代码包"""
    version = "1.6.3"
    release_dir = os.path.join(os.getcwd(), "release")
    if not os.path.exists(release_dir):
        os.makedirs(release_dir)
    
    zip_path = os.path.join(release_dir, f"剑星MOD管理器_{version}_修复版_源码.zip")
    
    print("开始创建发布包...")
    print("=" * 52)
    print(f"开始打包剑星MOD管理器 v{version}")
    print("=" * 52)
    
    # 读取README.md文件
    readme_path = os.path.join(os.getcwd(), "README.md")
    if os.path.exists(readme_path):
        with open(readme_path, 'r', encoding='utf-8') as f:
            readme_content = f.read()
        print("\n" + readme_content + "\n")
    
    # 创建临时目录
    temp_dir = os.path.join(os.getcwd(), "temp_package")
    if os.path.exists(temp_dir):
        shutil.rmtree(temp_dir)
    os.makedirs(temp_dir)
    
    # 复制源代码文件
    files_to_copy = [
        "main.py",
        "config.json",
        "README.md",
        "requirements.txt",
        "build_fixed.py",
        "download_unrar.py"
    ]
    
    for file in files_to_copy:
        if os.path.exists(file):
            shutil.copy2(file, os.path.join(temp_dir, file))
            print(f"已复制: {file}")
    
    # 复制目录
    dirs_to_copy = [
        "utils",
        "ui",
        "icons",
        "libs"  # 确保包含libs目录
    ]
    
    for dir_name in dirs_to_copy:
        if os.path.exists(dir_name):
            shutil.copytree(
                dir_name,
                os.path.join(temp_dir, dir_name),
                dirs_exist_ok=True
            )
            print(f"已复制目录: {dir_name}")
    
    # 创建ZIP文件
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as zipf:
        for root, _, files in os.walk(temp_dir):
            for file in files:
                file_path = os.path.join(root, file)
                arcname = os.path.relpath(file_path, temp_dir)
                zipf.write(file_path, arcname)
    
    # 清理临时目录
    shutil.rmtree(temp_dir)
    print("临时目录已清理")
    
    print(f"发布包创建完成: {zip_path}")
    return True

def main():
    """主函数"""
    # 构建可执行文件
    if build_exe():
        print("构建完成!")
        print(f"打包成功，输出目录: dist/剑星MOD管理器_v1.6.3.exe")
        
        # 创建源代码包
        create_source_package()
    else:
        print("构建失败!")
        return 1
    
    return 0

if __name__ == "__main__":
    sys.exit(main()) 