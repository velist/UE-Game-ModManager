import os
import winreg
from pathlib import Path

class GameLocator:
    def __init__(self):
        self.possible_paths = [
            # Steam安装路径
            Path(os.environ.get('ProgramFiles(x86)', '')) / 'Steam/steamapps/common/StellarBlade',
            Path('D:/Steam/steamapps/common/StellarBlade'),
            Path('E:/Steam/steamapps/common/StellarBlade'),
            # Epic Games安装路径
            Path(os.environ.get('ProgramFiles', '')) / 'Epic Games/StellarBlade',
            # 默认安装路径
            Path('C:/Program Files/StellarBlade'),
            Path('C:/Program Files (x86)/StellarBlade'),
        ]
        
    def find_game_paths(self):
        """查找所有可能的游戏可执行文件路径，返回列表"""
        found_paths = []
        
        # 首先从注册表查找
        reg_path = self.locate_game()
        if reg_path:
            exe_paths = self._find_exe_in_path(Path(reg_path))
            found_paths.extend(exe_paths)
            
        # 然后检查可能的安装路径
        for path in self.possible_paths:
            if path.exists():
                exe_paths = self._find_exe_in_path(path)
                for exe_path in exe_paths:
                    if exe_path not in found_paths:
                        found_paths.append(exe_path)
                        
        return found_paths
    
    def _find_exe_in_path(self, path):
        """在给定路径中查找游戏可执行文件"""
        exe_paths = []
        
        # 检查常见的可执行文件名
        exe_names = ['SB.exe', 'StellarBlade.exe', 'StellarBlade-Win64-Shipping.exe']
        for exe_name in exe_names:
            exe_path = path / exe_name
            if exe_path.exists():
                exe_paths.append(str(exe_path))
                
        # 如果没有找到，尝试递归查找
        if not exe_paths:
            try:
                for item in path.glob('**/*.exe'):
                    if item.name in exe_names:
                        exe_paths.append(str(item))
            except Exception:
                pass
                
        return exe_paths
        
    def locate_game(self):
        """尝试定位游戏安装目录"""
        # 首先检查注册表
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, 
                r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") as key:
                for i in range(winreg.QueryInfoKey(key)[0]):
                    try:
                        subkey_name = winreg.EnumKey(key, i)
                        with winreg.OpenKey(key, subkey_name) as subkey:
                            try:
                                display_name = winreg.QueryValueEx(subkey, "DisplayName")[0]
                                if "Stellar Blade" in display_name:
                                    install_location = winreg.QueryValueEx(subkey, "InstallLocation")[0]
                                    if os.path.exists(install_location):
                                        return install_location
                            except:
                                continue
                    except:
                        continue
        except:
            pass
            
        # 检查可能的安装路径
        for path in self.possible_paths:
            if path.exists() and self._is_valid_game_path(path):
                return str(path)
                
        return None
        
    def _is_valid_game_path(self, path):
        """验证是否为有效的游戏目录"""
        required_files = [
            'SB.exe',
            'StellarBlade.exe',
            'StellarBlade-Win64-Shipping.exe'
        ]
        
        # 检查游戏可执行文件
        for file in required_files:
            if (path / file).exists():
                return True
                
        return False 