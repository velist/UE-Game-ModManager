#!/usr/bin/env python
# -*- coding: utf-8 -*-

"""
测试MOD启用功能
"""

import os
import sys
from pathlib import Path
from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager

def main():
    print("开始测试MOD启用功能")
    
    # 初始化配置管理器
    config = ConfigManager()
    
    # 初始化MOD管理器
    mod_manager = ModManager(config)
    
    # 获取所有MOD
    mods = config.get_mods()
    print(f"找到 {len(mods)} 个MOD")
    
    if not mods:
        print("没有找到MOD，请先导入MOD")
        return
    
    # 测试启用第一个MOD
    mod_id = list(mods.keys())[0]
    mod_info = mods[mod_id]
    print(f"\n===== 测试: 启用MOD {mod_id} =====")
    print(f"MOD信息: {mod_info}")
    
    result = mod_manager.enable_mod(mod_id)
    print(f"启用结果: {result}")
    
    # 检查MOD是否已启用
    updated_mods = config.get_mods()
    if mod_id in updated_mods:
        print(f"MOD状态: {'已启用' if updated_mods[mod_id].get('enabled', False) else '已禁用'}")
    else:
        print(f"MOD {mod_id} 不存在")

if __name__ == "__main__":
    main() 