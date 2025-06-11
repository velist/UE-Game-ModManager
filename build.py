import PyInstaller.__main__
import os
import shutil

def build_exe():
    # 清理旧的构建文件
    if os.path.exists('build'):
        shutil.rmtree('build')
    if os.path.exists('dist'):
        shutil.rmtree('dist')
        
    # 创建图标目录
    os.makedirs('icons', exist_ok=True)
    
    # PyInstaller参数
    args = [
        'main.py',  # 主程序文件
        '--name=剑星MOD管理器',  # 程序名称
        '--windowed',  # 使用GUI模式
        '--noconsole',  # 不显示控制台
        '--clean',  # 清理临时文件
        '--add-data=ui/style.qss;ui',  # 添加样式文件
        '--add-data=icons;icons',  # 添加图标文件
        '--icon=icons/app.ico',  # 程序图标
        '--hidden-import=PyQt6.QtCore',
        '--hidden-import=PyQt6.QtGui',
        '--hidden-import=PyQt6.QtWidgets',
        '--hidden-import=py7zr',
        '--hidden-import=rarfile',
        '--hidden-import=magic',
    ]
    
    # 运行PyInstaller
    PyInstaller.__main__.run(args)
    
    print('打包完成！')
    print('可执行文件位于 dist/剑星MOD管理器/剑星MOD管理器.exe')

if __name__ == '__main__':
    build_exe() 