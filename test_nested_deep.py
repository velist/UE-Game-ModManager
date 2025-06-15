#!/usr/bin/env python
# -*- coding: utf-8 -*-

import os
import sys
import shutil
from pathlib import Path
from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager

def main():
    print("开始测试深层嵌套压缩包导入功能")
    
    # 设置测试环境
    config = ConfigManager()
    config.set_game_path("D:\\Steam\\steamapps\\common\\StellarBlade\\SB.exe")
    config.set_mods_path("D:\\Steam\\steamapps\\common\\StellarBlade\\SB\\Content\\Paks\\~mods")
    config.set_backup_path("D:\\python\\mods111")
    
    # 创建MOD管理器
    mod_manager = ModManager(config)
    
    # 找到测试用的嵌套压缩包
    test_file = "doubletest.rar"
    if not os.path.exists(test_file):
        test_file = Path(config.get_backup_path()) / "doubletest.rar"
        if not os.path.exists(test_file):
            print(f"错误：找不到测试文件 {test_file}")
            return
    
    print(f"找到测试文件: {test_file}")
    
    # 清理之前的测试数据
    mod_ids = ["doubletest", "Latex Battlesuit", "SkinsuitEdit_P"]
    for mod_id in mod_ids:
        try:
            # 删除MOD目录
            mod_dir = Path(config.get_mods_path()) / mod_id
            if mod_dir.exists():
                print(f"清理MOD目录: {mod_dir}")
                shutil.rmtree(mod_dir)
            
            # 删除备份目录
            backup_dir = Path(config.get_backup_path()) / mod_id
            if backup_dir.exists():
                print(f"清理备份目录: {backup_dir}")
                shutil.rmtree(backup_dir)
            
            # 从配置中删除MOD
            if mod_id in config.get_mods():
                print(f"从配置中删除MOD: {mod_id}")
                config.remove_mod(mod_id)
        except Exception as e:
            print(f"清理失败: {e}")
    
    # 测试导入嵌套压缩包
    print("\n===== 测试: 导入嵌套压缩包 =====")
    try:
        # 导入MOD
        result = mod_manager.import_mod(test_file)
        
        # 检查结果类型
        if isinstance(result, list):
            print(f"成功导入 {len(result)} 个MOD:")
            for i, mod in enumerate(result):
                print(f"\nMOD {i+1}:")
                print(f"  - 名称: {mod.get('name')}")
                print(f"  - 文件: {mod.get('files')}")
                print(f"  - 是否嵌套: {mod.get('is_nested', False)}")
                if mod.get('is_nested', False):
                    print(f"  - 父压缩包: {mod.get('parent_archive')}")
                
                # 检查备份目录
                backup_dir = Path(config.get_backup_path()) / mod['name']
                if backup_dir.exists():
                    backup_files = list(backup_dir.glob("*"))
                    print(f"  - 备份文件数量: {len(backup_files)}")
                    for bf in backup_files:
                        print(f"    - {bf.name}")
                else:
                    print(f"  - 备份目录不存在: {backup_dir}")
                
                # 检查MOD目录
                mod_dir = Path(config.get_mods_path()) / mod['name']
                if mod_dir.exists():
                    mod_files = list(mod_dir.glob("*"))
                    print(f"  - MOD目录文件数量: {len(mod_files)}")
                    for mf in mod_files:
                        print(f"    - {mf.name}")
                else:
                    print(f"  - MOD目录不存在: {mod_dir}")
        else:
            print("导入结果不是列表，可能只有一个MOD:")
            mod = result
            print(f"  - 名称: {mod.get('name')}")
            print(f"  - 文件: {mod.get('files')}")
            print(f"  - 是否嵌套: {mod.get('is_nested', False)}")
            if mod.get('is_nested', False):
                print(f"  - 父压缩包: {mod.get('parent_archive')}")
            
            # 检查备份目录
            backup_dir = Path(config.get_backup_path()) / mod['name']
            if backup_dir.exists():
                backup_files = list(backup_dir.glob("*"))
                print(f"  - 备份文件数量: {len(backup_files)}")
                for bf in backup_files:
                    print(f"    - {bf.name}")
            else:
                print(f"  - 备份目录不存在: {backup_dir}")
            
            # 检查MOD目录
            mod_dir = Path(config.get_mods_path()) / mod['name']
            if mod_dir.exists():
                mod_files = list(mod_dir.glob("*"))
                print(f"  - MOD目录文件数量: {len(mod_files)}")
                for mf in mod_files:
                    print(f"    - {mf.name}")
            else:
                print(f"  - MOD目录不存在: {mod_dir}")
        
        # 测试禁用和启用
        print("\n===== 测试: 禁用和启用嵌套MOD =====")
        if isinstance(result, list):
            for mod in result:
                mod_id = mod['name']
                print(f"\n禁用MOD: {mod_id}")
                disable_result = mod_manager.disable_mod(mod_id)
                print(f"禁用结果: {disable_result}")
                
                # 检查MOD目录是否已清空
                mod_dir = Path(config.get_mods_path()) / mod_id
                if mod_dir.exists():
                    mod_files = list(mod_dir.glob("*"))
                    print(f"禁用后MOD目录文件数量: {len(mod_files)}")
                else:
                    print(f"MOD目录已删除: {mod_dir}")
                
                print(f"\n重新启用MOD: {mod_id}")
                enable_result = mod_manager.enable_mod(mod_id)
                print(f"启用结果: {enable_result}")
                
                # 检查MOD目录是否已恢复
                mod_dir = Path(config.get_mods_path()) / mod_id
                if mod_dir.exists():
                    mod_files = list(mod_dir.glob("*"))
                    print(f"启用后MOD目录文件数量: {len(mod_files)}")
                    for mf in mod_files:
                        print(f"  - {mf.name}")
                else:
                    print(f"MOD目录不存在: {mod_dir}")
        else:
            mod_id = result['name']
            print(f"\n禁用MOD: {mod_id}")
            disable_result = mod_manager.disable_mod(mod_id)
            print(f"禁用结果: {disable_result}")
            
            # 检查MOD目录是否已清空
            mod_dir = Path(config.get_mods_path()) / mod_id
            if mod_dir.exists():
                mod_files = list(mod_dir.glob("*"))
                print(f"禁用后MOD目录文件数量: {len(mod_files)}")
            else:
                print(f"MOD目录已删除: {mod_dir}")
            
            print(f"\n重新启用MOD: {mod_id}")
            enable_result = mod_manager.enable_mod(mod_id)
            print(f"启用结果: {enable_result}")
            
            # 检查MOD目录是否已恢复
            mod_dir = Path(config.get_mods_path()) / mod_id
            if mod_dir.exists():
                mod_files = list(mod_dir.glob("*"))
                print(f"启用后MOD目录文件数量: {len(mod_files)}")
                for mf in mod_files:
                    print(f"  - {mf.name}")
            else:
                print(f"MOD目录不存在: {mod_dir}")
        
    except Exception as e:
        print(f"测试失败: {e}")
    
    print("\n测试完成")

if __name__ == "__main__":
    main() 