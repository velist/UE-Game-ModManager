#!/usr/bin/env python
# -*- coding: utf-8 -*-

from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager
from pathlib import Path
import os
import sys

def main():
    print("开始测试MOD备份功能")
    
    # 初始化配置
    config = ConfigManager()
    print(f"当前备份路径: {config.get_backup_path()}")
    print(f"当前MOD路径: {config.get_mods_path()}")
    
    # 设置测试备份路径
    backup_path = Path("D:/cursor/1.5版本/1.53版本/B").absolute()
    print(f"设置测试备份路径: {backup_path}")
    config.set_backup_path(str(backup_path))
    
    # 创建MOD管理器
    mod_manager = ModManager(config)
    
    # 获取所有MOD
    mods = config.get_mods()
    print(f"当前MOD总数: {len(mods)}")
    
    if not mods:
        print("没有找到任何MOD，无法测试备份功能")
        return
    
    # 检查备份目录
    backup_path = Path(config.get_backup_path())
    print(f"检查备份目录: {backup_path}")
    if not backup_path.exists():
        print(f"创建备份目录: {backup_path}")
        backup_path.mkdir(parents=True, exist_ok=True)
    
    # 测试每个MOD的备份
    success_count = 0
    fail_count = 0
    
    for i, (mod_id, mod_info) in enumerate(mods.items(), 1):
        print(f"\n[{i}/{len(mods)}] 测试备份MOD: {mod_id}")
        print(f"MOD信息: {mod_info}")
        
        # 验证MOD文件是否存在
        mods_path = Path(config.get_mods_path())
        files_exist = []
        
        for file_path in mod_info.get('files', []):
            full_path = mods_path / file_path
            files_exist.append(full_path.exists())
            print(f"  - 文件 {full_path}: {'存在' if full_path.exists() else '不存在'}")
        
        if not any(files_exist):
            print(f"  警告: MOD {mod_id} 的所有文件都不存在，备份可能失败")
            
        # 备份MOD
        try:
            result = config.backup_mod(mod_id, mod_info)
            if result:
                print(f"  备份成功: {mod_id}")
                success_count += 1
            else:
                print(f"  备份失败: {mod_id}")
                fail_count += 1
        except Exception as e:
            print(f"  备份出错: {mod_id}, 错误: {str(e)}")
            fail_count += 1
    
    # 打印结果统计
    print(f"\n备份测试完成:")
    print(f"成功备份: {success_count}/{len(mods)}")
    print(f"备份失败: {fail_count}/{len(mods)}")
    
    # 检查备份文件夹中的文件
    backup_files = list(backup_path.glob("*"))
    print(f"\n备份目录中的文件夹数量: {len(backup_files)}")
    for i, backup_dir in enumerate(backup_files, 1):
        if backup_dir.is_dir():
            files = list(backup_dir.glob("*"))
            print(f"  {i}. {backup_dir.name}: {len(files)} 个文件")

if __name__ == "__main__":
    main() 