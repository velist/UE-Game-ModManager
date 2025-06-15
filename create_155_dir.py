import os
import shutil
from pathlib import Path

# 创建1.55目录
if not os.path.exists('1.55'):
    os.makedirs('1.55')
    print("创建1.55目录成功")
else:
    print("1.55目录已存在")

# 复制必要文件到1.55目录
files_to_copy = [
    'main.py',
    'build.py',
    'requirements.txt',
    'config.json',
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

print("完成!") 