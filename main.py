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
        # 1. 先弹窗提醒需要绑定MOD文件夹
        QMessageBox.information(None, "提示", "首次使用需绑定MOD文件夹。请点击确定后选择MOD文件夹。")
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
                
        # 2. 弹窗提醒需要绑定备份文件夹
        while True:
            QMessageBox.information(None, "提示", "请绑定备份目录（不能与MOD目录相同）。点击确定后选择备份目录。")
            backup_path = QFileDialog.getExistingDirectory(None, "选择备份目录（用于存放MOD压缩包和解压文件）")
            if not backup_path:
                QMessageBox.critical(None, "错误", "未选择备份目录，程序退出")
                sys.exit(1)
            if backup_path == mods_path:
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
                
        # 3. 新增：首次绑定后自动扫描并备份所有MOD
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