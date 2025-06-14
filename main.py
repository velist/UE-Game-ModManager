import sys
import os
from PySide6.QtWidgets import QApplication, QMainWindow, QMessageBox, QFileDialog, QWidget, QHBoxLayout, QVBoxLayout, QLabel, QFrame, QScrollArea, QListWidget
from PySide6.QtCore import Qt
from ui.main_window import MainWindow
from utils.config_manager import ConfigManager
from utils.game_locator import GameLocator
from utils.mod_manager import ModManager
from pathlib import Path

def main():
    print("[调试] 启动 QApplication")
    app = QApplication(sys.argv)
    
    print("[调试] 初始化配置管理器")
    config = ConfigManager()
    
    print("[调试] 检查是否首次运行")
    if not config.is_initialized():
        # 1. 先弹窗提醒需要绑定游戏路径
        QMessageBox.information(None, "提示", "首次使用需绑定游戏路径。请点击确定后选择游戏可执行文件。")
        
        # 尝试自动查找游戏路径
        possible_paths = []
        steam_paths = [
            "C:/Program Files (x86)/Steam/steamapps/common/StellarBlade",
            "D:/Steam/steamapps/common/StellarBlade",
            "E:/Steam/steamapps/common/StellarBlade",
            "F:/Steam/steamapps/common/StellarBlade"
        ]
        exe_names = ["SB.exe", "BootstrapPackagedGame-Win64-Shipping.exe"]
        for base_path in steam_paths:
            for exe_name in exe_names:
                full_path = Path(base_path) / exe_name
                if full_path.exists():
                    possible_paths.append(str(full_path))
        
        game_path = None
        if possible_paths:
            paths_str = "\n".join([f"{i+1}. {path}" for i, path in enumerate(possible_paths)])
            auto_reply = QMessageBox.question(None, "找到游戏", 
                                           f"找到以下可能的游戏路径，是否使用？\n{paths_str}",
                                           QMessageBox.Yes | QMessageBox.No)
            if auto_reply == QMessageBox.Yes:
                if len(possible_paths) == 1:
                    game_path = possible_paths[0]
                    config.set_game_path(game_path)
                    QMessageBox.information(None, "成功", f"游戏路径已设置为: {game_path}")
                else:
                    from PySide6.QtWidgets import QInputDialog
                    path, ok = QInputDialog.getItem(
                        None, '选择游戏路径', '请选择游戏路径:', 
                        possible_paths, 0, False
                    )
                    if ok and path:
                        game_path = path
                        config.set_game_path(game_path)
                        QMessageBox.information(None, "成功", f"游戏路径已设置为: {game_path}")
        
        if not game_path:
            # 手动选择游戏路径
            game_path, _ = QFileDialog.getOpenFileName(
                None, "选择游戏可执行文件", "", "游戏可执行文件 (*.exe)"
            )
            if game_path:
                config.set_game_path(game_path)
                QMessageBox.information(None, "成功", f"游戏路径已设置为: {game_path}")
            else:
                QMessageBox.critical(None, "错误", "未选择游戏路径，程序退出")
                sys.exit(1)
        
        # 2. 根据游戏路径自动设置MOD文件夹
        try:
            # 获取游戏根目录
            game_exe = Path(game_path)
            game_dir = game_exe.parent
            
            # 构建MOD文件夹路径
            if "StellarBlade" in str(game_dir):
                # 找到游戏根目录
                while game_dir.name != "StellarBlade" and game_dir.parent != game_dir:
                    game_dir = game_dir.parent
                    
                # 构建MOD文件夹路径
                mods_path = game_dir / "SB" / "Content" / "Paks" / "~mods"
                
                # 如果MOD文件夹不存在，创建它
                if not mods_path.exists():
                    print(f"[调试] 创建MOD文件夹: {mods_path}")
                    mods_path.mkdir(parents=True, exist_ok=True)
                
                # 设置MOD文件夹路径
                if mods_path.exists():
                    config.set_mods_path(str(mods_path))
                    QMessageBox.information(None, "成功", f"MOD文件夹已自动设置为: {mods_path}")
                else:
                    raise Exception("无法创建MOD文件夹")
            else:
                raise Exception("无法从游戏路径找到StellarBlade目录")
                
        except Exception as e:
            print(f"[调试] 自动设置MOD文件夹失败: {e}")
            # 如果自动设置失败，手动选择MOD文件夹
            QMessageBox.information(None, "提示", "自动设置MOD文件夹失败，请手动选择MOD文件夹。")
            while True:
                mods_path = QFileDialog.getExistingDirectory(None, "选择MOD目录（用于游戏加载MOD）")
                if not mods_path:
                    QMessageBox.critical(None, "错误", "未选择MOD目录，程序退出")
                    sys.exit(1)
                try:
                    # 测试目录权限
                    test_file = Path(mods_path) / "test_write.tmp"
                    test_file.touch()
                    test_file.unlink()
                    config.set_mods_path(mods_path)
                    break
                except Exception as e:
                    QMessageBox.critical(None, "错误", f"MOD目录无写入权限，请选择其他目录：{str(e)}")
                    continue
                
        # 3. 弹窗提醒需要绑定备份文件夹
        while True:
            QMessageBox.information(None, "提示", "请绑定备份目录（不能与MOD目录相同）。点击确定后选择备份目录。")
            backup_path = QFileDialog.getExistingDirectory(None, "选择备份目录（用于存放MOD压缩包和解压文件）")
            if not backup_path:
                QMessageBox.critical(None, "错误", "未选择备份目录，程序退出")
                sys.exit(1)
            if backup_path == config.get_mods_path():
                QMessageBox.critical(None, "错误", "备份目录不能与MOD目录相同，请重新选择。")
                continue
            try:
                # 测试目录权限
                test_file = Path(backup_path) / "test_write.tmp"
                test_file.touch()
                test_file.unlink()
                config.set_backup_path(backup_path)
                break
            except Exception as e:
                QMessageBox.critical(None, "错误", f"备份目录无写入权限，请选择其他目录：{str(e)}")
                continue
        
        # 4. 新增：首次绑定后自动扫描并备份所有MOD
        try:
            mod_manager = ModManager(config)
            found_mods = mod_manager.scan_mods_directory()
            print(f"[调试] 扫描到MOD数量: {len(found_mods)}")
            
            if found_mods:
                QMessageBox.information(None, "提示", f"扫描到 {len(found_mods)} 个MOD，开始备份...")
                
            for mod_info in found_mods:
                mod_id = mod_info['name']
                print(f"[调试] mod_id={mod_id}")
                print(f"[调试] original_path={mod_info.get('original_path')}")
                print(f"[调试] files={mod_info.get('files')}")
                config.add_mod(mod_id, mod_info)
                try:
                    backup_result = config.backup_mod(mod_id, mod_info)
                    print(f"[调试] 备份结果: {backup_result}")
                except Exception as e:
                    print(f"[调试] 备份失败: {str(e)}")
                    QMessageBox.warning(None, "警告", f"MOD {mod_id} 备份失败：{str(e)}")
                    continue
                    
            QMessageBox.information(None, "成功", "初始化完成！")
            
        except Exception as e:
            print(f"[调试] 初始化失败: {str(e)}")
            QMessageBox.critical(None, "错误", f"初始化失败：{str(e)}")
            sys.exit(1)
            
        config.set_initialized(True)
    
    print("[调试] 创建主窗口")
    window = MainWindow(config)
    print("[调试] 显示主窗口")
    window.show()
    
    print("[调试] 进入主事件循环")
    sys.exit(app.exec())

if __name__ == "__main__":
    main() 