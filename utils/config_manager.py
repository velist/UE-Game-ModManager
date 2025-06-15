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
        
    def set_categories(self, categories):
        """设置分类列表"""
        self.config["categories"] = categories
        self._save_config()
        
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
            # 获取配置的备份路径
            backup_path = Path(self.get_backup_path())
            print(f"[调试] backup_mod: 从配置获取的备份路径: {backup_path}, 类型: {type(backup_path)}")
            
            if not backup_path or str(backup_path).strip() == "":
                # 如果没有配置备份路径，使用默认的modbackup目录
                print(f"[调试] backup_mod: 未配置备份路径，使用默认备份目录")
                backup_path = self.config_dir / "modbackup"
            
            print(f"[调试] backup_mod: 最终使用的备份路径: {backup_path}, 绝对路径: {backup_path.absolute()}")
            
            if not backup_path.exists():
                print(f"[调试] backup_mod: 创建备份目录 {backup_path}")
                backup_path.mkdir(parents=True, exist_ok=True)
                
            # 创建MOD专属备份目录
            mod_backup_dir = backup_path / actual_mod_id
            print(f"[调试] backup_mod: MOD专属备份目录: {mod_backup_dir}, 绝对路径: {mod_backup_dir.absolute()}")
            
            if mod_backup_dir.exists():
                print(f"[调试] backup_mod: 清理已存在的备份目录 {mod_backup_dir}")
                shutil.rmtree(mod_backup_dir)
            
            # 获取MOD目录路径
            mods_path = Path(self.get_mods_path())
            print(f"[调试] backup_mod: MOD目录路径: {mods_path}, 绝对路径: {mods_path.absolute()}")
            
            # 确定MOD所在的目录
            mod_folder = None
            if len(mod_info.get('files', [])) > 0:
                first_file = mod_info['files'][0]
                # 确保file_name是字符串
                if not isinstance(first_file, str):
                    first_file = str(first_file)
                
                print(f"[调试] backup_mod: 第一个文件路径: {first_file}")
                
                # 获取文件夹路径
                if '/' in first_file:
                    folder_name = first_file.split('/')[0]
                    mod_folder = mods_path / folder_name
                    print(f"[调试] backup_mod: 使用'/'分隔符解析文件夹: {folder_name}, 完整路径: {mod_folder}")
                elif '\\' in first_file:
                    folder_name = first_file.split('\\')[0]
                    mod_folder = mods_path / folder_name
                    print(f"[调试] backup_mod: 使用'\\'分隔符解析文件夹: {folder_name}, 完整路径: {mod_folder}")
                else:
                    # 直接在mods目录下的文件
                    mod_folder = mods_path
                    print(f"[调试] backup_mod: 文件直接在mods目录下, 使用mods目录: {mod_folder}")
            
            # 如果找到了MOD文件夹，备份该文件夹中的所有文件
            copied_count = 0
            files_to_backup = []
            
            if mod_folder and mod_folder.exists() and mod_folder.is_dir():
                print(f"[调试] backup_mod: 备份文件夹 {mod_folder} 中的所有文件")
                print(f"[调试] backup_mod: 文件夹内容: {list(mod_folder.glob('*'))}")
                for src_file in mod_folder.glob('*'):
                    if src_file.is_file():
                        files_to_backup.append((src_file, src_file.name))
                        print(f"[调试] backup_mod: 找到文件 {src_file}")
            else:
                # 如果没有找到MOD文件夹，尝试按照文件列表备份
                print(f"[调试] backup_mod: 按照文件列表备份MOD文件, mod_folder={mod_folder}")
                if mod_folder:
                    print(f"[调试] backup_mod: mod_folder.exists()={mod_folder.exists()}, mod_folder.is_dir()={mod_folder.is_dir()}")
                
                for file_name in mod_info.get('files', []):
                    try:
                        # 确保file_name是字符串
                        if not isinstance(file_name, str):
                            print(f"[调试] backup_mod: 文件名不是字符串 {file_name}，尝试转换")
                            file_name = str(file_name)
                            
                        src_file = mods_path / file_name
                        print(f"[调试] backup_mod: 尝试按文件列表备份文件 {file_name}, 完整路径: {src_file}, 存在: {src_file.exists()}")
                        
                        if src_file.exists():
                            # 只获取文件名，不保留路径结构
                            dest_name = src_file.name
                            files_to_backup.append((src_file, dest_name))
                            print(f"[调试] backup_mod: 文件存在，将添加到备份列表: {src_file} -> {dest_name}")
                        else:
                            print(f"[调试] backup_mod: 源文件不存在 {src_file}")
                    except Exception as e:
                        print(f"[调试] backup_mod: 处理文件 {file_name} 时出错: {e}")
                        import traceback
                        traceback.print_exc()
            
            print(f"[调试] backup_mod: 备份文件列表准备完成，共有 {len(files_to_backup)} 个文件")
            
            # 只有当有文件要备份时才创建备份目录
            if files_to_backup:
                mod_backup_dir.mkdir(parents=True, exist_ok=True)
                
                # 复制所有文件
                for src_file, dest_name in files_to_backup:
                    dest_file = mod_backup_dir / dest_name
                    try:
                        print(f"[调试] backup_mod: 复制文件 {src_file} -> {dest_file}")
                        shutil.copy2(src_file, dest_file)
                        copied_count += 1
                        print(f"[调试] backup_mod: 复制成功")
                    except Exception as e:
                        print(f"[错误] backup_mod: 复制文件失败 {src_file} -> {dest_file}: {e}")
                        import traceback
                        traceback.print_exc()
            else:
                print(f"[警告] backup_mod: 没有找到任何文件可以备份，不创建空备份目录")
                print(f"[调试] backup_mod: MOD信息: {mod_info}")
                return False
            
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
            import traceback
            traceback.print_exc()
            # 清理失败的备份
            if 'mod_backup_dir' in locals() and mod_backup_dir.exists():
                try:
                    shutil.rmtree(mod_backup_dir)
                except:
                    pass
            # 不抛出异常，返回失败状态
            return False

    def clean_invalid_mods(self):
        """清理无效的MOD记录（没有实际文件和备份的MOD）"""
        print("[调试] clean_invalid_mods: 开始清理无效MOD记录")
        mods_to_remove = []
        mods = self.get_mods()
        backup_path = Path(self.get_backup_path())
        mods_path = Path(self.get_mods_path())
        
        # 如果备份路径不存在，创建它
        if not backup_path.exists():
            print(f"[调试] clean_invalid_mods: 创建备份目录 {backup_path}")
            backup_path.mkdir(parents=True, exist_ok=True)
        
        # 遍历所有MOD，检查文件是否存在
        for mod_id, mod_info in mods.items():
            files_exist = False
            backup_exists = False
            original_exists = False
            
            # 检查MOD文件是否存在
            if 'files' in mod_info:
                for file_path in mod_info.get('files', []):
                    full_path = mods_path / file_path
                    if full_path.exists():
                        print(f"[调试] clean_invalid_mods: MOD {mod_id} 文件存在: {full_path}")
                        files_exist = True
                        break
            
            # 如果MOD文件不存在，检查备份是否存在
            if not files_exist:
                # 检查原始文件是否存在
                if 'original_path' in mod_info:
                    orig_path = Path(mod_info['original_path'])
                    if orig_path.exists():
                        print(f"[调试] clean_invalid_mods: MOD {mod_id} 原始文件存在: {orig_path}")
                        original_exists = True
                
                # 检查备份是否存在
                # 列出所有可能的备份目录名
                base_names = [
                    mod_id,
                    mod_info.get('display_name', ''),
                    mod_info.get('mod_name', ''),
                    mod_info.get('folder_name', ''),
                    mod_info.get('name', ''),
                    mod_info.get('real_name', '')
                ]
                
                # 移除空字符串和重复项
                base_names = list(set([name for name in base_names if name]))
                
                # 检查每个可能的备份目录是否存在且不为空
                for name in base_names:
                    backup_dir = backup_path / name
                    if backup_dir.exists() and list(backup_dir.glob('*')):
                        print(f"[调试] clean_invalid_mods: MOD {mod_id} 备份存在: {backup_dir}")
                        backup_exists = True
                        break
            
            # 如果MOD文件、原始文件和备份都不存在，标记为删除
            if not files_exist and not backup_exists and not original_exists:
                print(f"[调试] clean_invalid_mods: MOD {mod_id} 无效（无文件也无备份），将标记为删除")
                mods_to_remove.append(mod_id)
        
        # 删除无效的MOD
        for mod_id in mods_to_remove:
            print(f"[调试] clean_invalid_mods: 删除无效MOD记录 {mod_id}")
            self.remove_mod(mod_id)
            
        # 保存更改
        self._save_config()
        print(f"[调试] clean_invalid_mods: 清理完成，共删除 {len(mods_to_remove)} 条无效记录")
        return len(mods_to_remove)

    def update_mod_file_paths(self, mod_id, new_paths=None):
        """更新MOD文件路径，用于修正错误的文件路径"""
        mod_info = self.config["mods"].get(mod_id)
        if not mod_info:
            return False
            
        # 如果没有提供新路径，根据folder_name重新生成
        if not new_paths:
            folder_name = mod_info.get('folder_name', '')
            if not folder_name:
                folder_name = mod_id
                
            new_paths = []
            for file_path in mod_info.get('files', []):
                # 检查路径是否已经包含文件夹名
                if folder_name and '/' in file_path and folder_name == file_path.split('/')[0]:
                    new_paths.append(file_path)
                elif folder_name and '\\' in file_path and folder_name == file_path.split('\\')[0]:
                    new_paths.append(file_path)
                else:
                    # 提取文件名部分
                    file_name = Path(file_path).name
                    # 使用folder_name作为目录
                    new_paths.append(f"{folder_name}/{file_name}")
        
        # 更新MOD信息
        mod_info['files'] = new_paths
        self.update_mod(mod_id, mod_info)
        return True
    
    def restore_mod_from_backup(self, mod_id, mod_info):
        """从备份还原MOD文件"""
        try:
            # 使用MOD ID作为备份目录名
            print(f"[调试] restore_mod_from_backup: 开始还原MOD {mod_id}, mod_info类型: {type(mod_info)}")
            backup_path = Path(self.get_backup_path())
            # 检查备份路径是否为空
            print(f"[调试] restore_mod_from_backup: 从配置获取的备份路径: {backup_path}, 类型: {type(backup_path)}")
            
            if not backup_path or not str(backup_path).strip():
                # 如果没有配置备份路径，使用默认的modbackup目录
                print(f"[调试] restore_mod_from_backup: 未配置备份路径，使用默认备份目录")
                backup_path = self.config_dir / "modbackup"
            
            print(f"[调试] restore_mod_from_backup: 最终使用的备份路径: {backup_path}, 绝对路径: {backup_path.absolute()}")
            
            # 确保文件路径格式正确
            self.update_mod_file_paths(mod_id)
            # 重新获取更新后的MOD信息
            mod_info = self.config["mods"].get(mod_id)
            
            # 验证备份目录存在
            if not backup_path.exists():
                print(f"[警告] restore_mod_from_backup: 备份目录不存在: {backup_path}")
                backup_path.mkdir(parents=True, exist_ok=True)
                return False
            
            # 确保MOD目录存在
            mods_path = Path(self.get_mods_path())
            print(f"[调试] restore_mod_from_backup: MOD目录路径: {mods_path}, 绝对路径: {mods_path.absolute()}")
            
            if not mods_path.exists():
                print(f"[调试] restore_mod_from_backup: 创建MOD目录 {mods_path}")
                mods_path.mkdir(parents=True, exist_ok=True)
            
            # 获取MOD文件列表和目标路径
            files = mod_info.get('files', [])
            folder_name = mod_info.get('folder_name', '')
            
            if not folder_name:
                folder_name = mod_id
            
            # 创建目标目录
            target_dir = mods_path / folder_name
            print(f"[调试] restore_mod_from_backup: 目标目录: {target_dir}, 绝对路径: {target_dir.absolute()}")
            target_dir.mkdir(parents=True, exist_ok=True)
            
            # 检查目标文件是否已经存在
            if files:
                all_exist = True
                for file_path in files:
                    file_name = Path(file_path).name
                    dest_file = target_dir / file_name
                    if not dest_file.exists():
                        all_exist = False
                        break
                
                if all_exist:
                    print(f"[调试] restore_mod_from_backup: 所有目标文件已存在，无需还原")
                    return True
            
            # 检查MOD源文件是否存在，如果存在则提取到目标目录
            if 'original_path' in mod_info and (path := Path(mod_info['original_path'])).exists():
                print(f"[调试] restore_mod_from_backup: 原始MOD文件存在: {path}")
                
                # 是压缩包，需要重新解压
                if path.suffix.lower() in ['.zip', '.rar', '.7z']:
                    print(f"[调试] restore_mod_from_backup: 原始文件是压缩包，尝试重新导入")
                    # 使用ModManager重新导入模块较复杂，这里不实现
                    # 继续尝试从备份恢复
                else:
                    # 是普通文件，直接复制到目标目录
                    dest_file = target_dir / path.name
                    if not dest_file.exists():
                        print(f"[调试] restore_mod_from_backup: 复制原始文件 {path} -> {dest_file}")
                        shutil.copy2(path, dest_file)
                        return True
            
            # 寻找可能的备份目录
            mod_backup_dir = None
            possible_dirs = []
            
            # 列出所有可能的备份目录名
            base_names = [
                mod_id,
                mod_info.get('display_name', ''),
                mod_info.get('mod_name', ''),
                mod_info.get('folder_name', ''),
                mod_info.get('name', ''),
                mod_info.get('real_name', '')
            ]
            
            # 移除空字符串和重复项
            base_names = list(set([name for name in base_names if name]))
            
            # 为每个基础名称创建可能的备份目录路径
            for name in base_names:
                possible_dirs.append(backup_path / name)
            
            print(f"[调试] restore_mod_from_backup: 可能的备份目录: {[str(d) for d in possible_dirs]}")
            
            # 检查每个可能的备份目录
            for dir_path in possible_dirs:
                if dir_path.exists() and list(dir_path.glob('*')):
                    mod_backup_dir = dir_path
                    print(f"[调试] restore_mod_from_backup: 找到有效的备份目录: {mod_backup_dir}")
                    break
            
            if not mod_backup_dir:
                print(f"[警告] restore_mod_from_backup: 没有找到有效的备份目录")
                print(f"[调试] restore_mod_from_backup: MOD信息: {mod_info}")
                print(f"[调试] restore_mod_from_backup: 所有可能的目录都不存在: {[str(d) for d in possible_dirs]}")
                return False
            
            # 确认备份目录中有文件
            backup_files = list(mod_backup_dir.glob('*'))
            print(f"[调试] restore_mod_from_backup: 备份目录中的文件: {backup_files}")
            
            if not backup_files:
                print(f"[警告] restore_mod_from_backup: 备份目录为空: {mod_backup_dir}")
                return False
            
            # 从备份目录复制所有文件到目标目录
            print(f"[调试] restore_mod_from_backup: 从备份目录复制文件到 {target_dir}")
            copied_count = 0
            
            # 复制备份目录中的所有文件
            for src_file in backup_files:
                if src_file.is_file():
                    try:
                        # 直接复制到目标目录
                        dest_file = target_dir / src_file.name
                        print(f"[调试] restore_mod_from_backup: 复制文件 {src_file} -> {dest_file}")
                        shutil.copy2(src_file, dest_file)
                        copied_count += 1
                    except Exception as e:
                        print(f"[错误] restore_mod_from_backup: 复制文件失败 {src_file} -> {dest_file}: {e}")
                        import traceback
                        traceback.print_exc()
            
            if copied_count == 0:
                print(f"[警告] restore_mod_from_backup: 没有复制任何文件")
                # 如果没有复制任何文件，清理创建的空目录
                if target_dir.exists() and not list(target_dir.glob('*')):
                    print(f"[调试] restore_mod_from_backup: 删除空目标目录 {target_dir}")
                    shutil.rmtree(target_dir)
                return False
            
            print(f"[调试] restore_mod_from_backup: 还原完成，复制了 {copied_count} 个文件")
            return True
            
        except Exception as e:
            print(f"[调试] restore_mod_from_backup: 还原失败 {str(e)}")
            import traceback
            traceback.print_exc()
            
            # 如果发生错误，清理可能创建的空目录
            if 'target_dir' in locals() and target_dir.exists() and not list(target_dir.glob('*')):
                shutil.rmtree(target_dir)
                print(f"[调试] restore_mod_from_backup: 删除错误创建的空目录 {target_dir}")
            raise ValueError(f"MOD还原失败: {str(e)}")

    def restore_mod(self, mod_id, mod_info):
        """还原MOD"""
        try:
            # 确保使用正确的MOD ID
            print(f"[调试] restore_mod: 开始还原MOD {mod_id}, mod_info={mod_info}")
            return self.restore_mod_from_backup(mod_id, mod_info)
        except Exception as e:
            print(f"[调试] restore_mod: 还原失败 {str(e)}")
            raise ValueError(f"MOD还原失败: {str(e)}") 