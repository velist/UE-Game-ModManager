import PyInstaller.__main__
import os
import shutil
from pathlib import Path

def build_exe():
    # 创建1.55目录
    if not os.path.exists('1.55'):
        os.makedirs('1.55')
        print("创建1.55目录成功")
    else:
        print("1.55目录已存在")
        
    # 清理旧的构建文件
    if os.path.exists('1.55/build'):
        shutil.rmtree('1.55/build')
    if os.path.exists('1.55/dist'):
        shutil.rmtree('1.55/dist')
        
    # 复制必要文件到1.55目录
    files_to_copy = [
        'main.py',
        'requirements.txt',
        'README.md',
        'LICENSE'
    ]

    for file in files_to_copy:
        if os.path.exists(file):
            shutil.copy(file, os.path.join('1.55', file))
            print(f"复制 {file} 到 1.55 目录成功")
        else:
            print(f"文件 {file} 不存在")

    # 复制目录
    dirs_to_copy = [
        'ui',
        'utils',
        'icons'
    ]

    for dir_name in dirs_to_copy:
        if os.path.exists(dir_name):
            dest_dir = os.path.join('1.55', dir_name)
            if os.path.exists(dest_dir):
                shutil.rmtree(dest_dir)
            shutil.copytree(dir_name, dest_dir)
            print(f"复制 {dir_name} 目录到 1.55 成功")
        else:
            print(f"目录 {dir_name} 不存在")
    
    # 创建空的config.json文件
    with open('1.55/config.json', 'w', encoding='utf-8') as f:
        f.write('{}')
    
    # 创建图标目录
    os.makedirs('1.55/icons', exist_ok=True)
    
    # 确保app.ico存在，如果不存在则使用4.ico
    if not os.path.exists('1.55/icons/app.ico'):
        if os.path.exists('4.ico'):
            shutil.copy('4.ico', '1.55/icons/app.ico')
        else:
            print('警告：未找到app.ico，将使用默认图标')
    
    # 切换到1.55目录
    os.chdir('1.55')
    
    # PyInstaller参数
    args = [
        'main.py',  # 主程序文件
        '--name=剑星MOD管理器',  # 程序名称
        '--windowed',  # 使用GUI模式
        '--noconsole',  # 不显示控制台
        '--clean',  # 清理临时文件
        '--add-data=ui/style.qss;ui',  # 添加样式文件
        '--add-data=icons;icons',  # 添加图标文件
        '--add-data=README.md;.',  # 添加README文件
        '--add-data=LICENSE;.',  # 添加许可证文件
        '--icon=icons/app.ico',  # 程序图标
        '--hidden-import=PySide6.QtCore',
        '--hidden-import=PySide6.QtGui',
        '--hidden-import=PySide6.QtWidgets',
        '--hidden-import=py7zr',
        '--hidden-import=rarfile',
        '--hidden-import=magic',
        '--hidden-import=PIL',
    ]
    
    # 运行PyInstaller
    PyInstaller.__main__.run(args)
    
    # 打包完成后，复制额外的文件到dist目录
    dist_dir = Path('dist') / '剑星MOD管理器'
    
    # 创建空的config.json文件
    with open(dist_dir / 'config.json', 'w', encoding='utf-8') as f:
        f.write('{}')
    
    # 创建zip文件
    try:
        shutil.make_archive('剑星MOD管理器_v1.55', 'zip', 'dist/剑星MOD管理器')
        print('已创建压缩包：剑星MOD管理器_v1.55.zip')
    except Exception as e:
        print(f'创建压缩包失败: {e}')
    
    print('打包完成！')
    print('可执行文件位于 1.55/dist/剑星MOD管理器/剑星MOD管理器.exe')
    print('压缩包位于 1.55/剑星MOD管理器_v1.55.zip')

if __name__ == '__main__':
    build_exe() 