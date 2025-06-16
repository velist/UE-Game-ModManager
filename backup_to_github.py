import os
import subprocess
import shutil
from pathlib import Path

def run_cmd(cmd, cwd=None):
    """运行命令并输出结果"""
    print(f"执行命令: {cmd}")
    result = subprocess.run(cmd, shell=True, cwd=cwd, capture_output=True, text=True)
    if result.stdout:
        print(result.stdout)
    if result.stderr:
        print(f"错误: {result.stderr}")
    return result.returncode == 0

def create_github_backup():
    """备份代码到GitHub"""
    # 检查git命令是否可用
    if not run_cmd("git --version"):
        print("未找到git命令，请先安装Git并确保其在PATH中")
        return False
    
    # 准备版本信息
    version = "1.6.2"
    version_tag = f"v{version}"
    
    # 检查当前目录是否是git仓库
    if not os.path.exists(".git"):
        print("当前目录不是git仓库，正在初始化...")
        if not run_cmd("git init"):
            print("初始化git仓库失败")
            return False
    
    # 添加所有文件到暂存区
    print("添加文件到git暂存区...")
    run_cmd("git add .")
    
    # 提交更改
    commit_message = f"更新到版本 {version}: 修复点击C2区禁用/启用MOD引起的子分类问题"
    print(f"提交更改: {commit_message}")
    if not run_cmd(f'git commit -m "{commit_message}"'):
        print("提交更改失败，可能没有要提交的更改或git用户未配置")
        # 检查git用户配置
        run_cmd('git config --get user.name')
        run_cmd('git config --get user.email')
        # 如果未配置，设置一个临时的
        run_cmd('git config user.name "MOD Manager"')
        run_cmd('git config user.email "mod_manager@example.com"')
        # 再次尝试提交
        if not run_cmd(f'git commit -m "{commit_message}"'):
            print("再次提交失败，请检查git配置")
            return False
    
    # 创建标签
    print(f"创建标签: {version_tag}")
    run_cmd(f'git tag -a {version_tag} -m "版本 {version}"')
    
    # 推送到远程仓库（如果已配置）
    print("检查远程仓库配置...")
    if run_cmd("git remote -v") and input("是否推送到远程仓库？(y/n) ").lower() == 'y':
        remote = input("请输入远程仓库名称 (默认: origin): ") or "origin"
        branch = input("请输入分支名称 (默认: master): ") or "master"
        
        print(f"推送代码到 {remote}/{branch}...")
        if run_cmd(f"git push {remote} {branch}"):
            print(f"推送标签 {version_tag}...")
            run_cmd(f"git push {remote} {version_tag}")
    else:
        print("未推送到远程仓库，请手动使用以下命令推送：")
        print("git push <remote> <branch>")
        print(f"git push <remote> {version_tag}")
    
    print(f"\n代码已成功备份到本地git仓库，版本标签: {version_tag}")
    print("修改内容: 修复点击C2区禁用/启用MOD时自动创建子分类并且删除一个子分类导致所有子分类一起删除的问题")
    return True

if __name__ == "__main__":
    # 执行GitHub备份
    create_github_backup()
    
    print("\n备份完成后，请执行 build_fixed.py 进行打包")
    if input("是否立即开始打包？(y/n) ").lower() == 'y':
        import build_fixed 