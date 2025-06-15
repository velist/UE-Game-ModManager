#!/usr/bin/env python
# -*- coding: utf-8 -*-

"""
检查配置信息
"""

import sys
from pathlib import Path
from utils.config_manager import ConfigManager

def main():
    # 将输出重定向到文件
    with open('config_check.log', 'w', encoding='utf-8') as f:
        sys.stdout = f
        
        print("检查配置信息")
        
        # 初始化配置管理器
        config = ConfigManager()
        
        # 检查配置文件路径
        print(f"配置文件路径: {config.config_file}")
        print(f"配置文件是否存在: {config.config_file.exists()}")
        
        # 检查游戏路径
        print(f"游戏路径: {config.get_game_path()}")
        
        # 检查MOD路径
        mods_path = config.get_mods_path()
        print(f"MOD路径: {mods_path}")
        print(f"MOD路径是否存在: {Path(mods_path).exists() if mods_path else False}")
        
        # 检查备份路径
        backup_path = config.get_backup_path()
        print(f"备份路径: {backup_path}")
        print(f"备份路径是否存在: {Path(backup_path).exists() if backup_path else False}")
        
        # 检查MOD列表
        mods = config.get_mods()
        print(f"MOD数量: {len(mods)}")
        for mod_id, mod_info in mods.items():
            print(f"\nMOD ID: {mod_id}")
            for key, value in mod_info.items():
                if key == 'files':
                    print(f"  - {key}: {len(value)} 个文件")
                    for file in value:
                        print(f"    - {file}")
                else:
                    print(f"  - {key}: {value}")
            
            # 检查备份文件
            if backup_path:
                mod_backup_dir = Path(backup_path) / mod_id
                print(f"  - 备份目录: {mod_backup_dir}")
                print(f"  - 备份目录是否存在: {mod_backup_dir.exists()}")
                if mod_backup_dir.exists():
                    backup_files = list(mod_backup_dir.glob('*'))
                    print(f"  - 备份文件数量: {len(backup_files)}")
                    for file in backup_files:
                        print(f"    - {file}")
    
    # 恢复标准输出
    sys.stdout = sys.__stdout__
    print(f"配置信息已保存到 config_check.log")

if __name__ == "__main__":
    main() 