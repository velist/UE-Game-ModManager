import os
import shutil
import zipfile
import py7zr
import rarfile
from pathlib import Path
import uuid
from PySide6.QtWidgets import QMessageBox
from datetime import datetime
import subprocess
import tempfile

# 移除rarfile工具检查，使用内置解压功能
# rarfile.UNRAR_TOOL = "C:\\Program Files\\WinRAR\\UnRAR.exe"
# if not os.path.exists(rarfile.UNRAR_TOOL):
#     # 尝试其他常见安装路径
#     alternative_paths = [
#         "C:\\Program Files (x86)\\WinRAR\\UnRAR.exe",
#         os.path.expandvars("%ProgramFiles%\\WinRAR\\UnRAR.exe"),
#         os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\UnRAR.exe")
#     ]
#     found = False
#     for path in alternative_paths:
#         if os.path.exists(path):
#             rarfile.UNRAR_TOOL = path
#             found = True
#             break
#     if not found:
#         raise ValueError("Cannot find working tool: UnRAR.exe not found. Please install WinRAR or ensure UnRAR.exe is in the expected path.")

def extract_rar_with_winrar(rar_file, extract_path):
    """使用WinRAR解压RAR文件"""
    try:
        # 尝试查找WinRAR.exe或UnRAR.exe的位置
        winrar_paths = [
            "C:\\Program Files\\WinRAR\\WinRAR.exe",
            "C:\\Program Files (x86)\\WinRAR\\WinRAR.exe",
            os.path.expandvars("%ProgramFiles%\\WinRAR\\WinRAR.exe"),
            os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\WinRAR.exe"),
            "C:\\Program Files\\WinRAR\\UnRAR.exe",
            "C:\\Program Files (x86)\\WinRAR\\UnRAR.exe",
            os.path.expandvars("%ProgramFiles%\\WinRAR\\UnRAR.exe"),
            os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\UnRAR.exe")
        ]
        
        rar_exe = None
        for path in winrar_paths:
            if os.path.exists(path):
                rar_exe = path
                break
        
        if not rar_exe:
            print("[调试] extract_rar_with_winrar: WinRAR/UnRAR未找到")
            return False
        
        print(f"[调试] extract_rar_with_winrar: 找到RAR工具: {rar_exe}")
        
        # 确保目标目录存在
        extract_path = Path(extract_path)
        extract_path.mkdir(parents=True, exist_ok=True)
        
        # 使用命令行解压
        if "UnRAR" in rar_exe:
            # UnRAR命令格式
            cmd = f'"{rar_exe}" x -y "{rar_file}" "{extract_path}\\"'
        else:
            # WinRAR命令格式
            cmd = f'"{rar_exe}" x -ibck -y "{rar_file}" "{extract_path}\\"'
        
        print(f"[调试] extract_rar_with_winrar: 执行命令: {cmd}")
        
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=True
        )
        stdout, stderr = process.communicate()
        
        # 检查解压后的文件
        files = list(extract_path.glob('**/*'))
        if files and (process.returncode == 0 or len(files) > 1):
            print(f"[调试] extract_rar_with_winrar: RAR解压成功，文件数: {len(files)}")
            return True
        else:
            print(f"[调试] extract_rar_with_winrar: RAR解压失败或未解压出文件: {stderr.decode('utf-8', errors='ignore')}")
            return False
    except Exception as e:
        print(f"[调试] extract_rar_with_winrar: 解压异常: {e}")
        return False

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
        """递归扫描MOD目录及子目录，支持子文件夹下同名pak/ucas/utoc"""
        print(f"[调试] scan_mods_directory: 开始扫描 {self.mods_path}")
        found_mods = []
        try:
            if not self.mods_path.exists():
                print(f"[调试] scan_mods_directory: 创建MOD目录 {self.mods_path}")
                self.mods_path.mkdir(parents=True, exist_ok=True)
                return found_mods
                
            # 递归查找所有pak文件
            for file in self.mods_path.rglob('*.pak'):
                ucas_file = file.with_suffix('.ucas')
                utoc_file = file.with_suffix('.utoc')
                # 必须同目录下有同名ucas/utoc才算完整MOD
                if ucas_file.exists() and utoc_file.exists():
                    mod_name = file.stem
                    print(f"[调试] scan_mods_directory: 找到完整MOD {mod_name} in {file.parent}")
                    
                    # 确定文件相对路径
                    rel_path = file.parent.relative_to(self.mods_path) if file.parent != self.mods_path else Path("")
                    
                    # 根据文件位置构建相对路径
                    if rel_path == Path(""):
                        # 情况二：文件直接在A文件夹中
                        files = [
                            str(file.name),
                            str(ucas_file.name),
                            str(utoc_file.name)
                        ]
                    else:
                        # 情况一：文件在子文件夹中
                        files = [
                            str(rel_path / file.name),
                            str(rel_path / ucas_file.name),
                            str(rel_path / utoc_file.name)
                        ]
                    
                    mod_info = {
                        'name': mod_name,
                        'files': files,
                        'original_path': str(file),
                        'import_date': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                        'enabled': True,
                        'size': round(file.stat().st_size / (1024 * 1024), 2),
                        'folder_structure': str(rel_path) != ""  # 标记是否在子文件夹中
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
        """导入MOD文件，支持递归解压，宽松识别pak/ucas/utoc，支持嵌套压缩包自动处理"""
        import zipfile, py7zr
        import shutil
        
        def recursive_extract(path, to_dir, visited=None, depth=0):
            if visited is None:
                visited = set()
            if str(path) in visited:
                print(f"[调试] 跳过已处理压缩包: {path}")
                return
            visited.add(str(path))
            if depth > 10:
                raise RuntimeError(f"递归解压深度过大，可能存在嵌套死循环: {path}")
            try:
                # 使用更安全的解压方法
                if zipfile.is_zipfile(path):
                    print(f"[调试] 解压zip: {path} -> {to_dir}")
                    with zipfile.ZipFile(path, 'r') as z:
                        z.extractall(to_dir)
                elif str(path).lower().endswith('.rar'):
                    print(f"[调试] 处理rar文件: {path} -> {to_dir}")
                    extracted = False
                    
                    # 尝试使用WinRAR解压
                    try:
                        print(f"[调试] 尝试使用WinRAR解压")
                        if extract_rar_with_winrar(path, to_dir):
                            print(f"[调试] WinRAR解压成功")
                            extracted = True
                        else:
                            print(f"[调试] WinRAR解压失败")
                    except Exception as e:
                        print(f"[调试] WinRAR解压失败: {e}")
                    
                    # 如果WinRAR解压失败，尝试使用7z命令行工具解压
                    if not extracted:
                        try:
                            # 创建临时目录
                            temp_dir = tempfile.mkdtemp()
                            print(f"[调试] 创建临时目录: {temp_dir}")
                            
                            # 尝试使用7z命令行工具解压
                            process = subprocess.Popen(
                                ["7z", "x", str(path), f"-o{temp_dir}", "-y"],
                                stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE,
                                shell=True
                            )
                            stdout, stderr = process.communicate()
                            
                            if process.returncode == 0:
                                print(f"[调试] 7z解压成功，将文件从临时目录复制到目标目录")
                                # 复制解压后的文件到目标目录
                                for item in Path(temp_dir).glob('*'):
                                    dest_path = Path(to_dir) / item.name
                                    if item.is_dir():
                                        if dest_path.exists():
                                            shutil.rmtree(dest_path)
                                        shutil.copytree(item, dest_path)
                                    else:
                                        shutil.copy2(item, dest_path)
                                print(f"[调试] 文件已复制到目标目录")
                                extracted = True
                            else:
                                print(f"[调试] 7z解压失败: {stderr.decode('utf-8', errors='ignore')}")
                        except Exception as e:
                            print(f"[调试] 7z解压失败: {e}")
                        finally:
                            # 清理临时目录
                            if 'temp_dir' in locals() and Path(temp_dir).exists():
                                shutil.rmtree(temp_dir)
                                print(f"[调试] 清理临时目录: {temp_dir}")
                    
                    # 如果7z解压失败，尝试使用rarfile库
                    if not extracted:
                        try:
                            print(f"[调试] 尝试使用rarfile库解压")
                            with rarfile.RarFile(path) as rf:
                                rf.extractall(to_dir)
                            print(f"[调试] rarfile库解压成功")
                            extracted = True
                        except Exception as e:
                            print(f"[调试] rarfile库解压失败: {e}")
                    
                    # 如果所有解压方法都失败，则复制原始文件
                    if not extracted:
                        dest_file = Path(to_dir) / Path(path).name
                        shutil.copy2(path, dest_file)
                        print(f"[调试] RAR文件已复制到目标文件夹: {dest_file}")
                    
                elif str(path).lower().endswith('.7z'):
                    print(f"[调试] 解压7z: {path} -> {to_dir}")
                    try:
                        with py7zr.SevenZipFile(path, 'r') as sz:
                            sz.extractall(to_dir)
                    except Exception as e:
                        print(f"[警告] 使用py7zr解压失败: {e}")
                        # 复制文件作为备选
                        dest_file = Path(to_dir) / Path(path).name
                        shutil.copy2(path, dest_file)
                        print(f"[调试] 7z文件已复制到目标文件夹: {dest_file}")
                
                # 解压后递归查找新压缩包，但跳过RAR文件
                for f in Path(to_dir).glob('**/*'):
                    if f.is_file() and (zipfile.is_zipfile(f) or str(f).lower().endswith('.7z') or str(f).lower().endswith('.rar')):
                        recursive_extract(f, to_dir, visited, depth+1)
            except Exception as e:
                print(f"[调试] recursive_extract异常: {path} -> {to_dir}, 错误: {e}")
                # 不抛出异常，尝试继续处理其他文件
                # 复制文件作为备选
                try:
                    dest_file = Path(to_dir) / Path(path).name
                    shutil.copy2(path, dest_file)
                    print(f"[调试] 文件已复制到目标文件夹: {dest_file}")
                except Exception as copy_e:
                    print(f"[警告] 复制文件也失败了: {copy_e}")
        
        def find_mod_files(directory):
            """查找目录中的MOD文件组（pak/ucas/utoc）"""
            mod_groups = []
            
            # 收集所有pak文件
            pak_files = list(directory.glob('**/*.pak'))
            
            for pak_file in pak_files:
                base_name = pak_file.stem
                dir_path = pak_file.parent
                
                # 查找对应的ucas和utoc文件
                ucas_file = dir_path / f"{base_name}.ucas"
                utoc_file = dir_path / f"{base_name}.utoc"
                
                if ucas_file.exists() and utoc_file.exists():
                    # 找到一组完整的MOD文件
                    mod_groups.append({
                        'name': base_name,
                        'files': [pak_file, ucas_file, utoc_file],
                        'dir': dir_path
                    })
            
            return mod_groups
        
        def check_nested_archives(directory):
            """检查目录中是否有嵌套的压缩包，如果有则返回这些压缩包的路径"""
            nested_archives = []
            
            # 收集所有压缩包
            for archive in directory.glob('**/*'):
                if archive.is_file() and (zipfile.is_zipfile(archive) or 
                                          archive.suffix.lower() in ['.rar', '.7z']):
                    nested_archives.append(archive)
            
            return nested_archives
        
        def extract_nested_archive(archive_path, output_dir, original_archive_path=None, processed_mod_names=None):
            """处理嵌套压缩包，提取MOD文件并返回MOD信息列表"""
            if processed_mod_names is None:
                processed_mod_names = set()
            
            # 创建以压缩包名称命名的临时目录
            temp_extract_dir = Path(tempfile.mkdtemp())
            print(f"[调试] extract_nested_archive: 创建临时目录: {temp_extract_dir}")
            
            try:
                # 解压嵌套压缩包到临时目录
                recursive_extract(archive_path, temp_extract_dir)
                
                # 查找解压后的MOD文件
                mod_groups = find_mod_files(temp_extract_dir)
                
                if mod_groups:
                    print(f"[调试] extract_nested_archive: 在压缩包 {archive_path.name} 中找到 {len(mod_groups)} 组MOD文件")
                    
                    # 过滤掉已处理过的MOD名称
                    filtered_mod_groups = [group for group in mod_groups if group['name'] not in processed_mod_names]
                    
                    if filtered_mod_groups:
                        # 使用MOD文件名作为MOD文件夹名称，而不是压缩包名称
                        processed_mods = []
                        for mod_group in filtered_mod_groups:
                            mod_name = mod_group['name']
                            processed_mod_names.add(mod_name)
                            mod_folder = output_dir / mod_name
                            if not mod_folder.exists():
                                mod_folder.mkdir(parents=True, exist_ok=True)
                            
                            # 复制MOD文件到目标文件夹
                            for file in mod_group['files']:
                                dest_file = mod_folder / file.name
                                shutil.copy2(file, dest_file)
                            
                            # 创建MOD信息
                            mod_info = {
                                'name': mod_name,
                                'files': [f"{mod_name}/{file.name}" for file in mod_group['files']],
                                'original_path': str(archive_path),
                                'import_date': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                                'enabled': True,
                                'size': round(archive_path.stat().st_size / (1024 * 1024), 2),
                                'folder_structure': True
                            }
                            
                            # 如果有父压缩包，记录关系
                            if original_archive_path:
                                mod_info['parent_archive'] = str(original_archive_path)
                                mod_info['is_nested'] = True
                            
                            processed_mods.append(mod_info)
                        
                        return processed_mods
                    else:
                        print(f"[调试] extract_nested_archive: 所有MOD已在主压缩包中处理，跳过")
                        return None
                
                # 如果没有找到MOD文件，检查是否有更深层次的嵌套压缩包
                nested_archives = check_nested_archives(temp_extract_dir)
                if nested_archives:
                    print(f"[调试] extract_nested_archive: 在压缩包 {archive_path.name} 中找到 {len(nested_archives)} 个嵌套压缩包")
                    
                    # 处理每个嵌套压缩包
                    nested_mods = []
                    for nested_archive in nested_archives:
                        # 对每个嵌套压缩包递归处理
                        nested_result = extract_nested_archive(
                            nested_archive, 
                            output_dir, 
                            original_archive_path or archive_path,
                            processed_mod_names
                        )
                        if nested_result:
                            if isinstance(nested_result, list):
                                nested_mods.extend(nested_result)
                            else:
                                nested_mods.append(nested_result)
                    
                    return nested_mods if nested_mods else None
                
                # 如果没有找到MOD文件也没有嵌套压缩包，返回None
                return None
                
            finally:
                # 清理临时目录
                if temp_extract_dir.exists():
                    shutil.rmtree(temp_extract_dir)
                    print(f"[调试] extract_nested_archive: 清理临时目录: {temp_extract_dir}")
        
        try:
            print(f"[调试] import_mod: 开始导入MOD文件 {file_path}")
            original_zip = Path(file_path)
            mods_path = Path(self.config.get_mods_path())
            if not mods_path.exists():
                mods_path.mkdir(parents=True, exist_ok=True)
            
            # 创建临时工作目录用于解压
            temp_work_dir = Path(tempfile.mkdtemp())
            print(f"[调试] import_mod: 创建临时工作目录: {temp_work_dir}")
            
            try:
                # 先将原始文件解压到临时工作目录
                recursive_extract(original_zip, temp_work_dir)
                
                # 检查临时目录中是否有直接的MOD文件
                direct_mod_groups = find_mod_files(temp_work_dir)
                
                # 检查是否有嵌套的压缩包
                nested_archives = check_nested_archives(temp_work_dir)
                
                # 存储所有导入的MOD信息
                imported_mods = []
                
                # 跟踪已处理的MOD名称，避免重复处理
                processed_mod_names = set()
                
                # 如果有直接的MOD文件，为每个MOD创建独立文件夹
                if direct_mod_groups:
                    print(f"[调试] import_mod: 在主压缩包中找到 {len(direct_mod_groups)} 组MOD文件")
                    
                    # 为每个MOD组创建独立文件夹
                    for mod_group in direct_mod_groups:
                        mod_name = mod_group['name']
                        processed_mod_names.add(mod_name)
                        mod_folder = mods_path / mod_name
                        if not mod_folder.exists():
                            mod_folder.mkdir(parents=True, exist_ok=True)
                        
                        # 复制MOD文件到目标文件夹
                        for file in mod_group['files']:
                            dest_file = mod_folder / file.name
                            shutil.copy2(file, dest_file)
                        
                        # 创建MOD信息
                        mod_info = {
                            'name': mod_name,
                            'files': [f"{mod_name}/{file.name}" for file in mod_group['files']],
                            'original_path': str(original_zip),
                            'import_date': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                            'enabled': True,
                            'size': round(original_zip.stat().st_size / (1024 * 1024), 2),
                            'folder_structure': True
                        }
                        
                        # 备份MOD文件
                        self.config.backup_mod(mod_info['name'], mod_info)
                        
                        # 添加到导入列表
                        imported_mods.append(mod_info)
                
                # 处理嵌套的压缩包
                if nested_archives:
                    print(f"[调试] import_mod: 发现 {len(nested_archives)} 个嵌套压缩包")
                    
                    for archive in nested_archives:
                        # 处理每个嵌套压缩包，传递已处理的MOD名称
                        nested_result = extract_nested_archive(archive, mods_path, original_zip, processed_mod_names)
                        
                        if nested_result:
                            if isinstance(nested_result, list):
                                # 如果返回了多个MOD信息
                                for mod_info in nested_result:
                                    # 备份MOD文件
                                    self.config.backup_mod(mod_info['name'], mod_info)
                                    imported_mods.append(mod_info)
                            else:
                                # 如果只返回了一个MOD信息
                                self.config.backup_mod(nested_result['name'], nested_result)
                                imported_mods.append(nested_result)
                
                # 如果没有找到任何MOD文件，创建一个虚拟MOD
                if not imported_mods:
                    print(f"[调试] import_mod: 未找到MOD文件，创建虚拟MOD")
                    
                    # 创建以压缩包名称命名的文件夹
                    mod_name = original_zip.stem
                    mod_folder = mods_path / mod_name
                    if not mod_folder.exists():
                        mod_folder.mkdir(parents=True, exist_ok=True)
                    
                    # 复制原始压缩包到目标文件夹
                    dest_file = mod_folder / original_zip.name
                    shutil.copy2(original_zip, dest_file)
                    
                    # 创建虚拟MOD信息
                    mod_info = {
                        'name': mod_name,
                        'files': [f"{mod_name}/{original_zip.name}"],
                        'original_path': str(original_zip),
                        'import_date': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                        'enabled': True,
                        'size': round(original_zip.stat().st_size / (1024 * 1024), 2),
                        'folder_structure': True,
                        'is_virtual': True  # 标记为虚拟MOD
                    }
                    
                    # 备份MOD文件
                    self.config.backup_mod(mod_info['name'], mod_info)
                    
                    # 返回结果
                    imported_mods = [mod_info]
                
                # 返回导入的MOD列表
                if len(imported_mods) == 1:
                    return imported_mods[0]  # 兼容旧版本接口
                else:
                    return imported_mods
                
            finally:
                # 清理临时工作目录
                if temp_work_dir.exists():
                    shutil.rmtree(temp_work_dir)
                    print(f"[调试] import_mod: 清理临时工作目录: {temp_work_dir}")
            
        except Exception as e:
            print(f"[调试] import_mod: 导入失败 {str(e)}")
            raise

    def enable_mod(self, mod_id, mod_info=None, parent_widget=None):
        """启用MOD，优先从备份还原"""
        if not mod_info:
            mod_info = self.config.get_mods().get(mod_id)
        
        print(f"[调试] enable_mod: mod_id={mod_id}, mod_info={mod_info}")
        if not mod_info:
            print("[调试] enable_mod: MOD不存在")
            raise ValueError('MOD不存在')
            
        try:
            # 优先从备份还原
            restore_result = self.config.restore_mod_from_backup(mod_id, mod_info)
            print(f"[调试] restore_mod_from_backup结果: {restore_result}")
            
            if not restore_result:
                # 如果是嵌套MOD，尝试从父压缩包重新导入
                if mod_info.get('is_nested', False) and 'parent_archive' in mod_info:
                    parent_archive = Path(mod_info['parent_archive'])
                    if parent_archive.exists():
                        print(f"[调试] enable_mod: 尝试从父压缩包重新导入嵌套MOD: {parent_archive}")
                        # 重新导入父压缩包
                        imported_mods = self.import_mod(parent_archive)
                        if isinstance(imported_mods, list):
                            # 查找匹配的嵌套MOD
                            for imported_mod in imported_mods:
                                if imported_mod.get('name') == mod_id:
                                    return True
                        return True
                
                # 如果是虚拟MOD，尝试直接使用原始文件
                if mod_info.get('is_virtual', False) and 'original_path' in mod_info:
                    original_path = Path(mod_info['original_path'])
                    if original_path.exists():
                        # 确保目标目录存在
                        mods_path = Path(self.config.get_mods_path())
                        if len(mod_info.get('files', [])) > 0:
                            first_file = mod_info['files'][0]
                            # 确保file_name是字符串
                            if not isinstance(first_file, str):
                                first_file = str(first_file)
                            
                            # 解析目标路径
                            if '/' in first_file:
                                # 有子目录结构
                                sub_dir = first_file.split('/')[0]
                                target_dir = mods_path / sub_dir
                            else:
                                # 没有子目录结构
                                target_dir = mods_path / mod_id
                            
                            # 创建目标目录
                            target_dir.mkdir(parents=True, exist_ok=True)
                            
                            # 复制文件到目标目录
                            target_file = target_dir / original_path.name
                            print(f"[调试] enable_mod: 直接从原始文件复制 {original_path} -> {target_file}")
                            shutil.copy2(original_path, target_file)
                            return True
                
                raise ValueError('MOD还原失败，备份不存在或解压失败')
            
            print(f"[调试] enable_mod: 完成，目标目录文件列表: {list(Path(self.config.get_mods_path()).rglob('*'))}")
            return True
        except Exception as e:
            if parent_widget:
                from PySide6.QtWidgets import QMessageBox
                QMessageBox.warning(parent_widget, "警告", f"启用MOD失败：{str(e)}\n\n这可能是因为备份文件丢失或损坏。")
            print(f"[调试] enable_mod: 启用失败: {e}")
            return False
            
    def disable_mod(self, mod_id):
        """禁用MOD"""
        try:
            mod_info = self.config.get_mods().get(mod_id)
            if not mod_info:
                raise ValueError(f"MOD不存在: {mod_id}")
                
            # 删除MOD文件
            mods_path = self.mods_path
            for file in mod_info.get('files', []):
                file_path = mods_path / file if not file.endswith('.zip') else None
                if file_path and file_path.exists():
                    file_path.unlink()
                    print(f"[调试] disable_mod: 删除文件 {file_path}")
                    
            # 清理空文件夹
            if mod_info.get('folder_structure', False):
                # 获取第一个文件的父目录
                first_file = mod_info.get('files', [''])[0]
                if first_file:
                    parent_dir = (mods_path / first_file).parent
                    if parent_dir.exists() and parent_dir != mods_path:
                        # 检查目录是否为空
                        if not any(parent_dir.iterdir()):
                            parent_dir.rmdir()
                            print(f"[调试] disable_mod: 删除空目录 {parent_dir}")
            
            mod_info['enabled'] = False
            self.config.update_mod(mod_id, mod_info)
            return True
        except Exception as e:
            print(f"[调试] disable_mod: 禁用失败 {str(e)}")
            return False
            
    def delete_mod(self, mod_id):
        """删除MOD"""
        # 先禁用MOD
        self.disable_mod(mod_id)
        
        # 删除备份
        backup_dir = Path(self.config.get_backup_path()) / mod_id
        if backup_dir.exists():
            shutil.rmtree(backup_dir)
        
        # 从配置中删除
        self.config.remove_mod(mod_id)
        
    def update_mod_info(self, mod_id, mod_info):
        """更新MOD信息"""
        self.config.update_mod(mod_id, mod_info)
        
    def set_preview_image(self, mod_id, image_path):
        """设置MOD预览图"""
        try:
            mod_info = self.config.get_mods().get(mod_id)
            if not mod_info:
                raise ValueError(f"MOD不存在: {mod_id}")
                
            # 创建备份目录
            backup_dir = Path(self.config.get_backup_path()) / mod_id
            backup_dir.mkdir(parents=True, exist_ok=True)
            
            # 复制预览图
            preview_path = backup_dir / f"preview{Path(image_path).suffix}"
            shutil.copy2(image_path, preview_path)
            
            # 更新MOD信息
            mod_info['preview_image'] = str(preview_path)
            self.config.update_mod(mod_id, mod_info)
            
            return True
        except Exception as e:
            print(f"[调试] set_preview_image: 设置预览图失败 {str(e)}")
            return False
