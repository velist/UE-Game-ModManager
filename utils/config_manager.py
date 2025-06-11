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
        """设置游戏路径"""
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
        """获取游戏路径"""
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
            mod_info['import_date'] = datetime.datetime.now().strftime('%Y-%m-%d')
        self.config["mods"][mod_id] = mod_info
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
        """备份MOD文件：将相关文件压缩为zip包"""
        try:
            print(f"[调试] backup_mod: 开始备份MOD {mod_id}")
            backup_path = Path(self.get_backup_path())
            if not backup_path.exists():
                print(f"[调试] backup_mod: 创建备份目录 {backup_path}")
                backup_path.mkdir(parents=True, exist_ok=True)
            # 创建MOD专属备份目录
            mod_backup_dir = backup_path / mod_id
            if mod_backup_dir.exists():
                print(f"[调试] backup_mod: 清理已存在的备份目录 {mod_backup_dir}")
                shutil.rmtree(mod_backup_dir)
            mod_backup_dir.mkdir(parents=True, exist_ok=True)
            # 目标zip路径
            backup_zip = mod_backup_dir / f"{mod_id}.zip"
            mods_path = Path(self.get_mods_path())
            with zipfile.ZipFile(backup_zip, 'w', zipfile.ZIP_DEFLATED) as zipf:
                for file_name in mod_info['files']:
                    src_file = mods_path / file_name
                    if src_file.exists():
                        print(f"[调试] backup_mod: 添加到zip {src_file}")
                        zipf.write(src_file, arcname=file_name)
                    else:
                        print(f"[调试] backup_mod: 源文件不存在 {src_file}")
            print(f"[调试] backup_mod: 备份完成，生成zip: {backup_zip}")
            return True
        except Exception as e:
            print(f"[调试] backup_mod: 备份失败 {str(e)}")
            # 清理失败的备份
            if 'mod_backup_dir' in locals() and mod_backup_dir.exists():
                try:
                    shutil.rmtree(mod_backup_dir)
                except:
                    pass
            raise ValueError(f"MOD备份失败: {str(e)}")

    def restore_mod_from_backup(self, mod_id, mod_info):
        """从备份还原MOD文件"""
        try:
            print(f"[调试] restore_mod_from_backup: 开始还原MOD {mod_id}")
            backup_path = Path(self.get_backup_path())
            mod_backup_dir = backup_path / mod_id
            
            if not mod_backup_dir.exists():
                raise ValueError(f"备份目录不存在: {mod_backup_dir}")
                
            backup_zip = mod_backup_dir / f"{mod_id}.zip"
            if not backup_zip.exists():
                raise ValueError(f"备份zip不存在: {backup_zip}")
                
            # 解压到MOD目录
            mods_path = Path(self.get_mods_path())
            if not mods_path.exists():
                print(f"[调试] restore_mod_from_backup: 创建MOD目录 {mods_path}")
                mods_path.mkdir(parents=True, exist_ok=True)
                
            print(f"[调试] restore_mod_from_backup: 解压文件到 {mods_path}")
            with zipfile.ZipFile(backup_zip, 'r') as zip_ref:
                zip_ref.extractall(mods_path)
                
            print(f"[调试] restore_mod_from_backup: 还原完成")
            return True
            
        except Exception as e:
            print(f"[调试] restore_mod_from_backup: 还原失败 {str(e)}")
            raise ValueError(f"MOD还原失败: {str(e)}")

    def restore_mod(self, mod_id, mod_info):
        """还原MOD"""
        try:
            print(f"[调试] restore_mod: 开始还原MOD {mod_id}")
            return self.restore_mod_from_backup(mod_id, mod_info)
        except Exception as e:
            print(f"[调试] restore_mod: 还原失败 {str(e)}")
            raise ValueError(f"MOD还原失败: {str(e)}") 