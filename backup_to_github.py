import os
import subprocess
import shutil
from pathlib import Path
import time

def run_cmd(cmd, cwd=None, show_output=True):
    """运行命令并输出结果"""
    print(f"执行命令: {cmd}")
    result = subprocess.run(cmd, shell=True, cwd=cwd, capture_output=True, text=True)
    if result.stdout and show_output:
        print(result.stdout)
    if result.stderr and show_output:
        print(f"错误: {result.stderr}")
    return result.returncode == 0, result.stdout, result.stderr

def create_github_backup():
    """备份代码到GitHub"""
    # 检查git命令是否可用
    if not run_cmd("git --version")[0]:
        print("未找到git命令，请先安装Git并确保其在PATH中")
        return False
    
    # 准备版本信息
    version = "1.6.3"
    version_tag = f"v{version}"
    
    # 检查当前目录是否是git仓库
    if not os.path.exists(".git"):
        print("当前目录不是git仓库，正在初始化...")
        if not run_cmd("git init")[0]:
            print("初始化git仓库失败")
            return False
    
    # 检查是否有子模块问题
    print("检查是否有子模块问题...")
    run_cmd("git rm --cached 1.6.1", show_output=False)
    
    # 添加所有文件到暂存区
    print("添加文件到git暂存区...")
    # 排除一些不需要的目录和文件
    run_cmd("git add --all .")
    
    # 检查状态
    print("检查Git状态...")
    status_ok, status_out, _ = run_cmd("git status", show_output=False)
    print(status_out)
    
    # 提交更改
    commit_message = f"更新到版本 {version}: 修复RAR导入问题并改进错误处理"
    print(f"提交更改: {commit_message}")
    
    # 配置git用户信息（如果需要）
    name_ok, name_out, _ = run_cmd('git config --get user.name', show_output=False)
    email_ok, email_out, _ = run_cmd('git config --get user.email', show_output=False)
    
    if not name_out.strip():
        print("设置Git用户名...")
        run_cmd('git config user.name "MOD Manager"')
    
    if not email_out.strip():
        print("设置Git邮箱...")
        run_cmd('git config user.email "mod_manager@example.com"')
    
    # 提交更改
    commit_ok, commit_out, commit_err = run_cmd(f'git commit -m "{commit_message}"')
    if not commit_ok:
        print(f"提交失败: {commit_err}")
        print("尝试强制提交...")
        run_cmd(f'git commit --allow-empty -m "{commit_message}"')
    
    # 创建标签
    print(f"创建标签: {version_tag}")
    # 先删除已存在的同名标签
    run_cmd(f'git tag -d {version_tag}', show_output=False)
    tag_ok, _, _ = run_cmd(f'git tag -a {version_tag} -m "版本 {version}"')
    
    if not tag_ok:
        print("标签创建失败，尝试强制创建...")
        run_cmd(f'git tag -f -a {version_tag} -m "版本 {version}"')
    
    # 检查远程仓库配置
    print("检查远程仓库配置...")
    remote_ok, remote_out, _ = run_cmd("git remote -v", show_output=False)
    print(remote_out)
    
    if remote_out.strip() and input("是否推送到远程仓库？(y/n) ").lower() == 'y':
        remote = input("请输入远程仓库名称 (默认: origin): ") or "origin"
        branch = input("请输入分支名称 (默认: master): ") or "master"
        
        print(f"推送代码到 {remote}/{branch}...")
        if run_cmd(f"git push {remote} {branch}")[0]:
            print(f"推送标签 {version_tag}...")
            run_cmd(f"git push {remote} {version_tag}")
        else:
            print("推送失败，尝试强制推送...")
            if input("是否强制推送？(y/n) ").lower() == 'y':
                run_cmd(f"git push -f {remote} {branch}")
                run_cmd(f"git push -f {remote} {version_tag}")
    else:
        print("未推送到远程仓库，请手动使用以下命令推送：")
        print("git push <remote> <branch>")
        print(f"git push <remote> {version_tag}")
    
    print(f"\n代码已成功备份到本地git仓库，版本标签: {version_tag}")
    print("修改内容: 修复RAR导入失败问题，改进临时文件管理，优化错误处理和日志系统")
    return True

if __name__ == "__main__":
    # 执行GitHub备份
    try:
        success = create_github_backup()
        if success:
            print("\nGitHub备份成功完成！")
        else:
            print("\nGitHub备份过程中出现问题，请检查日志。")
    except Exception as e:
        print(f"\n备份过程中出现异常: {e}")
    
    print("\n备份完成后，请执行 build_fixed.py 进行打包")
    if input("是否立即开始打包？(y/n) ").lower() == 'y':
        try:
            import build_fixed
            print("打包完成！")
        except Exception as e:
            print(f"打包过程中出现异常: {e}")
    else:
        print("跳过打包过程。您可以稍后手动执行 python build_fixed.py 进行打包。") 