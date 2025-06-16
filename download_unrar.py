import os
import urllib.request
import zipfile
import io
import shutil
from pathlib import Path
import sys
import struct

def is_os_64bit():
    """检查操作系统是否是64位"""
    return struct.calcsize("P") * 8 == 64

def download_unrar_dll():
    """下载32位UnRAR.dll并保存到libs目录"""
    print("开始下载32位UnRAR.dll...")
    
    # 创建libs目录
    libs_dir = Path("libs")
    libs_dir.mkdir(exist_ok=True)

    # 从RARLab下载UnRAR.dll
    url = "https://www.rarlab.com/rar/UnRARDLL.exe"
    
    try:
        # 创建临时目录用于解压
        temp_dir = Path("temp_unrar")
        if temp_dir.exists():
            shutil.rmtree(temp_dir)
        temp_dir.mkdir()
        
        # 下载安装包到临时文件
        print(f"从 {url} 下载UnRAR.dll安装包...")
        temp_file = temp_dir / "UnRARDLL.exe"
        urllib.request.urlretrieve(url, temp_file)
        
        if not temp_file.exists():
            print("下载安装包失败")
            return False
            
        print("下载完成，开始解压...")
        
        # 尝试直接复制DLL
        try:
            # 尝试从GitHub下载预编译的UnRAR.dll
            github_url = "https://github.com/pmachapman/unrar.dll/raw/master/unrar.dll"
            unrar_dll_path = libs_dir / "UnRAR.dll"
            print(f"从GitHub下载UnRAR.dll: {github_url}")
            urllib.request.urlretrieve(github_url, unrar_dll_path)
            
            if unrar_dll_path.exists():
                print(f"成功从GitHub下载UnRAR.dll到 {unrar_dll_path}")
                return True
        except Exception as e:
            print(f"从GitHub下载UnRAR.dll失败: {e}")
        
        # 尝试使用7z命令行提取DLL
        try:
            # 查找7z.exe
            seven_zip_paths = [
                "C:\\Program Files\\7-Zip\\7z.exe",
                "C:\\Program Files (x86)\\7-Zip\\7z.exe"
            ]
            
            seven_zip_exe = None
            for path in seven_zip_paths:
                if os.path.exists(path):
                    seven_zip_exe = path
                    break
            
            if seven_zip_exe:
                # 使用7z命令解压
                cmd = f'"{seven_zip_exe}" e -y -o"{temp_dir}" "{temp_file}" "UnRAR.dll"'
                print(f"执行命令: {cmd}")
                
                import subprocess
                process = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    shell=True
                )
                stdout, stderr = process.communicate()
                
                # 检查是否成功解压
                extracted_dll = temp_dir / "UnRAR.dll"
                if process.returncode == 0 and extracted_dll.exists():
                    # 复制到libs目录
                    shutil.copy2(extracted_dll, libs_dir / "UnRAR.dll")
                    print(f"成功解压并复制UnRAR.dll到 {libs_dir / 'UnRAR.dll'}")
                    return True
                else:
                    print(f"使用7z解压失败: {stderr.decode('utf-8', errors='ignore')}")
            else:
                print("未找到7z.exe")
        except Exception as e:
            print(f"使用7z解压失败: {e}")
        
        # 如果上述方法都失败，尝试使用内置的zipfile模块
        try:
            # 尝试使用zipfile模块解压
            print("尝试使用Python内置模块解压...")
            
            # 检查是否是有效的ZIP文件
            if zipfile.is_zipfile(temp_file):
                with zipfile.ZipFile(temp_file, 'r') as zipf:
                    # 查找UnRAR.dll
                    for file_info in zipf.infolist():
                        if file_info.filename.lower() == "unrar.dll":
                            zipf.extract(file_info, temp_dir)
                            # 复制到libs目录
                            extracted_dll = temp_dir / "UnRAR.dll"
                            if extracted_dll.exists():
                                shutil.copy2(extracted_dll, libs_dir / "UnRAR.dll")
                                print(f"成功解压并复制UnRAR.dll到 {libs_dir / 'UnRAR.dll'}")
                                return True
                            break
                    else:
                        print("在ZIP文件中未找到UnRAR.dll")
            else:
                print("下载的文件不是有效的ZIP文件")
        except Exception as e:
            print(f"使用Python内置模块解压失败: {e}")
        
        # 最后的尝试：手动下载
        print("所有自动方法都失败，请手动下载UnRAR.dll并放置在libs目录中")
        print("您可以从以下链接下载:")
        print("1. https://www.rarlab.com/rar_add.htm (UnRAR.dll for Windows)")
        print("2. https://github.com/pmachapman/unrar.dll/raw/master/unrar.dll")
        
        return False
    except Exception as e:
        print(f"下载UnRAR.dll失败: {e}")
        return False
    finally:
        # 清理临时目录
        try:
            if temp_dir.exists():
                shutil.rmtree(temp_dir)
                print("临时目录已清理")
        except Exception as e:
            print(f"清理临时目录失败: {e}")

def download_unrar64_dll():
    """下载64位UnRAR.dll并保存到libs目录"""
    print("开始下载64位UnRAR.dll...")
    
    # 创建libs目录
    libs_dir = Path("libs")
    libs_dir.mkdir(exist_ok=True)

    # 从RARLab下载UnRAR64.dll
    url = "https://www.rarlab.com/rar/UnRARDLL.exe"
    
    try:
        # 创建临时目录用于解压
        temp_dir = Path("temp_unrar64")
        if temp_dir.exists():
            shutil.rmtree(temp_dir)
        temp_dir.mkdir()
        
        # 下载安装包到临时文件
        print(f"从 {url} 下载UnRAR64.dll安装包...")
        temp_file = temp_dir / "UnRARDLL.exe"
        urllib.request.urlretrieve(url, temp_file)
        
        if not temp_file.exists():
            print("下载安装包失败")
            return False
            
        print("下载完成，开始解压...")
        
        # 尝试直接复制DLL
        try:
            # 尝试从GitHub下载预编译的UnRAR64.dll
            github_url = "https://github.com/pmachapman/unrar.dll/raw/master/x64/unrar.dll"
            unrar_dll_path = libs_dir / "UnRAR64.dll"
            print(f"从GitHub下载UnRAR64.dll: {github_url}")
            urllib.request.urlretrieve(github_url, unrar_dll_path)
            
            if unrar_dll_path.exists():
                print(f"成功从GitHub下载UnRAR64.dll到 {unrar_dll_path}")
                return True
        except Exception as e:
            print(f"从GitHub下载UnRAR64.dll失败: {e}")
        
        # 尝试使用7z命令行提取DLL
        try:
            # 查找7z.exe
            seven_zip_paths = [
                "C:\\Program Files\\7-Zip\\7z.exe",
                "C:\\Program Files (x86)\\7-Zip\\7z.exe"
            ]
            
            seven_zip_exe = None
            for path in seven_zip_paths:
                if os.path.exists(path):
                    seven_zip_exe = path
                    break
            
            if seven_zip_exe:
                # 使用7z命令解压
                cmd = f'"{seven_zip_exe}" e -y -o"{temp_dir}" "{temp_file}" "UnRAR.dll"'
                print(f"执行命令: {cmd}")
                
                import subprocess
                process = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    shell=True
                )
                stdout, stderr = process.communicate()
                
                # 检查是否成功解压
                extracted_dll = temp_dir / "UnRAR.dll"
                if process.returncode == 0 and extracted_dll.exists():
                    # 复制到libs目录并重命名
                    shutil.copy2(extracted_dll, libs_dir / "UnRAR64.dll")
                    print(f"成功解压并复制UnRAR.dll到 {libs_dir / 'UnRAR64.dll'}")
                    return True
                else:
                    print(f"使用7z解压失败: {stderr.decode('utf-8', errors='ignore')}")
            else:
                print("未找到7z.exe")
        except Exception as e:
            print(f"使用7z解压失败: {e}")
        
        # 如果上述方法都失败，尝试手动下载
        print("自动下载方法失败，请手动下载UnRAR64.dll并放置在libs目录中")
        print("您可以从以下链接下载64位版本:")
        print("1. https://www.rarlab.com/rar_add.htm (UnRAR.dll for Windows x64)")
        print("2. https://github.com/pmachapman/unrar.dll/raw/master/x64/unrar.dll")
        
        return False
    except Exception as e:
        print(f"下载UnRAR64.dll失败: {e}")
        return False
    finally:
        # 清理临时目录
        try:
            if temp_dir.exists():
                shutil.rmtree(temp_dir)
                print("临时目录已清理")
        except Exception as e:
            print(f"清理临时目录失败: {e}")

# 手动下载UnRAR.dll的备选方案
def manual_download_instructions():
    """显示手动下载UnRAR.dll的说明"""
    print("=" * 60)
    print("手动下载UnRAR.dll的说明")
    print("=" * 60)
    print("1. 访问 https://www.rarlab.com/rar_add.htm")
    print("2. 下载 'UnRAR.dll for Windows'")
    print("3. 解压下载的文件")
    print("4. 将UnRAR.dll复制到程序的libs目录中")
    print("=" * 60)

if __name__ == "__main__":
    # 检查操作系统架构
    if is_os_64bit():
        print("检测到64位操作系统，将下载32位和64位UnRAR.dll")
        success32 = download_unrar_dll()
        success64 = download_unrar64_dll()
        
        if success32 and success64:
            print("成功下载32位和64位UnRAR.dll")
        elif success32:
            print("成功下载32位UnRAR.dll，但64位下载失败")
        elif success64:
            print("成功下载64位UnRAR.dll，但32位下载失败")
        else:
            print("32位和64位UnRAR.dll下载均失败")
            manual_download_instructions()
    else:
        print("检测到32位操作系统，只下载32位UnRAR.dll")
        if download_unrar_dll():
            print("成功下载UnRAR.dll")
        else:
            print("下载UnRAR.dll失败")
            manual_download_instructions() 