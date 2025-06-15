#!/usr/bin/env python
# -*- coding: utf-8 -*-

"""
测试RAR文件导入和解压功能
"""

import os
import sys
from pathlib import Path
from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager, extract_rar_with_winrar

def main():
    print("开始测试RAR文件导入和解压功能")
    
    # 初始化配置管理器
    config = ConfigManager()
    
    # 初始化MOD管理器
    mod_manager = ModManager(config)
    
    # 测试RAR文件路径
    rar_file = Path("Cow Bikini-187-1-1-1749597977.rar")
    if not rar_file.exists():
        print(f"错误: RAR文件不存在: {rar_file}")
        return
    
    print(f"找到RAR文件: {rar_file}")
    
    # 测试直接解压RAR文件
    print("\n===== 测试1: 使用extract_rar_with_winrar函数解压RAR文件 =====")
    test_dir = Path("test_extract")
    test_dir.mkdir(exist_ok=True)
    
    result = extract_rar_with_winrar(rar_file, test_dir)
    print(f"解压结果: {result}")
    
    if result:
        print("解压后的文件列表:")
        for item in test_dir.glob("**/*"):
            if item.is_file():
                print(f" - {item}")
    
    # 测试通过ModManager导入RAR文件
    print("\n===== 测试2: 使用ModManager.import_mod导入RAR文件 =====")
    try:
        mod_info = mod_manager.import_mod(rar_file)
        print(f"导入结果: {mod_info}")
        
        if mod_info:
            print("\nMOD信息:")
            for key, value in mod_info.items():
                print(f" - {key}: {value}")
            
            # 检查MOD目录
            mods_path = Path(config.get_mods_path())
            mod_folder = mods_path / mod_info['name']
            
            print(f"\nMOD文件夹内容 ({mod_folder}):")
            if mod_folder.exists():
                for item in mod_folder.glob("**/*"):
                    if item.is_file():
                        print(f" - {item}")
            else:
                print(f"MOD文件夹不存在: {mod_folder}")
    except Exception as e:
        print(f"导入失败: {e}")

if __name__ == "__main__":
    main() 