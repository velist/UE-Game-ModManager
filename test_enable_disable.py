#!/usr/bin/env python
# -*- coding: utf-8 -*-

from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager
from pathlib import Path
import os
import sys
import time

def main():
    print("开始测试MOD启用和禁用功能")
    
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
    
    # 清理无效MOD
    removed = config.clean_invalid_mods()
    print(f"清理了 {removed} 个无效MOD记录")
    
    # 获取所有MOD
    mods = config.get_mods()
    print(f"当前MOD总数: {len(mods)}")
    
    if not mods:
        print("没有找到任何MOD，无法测试启用/禁用功能")
        return
    
    # 显示现有MOD信息
    print("\n现有MOD列表:")
    for i, (mod_id, mod_info) in enumerate(mods.items(), 1):
        enabled = "已启用" if mod_info.get('enabled', False) else "已禁用"
        print(f"{i}. {mod_id} - {enabled}")
        
    # 选择第一个MOD进行测试
    test_mod_id = list(mods.keys())[0]
    test_mod_info = mods[test_mod_id]
    print(f"\n选择MOD: {test_mod_id} 进行测试")
    print(f"MOD信息: {test_mod_info}")
    
    # 检查MOD当前状态
    is_enabled = test_mod_info.get('enabled', False)
    print(f"当前状态: {'已启用' if is_enabled else '已禁用'}")
    
    # 如果当前是启用状态，先禁用
    if is_enabled:
        print("\n测试禁用MOD...")
        try:
            result = mod_manager.disable_mod(test_mod_id)
            print(f"禁用结果: {result}")
            
            # 验证状态已更新
            updated_info = config.get_mods()[test_mod_id]
            print(f"更新后状态: {'已启用' if updated_info.get('enabled', False) else '已禁用'}")
            
            # 验证文件是否被移除
            for file_path in test_mod_info.get('files', []):
                full_path = Path(config.get_mods_path()) / file_path
                print(f"检查文件是否已移除: {full_path} - {'存在' if full_path.exists() else '不存在'}")
            
            # 等待一会儿再继续
            time.sleep(1)
        except Exception as e:
            print(f"禁用MOD出错: {str(e)}")
    
    # 测试启用MOD
    print("\n测试启用MOD...")
    try:
        result = mod_manager.enable_mod(test_mod_id, test_mod_info)
        print(f"启用结果: {result}")
        
        # 验证状态已更新
        updated_info = config.get_mods()[test_mod_id]
        print(f"更新后状态: {'已启用' if updated_info.get('enabled', False) else '已禁用'}")
        
        # 验证文件是否被创建
        for file_path in test_mod_info.get('files', []):
            full_path = Path(config.get_mods_path()) / file_path
            print(f"检查文件是否已创建: {full_path} - {'存在' if full_path.exists() else '不存在'}")
    except Exception as e:
        print(f"启用MOD出错: {str(e)}")
    
    print("\n测试完成")

if __name__ == "__main__":
    main() 