#!/usr/bin/env python
# -*- coding: utf-8 -*-

"""
测试MOD导入和启用功能
"""

import os
import sys
import shutil
from pathlib import Path
from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager

def print_dir_contents(path):
    """打印目录内容"""
    path = Path(path)
    files = list(path.glob('*'))
    return f"{len(files)} 个文件/文件夹"

def print_backup_folder_contents(path):
    """打印备份文件夹内容"""
    path = Path(path)
    if not path.exists():
        print(f"  {path.name}: 目录不存在")
        return
        
    files = [f for f in path.glob('*') if f.is_file()]
    print(f"  {path.name}: {len(files)} 个文件")
    for file in files:
        print(f"    - {file.name}")

def main():
    try:
        print("开始测试MOD导入和启用功能")
        
        # 初始化配置
        config = ConfigManager()
        mod_manager = ModManager(config)
        
        # 设置备份目录为当前目录下的B
        backup_path = Path(os.getcwd()) / "B"
        test_mod_file = "Cow Bikini-187-1-1-1749597977.rar"
        
        print(f"当前备份路径: {config.get_backup_path()}")
        print(f"当前MOD路径: {config.get_mods_path()}")
        
        # 设置备份路径
        print(f"设置测试备份路径: {backup_path}")
        config.set_backup_path(str(backup_path))
        
        # 清理备份目录
        print(f"清理测试备份目录: {backup_path}")
        if backup_path.exists():
            shutil.rmtree(backup_path)
        backup_path.mkdir(exist_ok=True)
        
        # 清理MOD目录
        mods_path = Path(config.get_mods_path())
        print(f"清理MOD目录: {mods_path}")
        for item in mods_path.glob('*'):
            if item.is_dir():
                shutil.rmtree(item)
            else:
                item.unlink()
                
        # 导入测试MOD
        print(f"\n导入测试MOD: {test_mod_file}")
        result = mod_manager.import_mod(test_mod_file)
        print(f"导入结果: {result}")
        
        # 导入可能返回列表或单个MOD
        mod_id = None
        if isinstance(result, list) and result:
            mod_id = result[0]['name']
        elif isinstance(result, dict):
            mod_id = result['name']
            
        print(f"导入的MOD ID: {mod_id}")
        if not mod_id:
            print("导入失败，未获取到MOD ID")
            return
            
        # 检查备份目录
        print("\n检查备份目录:")
        for subfolder in backup_path.glob('*'):
            if subfolder.is_dir():
                print_backup_folder_contents(subfolder)
                
        # 禁用所有MOD
        print("\n禁用所有MOD:")
        mods = config.get_mods()
        for m_id in mods:
            print(f"  禁用 {m_id}")
            mod_manager.disable_mod(m_id)
            # 标记为已禁用
            mods[m_id]['enabled'] = False
            
        # 检查MOD目录是否为空
        print("\n检查MOD目录:")
        print(f"MOD目录中有 {print_dir_contents(mods_path)}")
        
        # 重新尝试启用MOD
        print("\n启用导入的MOD:")
        mod_info = config.get_mods().get(mod_id)
        print(f"MOD信息: {mod_info}")
        
        # 检查备份是否存在
        backup_mod_dir = backup_path / mod_id
        print(f"检查备份目录: {backup_mod_dir}")
        if backup_mod_dir.exists():
            backup_files = [f for f in backup_mod_dir.glob('*') if f.is_file()]
            print(f"备份目录中有 {len(backup_files)} 个文件")
            for file in backup_files:
                print(f"  - {file.name}")
        else:
            print(f"备份目录不存在: {backup_mod_dir}")
            
        # 启用MOD
        print(f"启用MOD: {mod_id}")
        try:
            # 使用新的接口，只传递mod_id
            result = mod_manager.enable_mod(mod_id)
            if result:
                print(f"启用成功: {result}")
                # 检查MOD目录是否包含文件
                mod_folder = mods_path / mod_id
                if mod_folder.exists():
                    files = list(mod_folder.glob('*'))
                    print(f"MOD目录 {mod_folder} 包含 {len(files)} 个文件:")
                    for file in files:
                        print(f"  - {file.name}")
                else:
                    print(f"MOD文件夹不存在: {mod_folder}")
                    
                # 检查MOD记录是否标记为已启用
                updated_mod = config.get_mods().get(mod_id, {})
                print(f"MOD状态 - 已启用: {updated_mod.get('enabled', False)}")
            else:
                print("启用失败")
        except Exception as e:
            print(f"测试失败: {str(e)}")
            import traceback
            traceback.print_exc()
            
    except Exception as e:
        print(f"测试出现异常: {str(e)}")
        import traceback
        traceback.print_exc()
    
    print("\n测试完成")

if __name__ == "__main__":
    main() 