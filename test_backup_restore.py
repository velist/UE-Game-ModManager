#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
测试MOD备份和还原功能
"""

import sys
import os
import json
import shutil
from pathlib import Path
from utils.config_manager import ConfigManager
from utils.mod_manager import ModManager

def test_backup_restore():
    """测试MOD备份和还原功能"""
    print("开始测试MOD备份和还原功能...")
    
    # 初始化配置管理器
    config = ConfigManager()
    mod_manager = ModManager(config)
    
    # 获取所有MOD
    mods = config.get_mods()
    print(f"当前有 {len(mods)} 个MOD")
    
    # 如果没有MOD，创建一个测试MOD
    if not mods:
        print("没有找到MOD，创建测试MOD...")
        test_mod_id = "test_mod"
        test_mod_info = {
            "name": test_mod_id,
            "display_name": "测试MOD",
            "files": ["test_mod/test.pak", "test_mod/test.ucas", "test_mod/test.utoc"],
            "enabled": True,
            "category": "默认分类",
            "import_date": "2023-01-01 00:00:00",
            "folder_name": "test_mod"
        }
        config.add_mod(test_mod_id, test_mod_info)
        print(f"创建测试MOD: {test_mod_id}")
        mods = config.get_mods()
    
    # 选择第一个MOD进行备份测试
    mod_id = list(mods.keys())[0]
    mod_info = mods[mod_id]
    print(f"选择MOD进行测试: {mod_id}")
    print(f"MOD信息: {json.dumps(mod_info, indent=2, ensure_ascii=False)}")
    
    # 获取备份路径
    backup_path = config.get_backup_path()
    if not backup_path:
        backup_path = os.path.join(os.getcwd(), "modbackup")
        config.set_backup_path(backup_path)
        print(f"设置备份路径: {backup_path}")
    else:
        print(f"当前备份路径: {backup_path}")
    
    # 确保备份目录存在
    backup_path = Path(backup_path)
    if not backup_path.exists():
        backup_path.mkdir(parents=True, exist_ok=True)
        print(f"创建备份目录: {backup_path}")
    
    # 备份MOD
    print(f"备份MOD: {mod_id}")
    result = config.backup_mod(mod_id, mod_info)
    if result:
        print(f"MOD备份成功")
    else:
        print(f"MOD备份失败")
        return
    
    # 检查备份目录
    mod_backup_dir = backup_path / mod_id
    if mod_backup_dir.exists():
        print(f"备份目录存在: {mod_backup_dir}")
        files = list(mod_backup_dir.glob("*"))
        print(f"备份文件数量: {len(files)}")
        for file in files:
            print(f"  - {file.name}")
    else:
        print(f"错误: 备份目录不存在: {mod_backup_dir}")
        return
    
    # 模拟MOD被删除
    print(f"模拟MOD被删除...")
    mods_path = Path(config.get_mods_path())
    mod_folder = None
    
    # 尝试找到MOD文件夹
    if mod_info.get('folder_name'):
        mod_folder = mods_path / mod_info.get('folder_name')
    elif len(mod_info.get('files', [])) > 0:
        first_file = mod_info['files'][0]
        if '/' in first_file:
            folder_name = first_file.split('/')[0]
            mod_folder = mods_path / folder_name
        elif '\\' in first_file:
            folder_name = first_file.split('\\')[0]
            mod_folder = mods_path / folder_name
    
    # 如果找到MOD文件夹，备份它然后删除
    original_files = []
    if mod_folder and mod_folder.exists():
        print(f"找到MOD文件夹: {mod_folder}")
        temp_backup = Path("temp_backup")
        temp_backup.mkdir(exist_ok=True)
        
        # 备份原始文件
        for file in mod_folder.glob("*"):
            if file.is_file():
                dest = temp_backup / file.name
                shutil.copy2(file, dest)
                original_files.append((file, dest))
                print(f"备份原始文件: {file} -> {dest}")
        
        # 删除MOD文件夹中的文件
        for file in mod_folder.glob("*"):
            if file.is_file():
                file.unlink()
                print(f"删除文件: {file}")
    else:
        print(f"未找到MOD文件夹，跳过删除步骤")
    
    # 还原MOD
    print(f"还原MOD: {mod_id}")
    result = config.restore_mod(mod_id, mod_info)
    if result:
        print(f"MOD还原成功")
    else:
        print(f"MOD还原失败")
    
    # 检查MOD文件是否已还原
    if mod_folder and mod_folder.exists():
        files = list(mod_folder.glob("*"))
        print(f"还原后的文件数量: {len(files)}")
        for file in files:
            print(f"  - {file.name}")
    
    # 恢复原始文件
    if original_files:
        print(f"恢复原始文件...")
        for orig_file, backup_file in original_files:
            if not orig_file.exists() and backup_file.exists():
                shutil.copy2(backup_file, orig_file)
                print(f"恢复原始文件: {backup_file} -> {orig_file}")
        
        # 清理临时备份
        if temp_backup.exists():
            shutil.rmtree(temp_backup)
            print(f"清理临时备份目录: {temp_backup}")
    
    print("测试完成")

if __name__ == "__main__":
    test_backup_restore() 