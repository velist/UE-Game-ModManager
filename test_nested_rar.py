#!/usr/bin/env python
# -*- coding: utf-8 -*-

"""
测试嵌套RAR文件导入和解压功能
"""

import os
import sys
from pathlib import Path
from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager, extract_rar_with_winrar

def main():
    print("开始测试嵌套RAR文件导入和解压功能")
    
    # 初始化配置管理器
    config = ConfigManager()
    
    # 初始化MOD管理器
    mod_manager = ModManager(config)
    
    # 测试RAR文件路径
    rar_file = Path("doubletest.rar")
    if not rar_file.exists():
        print(f"错误: RAR文件不存在: {rar_file}")
        return
    
    print(f"找到RAR文件: {rar_file}")
    
    # 测试通过ModManager导入RAR文件
    print("\n===== 测试: 使用ModManager.import_mod导入嵌套RAR文件 =====")
    
    # 先清理可能存在的测试文件
    mods_path = Path(config.get_mods_path())
    for folder in ["SkinsuitEdit_P", "Lemi21_LatexBattlesuit_P"]:
        test_folder = mods_path / folder
        if test_folder.exists():
            import shutil
            shutil.rmtree(test_folder)
            print(f"清理已存在的测试文件夹: {test_folder}")
    
    # 导入测试
    result = mod_manager.import_mod(rar_file)
    print(f"导入结果: {result}")
    
    # 检查导入结果
    if isinstance(result, list):
        print(f"导入了 {len(result)} 个MOD:")
        for mod_info in result:
            print(f"\nMOD信息:")
            print(f" - name: {mod_info.get('name')}")
            print(f" - files: {mod_info.get('files')}")
            print(f" - original_path: {mod_info.get('original_path')}")
            print(f" - import_date: {mod_info.get('import_date')}")
            print(f" - enabled: {mod_info.get('enabled')}")
            print(f" - size: {mod_info.get('size')}")
            print(f" - folder_structure: {mod_info.get('folder_structure')}")
            
            # 检查MOD文件夹内容
            mod_name = mod_info.get('name')
            mod_folder = mods_path / mod_name
            print(f"\nMOD文件夹内容 ({mod_folder}):")
            if mod_folder.exists():
                for file in mod_folder.glob("*"):
                    print(f" - {file}")
            else:
                print(f"错误: MOD文件夹不存在: {mod_folder}")
    else:
        mod_info = result
        print(f"\nMOD信息:")
        print(f" - name: {mod_info.get('name')}")
        print(f" - files: {mod_info.get('files')}")
        print(f" - original_path: {mod_info.get('original_path')}")
        print(f" - import_date: {mod_info.get('import_date')}")
        print(f" - enabled: {mod_info.get('enabled')}")
        print(f" - size: {mod_info.get('size')}")
        print(f" - folder_structure: {mod_info.get('folder_structure')}")
        
        # 检查MOD文件夹内容
        mod_name = mod_info.get('name')
        mod_folder = mods_path / mod_name
        print(f"\nMOD文件夹内容 ({mod_folder}):")
        if mod_folder.exists():
            for file in mod_folder.glob("*"):
                print(f" - {file}")
        else:
            print(f"错误: MOD文件夹不存在: {mod_folder}")
    
    # 检查备份目录
    backup_path = Path(config.get_backup_path())
    print("\n备份目录内容:")
    for mod_dir in backup_path.glob("*"):
        if mod_dir.is_dir():
            print(f" - {mod_dir.name}:")
            for file in mod_dir.glob("*"):
                print(f"   - {file.name}")

if __name__ == "__main__":
    main() 