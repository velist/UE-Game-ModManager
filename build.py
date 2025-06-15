import PyInstaller.__main__
import os
import shutil
import zipfile
import subprocess
from pathlib import Path
import json

def create_backup_dir():
    """创建备份目录"""
    backup_dir = os.path.join(os.getcwd(), "modbackup")
    if not os.path.exists(backup_dir):
        os.makedirs(backup_dir)
        print(f"创建modbackup目录: {backup_dir}")
    return backup_dir

def backup_config():
    """备份配置文件"""
    config_file = os.path.join(os.getcwd(), "config.json")
    if os.path.exists(config_file):
        backup_file = f"{config_file}.bak"
        shutil.copy2(config_file, backup_file)
        print(f"备份当前config.json到: {backup_file}")
        
        # 删除原配置文件，避免被打包进去
        os.remove(config_file)
        print("删除当前config.json，避免被打包进去")
    
def restore_config():
    """恢复配置文件"""
    config_file = os.path.join(os.getcwd(), "config.json")
    backup_file = f"{config_file}.bak"
    if os.path.exists(backup_file):
        shutil.copy2(backup_file, config_file)
        os.remove(backup_file)
        print("恢复config.json备份")

def build_exe():
    """使用PyInstaller打包为exe"""
    print("开始打包...")
    cmd = ["pyinstaller", "--windowed", "--name=剑星MOD管理器", "--clean", "main.py"]
    result = subprocess.run(cmd, capture_output=True, text=True)
    
    if result.returncode == 0:
        print("打包完成")
        return True
    else:
        print(f"打包失败: {result.stderr}")
        return False

def create_release_package():
    """创建发布包"""
    print("开始创建发布包...")
    
    # 版本信息
    version = "1.59"
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
    source_files = ["main.py", "config.json", "README.md", "requirements.txt", "build.py", "test_backup_restore.py"]
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
    print("1. 修复了重命名MOD后重启应用名称恢复原状的问题")
    print("2. 修复了添加预览图后重启时重命名和图片未保留的问题")
    print("3. 修复了备份目录只备份导入图片的问题")
    print("4. 修复了C3区操作按钮点击无反应的问题")
    print("5. 修复了C1区卡片信息区域字段信息重复和颜色对比度问题")
    print("6. 修复了修改名称后重复备份的问题")
    print("7. 修复了重命名MOD后预览图路径不正确的问题") 
    print("8. 修复了启用/禁用MOD功能不稳定的问题")
    print("9. 修复了列表改名导致备份目录重复备份的问题")
    print("10. 修复了首次打开时备份不成功的问题")
    print("11. 修复了重命名MOD后预览图不显示的问题")
    print("12. 修复了选中新建的分类导入的MOD在刷新后移动至默认分类的问题")
    print("13. 修复了B区顶部删除按钮不起作用的问题")
    print("14. 修复了分类拖拽后可能导致MOD分类被重置的问题")
    print("15. 修复了默认分类被误操作拖拽到其他分类导致消失的问题")
    print("16. 修复了默认分类无法重命名的问题")
    print("17. 分类现在按创建时间排序，默认分类始终在最前面")
    print("18. 更新游戏可执行文件名为SB-Win64-Shipping.exe")

if __name__ == "__main__":
    # 创建备份目录
    create_backup_dir()
    
    # 备份配置文件
    backup_config()
    
    # 构建可执行文件
    if build_exe():
        # 恢复配置文件
        restore_config()
        print("打包成功，输出目录: D:\\cursor\\1.5版本\\1.53版本\\dist\\剑星MOD管理器")
        
        # 创建发布包
        create_release_package() 