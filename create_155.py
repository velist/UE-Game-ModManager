import os
import shutil
from pathlib import Path
import sys

# 获取当前脚本的绝对路径
current_dir = os.path.dirname(os.path.abspath(__file__))
print(f"当前工作目录: {current_dir}")

# 切换到正确的工作目录
os.chdir(current_dir)
print(f"切换后的工作目录: {os.getcwd()}")

# 创建1.55目录
target_dir = os.path.join(current_dir, "1.55")
if not os.path.exists(target_dir):
    os.makedirs(target_dir)
    print(f"创建目录成功: {target_dir}")
else:
    print(f"目录已存在: {target_dir}")

# 复制必要文件到1.55目录
files_to_copy = [
    'main.py',
    'build.py',
    'requirements.txt',
    'README.md',
    'LICENSE'
]

for file in files_to_copy:
    src_file = os.path.join(current_dir, file)
    dst_file = os.path.join(target_dir, file)
    if os.path.exists(src_file):
        shutil.copy(src_file, dst_file)
        print(f"复制文件成功: {src_file} -> {dst_file}")
    else:
        print(f"文件不存在: {src_file}")

# 复制目录
dirs_to_copy = [
    'ui',
    'utils',
    'icons'
]

for dir_name in dirs_to_copy:
    src_dir = os.path.join(current_dir, dir_name)
    dst_dir = os.path.join(target_dir, dir_name)
    if os.path.exists(src_dir):
        if os.path.exists(dst_dir):
            shutil.rmtree(dst_dir)
        shutil.copytree(src_dir, dst_dir)
        print(f"复制目录成功: {src_dir} -> {dst_dir}")
    else:
        print(f"目录不存在: {src_dir}")

# 创建空的config.json文件
config_file = os.path.join(target_dir, "config.json")
with open(config_file, 'w', encoding='utf-8') as f:
    f.write('{}')
print(f"创建空配置文件: {config_file}")

print("完成!") 