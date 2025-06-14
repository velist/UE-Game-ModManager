import json
import os
import shutil
from pathlib import Path
import datetime
import zipfile

class ConfigManager:
    def __init__(self):
        self.config_dir = Path(os.getcwd())  # 程序根目录
        self.config_file = self.config_dir / "config.json"
        self.backup_dir = self.config_dir / "modbackup"  # 备份目录也在根目录
        self.config = self._load_config()
        self.backup_dir.mkdir(parents=True, exist_ok=True)
        
    def _load_config(self):
        """加载配置文件"""
        if not self.config_dir.exists():
            self.config_dir.mkdir(parents=True)
            
        if not self.config_file.exists():
            return {
                "game_path": "",
                "mods_path": "",
                "backup_path": "",
                "categories": ["默认分类"],
                "mods": {},
                "initialized": False
            }
            
        try:
            with open(self.config_file, 'r', encoding='utf-8') as f:
                return json.load(f)
        except:
            return {
                "game_path": "",
                "mods_path": "",
                "backup_path": "",
                "categories": ["默认分类"],
                "mods": {},
                "initialized": False
            }
            
    def _save_config(self):
        """保存配置到文件"""
        with open(self.config_file, 'w', encoding='utf-8') as f:
            json.dump(self.config, f, ensure_ascii=False, indent=4)
            
    def is_initialized(self):
        """检查是否已初始化"""
        return self.config.get("initialized", False)
        
    def set_game_path(self, path):
        """设置游戏可执行文件路径"""
        self.config["game_path"] = str(path)
        self.config["initialized"] = True
        self._save_config()
        
    def set_mods_path(self, path):
        """设置MOD文件夹路径"""
        self.config["mods_path"] = str(path)
        self._save_config()
        
    def set_backup_path(self, path):
        self.config["backup_path"] = str(path)
        self._save_config()
        
    def set_initialized(self, value=True):
        self.config["initialized"] = value
        self._save_config()
        
    def get_game_path(self):
        """获取游戏可执行文件路径"""
        return self.config.get("game_path", "")
        
    def get_mods_path(self):
        """获取MOD文件夹路径"""
        return self.config.get("mods_path", "")
        
    def get_backup_path(self):
        """获取备份目录路径"""
        return self.config.get("backup_path", "")
        
    def get_categories(self):
        """获取所有分类"""
        return self.config.get("categories", ["默认分类"])
        
    def add_category(self, name):
        """添加新分类"""
        if name not in self.config["categories"]:
            self.config["categories"].append(name)
            self._save_config()
            
    def rename_category(self, old_name, new_name):
        """重命名分类，并同步所有MOD的category字段"""
        if old_name in self.config["categories"] and new_name not in self.config["categories"]:
            idx = self.config["categories"].index(old_name)
            self.config["categories"][idx] = new_name
            # 同步所有MOD的category字段
            mods = self.config.get("mods", {})
            updated = 0
            for mod_id, mod_info in mods.items():
                if mod_info.get("category") == old_name:
                    mod_info["category"] = new_name
                    updated += 1
            if updated:
                print(f"[调试] rename_category: 同步更新了{updated}个MOD的分类字段")
            self._save_config()
            
    def delete_category(self, name):
        """删除分类"""
        if name != "默认分类" and name in self.config["categories"]:
            self.config["categories"].remove(name)
            self._save_config()
            
    def get_mods(self):
        """获取所有MOD信息"""
        return self.config.get("mods", {})
        
    def add_mod(self, mod_id, mod_info):
        """添加MOD信息，自动补充导入日期"""
        if 'import_date' not in mod_info:
            mod_info['import_date'] = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')
        # 确保使用mod_info中的name作为MOD ID
        actual_mod_id = mod_info.get('name', mod_id)
        self.config["mods"][actual_mod_id] = mod_info
        self._save_config()
        
    def remove_mod(self, mod_id):
        """删除MOD信息"""
        if mod_id in self.config["mods"]:
            del self.config["mods"][mod_id]
            self._save_config()
            
    def update_mod(self, mod_id, mod_info):
        """更新MOD信息"""
        if mod_id in self.config["mods"]:
            self.config["mods"][mod_id].update(mod_info)
            self._save_config()
            
    def backup_mod(self, mod_id, mod_info):
        """备份MOD文件：将相关文件复制到备份目录"""
        try:
            # 确保使用正确的MOD ID
            actual_mod_id = mod_info.get('name', mod_id)
            print(f"[调试] backup_mod: 开始备份MOD {actual_mod_id}")
            backup_path = Path(self.get_backup_path())
            if not backup_path.exists():
                print(f"[调试] backup_mod: 创建备份目录 {backup_path}")
                backup_path.mkdir(parents=True, exist_ok=True)
                
            # 创建MOD专属备份目录
            mod_backup_dir = backup_path / actual_mod_id
            if mod_backup_dir.exists():
                print(f"[调试] backup_mod: 清理已存在的备份目录 {mod_backup_dir}")
                shutil.rmtree(mod_backup_dir)
            mod_backup_dir.mkdir(parents=True, exist_ok=True)
            
            # 获取MOD目录路径
            mods_path = Path(self.get_mods_path())
            
            # 检查是否为虚拟MOD（RAR文件）
            is_virtual = mod_info.get('is_virtual', False)
            
            # 无论是虚拟MOD还是普通MOD，都从MOD目录复制所有文件到备份目录
            copied_count = 0
            for file_name in mod_info.get('files', []):
                try:
                    # 确保file_name是字符串
                    if not isinstance(file_name, str):
                        print(f"[调试] backup_mod: 文件名不是字符串 {file_name}，尝试转换")
                        file_name = str(file_name)
                        
                    src_file = mods_path / file_name
                    if src_file.exists():
                        # 目标文件路径（只保留文件名，不保留路径结构）
                        dest_file = mod_backup_dir / src_file.name
                        
                        print(f"[调试] backup_mod: 复制文件 {src_file} -> {dest_file}")
                        shutil.copy2(src_file, dest_file)
                        copied_count += 1
                    else:
                        print(f"[调试] backup_mod: 源文件不存在 {src_file}")
                except Exception as e:
                    print(f"[调试] backup_mod: 处理文件 {file_name} 时出错: {e}")
            
            # 如果没有复制任何文件，但是文件夹存在，则复制文件夹中的所有文件
            if copied_count == 0 and is_virtual:
                # 检查是否有文件夹
                if len(mod_info.get('files', [])) > 0:
                    first_file = mod_info['files'][0]
                    # 确保file_name是字符串
                    if not isinstance(first_file, str):
                        first_file = str(first_file)
                    
                    # 获取文件夹路径
                    if '/' in first_file:
                        folder_path = mods_path / first_file.split('/')[0]
                    else:
                        folder_path = mods_path / actual_mod_id
                    
                    # 如果文件夹存在，复制所有文件
                    if folder_path.exists() and folder_path.is_dir():
                        print(f"[调试] backup_mod: 复制文件夹中的所有文件 {folder_path} -> {mod_backup_dir}")
                        for src_file in folder_path.glob('**/*'):
                            if src_file.is_file():
                                # 目标文件路径（只保留文件名，不保留路径结构）
                                dest_file = mod_backup_dir / src_file.name
                                print(f"[调试] backup_mod: 复制文件 {src_file} -> {dest_file}")
                                shutil.copy2(src_file, dest_file)
                                copied_count += 1
            
            print(f"[调试] backup_mod: 备份完成，复制了 {copied_count} 个文件")
            
            # 检查是否已存在相同ID的MOD
            if actual_mod_id in self.config["mods"]:
                print(f"[调试] backup_mod: 更新现有MOD信息: {actual_mod_id}")
                self.update_mod(actual_mod_id, mod_info)
            else:
                print(f"[调试] backup_mod: 添加新MOD: {actual_mod_id}")
                self.add_mod(actual_mod_id, mod_info)
            
            return copied_count > 0
        except Exception as e:
            print(f"[调试] backup_mod: 备份失败 {str(e)}")
            # 清理失败的备份
            if 'mod_backup_dir' in locals() and mod_backup_dir.exists():
                try:
                    shutil.rmtree(mod_backup_dir)
                except:
                    pass
            # 不抛出异常，返回失败状态
            return False

    def restore_mod_from_backup(self, mod_id, mod_info):
        """从备份还原MOD文件"""
        try:
            # 确保使用正确的MOD ID
            actual_mod_id = mod_info.get('name', mod_id)
            print(f"[调试] restore_mod_from_backup: 开始还原MOD {actual_mod_id}")
            backup_path = Path(self.get_backup_path())
            mod_backup_dir = backup_path / actual_mod_id
            
            if not mod_backup_dir.exists():
                raise ValueError(f"备份目录不存在: {mod_backup_dir}")
            
            # 检查是否为虚拟MOD（RAR文件）
            is_virtual = mod_info.get('is_virtual', False)
            
            # 确保MOD目录存在
            mods_path = Path(self.get_mods_path())
            if not mods_path.exists():
                print(f"[调试] restore_mod_from_backup: 创建MOD目录 {mods_path}")
                mods_path.mkdir(parents=True, exist_ok=True)
            
            # 检查是否有子文件夹结构
            has_folder_structure = mod_info.get('folder_structure', False)
            print(f"[调试] restore_mod_from_backup: 子文件夹结构: {has_folder_structure}")
            
            # 创建目标目录
            target_dir = None
            if len(mod_info.get('files', [])) > 0:
                first_file = mod_info['files'][0]
                # 确保file_name是字符串
                if not isinstance(first_file, str):
                    first_file = str(first_file)
                
                print(f"[调试] restore_mod_from_backup: 第一个文件: {first_file}")
                
                # 解析目标路径
                if has_folder_structure and '\\' in first_file:
                    # Windows路径分隔符
                    sub_dir = first_file.split('\\')[0]
                    target_dir = mods_path / sub_dir
                    print(f"[调试] restore_mod_from_backup: 检测到Windows路径分隔符，子目录: {sub_dir}")
                elif has_folder_structure and '/' in first_file:
                    # Unix路径分隔符
                    sub_dir = first_file.split('/')[0]
                    target_dir = mods_path / sub_dir
                    print(f"[调试] restore_mod_from_backup: 检测到Unix路径分隔符，子目录: {sub_dir}")
                else:
                    # 没有子目录结构
                    target_dir = mods_path / actual_mod_id
                    print(f"[调试] restore_mod_from_backup: 没有检测到子目录结构")
                
                # 创建目标目录
                target_dir.mkdir(parents=True, exist_ok=True)
                print(f"[调试] restore_mod_from_backup: 创建目标目录: {target_dir}")
            else:
                # 如果没有文件列表，使用MOD ID作为目录名
                target_dir = mods_path / actual_mod_id
                target_dir.mkdir(parents=True, exist_ok=True)
                print(f"[调试] restore_mod_from_backup: 没有文件列表，创建目标目录: {target_dir}")
            
            # 从备份目录复制所有文件到目标目录
            print(f"[调试] restore_mod_from_backup: 从备份目录复制文件到 {target_dir}")
            copied_count = 0
            
            # 检查备份目录中的文件
            backup_files = list(mod_backup_dir.glob('*'))
            print(f"[调试] restore_mod_from_backup: 备份目录中有 {len(backup_files)} 个文件")
            
            # 复制备份目录中的所有文件
            for src_file in backup_files:
                if src_file.is_file():
                    print(f"[调试] restore_mod_from_backup: 处理文件 {src_file}")
                    # 如果有子文件夹结构，文件应该放在子文件夹中
                    if has_folder_structure:
                        # 从文件列表中找到对应的文件路径
                        file_matched = False
                        for file_path in mod_info.get('files', []):
                            if src_file.name in file_path:
                                file_matched = True
                                # 获取文件在子文件夹中的路径
                                if '\\' in file_path:
                                    rel_path = '\\'.join(file_path.split('\\')[1:]) if '\\' in file_path else file_path
                                else:
                                    rel_path = '/'.join(file_path.split('/')[1:]) if '/' in file_path else file_path
                                
                                # 创建子目录（如果需要）
                                if '\\' in rel_path or '/' in rel_path:
                                    sub_dir = (target_dir / rel_path).parent
                                    sub_dir.mkdir(parents=True, exist_ok=True)
                                
                                dest_file = target_dir / rel_path
                                print(f"[调试] restore_mod_from_backup: 复制文件 {src_file} -> {dest_file}")
                                shutil.copy2(src_file, dest_file)
                                copied_count += 1
                                break
                        
                        # 如果没有匹配的文件路径，直接复制到目标目录
                        if not file_matched:
                            dest_file = target_dir / src_file.name
                            print(f"[调试] restore_mod_from_backup: 未找到匹配路径，直接复制 {src_file} -> {dest_file}")
                            shutil.copy2(src_file, dest_file)
                            copied_count += 1
                    else:
                        # 没有子文件夹结构，直接复制到目标目录
                        dest_file = target_dir / src_file.name
                        print(f"[调试] restore_mod_from_backup: 复制文件 {src_file} -> {dest_file}")
                        shutil.copy2(src_file, dest_file)
                        copied_count += 1
            
            print(f"[调试] restore_mod_from_backup: 还原完成，复制了 {copied_count} 个文件")
            return copied_count > 0
            
        except Exception as e:
            print(f"[调试] restore_mod_from_backup: 还原失败 {str(e)}")
            raise ValueError(f"MOD还原失败: {str(e)}")

    def restore_mod(self, mod_id, mod_info):
        """还原MOD"""
        try:
            # 确保使用正确的MOD ID
            actual_mod_id = mod_info.get('name', mod_id)
            print(f"[调试] restore_mod: 开始还原MOD {actual_mod_id}")
            return self.restore_mod_from_backup(actual_mod_id, mod_info)
        except Exception as e:
            print(f"[调试] restore_mod: 还原失败 {str(e)}")
            raise ValueError(f"MOD还原失败: {str(e)}")

    def set_categories(self, categories):
        """设置分类顺序"""
        self.config["categories"] = categories
        self._save_config() 