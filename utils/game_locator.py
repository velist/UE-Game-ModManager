import os
import winreg
from pathlib import Path

class GameLocator:
    def __init__(self):
        self.possible_paths = [
            # Steam安装路径
            Path(os.environ.get('ProgramFiles(x86)', '')) / 'Steam/steamapps/common/Stellar Blade',
            # Epic Games安装路径
            Path(os.environ.get('ProgramFiles', '')) / 'Epic Games/Stellar Blade',
            # 默认安装路径
            Path('C:/Program Files/Stellar Blade'),
            Path('C:/Program Files (x86)/Stellar Blade'),
        ]
        
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
            'StellarBlade.exe',
            'StellarBlade-Win64-Shipping.exe'
        ]
        
        # 检查游戏可执行文件
        for file in required_files:
            if (path / file).exists():
                return True
                
        return False 