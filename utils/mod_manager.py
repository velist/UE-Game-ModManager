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
import logging
import traceback
import sys

# 配置日志记录
mod_logger = logging.getLogger('mod_manager')
mod_logger.setLevel(logging.INFO)

# 确保有文件处理器
file_handler = logging.FileHandler("mod_manager.log", encoding='utf-8')
file_handler.setFormatter(logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s'))
mod_logger.addHandler(file_handler)

# 确保有控制台处理器
console_handler = logging.StreamHandler()
console_handler.setFormatter(logging.Formatter('%(levelname)s: %(message)s'))
mod_logger.addHandler(console_handler)

# 测试UnRAR.dll是否可用
def test_unrar_dll():
    """测试UnRAR.dll是否可用"""
    try:
        # 获取应用程序目录
        if getattr(sys, 'frozen', False):
            # 如果是打包后的可执行文件
            app_dir = os.path.dirname(sys.executable)
        else:
            # 如果是源代码运行
            app_dir = os.path.dirname(os.path.abspath(__file__))
            # 如果是在utils目录下，返回上一级
            if os.path.basename(app_dir) == 'utils':
                app_dir = os.path.dirname(app_dir)
        
        mod_logger.info(f"测试UnRAR.dll: 应用程序目录: {app_dir}")
        
        # 查找可能的UnRAR.dll路径
        possible_paths = [
            os.path.join(app_dir, "libs", "UnRAR.dll"),
            os.path.join(app_dir, "UnRAR.dll"),
            os.path.join(app_dir, "libs", "UnRAR64.dll"),
            os.path.join(app_dir, "UnRAR64.dll")
        ]
        
        # 如果是PyInstaller打包的应用程序，添加特殊路径
        if getattr(sys, 'frozen', False):
            possible_paths.extend([
                os.path.join(sys._MEIPASS, "libs", "UnRAR.dll"),
                os.path.join(sys._MEIPASS, "UnRAR.dll"),
                os.path.join(sys._MEIPASS, "libs", "UnRAR64.dll"),
                os.path.join(sys._MEIPASS, "UnRAR64.dll")
            ])
        
        # 检查是否有可用的UnRAR.dll
        unrar_dll_path = None
        for path in possible_paths:
            mod_logger.info(f"测试UnRAR.dll: 检查路径: {path}")
            if os.path.exists(path):
                unrar_dll_path = path
                mod_logger.info(f"测试UnRAR.dll: 找到UnRAR.dll: {path}")
                
                # 检查DLL架构
                try:
                    # 尝试检测DLL架构
                    with open(path, 'rb') as f:
                        # 读取PE头
                        f.seek(0x3C)  # PE头偏移位置
                        pe_offset = int.from_bytes(f.read(4), byteorder='little')
                        
                        # 检查是否是有效的PE文件
                        f.seek(pe_offset)
                        if f.read(4) != b'PE\0\0':
                            mod_logger.warning(f"测试UnRAR.dll: {path} 不是有效的PE文件")
                            continue
                        
                        # 读取机器类型
                        f.seek(pe_offset + 4)
                        machine_type = int.from_bytes(f.read(2), byteorder='little')
                        
                        # 0x014c = x86, 0x8664 = x64
                        if machine_type == 0x014c:
                            mod_logger.info(f"测试UnRAR.dll: {path} 是32位DLL")
                        elif machine_type == 0x8664:
                            mod_logger.info(f"测试UnRAR.dll: {path} 是64位DLL")
                        else:
                            mod_logger.warning(f"测试UnRAR.dll: {path} 架构未知 (0x{machine_type:04x})")
                        
                        # 检查Python解释器架构
                        is_python_64bit = sys.maxsize > 2**32
                        mod_logger.info(f"测试UnRAR.dll: Python解释器是{'64' if is_python_64bit else '32'}位")
                        
                        # 检查架构兼容性
                        if (is_python_64bit and machine_type != 0x8664) or (not is_python_64bit and machine_type != 0x014c):
                            mod_logger.warning(f"测试UnRAR.dll: DLL架构与Python解释器不兼容")
                            # 继续查找兼容的DLL
                            continue
                except Exception as e:
                    mod_logger.error(f"测试UnRAR.dll: 检查DLL架构失败: {e}")
                
                break
        
        if not unrar_dll_path:
            mod_logger.warning("测试UnRAR.dll: 未找到UnRAR.dll")
            return False
        
        # 创建临时RAR文件进行测试
        temp_dir = tempfile.mkdtemp()
        test_file_path = os.path.join(temp_dir, "test.txt")
        test_rar_path = os.path.join(temp_dir, "test.rar")
        
        try:
            # 创建测试文件
            with open(test_file_path, "w", encoding="utf-8") as f:
                f.write("UnRAR.dll测试文件")
            
            # 尝试创建RAR文件
            mod_logger.info(f"测试UnRAR.dll: 创建测试RAR文件: {test_rar_path}")
            
            # 使用rarfile库配置
            old_unrar_tool = rarfile.UNRAR_TOOL
            rarfile.UNRAR_TOOL = unrar_dll_path
            
            # 尝试解压测试RAR文件
            extract_dir = os.path.join(temp_dir, "extract")
            os.makedirs(extract_dir, exist_ok=True)
            
            # 使用WinRAR命令行创建测试RAR文件
            winrar_paths = [
                "C:\\Program Files\\WinRAR\\WinRAR.exe",
                "C:\\Program Files (x86)\\WinRAR\\WinRAR.exe",
                os.path.expandvars("%ProgramFiles%\\WinRAR\\WinRAR.exe"),
                os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\WinRAR.exe")
            ]
            
            rar_exe = None
            for path in winrar_paths:
                if os.path.exists(path):
                    rar_exe = path
                    break
            
            if rar_exe:
                # 使用WinRAR创建测试RAR文件
                cmd = f'"{rar_exe}" a -ep "{test_rar_path}" "{test_file_path}"'
                process = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    shell=True
                )
                stdout, stderr = process.communicate()
                
                if process.returncode == 0 and os.path.exists(test_rar_path):
                    mod_logger.info(f"测试UnRAR.dll: 成功创建测试RAR文件")
                    
                    # 尝试使用rarfile库解压
                    try:
                        with rarfile.RarFile(test_rar_path) as rf:
                            rf.extractall(extract_dir)
                        
                        # 检查是否成功解压
                        extracted_file = os.path.join(extract_dir, "test.txt")
                        if os.path.exists(extracted_file):
                            with open(extracted_file, "r", encoding="utf-8") as f:
                                content = f.read()
                            
                            if content == "UnRAR.dll测试文件":
                                mod_logger.info("测试UnRAR.dll: 成功解压测试RAR文件，UnRAR.dll工作正常")
                                return True
                            else:
                                mod_logger.warning("测试UnRAR.dll: 解压后文件内容不匹配")
                        else:
                            mod_logger.warning(f"测试UnRAR.dll: 未找到解压后的文件: {extracted_file}")
                    except Exception as e:
                        mod_logger.error(f"测试UnRAR.dll: 解压测试RAR文件失败: {e}")
                        
                        # 如果是DLL兼容性问题，记录详细信息
                        if "WinError 193" in str(e) or "不是有效的 Win32 应用程序" in str(e):
                            mod_logger.error("测试UnRAR.dll: DLL架构与系统不兼容")
                else:
                    mod_logger.warning(f"测试UnRAR.dll: 创建测试RAR文件失败")
            else:
                mod_logger.warning("测试UnRAR.dll: 未找到WinRAR.exe，无法创建测试RAR文件")
        
        finally:
            # 恢复原始配置
            rarfile.UNRAR_TOOL = old_unrar_tool
            
            # 清理临时文件
            try:
                shutil.rmtree(temp_dir)
                mod_logger.info(f"测试UnRAR.dll: 清理临时目录: {temp_dir}")
            except Exception as e:
                mod_logger.error(f"测试UnRAR.dll: 清理临时目录失败: {e}")
        
        return False
    except Exception as e:
        mod_logger.error(f"测试UnRAR.dll失败: {e}")
        return False

# 配置rarfile库使用内置的UnRAR.dll
def setup_rarfile():
    """配置rarfile库使用内置的UnRAR.dll"""
    try:
        # 获取应用程序目录
        if getattr(sys, 'frozen', False):
            # 如果是打包后的可执行文件
            app_dir = os.path.dirname(sys.executable)
        else:
            # 如果是源代码运行
            app_dir = os.path.dirname(os.path.abspath(__file__))
            # 如果是在utils目录下，返回上一级
            if os.path.basename(app_dir) == 'utils':
                app_dir = os.path.dirname(app_dir)
        
        mod_logger.info(f"应用程序目录: {app_dir}")
        
        # 查找可能的UnRAR.dll路径
        possible_paths = [
            os.path.join(app_dir, "libs", "UnRAR.dll"),
            os.path.join(app_dir, "UnRAR.dll"),
            os.path.join(app_dir, "libs", "UnRAR64.dll"),
            os.path.join(app_dir, "UnRAR64.dll")
        ]
        
        # 如果是PyInstaller打包的应用程序，添加特殊路径
        if getattr(sys, 'frozen', False):
            possible_paths.extend([
                os.path.join(sys._MEIPASS, "libs", "UnRAR.dll"),
                os.path.join(sys._MEIPASS, "UnRAR.dll"),
                os.path.join(sys._MEIPASS, "libs", "UnRAR64.dll"),
                os.path.join(sys._MEIPASS, "UnRAR64.dll")
            ])
        
        # 检查是否有可用的UnRAR.dll
        unrar_dll_path = None
        for path in possible_paths:
            mod_logger.info(f"检查UnRAR.dll路径: {path}")
            if os.path.exists(path):
                unrar_dll_path = path
                mod_logger.info(f"找到内置UnRAR.dll: {path}")
                break
        
        # 配置rarfile库
        if unrar_dll_path:
            rarfile.UNRAR_TOOL = unrar_dll_path
            mod_logger.info(f"rarfile库配置为使用内置UnRAR.dll: {unrar_dll_path}")
            
            # 测试UnRAR.dll是否可用
            try:
                # 创建一个临时目录用于测试
                test_dir = tempfile.mkdtemp()
                test_file = os.path.join(test_dir, "test.txt")
                with open(test_file, "w") as f:
                    f.write("test")
                
                # 检查rarfile库是否正确配置
                mod_logger.info(f"测试rarfile库配置: UNRAR_TOOL={rarfile.UNRAR_TOOL}")
                mod_logger.info(f"测试rarfile库配置: USE_EXTRACT_HACK={rarfile.USE_EXTRACT_HACK}")
                
                # 清理测试目录
                shutil.rmtree(test_dir)
                mod_logger.info("UnRAR.dll测试完成")
            except Exception as e:
                mod_logger.error(f"测试UnRAR.dll失败: {e}")
        else:
            # 如果找不到内置的UnRAR.dll，尝试使用系统中的UnRAR.exe
            mod_logger.warning("未找到内置UnRAR.dll，尝试使用系统中的UnRAR.exe")
            
            # 尝试常见的UnRAR.exe路径
            unrar_paths = [
                "C:\\Program Files\\WinRAR\\UnRAR.exe",
                "C:\\Program Files (x86)\\WinRAR\\UnRAR.exe",
                os.path.expandvars("%ProgramFiles%\\WinRAR\\UnRAR.exe"),
                os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\UnRAR.exe")
            ]
            
            for path in unrar_paths:
                if os.path.exists(path):
                    rarfile.UNRAR_TOOL = path
                    mod_logger.info(f"rarfile库配置为使用系统UnRAR.exe: {path}")
                    break
        
        # 启用内部解压hack，这对于没有外部工具时很有用
        rarfile.USE_EXTRACT_HACK = True
        mod_logger.info("已启用rarfile库的内部解压hack")
        
        # 进行完整测试
        test_result = test_unrar_dll()
        if test_result:
            mod_logger.info("UnRAR.dll测试成功，可以正常使用")
        else:
            mod_logger.warning("UnRAR.dll测试失败，可能无法正常解压RAR文件")
        
        return True
    except Exception as e:
        mod_logger.error(f"配置rarfile库失败: {e}")
        return False

# 初始化rarfile库
setup_rarfile()

# 保存所有创建的临时目录路径，以便在出错时清理
TEMP_DIRECTORIES = set()

def log_and_show_error(message, title="错误", detailed=None):
    """记录错误并显示对话框"""
    if detailed:
        mod_logger.error(f"{message}: {detailed}")
    else:
        mod_logger.error(message)
    
    # 使用QMessageBox显示错误
    error_box = QMessageBox()
    error_box.setIcon(QMessageBox.Critical)
    error_box.setWindowTitle(title)
    error_box.setText(message)
    if detailed:
        error_box.setDetailedText(detailed)
    error_box.exec()

def cleanup_temp_directories():
    """清理所有已创建的临时目录"""
    global TEMP_DIRECTORIES
    for temp_dir in list(TEMP_DIRECTORIES):
        try:
            if Path(temp_dir).exists():
                shutil.rmtree(temp_dir)
                mod_logger.info(f"已清理临时目录: {temp_dir}")
        except Exception as e:
            mod_logger.error(f"清理临时目录失败 {temp_dir}: {e}")
    TEMP_DIRECTORIES.clear()

def create_temp_directory(base_dir=None):
    """创建临时目录并记录在全局变量中"""
    try:
        if base_dir and Path(base_dir).exists():
            # 在指定的基础目录中创建临时目录
            temp_dir_name = f"temp_{datetime.now().strftime('%Y%m%d%H%M%S%f')}"
            temp_dir = os.path.join(base_dir, temp_dir_name)
            os.makedirs(temp_dir, exist_ok=True)
        else:
            # 使用系统临时目录
            temp_dir = tempfile.mkdtemp()
        
        TEMP_DIRECTORIES.add(temp_dir)
        mod_logger.info(f"创建临时目录: {temp_dir}")
        return temp_dir
    except Exception as e:
        mod_logger.error(f"创建临时目录失败: {e}")
        # 回退到系统临时目录
        temp_dir = tempfile.mkdtemp()
        TEMP_DIRECTORIES.add(temp_dir)
        mod_logger.info(f"回退到系统临时目录: {temp_dir}")
        return temp_dir

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
            mod_logger.warning("extract_rar_with_winrar: WinRAR/UnRAR未找到")
            return False
        
        mod_logger.info(f"extract_rar_with_winrar: 找到RAR工具: {rar_exe}")
        
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
        
        mod_logger.info(f"extract_rar_with_winrar: 执行命令: {cmd}")
        
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
            mod_logger.info(f"extract_rar_with_winrar: RAR解压成功，文件数: {len(files)}")
            return True
        else:
            mod_logger.warning(f"extract_rar_with_winrar: RAR解压失败或未解压出文件: {stderr.decode('utf-8', errors='ignore')}")
            return False
    except Exception as e:
        mod_logger.error(f"extract_rar_with_winrar: 解压异常: {e}")
        return False

def extract_rar_with_rarfile(rar_file, extract_dir):
    """使用rarfile库解压RAR文件"""
    try:
        mod_logger.info(f"extract_rar_with_rarfile: 开始解压 {rar_file} 到 {extract_dir}")
        mod_logger.info(f"extract_rar_with_rarfile: UNRAR_TOOL={rarfile.UNRAR_TOOL}, USE_EXTRACT_HACK={rarfile.USE_EXTRACT_HACK}")
        
        # 尝试使用rarfile库解压
        try:
            with rarfile.RarFile(rar_file) as rf:
                rf.extractall(extract_dir)
            mod_logger.info(f"extract_rar_with_rarfile: 成功解压 {rar_file}")
            return True
        except Exception as e:
            mod_logger.error(f"extract_rar_with_rarfile: 解压失败: {e}")
            
            # 如果是DLL兼容性问题，尝试使用subprocess直接调用命令行
            if "WinError 193" in str(e) or "不是有效的 Win32 应用程序" in str(e):
                mod_logger.info("检测到DLL兼容性问题，尝试使用subprocess直接调用unrar命令")
                
                # 尝试查找系统中的UnRAR.exe
                unrar_paths = [
                    "C:\\Program Files\\WinRAR\\UnRAR.exe",
                    "C:\\Program Files (x86)\\WinRAR\\UnRAR.exe",
                    os.path.expandvars("%ProgramFiles%\\WinRAR\\UnRAR.exe"),
                    os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\UnRAR.exe")
                ]
                
                unrar_exe = None
                for path in unrar_paths:
                    if os.path.exists(path):
                        unrar_exe = path
                        mod_logger.info(f"找到系统UnRAR.exe: {unrar_exe}")
                        break
                
                if unrar_exe:
                    try:
                        # 使用subprocess直接调用UnRAR.exe
                        cmd = f'"{unrar_exe}" x -o+ "{rar_file}" "{extract_dir}"'
                        mod_logger.info(f"执行命令: {cmd}")
                        
                        process = subprocess.Popen(
                            cmd,
                            stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE,
                            shell=True
                        )
                        stdout, stderr = process.communicate()
                        
                        if process.returncode == 0:
                            mod_logger.info(f"使用subprocess成功解压 {rar_file}")
                            return True
                        else:
                            mod_logger.error(f"使用subprocess解压失败: 返回码 {process.returncode}")
                            mod_logger.error(f"stderr: {stderr.decode('utf-8', errors='ignore')}")
                    except Exception as sub_e:
                        mod_logger.error(f"使用subprocess解压失败: {sub_e}")
                else:
                    mod_logger.warning("未找到系统中的UnRAR.exe")
            
            return False
    except Exception as e:
        mod_logger.error(f"extract_rar_with_rarfile: 解压失败: {e}")
        return False

def extract_rar_with_python(rar_file, extract_path):
    """使用纯Python方法解压RAR文件（不依赖外部工具）"""
    try:
        mod_logger.info(f"extract_rar_with_python: 尝试使用纯Python方法解压 {rar_file}")
        
        # 尝试使用rarfile库的内部解压功能
        old_use_extract_hack = rarfile.USE_EXTRACT_HACK
        rarfile.USE_EXTRACT_HACK = True
        
        try:
            # 禁用外部工具，强制使用内部解压
            old_unrar_tool = rarfile.UNRAR_TOOL
            rarfile.UNRAR_TOOL = None
            
            with rarfile.RarFile(rar_file) as rf:
                rf.extractall(extract_path)
            
            mod_logger.info(f"extract_rar_with_python: 成功使用纯Python方法解压 {rar_file}")
            return True
        except Exception as e:
            mod_logger.error(f"extract_rar_with_python: 纯Python解压失败: {e}")
            return False
        finally:
            # 恢复原始配置
            rarfile.UNRAR_TOOL = old_unrar_tool
            rarfile.USE_EXTRACT_HACK = old_use_extract_hack
    except Exception as e:
        mod_logger.error(f"extract_rar_with_python: 解压失败: {e}")
        return False

def process_archive(file_path, extract_path):
    """处理归档文件，根据文件类型选择不同的解压方法"""
    try:
        file_path = os.path.abspath(file_path)
        extract_path = os.path.abspath(extract_path)
        
        # 确保目标目录存在
        os.makedirs(extract_path, exist_ok=True)
        
        # 获取文件扩展名
        _, ext = os.path.splitext(file_path)
        ext = ext.lower()
        
        # 根据文件类型选择解压方法
        if ext == '.zip':
            mod_logger.info(f"处理zip文件: {file_path} -> {extract_path}")
            return extract_zip(file_path, extract_path)
        elif ext == '.rar':
            mod_logger.info(f"处理rar文件: {file_path} -> {extract_path}")
            
            # 尝试使用rarfile库解压
            mod_logger.info("尝试使用rarfile库解压")
            if extract_rar_with_rarfile(file_path, extract_path):
                return True
            
            # 尝试使用纯Python方法解压
            mod_logger.info("尝试使用纯Python方法解压")
            if extract_rar_with_python(file_path, extract_path):
                return True
            
            # 如果rarfile库解压失败，尝试使用WinRAR解压
            mod_logger.warning("rarfile库解压失败")
            mod_logger.info("尝试使用WinRAR解压")
            if extract_rar_with_winrar(file_path, extract_path):
                return True
            
            mod_logger.warning("WinRAR解压失败")
            
            # 尝试使用7z解压
            mod_logger.info("尝试使用7z解压")
            if extract_with_7z(file_path, extract_path):
                return True
            
            # 所有方法都失败，复制原始文件到目标文件夹
            mod_logger.warning("所有解压方法都失败，复制原始文件到目标文件夹")
            shutil.copy2(file_path, os.path.join(extract_path, os.path.basename(file_path)))
            
            # 显示错误信息
            error_message = f"无法解压RAR文件: {os.path.basename(file_path)}\n这可能是由于以下原因之一：\n- 系统未安装WinRAR\n- 文件已损坏\n- 加密的RAR文件\n\n已将原始文件复制到目标文件夹。: {file_path}"
            mod_logger.error(error_message)
            
            return False
        elif ext == '.7z':
            mod_logger.info(f"处理7z文件: {file_path} -> {extract_path}")
            return extract_7z(file_path, extract_path)
        else:
            # 不支持的格式，直接复制文件
            mod_logger.warning(f"不支持的文件格式: {ext}，直接复制文件")
            shutil.copy2(file_path, os.path.join(extract_path, os.path.basename(file_path)))
            return True
    except Exception as e:
        mod_logger.error(f"处理归档文件失败: {e}")
        return False

def extract_with_7z(file_path, extract_path):
    """使用7z命令行工具解压文件"""
    try:
        # 创建临时目录用于解压
        temp_dir = create_temp_directory()
        
        # 尝试查找7z.exe的路径
        seven_zip_paths = [
            "C:\\Program Files\\7-Zip\\7z.exe",
            "C:\\Program Files (x86)\\7-Zip\\7z.exe",
            os.path.expandvars("%ProgramFiles%\\7-Zip\\7z.exe"),
            os.path.expandvars("%ProgramFiles(x86)%\\7-Zip\\7z.exe"),
            # 添加更多可能的路径
            "C:\\Program Files\\WinRAR\\7z.exe",
            "C:\\Program Files (x86)\\WinRAR\\7z.exe",
            os.path.expandvars("%ProgramFiles%\\WinRAR\\7z.exe"),
            os.path.expandvars("%ProgramFiles(x86)%\\WinRAR\\7z.exe"),
            # 在系统PATH中查找
            "7z.exe",
            "7z"
        ]
        
        seven_zip_exe = None
        for path in seven_zip_paths:
            try:
                # 对于相对路径，尝试使用which/where命令查找
                if path in ["7z.exe", "7z"]:
                    try:
                        # 在Windows上使用where命令
                        if os.name == 'nt':
                            result = subprocess.run(['where', path], capture_output=True, text=True, check=False)
                            if result.returncode == 0 and result.stdout.strip():
                                seven_zip_exe = result.stdout.strip().split('\n')[0]
                                mod_logger.info(f"在PATH中找到7z: {seven_zip_exe}")
                                break
                        # 在类Unix系统上使用which命令
                        else:
                            result = subprocess.run(['which', path], capture_output=True, text=True, check=False)
                            if result.returncode == 0 and result.stdout.strip():
                                seven_zip_exe = result.stdout.strip()
                                mod_logger.info(f"在PATH中找到7z: {seven_zip_exe}")
                                break
                    except Exception as e:
                        mod_logger.debug(f"查找7z路径失败: {e}")
                        continue
                # 对于绝对路径，直接检查文件是否存在
                elif os.path.exists(path):
                    seven_zip_exe = path
                    mod_logger.info(f"找到7z: {seven_zip_exe}")
                    break
            except Exception as e:
                mod_logger.debug(f"检查7z路径失败: {path}: {e}")
        
        if not seven_zip_exe:
            mod_logger.warning("未找到7z.exe")
            return False
        
        # 使用7z命令解压
        cmd = f'"{seven_zip_exe}" x -y -o"{extract_path}" "{file_path}"'
        mod_logger.info(f"执行7z命令: {cmd}")
        
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=True
        )
        stdout, stderr = process.communicate()
        
        if process.returncode == 0:
            mod_logger.info(f"7z解压成功: {file_path}")
            return True
        else:
            stderr_str = stderr.decode('utf-8', errors='ignore')
            mod_logger.warning(f"7z解压失败: {stderr_str}")
            return False
    except Exception as e:
        mod_logger.warning(f"7z解压失败: {e}")
        return False

class ModManager:
    """MOD管理器类"""
    
    VERSION = "1.63"  # 版本号
    
    def __init__(self, config_manager):
        self.config = config_manager
        self.game_path = Path(config_manager.get_game_path())
        
        # 添加更多的错误处理和日志记录
        try:
            mods_path = config_manager.get_mods_path()
            mod_logger.info(f"ModManager初始化: 获取到MOD路径 {mods_path}")
            
            if not mods_path:
                mod_logger.warning("ModManager初始化: MOD路径为空")
                # 如果MOD路径为空，尝试使用默认路径
                default_mods_path = Path.cwd() / "mods"
                mod_logger.info(f"ModManager初始化: 使用默认MOD路径 {default_mods_path}")
                self.mods_path = default_mods_path
            else:
                self.mods_path = Path(mods_path)
                
            mod_logger.info(f"ModManager初始化: 最终MOD路径 {self.mods_path}")
        except Exception as e:
            mod_logger.error(f"ModManager初始化: 设置MOD路径时出错: {str(e)}")
            # 如果出错，使用当前目录下的mods作为备选
            self.mods_path = Path.cwd() / "mods"
            mod_logger.info(f"ModManager初始化: 使用备选MOD路径 {self.mods_path}")
            
        # 使用专用的临时文件夹，避免使用系统默认的临时目录
        self.temp_base_path = Path.cwd() / "mod_temp"
        
        # 确保必要的目录存在
        try:
            mod_logger.info(f"ModManager初始化: 确保MOD路径存在 {self.mods_path}")
            self.mods_path.mkdir(parents=True, exist_ok=True)
            mod_logger.info(f"ModManager初始化: MOD路径创建成功或已存在")
        except Exception as e:
            mod_logger.error(f"ModManager初始化: 创建MOD目录失败: {str(e)}")
            
        try:
            mod_logger.info(f"ModManager初始化: 确保临时基础路径存在 {self.temp_base_path}")
            self.temp_base_path.mkdir(parents=True, exist_ok=True)
            mod_logger.info(f"ModManager初始化: 临时基础路径创建成功或已存在")
        except Exception as e:
            mod_logger.error(f"ModManager初始化: 创建临时基础路径失败: {str(e)}")
            # 如果无法创建自定义临时目录，使用备选路径
            self.temp_base_path = Path.cwd() / "temp"
            try:
                self.temp_base_path.mkdir(parents=True, exist_ok=True)
                mod_logger.info(f"ModManager初始化: 使用备选临时路径 {self.temp_base_path}")
            except Exception as e2:
                mod_logger.error(f"ModManager初始化: 创建备选临时目录也失败: {str(e2)}")
                # 不再尝试

    def scan_mods_directory(self):
        """递归扫描MOD目录及子目录，支持子文件夹下同名pak/ucas/utoc"""
        print(f"[调试] scan_mods_directory: 开始扫描 {self.mods_path}")
        found_mods = []
        mod_names_seen = set()  # 用于跟踪已经看到的MOD名称
        
        try:
            if not self.mods_path.exists():
                print(f"[调试] scan_mods_directory: 创建MOD目录 {self.mods_path}")
                self.mods_path.mkdir(parents=True, exist_ok=True)
                return found_mods
                
            # 获取现有MOD信息，用于保留用户自定义名称
            existing_mods = self.config.get_mods()
            # 创建文件路径到MOD ID的映射，用于识别重命名的MOD
            path_to_mod_id = {}
            for mod_id, mod_info in existing_mods.items():
                if 'original_path' in mod_info:
                    path_to_mod_id[mod_info['original_path']] = mod_id
            
            # 递归查找所有pak文件
            for file in self.mods_path.rglob('*.pak'):
                ucas_file = file.with_suffix('.ucas')
                utoc_file = file.with_suffix('.utoc')
                # 必须同目录下有同名ucas/utoc才算完整MOD
                if ucas_file.exists() and utoc_file.exists():
                    mod_name = file.stem
                    folder_name = file.parent.name
                    
                    # 创建MOD ID，优先使用mod_name，如果已存在则使用folder_name_mod_name
                    mod_id = mod_name
                    if mod_name in mod_names_seen:
                        # 如果MOD名称已存在，使用文件夹名称+MOD名称作为唯一标识
                        if folder_name != "~mods":
                            mod_id = f"{folder_name}_{mod_name}"
                            print(f"[调试] scan_mods_directory: MOD名称重复，使用文件夹名称作为标识: {mod_id}")
                    
                    mod_names_seen.add(mod_name)  # 记录已看到的MOD名称
                    print(f"[调试] scan_mods_directory: 找到完整MOD {mod_name} in {file.parent}, MOD ID: {mod_id}")
                    
                    # 确定文件相对路径
                    rel_path = file.parent.relative_to(self.mods_path) if file.parent != self.mods_path else Path("")
                    
                    # 收集同目录下的所有文件
                    all_files = []
                    if rel_path == Path(""):
                        # MOD文件直接在mods目录中
                        all_files = [
                            str(file.name),
                            str(ucas_file.name),
                            str(utoc_file.name)
                        ]
                        # 添加同目录下的其他文件
                        for other_file in file.parent.glob('*'):
                            if other_file.is_file() and other_file.name not in [file.name, ucas_file.name, utoc_file.name]:
                                all_files.append(str(other_file.name))
                    else:
                        # MOD文件在子目录中
                        all_files = [
                            str(rel_path / file.name),
                            str(rel_path / ucas_file.name),
                            str(rel_path / utoc_file.name)
                        ]
                        # 添加同目录下的其他文件
                        for other_file in file.parent.glob('*'):
                            if other_file.is_file() and other_file.name not in [file.name, ucas_file.name, utoc_file.name]:
                                all_files.append(str(rel_path / other_file.name))
                    
                    # 检查是否是已知的MOD（通过文件路径匹配）
                    existing_mod_id = path_to_mod_id.get(str(file), None)
                    if existing_mod_id and existing_mod_id in existing_mods:
                        # 如果是已知MOD，保留其ID和用户自定义名称
                        existing_mod_info = existing_mods[existing_mod_id]
                        print(f"[调试] scan_mods_directory: 匹配到已知MOD: {existing_mod_id}")
                        
                        mod_info = {
                            'name': existing_mod_info.get('name', mod_id),  # 保留用户自定义名称
                            'files': all_files,  # 更新文件列表
                            'original_path': str(file),
                            'import_date': existing_mod_info.get('import_date', datetime.now().strftime('%Y-%m-%d %H:%M:%S')),
                            'enabled': existing_mod_info.get('enabled', True),
                            'size': round(file.stat().st_size / (1024 * 1024), 2),
                            'folder_structure': str(rel_path) != "",  # 标记是否在子文件夹中
                            'folder_name': folder_name,  # 记录文件夹名称
                            'display_name': existing_mod_info.get('display_name', mod_name),  # 保留显示名称
                            'mod_name': mod_name,  # 保存原始MOD名称
                            'real_name': existing_mod_info.get('real_name', mod_name),  # 保留原始名称
                            'preview_image': existing_mod_info.get('preview_image', '')  # 保留预览图
                        }
                        
                        # 保留其他自定义属性
                        for key, value in existing_mod_info.items():
                            if key not in mod_info:
                                mod_info[key] = value
                        
                        # 使用原始MOD ID
                        mod_id = existing_mod_id
                    else:
                        # 新发现的MOD
                        mod_info = {
                            'name': mod_id,  # 使用MOD ID作为唯一标识
                            'files': all_files,  # 包含所有文件，包括txt等
                            'original_path': str(file),
                            'import_date': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                            'enabled': True,
                            'size': round(file.stat().st_size / (1024 * 1024), 2),
                            'folder_structure': str(rel_path) != "",  # 标记是否在子文件夹中
                            'folder_name': folder_name,  # 记录文件夹名称
                            'display_name': mod_name,  # 用于UI显示的名称
                            'mod_name': mod_name  # 保存原始MOD名称
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
        
        # 记录所有创建的临时目录
        temp_dirs = []
        
        def recursive_extract(path, to_dir, visited=None, depth=0):
            if visited is None:
                visited = set()
            if str(path) in visited:
                mod_logger.info(f"跳过已处理压缩包: {path}")
                return
            visited.add(str(path))
            if depth > 10:
                raise RuntimeError(f"递归解压深度过大，可能存在嵌套死循环: {path}")
            try:
                # 使用更安全的解压方法
                if zipfile.is_zipfile(path):
                    mod_logger.info(f"解压zip: {path} -> {to_dir}")
                    with zipfile.ZipFile(path, 'r') as z:
                        z.extractall(to_dir)
                elif str(path).lower().endswith('.rar'):
                    mod_logger.info(f"处理rar文件: {path} -> {to_dir}")
                    extracted = False
                    
                    # 1. 首先尝试使用rarfile库解压（使用内置UnRAR.dll）
                    try:
                        mod_logger.info(f"尝试使用rarfile库解压")
                        if extract_rar_with_rarfile(path, to_dir):
                            mod_logger.info(f"rarfile库解压成功")
                            extracted = True
                        else:
                            mod_logger.warning(f"rarfile库解压失败")
                    except Exception as e:
                        mod_logger.error(f"rarfile库解压失败: {e}")
                    
                    # 2. 如果rarfile库解压失败，尝试使用WinRAR
                    if not extracted:
                        try:
                            mod_logger.info(f"尝试使用WinRAR解压")
                            if extract_rar_with_winrar(path, to_dir):
                                mod_logger.info(f"WinRAR解压成功")
                                extracted = True
                            else:
                                mod_logger.warning(f"WinRAR解压失败")
                        except Exception as e:
                            mod_logger.error(f"WinRAR解压失败: {e}")
                    
                    # 3. 如果WinRAR解压失败，尝试使用7z命令行工具解压
                    if not extracted:
                        try:
                            # 创建临时目录
                            temp_dir = create_temp_directory(self.temp_base_path)
                            temp_dirs.append(temp_dir)
                            
                            # 尝试使用7z命令行工具解压
                            process = subprocess.Popen(
                                ["7z", "x", str(path), f"-o{temp_dir}", "-y"],
                                stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE,
                                shell=True
                            )
                            stdout, stderr = process.communicate()
                            
                            if process.returncode == 0:
                                mod_logger.info(f"7z解压成功，将文件从临时目录复制到目标目录")
                                # 复制解压后的文件到目标目录
                                for item in Path(temp_dir).glob('*'):
                                    dest_path = Path(to_dir) / item.name
                                    if item.is_dir():
                                        if dest_path.exists():
                                            shutil.rmtree(dest_path)
                                        shutil.copytree(item, dest_path)
                                    else:
                                        shutil.copy2(item, dest_path)
                                mod_logger.info(f"文件已复制到目标目录")
                                extracted = True
                            else:
                                stderr_text = stderr.decode('utf-8', errors='ignore')
                                mod_logger.warning(f"7z解压失败: {stderr_text}")
                                # 将错误信息记录到日志中
                                if stderr_text:
                                    with open("mod_manager_errors.log", "a", encoding="utf-8") as f:
                                        f.write(f"{datetime.now()} - 7z解压失败 - {path}:\n")
                                        f.write(stderr_text)
                                        f.write("\n\n")
                        except Exception as e:
                            mod_logger.error(f"7z解压失败: {e}")
                            traceback.print_exc()
                        finally:
                            # 临时目录的清理会在最外层函数完成
                            pass
                    
                    # 如果所有解压方法都失败，则复制原始文件
                    if not extracted:
                        dest_file = Path(to_dir) / Path(path).name
                        shutil.copy2(path, dest_file)
                        mod_logger.info(f"RAR文件已复制到目标文件夹: {dest_file}")
                        # 弹出错误提示
                        error_msg = f"无法解压RAR文件: {Path(path).name}\n这可能是由于以下原因之一：\n- 系统未安装WinRAR\n- 文件已损坏\n- 加密的RAR文件\n\n已将原始文件复制到目标文件夹。"
                        log_and_show_error(error_msg, "RAR解压失败", detailed=str(path))
                    
                elif str(path).lower().endswith('.7z'):
                    mod_logger.info(f"解压7z: {path} -> {to_dir}")
                    try:
                        with py7zr.SevenZipFile(path, 'r') as sz:
                            sz.extractall(to_dir)
                    except Exception as e:
                        mod_logger.warning(f"使用py7zr解压失败: {e}")
                        traceback.print_exc()
                        # 复制文件作为备选
                        dest_file = Path(to_dir) / Path(path).name
                        shutil.copy2(path, dest_file)
                        mod_logger.info(f"7z文件已复制到目标文件夹: {dest_file}")
                
                # 解压后递归查找新压缩包，但跳过RAR文件
                for f in Path(to_dir).glob('**/*'):
                    if f.is_file() and (zipfile.is_zipfile(f) or str(f).lower().endswith('.7z') or str(f).lower().endswith('.rar')):
                        recursive_extract(f, to_dir, visited, depth+1)
            except Exception as e:
                mod_logger.error(f"recursive_extract异常: {path} -> {to_dir}, 错误: {e}")
                traceback.print_exc()
                # 记录详细错误到日志
                error_details = traceback.format_exc()
                # 弹出错误提示
                error_msg = f"解压文件时发生错误: {Path(path).name}\n\n错误信息已写入日志文件。"
                log_and_show_error(error_msg, "解压失败", detailed=error_details)
                # 不抛出异常，尝试继续处理其他文件
                # 复制文件作为备选
                try:
                    dest_file = Path(to_dir) / Path(path).name
                    shutil.copy2(path, dest_file)
                    mod_logger.info(f"文件已复制到目标文件夹: {dest_file}")
                except Exception as copy_e:
                    mod_logger.warning(f"复制文件也失败了: {copy_e}")
        
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
            temp_extract_dir = create_temp_directory(self.temp_base_path)
            temp_dirs.append(temp_extract_dir)
            temp_extract_dir = Path(temp_extract_dir)
            mod_logger.info(f"extract_nested_archive: 创建临时目录: {temp_extract_dir}")
            
            try:
                # 解压嵌套压缩包到临时目录
                recursive_extract(archive_path, temp_extract_dir)
                
                # 查找解压后的MOD文件
                mod_groups = find_mod_files(temp_extract_dir)
                
                if mod_groups:
                    mod_logger.info(f"extract_nested_archive: 在压缩包 {archive_path.name} 中找到 {len(mod_groups)} 组MOD文件")
                    
                    # 过滤掉已处理过的MOD名称
                    filtered_mod_groups = [group for group in mod_groups if group['name'] not in processed_mod_names]
                    
                    if filtered_mod_groups:
                        # 使用MOD文件名作为MOD文件夹名称，而不是压缩包名称
                        processed_mods = []
                        for mod_group in filtered_mod_groups:
                            mod_name = mod_group['name']
                            processed_mod_names.add(mod_name)

                            # 只有当有实际文件时才创建目录
                            if mod_group['files']:
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
                            else:
                                mod_logger.info(f"extract_nested_archive: MOD组 {mod_name} 没有文件，跳过")
                        
                        return processed_mods
                    else:
                        mod_logger.info(f"extract_nested_archive: 所有MOD已在主压缩包中处理，跳过")
                        return None
                
                # 如果没有找到MOD文件，检查是否有更深层次的嵌套压缩包
                nested_archives = check_nested_archives(temp_extract_dir)
                if nested_archives:
                    mod_logger.info(f"extract_nested_archive: 在压缩包 {archive_path.name} 中找到 {len(nested_archives)} 个嵌套压缩包")
                    
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
                    mod_logger.info(f"extract_nested_archive: 清理临时目录: {temp_extract_dir}")
        
        try:
            mod_logger.info(f"import_mod: 开始导入MOD文件 {file_path}")
            original_zip = Path(file_path)
            mods_path = Path(self.config.get_mods_path())
            
            # 创建临时工作目录用于解压
            temp_work_dir = create_temp_directory(self.temp_base_path)
            temp_dirs.append(temp_work_dir)
            temp_work_dir = Path(temp_work_dir)
            mod_logger.info(f"import_mod: 创建临时工作目录: {temp_work_dir}")
            
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
                    mod_logger.info(f"import_mod: 在主压缩包中找到 {len(direct_mod_groups)} 组MOD文件")
                    
                    # 为每个MOD组创建独立文件夹
                    for mod_group in direct_mod_groups:
                        mod_name = mod_group['name']
                        processed_mod_names.add(mod_name)
                        
                        # 只有当MOD组有实际文件时才进行处理
                        if mod_group['files']:
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
                        else:
                            mod_logger.info(f"import_mod: MOD组 {mod_name} 没有文件，跳过")
                
                # 处理嵌套的压缩包
                if nested_archives:
                    mod_logger.info(f"import_mod: 发现 {len(nested_archives)} 个嵌套压缩包")
                    
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
                    mod_logger.info(f"import_mod: 未找到MOD文件，尝试创建虚拟MOD")
                    
                    # 检查原始文件是否存在
                    if original_zip.exists():
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
                    else:
                        mod_logger.warning(f"import_mod: 原始文件不存在，无法创建虚拟MOD: {original_zip}")
                
                # 返回导入的MOD列表
                if len(imported_mods) == 1:
                    return imported_mods[0]  # 兼容旧版本接口
                else:
                    return imported_mods
                
            finally:
                # 清理临时工作目录
                if temp_work_dir.exists():
                    shutil.rmtree(temp_work_dir)
                    mod_logger.info(f"import_mod: 清理临时工作目录: {temp_work_dir}")
            
        except Exception as e:
            mod_logger.error(f"import_mod: 导入失败 {str(e)}")
            raise

    def enable_mod(self, mod_id):
        """启用MOD"""
        try:
            mod_logger.info(f"enable_mod: 开始启用MOD {mod_id}")
            
            # 获取MOD信息
            mods = self.config.get_mods()
            mod_info = mods.get(mod_id)
            if not mod_info:
                mod_logger.error(f"enable_mod: 找不到MOD信息: {mod_id}")
                return False
                
            # 获取备份路径
            backup_path = Path(self.config.get_backup_path())
            if not str(backup_path).strip():
                backup_path = Path(os.getcwd()) / "modbackup"
                
            # 尝试查找备份目录
            mod_backup_dir = backup_path / mod_id
            
            # 如果指定的备份目录不存在，尝试查找其他可能的备份目录
            if not mod_backup_dir.exists() or not any(f for f in mod_backup_dir.glob('*.*') if f.name != 'preview.png'):
                mod_logger.warning(f"enable_mod: MOD {mod_id} 的备份目录不存在或没有MOD文件: {mod_backup_dir}")
                
                # 尝试查找可能的备份目录
                possible_backup_dirs = []
                
                # 检查预览图路径
                preview_image = mod_info.get('preview_image', '')
                if preview_image and os.path.exists(preview_image):
                    preview_dir = Path(preview_image).parent
                    if preview_dir.exists() and any(f for f in preview_dir.glob('*.*') if f.name != 'preview.png'):
                        possible_backup_dirs.append(preview_dir)
                        mod_logger.info(f"enable_mod: 从预览图路径找到可能的备份目录: {preview_dir}")
                
                # 检查其他可能的备份目录名
                base_names = [
                    mod_info.get('real_name', ''),
                    mod_info.get('display_name', ''),
                    mod_info.get('mod_name', ''),
                    mod_info.get('folder_name', '')
                ]
                
                # 移除空字符串和重复项
                base_names = list(set([name for name in base_names if name]))
                
                for name in base_names:
                    dir_path = backup_path / name
                    if dir_path.exists() and any(f for f in dir_path.glob('*.*') if f.name != 'preview.png'):
                        possible_backup_dirs.append(dir_path)
                        mod_logger.info(f"enable_mod: 找到可能的备份目录: {dir_path}")
                
                # 使用找到的第一个备份目录
                if possible_backup_dirs:
                    mod_backup_dir = possible_backup_dirs[0]
                    mod_logger.info(f"enable_mod: 使用备份目录: {mod_backup_dir}")
                else:
                    # 最后尝试在所有备份目录中查找
                    for backup_dir in backup_path.glob('*'):
                        if backup_dir.is_dir() and any(f for f in backup_dir.glob('*.*') if f.name != 'preview.png'):
                            possible_backup_dirs.append(backup_dir)
                            mod_logger.info(f"enable_mod: 找到其他备份目录: {backup_dir}")
                    
                    if possible_backup_dirs:
                        mod_backup_dir = possible_backup_dirs[0]
                        mod_logger.info(f"enable_mod: 使用其他备份目录: {mod_backup_dir}")
                    else:
                        mod_logger.error(f"enable_mod: MOD {mod_id} 没有可用的备份")
                        return False
            
            # 获取MOD目录路径
            mods_path = Path(self.config.get_mods_path())
            if not mods_path.exists():
                mods_path.mkdir(parents=True, exist_ok=True)
                
            # 获取MOD文件列表（排除预览图）
            backup_files = [f for f in mod_backup_dir.glob('*.*') if f.name != 'preview.png']
            if not backup_files:
                mod_logger.error(f"enable_mod: 备份目录没有MOD文件: {mod_backup_dir}")
                return False
                
            # 确定MOD文件夹名称
            folder_name = mod_info.get('folder_name', '')
            if not folder_name:
                # 尝试从文件路径获取文件夹名称
                if mod_info.get('files'):
                    first_file = mod_info['files'][0]
                    if '/' in first_file:
                        folder_name = first_file.split('/')[0]
                    elif '\\' in first_file:
                        folder_name = first_file.split('\\')[0]
                        
                # 如果仍然没有找到，使用备份文件名称推断
                if not folder_name and backup_files:
                    first_file = backup_files[0].name
                    if '.' in first_file:
                        folder_name = first_file.split('.')[0]
                
                # 如果仍然没有找到，使用MOD ID作为文件夹名称
                if not folder_name:
                    folder_name = mod_id
                    
            # 创建MOD目录
            mod_dir = mods_path / folder_name
            mod_dir.mkdir(parents=True, exist_ok=True)
            
            # 复制文件
            copied_count = 0
            for src_file in backup_files:
                if src_file.is_file():
                    dest_file = mod_dir / src_file.name
                    mod_logger.info(f"enable_mod: 复制文件 {src_file} -> {dest_file}")
                    shutil.copy2(src_file, dest_file)
                    copied_count += 1
                    
            if copied_count == 0:
                mod_logger.error(f"enable_mod: 没有复制任何文件")
                # 清理创建的空目录
                if mod_dir.exists() and not list(mod_dir.glob('*')):
                    shutil.rmtree(mod_dir)
                return False
                
            # 更新MOD状态
            mod_info['enabled'] = True
            self.config.update_mod(mod_id, mod_info)
            
            mod_logger.info(f"enable_mod: MOD {mod_id} 已启用，复制了 {copied_count} 个文件")
            return True
            
        except Exception as e:
            mod_logger.error(f"enable_mod: 启用MOD失败: {e}")
            import traceback
            traceback.print_exc()
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
                    mod_logger.info(f"disable_mod: 删除文件 {file_path}")
                    
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
                            mod_logger.info(f"disable_mod: 删除空目录 {parent_dir}")
            
            mod_info['enabled'] = False
            self.config.update_mod(mod_id, mod_info)
            return True
        except Exception as e:
            mod_logger.error(f"disable_mod: 禁用失败 {str(e)}")
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
        """设置MOD的预览图"""
        try:
            mod_logger.info(f"set_preview_image: 为MOD {mod_id} 设置预览图: {image_path}")
            
            # 获取MOD信息
            mods = self.config.get_mods()
            mod_info = mods.get(mod_id)
            if not mod_info:
                mod_logger.error(f"set_preview_image: 找不到MOD信息: {mod_id}")
                return False
                
            # 获取备份路径
            backup_path = self.config.get_backup_path()
            if not backup_path:
                backup_path = os.path.join(os.getcwd(), "modbackup")
            mod_logger.info(f"set_preview_image: 使用备份路径: {backup_path}")
            
            # 确保备份路径存在
            backup_path = Path(backup_path)
            if not backup_path.exists():
                backup_path.mkdir(parents=True, exist_ok=True)
                
            # 创建MOD专属备份目录
            mod_backup_dir = backup_path / mod_id
            if not mod_backup_dir.exists():
                mod_backup_dir.mkdir(parents=True, exist_ok=True)
                
            # 复制预览图到备份目录
            image_ext = os.path.splitext(image_path)[1]
            preview_path = mod_backup_dir / f"preview{image_ext}"
            mod_logger.info(f"set_preview_image: 复制预览图: {image_path} -> {preview_path}")
            shutil.copy2(image_path, preview_path)
            
            # 更新MOD信息
            mod_info['preview_image'] = str(preview_path)
            self.config.update_mod(mod_id, mod_info)
            
            # 验证预览图路径是否正确保存
            updated_mods = self.config.get_mods()
            updated_mod_info = updated_mods.get(mod_id)
            if updated_mod_info:
                saved_preview_path = updated_mod_info.get('preview_image', '')
                if saved_preview_path != str(preview_path):
                    mod_logger.warning(f"set_preview_image: 预览图路径未正确保存，期望: {preview_path}，实际: {saved_preview_path}")
                    # 再次尝试更新
                    updated_mod_info['preview_image'] = str(preview_path)
                    self.config.update_mod(mod_id, updated_mod_info)
                else:
                    mod_logger.info(f"set_preview_image: 预览图路径已正确保存: {saved_preview_path}")
            
            return True
        except Exception as e:
            mod_logger.error(f"set_preview_image: 设置预览图失败: {e}")
            import traceback
            traceback.print_exc()
            return False

    def backup_mod(self, mod_id, mod_info=None):
        """备份MOD文件"""
        try:
            mod_logger.info(f"ModManager.backup_mod: 开始备份MOD {mod_id}")
            
            # 如果没有提供mod_info，从配置获取
            if not mod_info:
                mods = self.config.get_mods()
                mod_info = mods.get(mod_id)
                if not mod_info:
                    mod_logger.error(f"ModManager.backup_mod: 找不到MOD信息: {mod_id}")
                    return False
            
            # 确保备份路径已设置
            backup_path = self.config.get_backup_path()
            if not backup_path or not str(backup_path).strip():
                default_backup_path = os.path.join(os.getcwd(), "modbackup")
                mod_logger.info(f"ModManager.backup_mod: 设置默认备份路径: {default_backup_path}")
                self.config.set_backup_path(default_backup_path)
            
            # 调用ConfigManager的backup_mod方法进行备份
            result = self.config.backup_mod(mod_id, mod_info)
            mod_logger.info(f"ModManager.backup_mod: 备份结果: {result}")
            return result
            
        except Exception as e:
            mod_logger.error(f"ModManager.backup_mod: 备份失败: {e}")
            import traceback
            traceback.print_exc()
            return False

    def restore_mod_from_backup(self, mod_id, backup_path):
        """从备份目录恢复MOD文件到游戏目录"""
        try:
            mod_logger.info(f"restore_mod_from_backup: 开始从 {backup_path} 恢复MOD {mod_id}")
            mods = self.config.get_mods()
            if mod_id not in mods:
                mod_logger.error(f"restore_mod_from_backup: 找不到MOD {mod_id}")
                return False
                
            mod = mods[mod_id]
            mod_files = mod.get('files', [])
            
            # 如果文件列表为空，尝试从备份目录推断
            if not mod_files:
                mod_logger.warning(f"restore_mod_from_backup: MOD {mod_id} 没有记录文件列表，将从备份目录推断")
                # 获取备份目录中的所有文件
                backup_files = list(backup_path.glob('**/*'))
                backup_files = [f for f in backup_files if f.is_file()]
                
                # 生成MOD目录路径
                mod_name = mod.get('folder_name') or mod.get('display_name') or mod.get('mod_name') or mod_id
                
                # 为每个备份文件构建目标路径
                for backup_file in backup_files:
                    # 计算相对于备份目录的路径
                    rel_path = backup_file.relative_to(backup_path)
                    # 构建完整的MOD文件路径
                    mod_file = f"{mod_name}/{str(rel_path)}"
                    mod_files.append(mod_file)
                
                # 更新MOD文件列表
                mod_logger.info(f"restore_mod_from_backup: 从备份推断的文件列表: {mod_files}")
                mod['files'] = mod_files
                self.config._save_config()
            
            # 将所有备份文件复制到游戏目录
            success_count = 0
            error_count = 0
            mods_path = Path(self.config.get_mods_path())
            
            for mod_file in mod_files:
                # 生成目标文件路径
                target_path = mods_path / mod_file
                
                # 确定备份文件路径
                # 如果有文件夹结构，需要去掉第一层（MOD目录名）
                file_parts = Path(mod_file).parts
                if len(file_parts) > 1:
                    # 使用除去第一个部分后的路径
                    rel_path = Path(*file_parts[1:])
                    source_path = backup_path / rel_path
                else:
                    # 直接从备份根目录获取
                    source_path = backup_path / file_parts[0]
                
                mod_logger.info(f"restore_mod_from_backup: 从 {source_path} 恢复到 {target_path}")
                
                # 检查源文件是否存在
                if not source_path.exists():
                    mod_logger.warning(f"restore_mod_from_backup: 备份文件不存在 {source_path}")
                    error_count += 1
                    continue
                    
                try:
                    # 确保目标目录存在
                    target_path.parent.mkdir(parents=True, exist_ok=True)
                    
                    # 复制文件
                    if not source_path.is_dir():
                        shutil.copy2(source_path, target_path)
                        mod_logger.info(f"restore_mod_from_backup: 成功恢复文件 {target_path}")
                        success_count += 1
                    else:
                        # 如果是目录，复制整个目录
                        if target_path.exists() and target_path.is_dir():
                            # 如果目标目录已存在，先清空
                            shutil.rmtree(target_path)
                        shutil.copytree(source_path, target_path)
                        mod_logger.info(f"restore_mod_from_backup: 成功恢复目录 {target_path}")
                        success_count += 1
                except Exception as e:
                    mod_logger.error(f"restore_mod_from_backup: 恢复文件 {source_path} 到 {target_path} 失败: {str(e)}")
                    error_count += 1
            
            mod_logger.info(f"restore_mod_from_backup: MOD {mod_id} 恢复完成，成功: {success_count}, 失败: {error_count}")
            return success_count > 0
                
        except Exception as e:
            mod_logger.error(f"restore_mod_from_backup: 恢复MOD {mod_id} 时出错: {str(e)}")
            traceback.print_exc()
            return False

    def get_game_exe_name(self):
        """获取游戏可执行文件名称"""
        return "SB-Win64-Shipping.exe"
