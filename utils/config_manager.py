import json
import os
import shutil
from pathlib import Path
import datetime
import zipfile
import time
import sys

class ConfigManager:
    def __init__(self):
        """初始化配置管理器"""
        # 配置目录和文件路径 - 总是使用当前工作目录
        self.config_dir = os.getcwd()
        self.config_file = os.path.join(self.config_dir, "config.json")
        print(f"[调试] ConfigManager初始化: 配置文件路径：{self.config_file}")
        
        # 初始化配置
        self.config = {}
        
        # 默认分类名称（可以被重命名）
        self.default_category_name = "默认分类"
        
        # 加载配置文件
        self._load_config()
        
        # 初始化分类创建时间戳
        if not hasattr(self, 'category_timestamps'):
            self.category_timestamps = {}
            
        # 确保默认分类存在并有时间戳
        if self.default_category_name not in self.category_timestamps:
            # 默认分类的时间戳设为最早，确保排在最前面
            self.category_timestamps[self.default_category_name] = 0
            
        # 确保备份路径存在
        backup_path = self.get_backup_path()
        if backup_path:
            backup_dir = Path(backup_path)
            if not backup_dir.exists():
                backup_dir.mkdir(parents=True, exist_ok=True)
                
        # 重置通知标志，确保每次重新初始化配置时都会显示提醒
        self.config['game_path_notified'] = False
        self.config['mods_path_notified'] = False
        
    def _load_config(self):
        """加载配置文件"""
        try:
            if os.path.exists(self.config_file):
                with open(self.config_file, 'r', encoding='utf-8') as f:
                    self.config = json.load(f)
                    print("[调试] _load_config: 成功加载配置文件")
                    
                    # 加载默认分类名称，如果存在
                    if "default_category_name" in self.config:
                        self.default_category_name = self.config["default_category_name"]
                        print(f"[调试] _load_config: 加载默认分类名称: {self.default_category_name}")
                    
                    # 加载分类时间戳，如果存在
                    if "category_timestamps" in self.config:
                        self.category_timestamps = self.config["category_timestamps"]
                    else:
                        # 初始化分类时间戳
                        self.category_timestamps = {}
                        # 为现有分类生成时间戳
                        for index, cat in enumerate(self.get_categories()):
                            if cat == self.default_category_name:
                                self.category_timestamps[cat] = 0  # 默认分类永远是最早的
                            else:
                                # 为其他分类生成递增的时间戳，确保顺序一致
                                self.category_timestamps[cat] = int(time.time()) + index
                                
                        # 保存时间戳到配置
                        self.config["category_timestamps"] = self.category_timestamps
                        self._save_config()
            else:
                # 创建默认配置
                self.config = {
                    "language": "zh",
                    "categories": [self.default_category_name],
                    "category_timestamps": {self.default_category_name: 0},
                    "default_category_name": self.default_category_name,
                    "mods_path": "",
                    "backup_path": "",
                    "game_path": "",
                    "mods": {}
                }
                self.category_timestamps = {self.default_category_name: 0}
                print("[调试] _load_config: 未找到配置文件，创建默认配置")
                self._save_config()
        except Exception as e:
            print(f"[错误] _load_config: 加载配置文件失败: {str(e)}")
            import traceback
            traceback.print_exc()
            # 创建默认配置
            self.config = {
                "language": "zh",
                "categories": [self.default_category_name],
                "category_timestamps": {self.default_category_name: 0},
                "default_category_name": self.default_category_name,
                "mods_path": "",
                "backup_path": "",
                "game_path": "",
                "mods": {}
            }
            self.category_timestamps = {self.default_category_name: 0}
            self._save_config()

    def _save_config(self):
        """保存配置到文件"""
        # 保存分类时间戳到配置
        self.config["category_timestamps"] = self.category_timestamps
        
        # 保存默认分类名称
        self.config["default_category_name"] = self.default_category_name
        
        try:
            # 保存配置
            with open(self.config_file, 'w', encoding='utf-8') as f:
                json.dump(self.config, f, ensure_ascii=False, indent=4)
                print(f"[调试] _save_config: 配置已保存到 {self.config_file}")
        except Exception as e:
            print(f"[错误] _save_config: 保存配置文件失败: {str(e)}")
            import traceback
            traceback.print_exc()
            
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
        
    def get(self, key, default=None):
        """通用方法：获取任意配置项"""
        return self.config.get(key, default)
        
    def set(self, key, value):
        """通用方法：设置任意配置项"""
        self.config[key] = value
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
        """获取所有分类，按创建时间排序"""
        categories = self.config.get("categories", [self.default_category_name])
        
        # 确保默认分类存在
        if self.default_category_name not in categories:
            categories.insert(0, self.default_category_name)
            
        # 确保所有分类都有时间戳
        for cat in categories:
            if cat not in self.category_timestamps:
                # 如果是默认分类，设置为最早时间戳
                if cat == self.default_category_name:
                    self.category_timestamps[cat] = 0
                else:
                    # 为其他分类设置当前时间戳
                    self.category_timestamps[cat] = int(time.time())
        
        # 按时间戳排序分类
        try:
            sorted_categories = sorted(categories, key=lambda x: self.category_timestamps.get(x, int(time.time())))
            return sorted_categories
        except Exception as e:
            print(f"[错误] get_categories: 排序分类失败: {e}")
            return categories
        
    def set_categories(self, categories):
        """设置分类列表"""
        # 确保默认分类始终存在
        if self.default_category_name not in categories:
            categories.insert(0, self.default_category_name)
        
        # 如果"默认分类"存在但不是当前默认分类名称，需要更新MOD的分类
        if "默认分类" in categories and "默认分类" != self.default_category_name:
            # 将所有使用"默认分类"的MOD更新为当前默认分类名称
            mods = self.config.get("mods", {})
            updated = 0
            for mod_id, mod_info in mods.items():
                if mod_info.get("category") == "默认分类":
                    mod_info["category"] = self.default_category_name
                    self.config["mods"][mod_id] = mod_info
                    updated += 1
            
            if updated:
                print(f"[调试] set_categories: 更新了 {updated} 个MOD的分类从 默认分类 到 {self.default_category_name}")
            
            # 从分类列表中移除旧的"默认分类"
            categories = [cat for cat in categories if cat != "默认分类"]
            
        # 保存原有分类列表，用于检测删除的分类
        old_categories = set(self.config.get("categories", [self.default_category_name]))
        new_categories = set(categories)
        
        # 检测已删除的分类，清理它们的时间戳
        deleted_categories = old_categories - new_categories
        for cat in deleted_categories:
            if cat in self.category_timestamps and cat != self.default_category_name:
                del self.category_timestamps[cat]
                print(f"[调试] set_categories: 删除分类时间戳: {cat}")
                
        # 更新分类时间戳，为新添加的分类创建时间戳
        current_time = int(time.time())
        for cat in categories:
            if cat not in self.category_timestamps:
                self.category_timestamps[cat] = current_time
                current_time += 1
                print(f"[调试] set_categories: 新增分类时间戳: {cat} = {current_time}")
                
        # 确保默认分类的时间戳是最小的
        if self.default_category_name in self.category_timestamps:
            self.category_timestamps[self.default_category_name] = 0
            
        # 确保默认分类始终在第一位
        if self.default_category_name in categories:
            categories.remove(self.default_category_name)
            categories.insert(0, self.default_category_name)
                
        # 按时间戳排序后保存
        sorted_categories = sorted(categories, key=lambda x: self.category_timestamps.get(x, current_time))
        self.config["categories"] = sorted_categories
        self._save_config()
        print(f"[调试] set_categories: 保存排序后的分类: {sorted_categories}")
        
    def add_category(self, name):
        """添加新分类"""
        if name not in self.config["categories"]:
            # 记录创建时间戳
            self.category_timestamps[name] = int(time.time())
            
            # 确保默认分类的时间戳是最小的
            self.category_timestamps["默认分类"] = 0
            
            # 添加分类到列表
            categories = self.config.get("categories", ["默认分类"])
            categories.append(name)
            
            # 按时间戳排序
            sorted_categories = sorted(categories, key=lambda x: self.category_timestamps.get(x, 0))
            self.config["categories"] = sorted_categories
            self._save_config()
            
    def rename_category(self, old_name, new_name):
        """重命名分类，并同步所有MOD的category字段"""
        print(f"[调试] rename_category: 开始重命名分类 {old_name} -> {new_name}")
        print(f"[调试] rename_category: 当前分类列表: {self.config['categories']}")
        print(f"[调试] rename_category: 当前默认分类名称: {self.default_category_name}")
        
        if old_name in self.config["categories"] and new_name not in self.config["categories"]:
            idx = self.config["categories"].index(old_name)
            print(f"[调试] rename_category: 找到分类 {old_name} 在索引 {idx}")
            
            # 如果是重命名默认分类，确保新名称成为新的默认分类
            if old_name == self.default_category_name:
                print(f"[调试] rename_category: 正在重命名默认分类 {old_name}")
                
                # 修改所有使用默认分类的MOD到新名称
                mods = self.config.get("mods", {})
                updated = 0
                for mod_id, mod_info in mods.items():
                    if mod_info.get("category") == old_name:
                        mod_info["category"] = new_name
                        self.config["mods"][mod_id] = mod_info
                        updated += 1
                        print(f"[调试] rename_category: 更新MOD {mod_id} 分类从 {old_name} 到 {new_name}")
                    # 同时处理"默认分类"的特殊情况
                    elif mod_info.get("category") == "默认分类" and old_name == self.default_category_name:
                        mod_info["category"] = new_name
                        self.config["mods"][mod_id] = mod_info
                        updated += 1
                        print(f"[调试] rename_category: 更新MOD {mod_id} 分类从 默认分类 到 {new_name}")
                
                if updated:
                    print(f"[调试] rename_category: 同步更新了{updated}个MOD的分类字段")
                
                # 将默认分类名称改为新名称
                self.config["categories"][idx] = new_name
                print(f"[调试] rename_category: 在分类列表中将 {old_name} 替换为 {new_name}")
                
                # 更新时间戳，确保新名称的时间戳是最早的（0），保持在第一位
                self.category_timestamps[new_name] = 0
                print(f"[调试] rename_category: 设置 {new_name} 的时间戳为 0")
                
                # 删除旧的默认分类时间戳
                if self.default_category_name in self.category_timestamps and self.default_category_name != new_name:
                    del self.category_timestamps[self.default_category_name]
                    print(f"[调试] rename_category: 删除旧默认分类 {self.default_category_name} 的时间戳")
                
                # 标记新名称为默认分类（用于set_categories方法）
                self.default_category_name = new_name
                print(f"[调试] rename_category: 默认分类名称已更新为 {new_name}")
                
                # 确保默认分类不会被重新添加
                print(f"[调试] rename_category: 修改后的分类列表: {self.config['categories']}")
                print(f"[调试] rename_category: 修改后的时间戳: {self.category_timestamps}")
            else:
                # 正常分类重命名
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
                
                # 更新时间戳
                self.category_timestamps[new_name] = self.category_timestamps.get(old_name, 0)
                # 删除旧分类的时间戳
                if old_name in self.category_timestamps:
                    del self.category_timestamps[old_name]
                    
            self._save_config()
            
    def delete_category(self, name):
        """删除分类"""
        if name != "默认分类" and name in self.config["categories"]:
            self.config["categories"].remove(name)
            
            # 删除分类的时间戳
            if name in self.category_timestamps:
                del self.category_timestamps[name]
                
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
            # 检查是否需要更改MOD ID（重命名）
            new_name = mod_info.get('name')
            if new_name and new_name != mod_id:
                # 如果有新名称且与当前ID不同，则更改MOD ID
                print(f"[调试] update_mod: 检测到MOD重命名 {mod_id} -> {new_name}")
                
                # 获取备份路径
                backup_path = self.get_backup_path()
                if not backup_path or not str(backup_path).strip():
                    backup_path = os.path.join(os.getcwd(), "modbackup")
                backup_path = Path(backup_path)
                
                # 旧备份目录
                old_backup_dir = backup_path / mod_id
                # 新备份目录
                new_backup_dir = backup_path / new_name
                
                # 如果旧备份目录存在，且新备份目录不存在或为空，则重命名备份目录
                if old_backup_dir.exists() and (not new_backup_dir.exists() or not list(new_backup_dir.glob('*'))):
                    try:
                        # 如果新备份目录已存在但为空，先删除它
                        if new_backup_dir.exists():
                            shutil.rmtree(new_backup_dir)
                        
                        # 重命名备份目录
                        print(f"[调试] update_mod: 重命名备份目录 {old_backup_dir} -> {new_backup_dir}")
                        shutil.move(str(old_backup_dir), str(new_backup_dir))
                    except Exception as e:
                        print(f"[警告] update_mod: 重命名备份目录失败: {e}")
                        # 如果重命名失败，尝试复制文件
                        try:
                            if not new_backup_dir.exists():
                                new_backup_dir.mkdir(parents=True, exist_ok=True)
                            
                            # 复制所有文件
                            for file in old_backup_dir.glob('*'):
                                if file.is_file():
                                    shutil.copy2(file, new_backup_dir / file.name)
                            
                            print(f"[调试] update_mod: 已复制备份文件到新目录 {new_backup_dir}")
                        except Exception as copy_err:
                            print(f"[错误] update_mod: 复制备份文件失败: {copy_err}")
                
                # 检查是否有预览图需要更新路径
                preview_image = mod_info.get('preview_image', '')
                if preview_image:
                    # 检查预览图是否在旧的备份目录中
                    if str(preview_image).startswith(str(old_backup_dir)):
                        # 确定新的预览图路径
                        preview_ext = os.path.splitext(preview_image)[1]
                        new_preview_path = new_backup_dir / f"preview{preview_ext}"
                        
                        # 更新预览图路径
                        print(f"[调试] update_mod: 更新预览图路径 {preview_image} -> {new_preview_path}")
                        mod_info['preview_image'] = str(new_preview_path)
                    else:
                        # 如果预览图不在旧备份目录中，检查新备份目录中是否有预览图
                        preview_files = list(new_backup_dir.glob("preview.*"))
                        if preview_files:
                            new_preview_path = str(preview_files[0])
                            print(f"[调试] update_mod: 在新备份目录中找到预览图: {new_preview_path}")
                            mod_info['preview_image'] = new_preview_path
                        else:
                            # 如果旧预览图存在，复制到新备份目录
                            if os.path.exists(preview_image):
                                preview_ext = os.path.splitext(preview_image)[1]
                                new_preview_path = new_backup_dir / f"preview{preview_ext}"
                                try:
                                    shutil.copy2(preview_image, new_preview_path)
                                    print(f"[调试] update_mod: 复制预览图到新备份目录: {preview_image} -> {new_preview_path}")
                                    mod_info['preview_image'] = str(new_preview_path)
                                except Exception as e:
                                    print(f"[警告] update_mod: 复制预览图失败: {e}")
                
                # 复制MOD信息到新ID
                self.config["mods"][new_name] = mod_info.copy()
                # 删除旧ID
                del self.config["mods"][mod_id]
                # 保存配置
                self._save_config()
                print(f"[调试] update_mod: MOD ID已更改，新配置: {self.config['mods'].keys()}")
                return
            
            # 正常更新MOD信息
            self.config["mods"][mod_id].update(mod_info)
            self._save_config()
            
    def set_mod_category(self, mod_id, category):
        """设置MOD的分类
        
        Args:
            mod_id: MOD的ID
            category: 要设置的分类名称
            
        Returns:
            bool: 是否成功设置
        """
        if mod_id not in self.config["mods"]:
            print(f"[警告] set_mod_category: MOD {mod_id} 不存在")
            return False
            
        try:
            # 获取MOD信息
            mod_info = self.config["mods"][mod_id]
            
            # 检查分类是否存在
            categories = self.get_categories()
            if category not in categories:
                print(f"[警告] set_mod_category: 分类 {category} 不存在，添加到分类列表")
                # 如果分类不存在，添加到分类列表
                self.add_category(category)
                
            # 更新MOD的分类
            old_category = mod_info.get('category', self.default_category_name)
            mod_info['category'] = category
            
            # 更新MOD信息
            self.config["mods"][mod_id] = mod_info
            self._save_config()
            
            print(f"[调试] set_mod_category: MOD {mod_id} 分类已从 {old_category} 更新为 {category}")
            return True
        except Exception as e:
            print(f"[错误] set_mod_category: 设置MOD分类失败: {e}")
            return False
            
    def backup_mod(self, mod_id, mod_info):
        """备份MOD文件：将相关文件复制到备份目录"""
        try:
            # 确保使用正确的MOD ID
            actual_mod_id = mod_info.get('name', mod_id)
            print(f"[调试] backup_mod: 开始备份MOD {actual_mod_id}")
            
            # 获取配置的备份路径
            backup_path = self.get_backup_path()
            print(f"[调试] backup_mod: 从配置获取的备份路径: {backup_path}, 类型: {type(backup_path)}")
            
            if not backup_path or str(backup_path).strip() == "":
                # 如果没有配置备份路径，使用默认的modbackup目录
                print(f"[调试] backup_mod: 未配置备份路径，使用默认备份目录")
                backup_path = os.path.join(os.getcwd(), "modbackup")
                # 更新配置中的备份路径
                self.set_backup_path(backup_path)
                print(f"[调试] backup_mod: 已更新配置中的备份路径: {backup_path}")
            
            # 确保备份路径是Path对象
            backup_path = Path(backup_path)
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
                
                # 创建MOD专属备份目录
                mod_backup_dir.mkdir(parents=True, exist_ok=True)
                
                for src_file in mod_folder.glob('*'):
                    if src_file.is_file():
                        files_to_backup.append((src_file, src_file.name))
                        print(f"[调试] backup_mod: 找到文件 {src_file}")
            else:
                # 如果没有找到MOD文件夹，尝试按照文件列表备份
                print(f"[调试] backup_mod: 按照文件列表备份MOD文件, mod_folder={mod_folder}")
                if mod_folder:
                    print(f"[调试] backup_mod: mod_folder.exists()={mod_folder.exists()}, mod_folder.is_dir()={mod_folder.is_dir()}")
                
                # 创建MOD专属备份目录
                mod_backup_dir.mkdir(parents=True, exist_ok=True)
                
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
            
            # 备份预览图（如果存在）
            preview_image = mod_info.get('preview_image', '')
            if preview_image and os.path.exists(preview_image):
                try:
                    preview_ext = os.path.splitext(preview_image)[1]
                    dest_preview = mod_backup_dir / f"preview{preview_ext}"
                    print(f"[调试] backup_mod: 复制预览图 {preview_image} -> {dest_preview}")
                    shutil.copy2(preview_image, dest_preview)
                    copied_count += 1
                except Exception as e:
                    print(f"[错误] backup_mod: 复制预览图失败: {e}")
            
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
            
            # 获取备份路径
            backup_path = self.get_backup_path()
            # 检查备份路径是否为空
            print(f"[调试] restore_mod_from_backup: 从配置获取的备份路径: {backup_path}, 类型: {type(backup_path)}")
            
            if not backup_path or not str(backup_path).strip():
                # 如果没有配置备份路径，使用默认的modbackup目录
                print(f"[调试] restore_mod_from_backup: 未配置备份路径，使用默认备份目录")
                backup_path = os.path.join(os.getcwd(), "modbackup")
            
            # 确保备份路径是Path对象
            backup_path = Path(backup_path)
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

    def update_mod_categories(self, valid_categories=None):
        """更新所有MOD的分类信息，确保它们的分类路径正确"""
        # 如果未提供有效分类列表，使用当前配置的分类列表
        if valid_categories is None:
            valid_categories = set(self.get_categories())
        else:
            # 确保valid_categories是集合类型，而不是列表
            valid_categories = set(valid_categories)
            
        # 确保默认分类始终存在
        valid_categories.add(self.default_category_name)
            
        print(f"[调试] config_manager.update_mod_categories: 有效分类列表: {valid_categories}")
        
        # 更新MOD的分类信息
        mods = self.get_mods()
        updated_count = 0
        default_count = 0
        
        # 先检查默认分类相关的特殊情况
        for mod_id, mod_info in mods.items():
            current_category = mod_info.get('category', self.default_category_name)
            
            # 特殊处理默认分类作为子分类的情况
            if '/' in current_category and current_category.endswith('/' + self.default_category_name):
                # 将其还原为顶级默认分类
                old_category = current_category
                mod_info['category'] = self.default_category_name
                self.update_mod(mod_id, mod_info)
                print(f"[调试] config_manager.update_mod_categories: 修正 MOD {mod_id} 的分类从 {old_category} 为顶级默认分类")
                updated_count += 1
                default_count += 1
        
        # 处理常规分类变更情况
        for mod_id, mod_info in mods.items():
            current_category = mod_info.get('category', self.default_category_name)
            
            # 如果当前分类有效，不做修改
            if current_category in valid_categories:
                continue
                
            # 如果分类不存在，检查父级分类是否存在
            parent_category = None
            if '/' in current_category:
                parent_name = current_category.split('/', 1)[0]
                if parent_name in valid_categories:
                    parent_category = parent_name
            
            # 分类变更处理
            old_category = current_category
            if parent_category:
                mod_info['category'] = parent_category
                print(f"[调试] config_manager.update_mod_categories: MOD {mod_id} 的分类从 {old_category} 更新为父级分类 {parent_category}")
            else:
                mod_info['category'] = self.default_category_name
                print(f"[调试] config_manager.update_mod_categories: MOD {mod_id} 的分类从 {old_category} 更新为默认分类 {self.default_category_name}")
                default_count += 1
            
            self.update_mod(mod_id, mod_info)
            updated_count += 1
        
        # 输出详细日志
        if updated_count > 0:
            print(f"[调试] config_manager.update_mod_categories: 已更新 {updated_count} 个MOD的分类信息，其中 {default_count} 个移至默认分类")
        
        # 确保配置中的categories列表只包含实际有效的分类，不添加新分类
        # 将传入的valid_categories视为权威来源，而不是添加新分类
        sorted_categories = list(valid_categories)
        
        # 确保默认分类在列表的第一位
        if self.default_category_name in sorted_categories:
            sorted_categories.remove(self.default_category_name)
        sorted_categories.insert(0, self.default_category_name)
        
        # 设置分类列表，但不创建新的分类
        print(f"[调试] config_manager.update_mod_categories: 更新配置中的分类列表: {sorted_categories}")
        # 不使用set_categories，而是直接更新config，避免创建新分类
        self.config["categories"] = sorted_categories
        self._save_config()
        
        return updated_count 