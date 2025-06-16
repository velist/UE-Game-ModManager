import os
import shutil
import zipfile
import subprocess
import sys
from pathlib import Path

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

def build_executable():
    """构建可执行文件"""
    # 确保PyInstaller已安装
    try:
        import PyInstaller
    except ImportError:
        print("正在安装PyInstaller...")
        subprocess.check_call([sys.executable, "-m", "pip", "install", "pyinstaller"])

    # 构建命令
    build_cmd = [
        "pyinstaller",
        "--onefile",  # 单文件模式
        "--windowed",  # 无控制台窗口
        "--name", "剑星MOD管理器_v1.6.1",  # 输出文件名
        "--icon=icons/app.ico",  # 使用app.ico作为图标
        "--add-data", "icons;icons",  # 添加图标资源
        "--add-data", "ui/style.qss;ui",  # 添加样式表
        "--add-data", "config.json;.",  # 添加配置文件
        "main.py"  # 主程序
    ]

    # 执行构建
    print("正在构建可执行文件...")
    subprocess.check_call(build_cmd)

    # 创建发布目录
    if not os.path.exists("release"):
        os.mkdir("release")

    # 复制可执行文件到发布目录
    dist_file = os.path.join("dist", "剑星MOD管理器_v1.6.1.exe")
    release_file = os.path.join("release", "剑星MOD管理器_v1.6.1.exe")

    if os.path.exists(dist_file):
        shutil.copy(dist_file, release_file)
        print(f"可执行文件已复制到: {release_file}")
    else:
        print("构建失败，未找到可执行文件")

    # 保留build和dist文件夹
    print("保留build和dist文件夹，以便调试")

    print("构建完成!")

def create_release_package():
    """创建发布包"""
    print("开始创建发布包...")

    # 版本信息
    version = "1.6.1"
    release_name = f"剑星MOD管理器_{version}_修复版"

    # 创建发布目录
    release_dir = os.path.join(os.getcwd(), "release")
    if not os.path.exists(release_dir):
        os.makedirs(release_dir)

    # 临时目录用于准备打包的文件
    temp_dir = os.path.join(os.getcwd(), "_temp_release")
    if os.path.exists(temp_dir):
        shutil.rmtree(temp_dir)
    os.makedirs(temp_dir)

    # 复制源代码文件
    source_files = ["main.py", "config.json", "README.md", "requirements.txt", "build_new.py"]
    for file in source_files:
        if os.path.exists(file):
            shutil.copy2(file, os.path.join(temp_dir, file))
            print(f"已复制: {file}")

    # 复制目录
    source_dirs = ["utils", "ui", "icons"]
    for dir_name in source_dirs:
        if os.path.exists(dir_name):
            shutil.copytree(dir_name, os.path.join(temp_dir, dir_name))
            print(f"已复制目录: {dir_name}")

    # 打包为zip
    release_zip = os.path.join(release_dir, f"{release_name}_源码.zip")
    with zipfile.ZipFile(release_zip, 'w') as zipf:
        for root, dirs, files in os.walk(temp_dir):
            for file in files:
                file_path = os.path.join(root, file)
                arc_name = os.path.relpath(file_path, temp_dir)
                zipf.write(file_path, arc_name)

    # 清理临时目录
    shutil.rmtree(temp_dir)
    print("临时目录已清理")

    print(f"发布包创建完成: {release_zip}")

    # 打印修复内容
    print("\n本次修复内容:")
    print("1. 修复了默认分类重命名后MOD无法正常显示的问题")
    print("2. 改进了分类顺序，确保按创建时间自上而下排序，默认分类始终在最前面")
    print("3. 修复了拖拽后导致部分分类消失的问题")
    print("4. 实现了MOD可以通过拖拽或右键菜单移动到指定分类")
    print("5. 实现了C2区拖拽至目标分类后，直接定位到目标分类并选中已拖拽的MOD")
    print("6. 在C2区的编辑模式下显示复选框，支持批量操作")
    print("7. 修复了勾选复选框批量多选后，拖拽只对单个MOD生效的问题")
    print("8. 首次探索到SB-Win64-Shipping.exe时，设为游戏启动按钮并弹窗提醒用户")
    print("9. 探索到steam\\steamapps\\common\\StellarBlade\\SB\\Content\\Paks\\~mods路径时，设为MOD存放路径并弹窗提醒用户")
    print("10. 修复了配置文件保存问题，确保设置能够正确保存并在重启后保留")

if __name__ == "__main__":
    # 创建备份目录
    create_backup_dir()

    # 创建空的配置文件
    create_empty_config()

    # 构建可执行文件
    build_executable()

    print("打包成功，输出目录: dist/剑星MOD管理器_v1.6.1.exe")

    # 创建发布包
    create_release_package() 