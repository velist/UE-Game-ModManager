import os
import shutil
import zipfile
import py7zr
import rarfile
from pathlib import Path
import magic
import uuid
from PySide6.QtWidgets import QMessageBox
from datetime import datetime

# 配置 rarfile
rarfile.UNRAR_TOOL = "C:\\Program Files\\WinRAR\\UnRAR.exe"
if not os.path.exists(rarfile.UNRAR_TOOL):
    # 尝试其他常见安装路径
    alternative_paths = [
        "C:\\Program Files (x86)\\WinRAR\\UnRAR.exe",
        os.path.expandvars("%ProgramFiles%\\WinRAR\\UnRAR.exe"),
        os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\UnRAR.exe")
    ]
    for path in alternative_paths:
        if os.path.exists(path):
            rarfile.UNRAR_TOOL = path
            break

class ModManager:
    def __init__(self, config_manager):
        self.config = config_manager
        self.game_path = Path(config_manager.get_game_path())
        self.mods_path = Path(config_manager.get_mods_path())
        self.temp_path = Path.home() / '.starbound_mod_manager' / 'temp'
        
        # 确保必要的目录存在
        if self.mods_path:
            self.mods_path.mkdir(parents=True, exist_ok=True)
        self.temp_path.mkdir(parents=True, exist_ok=True)
        
    def scan_mods_directory(self):
        """扫描MOD目录"""
        print(f"[调试] scan_mods_directory: 开始扫描 {self.mods_path}")
        found_mods = []
        
        try:
            # 确保目录存在
            if not self.mods_path.exists():
                print(f"[调试] scan_mods_directory: 创建MOD目录 {self.mods_path}")
                self.mods_path.mkdir(parents=True, exist_ok=True)
                return found_mods
                
            # 扫描目录
            for file in self.mods_path.glob('*.pak'):
                print(f"[调试] scan_mods_directory: 找到MOD文件 {file}")
                # 检查配套文件
                ucas_file = file.with_suffix('.ucas')
                utoc_file = file.with_suffix('.utoc')
                
                if ucas_file.exists() and utoc_file.exists():
                    mod_name = file.stem
                    print(f"[调试] scan_mods_directory: 找到完整MOD {mod_name}")
                    
                    # 创建MOD信息
                    mod_info = {
                        'name': mod_name,
                        'files': [file.name, ucas_file.name, utoc_file.name],
                        'original_path': str(file),  # 使用.pak文件作为原始路径
                        'import_date': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                        'enabled': True,  # 已存在的MOD默认启用
                        'size': round(file.stat().st_size / (1024 * 1024), 2)  # MB
                    }
                    found_mods.append(mod_info)
                else:
                    print(f"[调试] scan_mods_directory: MOD文件不完整 {file}")
                    
            print(f"[调试] scan_mods_directory: 扫描完成，找到 {len(found_mods)} 个MOD")
            return found_mods
            
        except Exception as e:
            print(f"[调试] scan_mods_directory: 扫描失败 {str(e)}")
            raise ValueError(f"扫描MOD目录失败: {str(e)}")
        
    def import_mod(self, file_path):
        """导入MOD文件"""
        try:
            print(f"[调试] import_mod: 开始导入MOD文件 {file_path}")
            original_zip = Path(file_path)  # 保存原始zip路径
            # 解压到临时目录
            temp_dir = Path(self.config.get_backup_path()) / 'temp'
            temp_dir.mkdir(exist_ok=True)
            print(f"[调试] import_mod: 临时目录 {temp_dir}")
            
            # 解压文件
            if original_zip.suffix.lower() == '.zip':
                import zipfile
                with zipfile.ZipFile(original_zip, 'r') as zip_ref:
                    zip_ref.extractall(temp_dir)
            elif original_zip.suffix.lower() == '.rar':
                import rarfile
                with rarfile.RarFile(original_zip, 'r') as rar_ref:
                    rar_ref.extractall(temp_dir)
            elif original_zip.suffix.lower() == '.7z':
                import py7zr
                with py7zr.SevenZipFile(original_zip, 'r') as sz_ref:
                    sz_ref.extractall(temp_dir)
            else:
                raise ValueError('不支持的压缩格式')
                
            print(f"[调试] import_mod: 解压完成，开始扫描文件")
            # 扫描解压后的文件
            mod_files = []
            for root, _, files in os.walk(temp_dir):
                for file in files:
                    file_path = Path(root) / file
                    mod_files.append(str(file_path.relative_to(temp_dir)))
                    
            print(f"[调试] import_mod: 扫描到 {len(mod_files)} 个文件")
            
            # 获取主文件名作为MOD名称
            main_file = mod_files[0] if mod_files else None
            if not main_file:
                raise ValueError('MOD文件为空')
                
            print(f"[调试] import_mod: 主文件 {main_file}")
            
            # 创建MOD信息
            mod_info = {
                'name': Path(main_file).stem,
                'files': mod_files,
                'original_path': str(original_zip),  # 使用原始zip路径
                'import_date': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                'enabled': False,
                'size': round(os.path.getsize(original_zip) / (1024 * 1024), 2)  # MB
            }
            
            print(f"[调试] import_mod: 创建MOD信息 {mod_info}")
            
            # 备份MOD文件
            print(f"[调试] import_mod: 开始备份MOD文件")
            backup_result = self.config.backup_mod(mod_info['name'], mod_info)
            print(f"[调试] import_mod: 备份结果 {backup_result}")
            
            # 清理临时目录
            import shutil
            shutil.rmtree(temp_dir)
            print(f"[调试] import_mod: 清理临时目录完成")
            
            return mod_info
            
        except Exception as e:
            print(f"[调试] import_mod: 导入失败 {str(e)}")
            # 确保清理临时目录
            if 'temp_dir' in locals():
                try:
                    shutil.rmtree(temp_dir)
                except:
                    pass
            raise
            
    def _validate_mod_files(self, mod_dir):
        """验证MOD文件"""
        required_extensions = {'.pak', '.ucas', '.utoc'}
        found_files = []
        
        for file in mod_dir.rglob('*'):
            if file.suffix.lower() in required_extensions:
                found_files.append(str(file.relative_to(mod_dir)))
                
        # 检查是否所有必需的文件都存在
        if len(found_files) >= 3:
            # 检查是否有同名文件（不同扩展名）
            base_names = {Path(f).stem for f in found_files}
            if len(base_names) == 1:
                return found_files
                
        return None
        
    def enable_mod(self, mod_id, parent_widget=None):
        """启用MOD，优先从备份zip还原"""
        mod_info = self.config.get_mods().get(mod_id)
        print(f"[调试] enable_mod: mod_id={mod_id}, mod_info={mod_info}")
        if not mod_info:
            print("[调试] enable_mod: MOD不存在")
            raise ValueError('MOD不存在')
        # 优先从zip还原
        restore_result = self.config.restore_mod_from_backup(mod_id, mod_info)
        print(f"[调试] restore_mod_from_backup结果: {restore_result}")
        if not restore_result:
            raise ValueError('MOD还原失败，备份zip不存在或解压失败')
        print(f"[调试] enable_mod: 完成，目标目录文件列表: {list(Path(self.config.get_mods_path()).rglob('*'))}")
        return True

    def disable_mod(self, mod_id):
        """禁用MOD，只删除MOD目录下的3个文件，不动备份目录"""
        mod_info = self.config.get_mods().get(mod_id)
        print(f"[调试] disable_mod: mod_id={mod_id}, mod_info={mod_info}")
        if not mod_info:
            print("[调试] disable_mod: MOD不存在")
            raise ValueError('MOD不存在')
        mods_path = Path(self.config.get_mods_path())
        print(f"[调试] disable_mod: mods_path={mods_path}")
        # 只删MOD目录下的文件
        for file in mod_info.get('files', []):
            file_path = mods_path / file if not file.endswith('.zip') else None
            if file_path and file_path.exists():
                print(f"[调试] disable_mod: 删除文件 {file_path}")
                file_path.unlink()
        print(f"[调试] disable_mod: 完成，目标目录文件列表: {list(mods_path.rglob('*'))}")
        return True
        
    def delete_mod(self, mod_id):
        """删除MOD"""
        print(f"[调试] delete_mod: mod_id={mod_id}")
        self.disable_mod(mod_id)
        backup_dir = self.config.backup_dir / mod_id
        print(f"[调试] delete_mod: 删除备份目录 {backup_dir}, exists={backup_dir.exists()}")
        if backup_dir.exists():
            shutil.rmtree(backup_dir)
        print(f"[调试] delete_mod: 完成，备份目录剩余: {list(self.config.backup_dir.rglob('*'))}")
        
    def update_mod_info(self, mod_id, mod_info):
        """更新MOD信息"""
        if mod_id not in self.config.get_mods():
            raise ValueError('MOD不存在')
            
        self.config.update_mod(mod_id, mod_info)
        
    def set_preview_image(self, mod_id, image_path):
        """设置MOD预览图"""
        mod_info = self.config.get_mods().get(mod_id)
        if not mod_info:
            raise ValueError('MOD不存在')
            
        # 复制预览图到备份目录
        preview_dir = self.config.backup_dir / mod_id
        preview_dir.mkdir(parents=True, exist_ok=True)
        
        preview_path = preview_dir / 'preview.png'
        shutil.copy2(image_path, preview_path)
        
        mod_info['preview_image'] = str(preview_path)
        self.config.update_mod(mod_id, mod_info) 