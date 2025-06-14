# 剑星MOD管理器（StellarBlade MOD Manager）

一个基于 PySide6 的剑星MOD桌面管理器，支持MOD导入、分类、启用/禁用、预览图、国际化等功能。

## 主要特性
- MOD一键导入、启用/禁用、重命名、删除
- 分类管理（支持拖动排序、层级调整）
- 预览图展示与修改
- 中英文切换
- 目录树双击展开/收起
- 现代美观UI，深色主题

## 安装说明

### 方法一：直接下载
1. 从 [Releases](https://github.com/yourusername/jianxing-mod-manager/releases) 页面下载最新版本
2. 解压后运行 `jianxing-mod-manager.exe`

### 方法二：从源码构建
1. 克隆仓库：
```bash
git clone https://github.com/yourusername/jianxing-mod-manager.git
cd jianxing-mod-manager
```

2. 安装依赖：
```bash
pip install -r requirements.txt
```

3. 运行程序：
```bash
python main.py
```

4. 构建可执行文件：
```bash
python build.py
```

## 依赖要求
- Python 3.8+
- PySide6
- 其他依赖见 requirements.txt

## 使用说明
详见软件内"设置-使用说明"菜单。

- 首次启动需选择MOD文件夹和备份文件夹，缺一不可，否则无法进入主界面。
- 导入MOD：点击右侧"导入MOD"按钮，选择压缩包文件，导入后可启用/禁用。
- 目录树支持双击展开/收起分类。
- 分类管理：左侧可新建、重命名、删除分类，支持拖动排序和层级调整。
- MOD管理：支持重命名、修改预览图、删除、启用/禁用等操作。
- 设置：可切换语言、修改MOD/备份路径、查看关于信息。
- 更多：如遇问题请加入QQ群反馈。

## RAR解压方案

本项目采用多重解压机制，确保RAR文件能够被正确处理：

1. 首先尝试使用WinRAR命令行工具解压
2. 如果失败，尝试使用7z命令行工具解压
3. 如果仍然失败，尝试使用Python的rarfile库解压
4. 如果所有方法都失败，则将RAR文件复制到目标文件夹

特性：
- 自动检测并递归解压嵌套的压缩包
- 智能识别MOD文件（pak/ucas/utoc组合）
- 支持子文件夹结构的MOD
- 备份和还原机制确保MOD完整性

### 嵌套压缩包处理

程序能够处理嵌套的压缩包（压缩包中包含其他压缩包），具有以下特性：

1. 递归解压嵌套压缩包，最多支持10层嵌套
2. 使用MOD文件名作为文件夹名称，而不是压缩包名称
3. 为每个包含MOD文件（pak/ucas/utoc）的嵌套压缩包创建独立的MOD
4. 跟踪嵌套MOD与父压缩包的关系，支持独立启用/禁用
5. 备份和还原机制确保嵌套MOD完整性

### 错误处理与回退机制
- 每个解压方法失败时有明确的错误日志
- 解压失败时有备选方案（复制原始文件）
- 临时目录的创建和清理

此方案确保了RAR文件的可靠解压和MOD的正确导入，解决了路径处理和文件结构问题。

## 开发说明
- 使用 PySide6 构建UI
- 使用 PyInstaller 打包
- 支持 Windows 系统

## 交流群
QQ群：788566495

## 贡献
欢迎提交 Issue 和 Pull Request！

## License
MIT License

## 最新更新 (v1.55)

### 功能增强
1. 添加了"收藏工具箱"按钮，链接到 https://codepen.io/aigame/full/MYwXoGq
   - 按钮样式与"启动游戏"按钮匹配，字体略小

2. 增强MOD列表界面
   - 添加右键上下文菜单，方便MOD管理
   - 实现编辑模式，支持多选功能
   - 新增拖放功能，可在不同分类间移动MOD

3. 新增图标
   - icon_check.svg
   - 移动.svg (移动图标)
   - 关闭-关闭.svg (关闭图标)
   - 开启-开启.svg (开启图标)

### 问题修复
1. 修复了enable_mod函数中restore_mod函数缺少mod_info参数的问题
2. 解决了RAR文件提取问题
   - 现在可以正确处理包含pak、utoc和ucas文件的RAR归档
   - 修复了嵌套RAR文件的提取问题，支持多层嵌套的MOD包
   - 确保正确创建文件夹结构，使MOD能够正常激活

### 其他改进
1. 完善了备份和恢复功能
   - 当前版本已备份至"1.55版本"文件夹
   - 验证了备份和恢复功能的可靠性

## 使用说明
[待添加] 