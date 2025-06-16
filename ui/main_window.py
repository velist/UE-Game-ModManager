from PySide6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, 
    QTreeWidget, QTreeWidgetItem, QLabel, QPushButton,
    QFileDialog, QMessageBox, QInputDialog, QMenu,
    QFrame, QSplitter, QDialog, QLineEdit, QTextEdit,
    QDialogButtonBox, QFormLayout, QToolBar, QToolButton,
    QStatusBar, QProgressBar, QListWidget, QListWidgetItem,
    QScrollArea, QAbstractItemView, QCheckBox, QProgressDialog
)
from PySide6.QtCore import Qt, QSize, Signal, QThread, QMimeData, QPoint, QByteArray
from PySide6.QtGui import QAction, QIcon, QPixmap, QFont, QImage, QDrag, QPainter
from utils.mod_manager import ModManager
from utils.config_manager import ConfigManager
import os
import uuid
from pathlib import Path
import sys
import json
import webbrowser  # 导入webbrowser模块用于打开URL
import shutil  # 导入shutil模块用于复制文件

def resource_path(relative_path):
    """兼容PyInstaller打包和开发环境的资源路径"""
    if hasattr(sys, '_MEIPASS'):
        return os.path.join(sys._MEIPASS, relative_path)
    return os.path.join(os.path.abspath("."), relative_path)

class ModInfoDialog(QDialog):
    def __init__(self, parent=None, mod_info=None):
        super().__init__(parent)
        self.mod_info = mod_info or {}
        self.init_ui()
        
    def init_ui(self):
        self.setWindowTitle('MOD信息')
        self.setMinimumWidth(400)
        
        layout = QFormLayout(self)
        
        # 名称输入
        self.name_edit = QLineEdit(self.mod_info.get('name', ''))
        layout.addRow('名称:', self.name_edit)
        
        # 描述输入
        self.desc_edit = QTextEdit()
        self.desc_edit.setPlainText(self.mod_info.get('description', ''))
        self.desc_edit.setMaximumHeight(100)
        layout.addRow('描述:', self.desc_edit)
        
        # 预览图
        preview_layout = QHBoxLayout()
        self.preview_label = QLabel()
        self.preview_label.setMinimumSize(200, 150)
        self.preview_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview_label.setStyleSheet('border: 1px solid #3d3d3d;')
        
        if self.mod_info.get('preview_image'):
            pixmap = QPixmap(self.mod_info['preview_image'])
            if not pixmap.isNull():
                self.preview_label.setPixmap(pixmap.scaled(
                    200, 150, Qt.AspectRatioMode.KeepAspectRatio,
                    Qt.TransformationMode.SmoothTransformation
                ))
                
        preview_layout.addWidget(self.preview_label)
        
        preview_btn = QPushButton('选择预览图')
        preview_btn.clicked.connect(self.select_preview)
        preview_layout.addWidget(preview_btn)
        
        layout.addRow('预览图:', preview_layout)
        
        # 按钮
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | 
            QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addRow(buttons)
        
    def select_preview(self):
        file_path, _ = QFileDialog.getOpenFileName(
            self,
            '选择预览图',
            '',
            '图片文件 (*.png *.jpg *.jpeg)'
        )
        
        if file_path:
            pixmap = QPixmap(file_path)
            if not pixmap.isNull():
                self.preview_label.setPixmap(pixmap.scaled(
                    200, 150, Qt.AspectRatioMode.KeepAspectRatio,
                    Qt.TransformationMode.SmoothTransformation
                ))
                self.preview_path = file_path
                
    def get_mod_info(self):
        return {
            'name': self.name_edit.text(),
            'description': self.desc_edit.toPlainText(),
            'preview_image': getattr(self, 'preview_path', self.mod_info.get('preview_image', ''))
        }

class ImportModThread(QThread):
    finished = Signal(object, str)  # 修改为接收任何类型的结果（单个MOD或MOD列表）
    
    def __init__(self, mod_manager, file_path):
        super().__init__()
        self.mod_manager = mod_manager
        self.file_path = file_path
    
    def run(self):
        """在单独线程中执行MOD导入过程"""
        try:
            mod_info = self.mod_manager.import_mod(self.file_path)
            self.finished.emit(mod_info, "")
        except Exception as e:
            # 捕获所有异常，并获取详细的错误信息
            error_msg = str(e)
            import traceback
            trace_info = traceback.format_exc()
            
            # 记录详细错误到日志
            print(f"[错误] ImportModThread.run: 导入MOD失败: {error_msg}")
            print(f"[错误] 详细错误信息: {trace_info}")
            
            # 将详细的错误堆栈写入错误日志文件
            try:
                from datetime import datetime
                timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
                error_log_path = f"logs/import_error_{timestamp}.log"
                os.makedirs("logs", exist_ok=True)
                with open(error_log_path, "w", encoding="utf-8") as f:
                    f.write(f"时间: {datetime.now()}\n")
                    f.write(f"导入文件: {self.file_path}\n")
                    f.write(f"错误消息: {error_msg}\n\n")
                    f.write("详细错误信息:\n")
                    f.write(trace_info)
                error_msg += f"\n\n详细错误已记录到: {error_log_path}"
            except Exception as log_e:
                print(f"[错误] 写入错误日志失败: {log_e}")
                
            # 确保清理所有临时文件
            try:
                from utils.mod_manager import cleanup_temp_directories
                cleanup_temp_directories()
            except Exception as cleanup_e:
                print(f"[错误] 清理临时文件失败: {cleanup_e}")
            
            self.finished.emit(None, error_msg)

class MainWindow(QMainWindow):
    def __init__(self, config_manager):
        super().__init__()
        self.config = config_manager
        self.mod_manager = ModManager(config_manager)
        self.load_style()
        self.init_ui()
        
        # 更新启动游戏按钮状态
        self.update_launch_button()
        
        # 检查是否需要设置备份目录
        if not self.config.get_backup_path():
            self.set_backup_directory()
        
        # 检查是否需要设置MOD文件夹
        if not self.config.get_mods_path():
            self.set_mods_directory()
            
        # 检查是否需要设置游戏路径
        if not self.config.get_game_path():
            reply = self.msgbox_question_zh('设置游戏路径', '是否设置游戏路径以便直接启动游戏？\n游戏可执行文件名为SB-Win64-Shipping.exe')
            if reply == QMessageBox.StandardButton.Yes:
                self.set_game_path()
        
        # 初始化当前激活的标签
        self.active_tab = "all"
        
        # 自动扫描并加载MOD
        self.auto_scan_mods()
        
    def load_style(self):
        """加载样式表"""
        style_file = os.path.join(os.path.dirname(__file__), 'style.qss')
        try:
            with open(style_file, 'r', encoding='utf-8') as f:
                self.setStyleSheet(f.read())
        except Exception as e:
            print(f"[警告] 样式表加载失败: {e}")
            
    def init_ui(self):
        """初始化用户界面"""
        self.setWindowTitle('爱酱剑星MOD管理器')
        self.setMinimumSize(1200, 800)
        
        # 设置应用图标
        app_icon = QIcon(resource_path('icons/your_icon.svg'))
        self.setWindowIcon(app_icon)
        
        # 创建中央部件
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        
        # 创建主布局
        main_layout = QVBoxLayout(central_widget)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(0)
        
        # 当前激活的标签
        self.active_tab = "all"
        
        # 加载样式表
        self.load_style()
        
        # A区 - 顶部区域
        top_area = QFrame()
        top_area.setObjectName("topArea")
        top_area.setStyleSheet("QFrame#topArea { background-color: #23243a; }")
        top_area.setFixedHeight(120)  # 减小高度
        top_layout = QHBoxLayout(top_area)
        
        # 左侧标题和logo
        title_layout = QHBoxLayout()
        title_layout.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
        
        logo_label = QLabel()
        # 使用新的图标
        logo_path = resource_path('icons/your_icon.svg')
        logo_pixmap = QPixmap(logo_path)
        if logo_pixmap.isNull():
            print(f"[警告] 找不到logo图片: {logo_path}")
            logo_label.setText("AX")
            logo_label.setStyleSheet("font-size: 48pt; color: #ff3333; font-weight: bold;")
        else:
            logo_label.setPixmap(logo_pixmap.scaled(80, 80, Qt.KeepAspectRatio, Qt.SmoothTransformation))
        logo_label.setAlignment(Qt.AlignCenter)
        
        title_label = QLabel("爱酱剑星MOD管理器")
        title_label.setObjectName("mainTitle")
        title_label.setStyleSheet("font-size: 24pt; color: #b18cff; font-weight: bold;")
        
        subtitle_label = QLabel("轻松管理你的游戏MOD")
        subtitle_label.setStyleSheet("font-size: 14pt; color: #bdbdbd;")
        
        title_text_layout = QVBoxLayout()
        title_text_layout.addWidget(title_label)
        title_text_layout.addWidget(subtitle_label)
        
        title_layout.addWidget(logo_label)
        title_layout.addSpacing(15)
        title_layout.addLayout(title_text_layout)
        
        # 中间启动游戏按钮
        center_layout = QVBoxLayout()
        center_layout.setAlignment(Qt.AlignCenter)
        
        self.launch_game_btn = QPushButton("启动游戏")
        self.launch_game_btn.setObjectName("launchGameBtn")
        self.launch_game_btn.setIcon(QIcon(resource_path('icons/your_icon.svg')))  # 使用游戏图标
        self.launch_game_btn.setIconSize(QSize(24, 24))
        self.launch_game_btn.setFixedSize(150, 40)
        self.launch_game_btn.clicked.connect(self.launch_game)
        
        center_layout.addWidget(self.launch_game_btn)
        
        # 添加收集工具箱按钮
        self.toolbox_btn = QPushButton("收集工具箱")
        self.toolbox_btn.setObjectName("toolboxBtn")
        self.toolbox_btn.setIcon(QIcon(resource_path('icons/icon_工具箱.svg')))  # 使用工具箱图标
        self.toolbox_btn.setIconSize(QSize(18, 18))  # 更小的图标
        self.toolbox_btn.setFixedSize(150, 36)  # 与启动游戏按钮宽度一致
        self.toolbox_btn.setStyleSheet("font-size: 12px;")  # 字号更小
        self.toolbox_btn.clicked.connect(self.open_toolbox)
        
        center_layout.addSpacing(5)  # 在两个按钮之间添加一些间距
        center_layout.addWidget(self.toolbox_btn)
        
        # 右侧工具栏按钮
        toolbar_layout = QHBoxLayout()
        toolbar_layout.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        toolbar_layout.setSpacing(10)
        
        # 搜索框
        search_layout = QHBoxLayout()
        search_layout.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        
        search_label = QLabel(self.tr('搜索:'))
        search_label.setObjectName('searchLabel')
        search_label.setStyleSheet('color: #b18cff; font-size: 16px; font-weight: bold;')
        self.search_box = QLineEdit()
        self.search_box.setObjectName('searchBox')
        self.search_box.setPlaceholderText('输入MOD名称或描述...')
        self.search_box.textChanged.connect(self.on_search_text_changed)
        self.search_box.setFixedWidth(250)
        
        search_layout.addWidget(search_label)
        search_layout.addWidget(self.search_box)
        
        refresh_btn = QPushButton("刷新")
        refresh_btn.setFixedSize(80, 30)
        refresh_btn.setIcon(QIcon(resource_path('icons/刷新.svg')))
        refresh_btn.setStyleSheet("font-size: 12px;")
        refresh_btn.setObjectName("refreshBtn")  # 添加ID
        refresh_btn.clicked.connect(self.refresh_mods)
        
        settings_btn = QPushButton("设置")
        settings_btn.setFixedSize(80, 30)
        settings_btn.setIcon(QIcon(resource_path('icons/icon_设置.svg')))
        settings_btn.setStyleSheet("font-size: 12px;")
        settings_btn.setObjectName("settingsBtn")  # 添加ID
        
        # 创建设置菜单
        settings_menu = QMenu(self)
        lang_action = QAction(self.tr('切换语言（language）'), self)
        lang_action.triggered.connect(self.toggle_language)
        settings_menu.addAction(lang_action)
        
        modpath_action = QAction(self.tr('切换MOD路径'), self)
        modpath_action.triggered.connect(self.set_mods_directory)
        settings_menu.addAction(modpath_action)
        
        backup_action = QAction(self.tr('切换备份目录'), self)
        backup_action.triggered.connect(self.set_backup_directory)
        settings_menu.addAction(backup_action)
        
        game_path_action = QAction(self.tr('设置游戏路径'), self)
        game_path_action.triggered.connect(self.set_game_path)
        settings_menu.addAction(game_path_action)
        
        about_action = QAction(self.tr('关于爱酱MOD管理器'), self)
        about_action.triggered.connect(self.show_about)
        settings_menu.addAction(about_action)
        
        help_action = QAction(self.tr('使用说明'), self)
        help_action.triggered.connect(self.show_help)
        settings_menu.addAction(help_action)
        
        settings_btn.setMenu(settings_menu)
        
        toolbar_layout.addLayout(search_layout)
        toolbar_layout.addWidget(refresh_btn)
        toolbar_layout.addWidget(settings_btn)
        
        # 添加到顶部布局
        top_layout.addLayout(title_layout, 2)
        top_layout.addLayout(center_layout, 1)
        top_layout.addLayout(toolbar_layout, 2)
        
        # 主内容区域（B+C）
        content_area = QSplitter(Qt.Horizontal)
        content_area.setHandleWidth(1)
        
        # B区 - 左侧分类区域
        left_frame = QFrame()
        left_frame.setObjectName("leftFrame")
        left_frame.setStyleSheet("QFrame#leftFrame { background-color: #292a3e; border-radius: 0px; }")
        left_layout = QVBoxLayout(left_frame)
        left_layout.setContentsMargins(10, 10, 10, 10)
        
        # MOD分类标题
        category_header = QFrame()
        category_header.setObjectName("categoryHeader")
        category_header_layout = QHBoxLayout(category_header)
        category_header_layout.setContentsMargins(10, 0, 10, 0)  # 减少上下边距
        category_header_layout.setAlignment(Qt.AlignVCenter)  # 垂直居中对齐
        
        category_title = QLabel("MOD分类")
        category_title.setStyleSheet("font-size: 16pt; font-weight: bold; color: #ffffff;")
        category_title.setAlignment(Qt.AlignVCenter)  # 垂直居中对齐
        
        category_header_layout.addWidget(category_title)
        category_header_layout.addStretch(1)  # 添加弹性空间使按钮靠右对齐
        
        # 分类操作按钮
        btn_layout = QHBoxLayout()
        btn_layout.setSpacing(6)
        btn_layout.setAlignment(Qt.AlignVCenter)  # 垂直居中对齐
        
        self.add_cat_btn = QPushButton(self.tr('新增'))
        self.add_cat_btn.setFixedSize(60, 26)  # 设置固定大小
        self.add_cat_btn.setObjectName('primaryButton')
        self.add_cat_btn.setStyleSheet('font-size:12px;')
        self.add_cat_btn.setIcon(QIcon(resource_path('icons/12C编辑,重命名.svg')))
        self.add_cat_btn.setIconSize(QSize(18, 18))
        self.add_cat_btn.clicked.connect(self.add_category)
        
        self.rename_cat_btn = QPushButton(self.tr('重命名'))
        self.rename_cat_btn.setFixedSize(70, 26)  # 设置固定大小
        self.rename_cat_btn.setObjectName('primaryButton')
        self.rename_cat_btn.setStyleSheet('font-size:12px;')
        self.rename_cat_btn.setIcon(QIcon(resource_path('icons/12C编辑,重命名.svg')))
        self.rename_cat_btn.setIconSize(QSize(18, 18))
        self.rename_cat_btn.clicked.connect(self.rename_selected_category)
        
        self.del_cat_btn = QPushButton(self.tr('删除'))
        self.del_cat_btn.setFixedSize(60, 26)  # 设置固定大小
        self.del_cat_btn.setObjectName('dangerButton')
        self.del_cat_btn.setStyleSheet('font-size:12px;')
        self.del_cat_btn.setIcon(QIcon(resource_path('icons/卸载.svg')))
        self.del_cat_btn.setIconSize(QSize(18, 18))
        self.del_cat_btn.clicked.connect(self.delete_selected_category)  # 确保这一行存在并且正确连接
        
        btn_layout.addWidget(self.add_cat_btn)
        btn_layout.addWidget(self.rename_cat_btn)
        btn_layout.addWidget(self.del_cat_btn)
        
        category_header_layout.addLayout(btn_layout)
        left_layout.addWidget(category_header)
        
        # 分类树
        self.tree = QTreeWidget()
        self.tree.setHeaderHidden(True)
        self.tree.setMinimumWidth(250)
        self.tree.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.tree.customContextMenuRequested.connect(self.show_tree_context_menu)
        # 支持一级分类拖动排序和接收外部拖拽
        self.tree.setDragDropMode(QTreeWidget.DragDrop)  # 允许内部拖放和接收外部拖放
        self.tree.setDragEnabled(True)
        self.tree.setAcceptDrops(True)
        
        # 启用从外部拖拽到目录树的功能
        self.tree.setDropIndicatorShown(True)  # 显示放置指示器
        self.tree.dropEvent = self.on_tree_drop_event  # 重载树控件的放置事件
        
        left_layout.addWidget(self.tree)
        
        # C区 - 右侧MOD详情与操作区域
        right_frame = QFrame()
        right_frame.setObjectName("rightFrame")
        right_frame.setStyleSheet("QFrame#rightFrame { background-color: #23243a; border-radius: 0px; }")
        right_layout = QVBoxLayout(right_frame)
        right_layout.setContentsMargins(20, 20, 20, 20)
        right_layout.setSpacing(15)
        
        # MOD信息卡片 (C1区)
        info_frame = QFrame()
        info_frame.setObjectName('infoFrame')
        info_frame.setStyleSheet("QFrame#infoFrame { background-color: #292a3e; border-radius: 20px; padding: 10px; }")
        info_layout = QHBoxLayout(info_frame)  # 改为水平布局
        info_layout.setContentsMargins(10, 10, 10, 10)  # 减小内边距
        info_layout.setSpacing(10)  # 减小间距
        
        # 左侧预览图
        preview_layout = QVBoxLayout()
        preview_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        self.preview_label = QLabel("请导入预览图\n(推荐使用1:1或16:9的图片)")
        self.preview_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px; color: #bdbdbd; font-size: 12px;')
        self.preview_label.setMinimumSize(220, 160)  # 减小最小尺寸
        self.preview_label.setMaximumWidth(250)  # 减小最大宽度
        
        # 使预览标签可以接收鼠标事件
        self.preview_label.setMouseTracking(True)
        self.preview_label.setCursor(Qt.PointingHandCursor)  # 设置鼠标指针为手型
        self.preview_label.mousePressEvent = self.on_preview_label_clicked
        
        preview_layout.addWidget(self.preview_label)
        
        # 右侧信息区
        info_right_layout = QVBoxLayout()
        
        self.mod_name_label = QLabel()
        self.mod_name_label.setObjectName('modNameLabel')
        self.mod_name_label.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
        self.mod_name_label.setStyleSheet('font-size:18px;font-weight:bold;color:#cba6f7;padding:2px 0;')  # 减小字号和内边距
        info_right_layout.addWidget(self.mod_name_label)
        
        # 竖排字段信息
        self.mod_fields_frame = QFrame()
        self.mod_fields_frame.setObjectName('modFieldsFrame')
        self.mod_fields_layout = QVBoxLayout(self.mod_fields_frame)  # 改为垂直布局
        self.mod_fields_layout.setContentsMargins(0, 4, 0, 4)  # 减小内边距
        self.mod_fields_layout.setSpacing(6)  # 减小间距
        self.mod_fields_layout.setAlignment(Qt.AlignTop)
        info_right_layout.addWidget(self.mod_fields_frame)
        
        self.info_label = QLabel('选择MOD查看详细信息')
        self.info_label.setObjectName('info_label')
        self.info_label.setAlignment(Qt.AlignmentFlag.AlignTop | Qt.AlignmentFlag.AlignLeft)
        self.info_label.setWordWrap(True)
        self.info_label.setStyleSheet('font-size: 12px;')  # 减小字号
        info_right_layout.addWidget(self.info_label)
        
        # 添加伸缩因子，使信息区域能够自适应
        info_right_layout.addStretch(1)
        
        # 将左右两部分添加到信息卡片布局
        info_layout.addLayout(preview_layout, 1)
        info_layout.addLayout(info_right_layout, 1)
        
        right_layout.addWidget(info_frame, 1)  # 减小C1区权重
        
        # MOD列表 (C2区)
        mod_list_frame = QFrame()
        mod_list_frame.setObjectName("modListFrame")
        mod_list_frame.setStyleSheet("QFrame#modListFrame { background-color: #292a3e; border-radius: 20px; }")
        mod_list_layout = QVBoxLayout(mod_list_frame)
        mod_list_layout.setContentsMargins(15, 10, 15, 15)  # 减小上边距
        
        # 标签布局
        tab_layout = QHBoxLayout()
        tab_layout.setSpacing(5)  # 减小间距
        tab_layout.setContentsMargins(0, 0, 0, 0)  # 减小内边距
        
        # 添加编辑模式按钮
        self.edit_mode_cb = QCheckBox("编辑模式")
        self.edit_mode_cb.setObjectName("editModeCheckBox")
        self.edit_mode_cb.setStyleSheet("font-size: 12px; color: #b18cff;")
        self.edit_mode_cb.stateChanged.connect(self.toggle_edit_mode)
        self.edit_mode_cb.setVisible(True)  # 显示编辑模式复选框
        
        # 标签按钮
        self.all_tab = QPushButton(self.tr("全部"))
        self.all_tab.setCheckable(True)
        self.all_tab.setChecked(True)
        self.all_tab.clicked.connect(lambda: self.on_tab_clicked("all"))
        
        self.enabled_tab = QPushButton(self.tr("已启用"))
        self.enabled_tab.setCheckable(True)
        self.enabled_tab.clicked.connect(lambda: self.on_tab_clicked("enabled"))
        
        self.disabled_tab = QPushButton(self.tr("已禁用"))
        self.disabled_tab.setCheckable(True)
        self.disabled_tab.clicked.connect(lambda: self.on_tab_clicked("disabled"))
        
        tab_buttons = [self.all_tab, self.enabled_tab, self.disabled_tab]
        
        for btn in tab_buttons:
            btn.setFixedHeight(28)  # 减小高度
            btn.setMinimumWidth(90)  # 减小宽度
            btn.setStyleSheet("font-size: 12px;")  # 减小字号
        
        tab_layout.addWidget(self.edit_mode_cb)  # 添加编辑模式复选框
        tab_layout.addWidget(self.all_tab)
        tab_layout.addWidget(self.enabled_tab)
        tab_layout.addWidget(self.disabled_tab)
        tab_layout.addStretch(1)  # 添加弹性空间使按钮靠左对齐
        
        mod_list_layout.addLayout(tab_layout)
        
        # C2区 - MOD列表
        self.mod_list = QListWidget()
        self.mod_list.setObjectName("modList")
        self.mod_list.setDragEnabled(True)
        self.mod_list.setAcceptDrops(False)  # 列表本身不接受拖放
        self.mod_list.setDragDropMode(QAbstractItemView.DragOnly)  # 只允许从列表中拖出
        self.mod_list.setSelectionMode(QAbstractItemView.ExtendedSelection)  # 允许多选
        self.mod_list.itemClicked.connect(self.on_mod_list_clicked)
        self.mod_list.setStyleSheet("QListWidget { background-color: #23243a; border: none; border-radius: 10px; padding: 5px; font-size: 13px; }")  # 减小字号
        
        # 设置自定义MIME类型，确保拖拽数据能被正确识别
        self.mod_list.setDefaultDropAction(Qt.MoveAction)
        
        # 添加右键菜单和拖拽支持
        self.mod_list.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.mod_list.customContextMenuRequested.connect(self.show_mod_list_context_menu)
        
        # 自定义拖拽事件处理，支持复选框多选拖拽
        self.mod_list.startDrag = self.mod_list_start_drag
        
        mod_list_layout.addWidget(self.mod_list)
        right_layout.addWidget(mod_list_frame, 3)  # 增加C2区权重，从2增加到3
        
        # 操作按钮面板 (C3区)
        button_frame = QFrame()
        button_frame.setObjectName("buttonFrame")
        button_frame.setStyleSheet("QFrame#buttonFrame { background-color: #292a3e; border-radius: 20px; }")
        button_layout = QHBoxLayout(button_frame)  # 改为水平布局，所有按钮一排
        button_layout.setContentsMargins(15, 10, 15, 10)  # 减小内边距
        button_layout.setSpacing(8)  # 减小间距
        
        # 所有操作按钮
        self.import_btn = QPushButton('导入MOD')
        self.import_btn.setObjectName('primaryButton')
        self.import_btn.setIcon(QIcon(resource_path('icons/下载.svg')))
        self.import_btn.setIconSize(QSize(16, 16))  # 减小图标
        self.import_btn.setStyleSheet('font-size: 12px;')  # 减小字号
        self.import_btn.clicked.connect(self.import_mod)
        
        self.enable_btn = QPushButton('启用MOD')
        self.enable_btn.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
        self.enable_btn.setIconSize(QSize(16, 16))  # 减小图标
        self.enable_btn.setStyleSheet('font-size: 12px;')  # 减小字号
        self.enable_btn.clicked.connect(self.toggle_mod)
        self.enable_btn.setEnabled(False)
        
        self.delete_btn = QPushButton('删除MOD')
        self.delete_btn.setObjectName('dangerButton')
        self.delete_btn.setIcon(QIcon(resource_path('icons/卸载.svg')))
        self.delete_btn.setIconSize(QSize(16, 16))  # 减小图标
        self.delete_btn.setStyleSheet('font-size: 12px;')  # 减小字号
        self.delete_btn.clicked.connect(self.delete_mod)
        self.delete_btn.setEnabled(False)
        
        self.rename_mod_btn = QPushButton(self.tr('修改名称'))
        self.rename_mod_btn.setObjectName('primaryButton')
        self.rename_mod_btn.setIcon(QIcon(resource_path('icons/12C编辑,重命名.svg')))
        self.rename_mod_btn.setIconSize(QSize(16, 16))  # 减小图标
        self.rename_mod_btn.setStyleSheet('font-size: 12px;')  # 减小字号
        self.rename_mod_btn.clicked.connect(self.rename_mod)
        self.rename_mod_btn.setEnabled(False)
        
        self.change_preview_btn = QPushButton(self.tr('修改预览图'))
        self.change_preview_btn.setObjectName('primaryButton')
        self.change_preview_btn.setIcon(QIcon(resource_path('icons/图片.svg')))
        self.change_preview_btn.setIconSize(QSize(16, 16))  # 减小图标
        self.change_preview_btn.setStyleSheet('font-size: 12px;')  # 减小字号
        self.change_preview_btn.clicked.connect(self.change_mod_preview)
        self.change_preview_btn.setEnabled(False)
        
        # 将所有按钮添加到一行
        button_layout.addWidget(self.import_btn)
        button_layout.addWidget(self.enable_btn)
        button_layout.addWidget(self.delete_btn)
        button_layout.addWidget(self.rename_mod_btn)
        button_layout.addWidget(self.change_preview_btn)
        
        right_layout.addWidget(button_frame)
        
        # 添加B区和C区到分割器
        content_area.addWidget(left_frame)
        content_area.addWidget(right_frame)
        content_area.setStretchFactor(0, 1)  # B区占比
        content_area.setStretchFactor(1, 2)  # C区占比
        
        # 将A区和内容区域添加到主布局
        main_layout.addWidget(top_area)
        main_layout.addWidget(content_area)
        
        # 创建状态栏
        self.create_status_bar()
        
        # 加载分类和MOD
        self.load_categories()
        self.load_mods()
        
        # 连接信号
        self.tree.itemClicked.connect(self.on_item_clicked)
        
        # 在所有控件都创建好后再自动扫描
        self.auto_scan_mods()
        
        # 首次自动选中第一个分类和第一个MOD
        if self.tree.topLevelItemCount() > 0:
            self.tree.setCurrentItem(self.tree.topLevelItem(0))
            self.refresh_mod_list()
        
        # 当前激活的标签
        self.active_tab = "all"
        
    def create_status_bar(self):
        """创建状态栏"""
        status_bar = QStatusBar()
        self.setStatusBar(status_bar)
        
        # 添加状态标签
        self.path_label = QLabel()
        self.path_label.setObjectName('statusLabel')
        status_bar.addWidget(self.path_label)
        
        self.mod_count_label = QLabel()
        self.mod_count_label.setObjectName('statusLabel')
        status_bar.addPermanentWidget(self.mod_count_label)
        
        about_label = QLabel("爱酱MOD管理器 v1.6.3 (20250620) | 作者：爱酱 | <a href='https://qm.qq.com/q/bShcpMFj1Y'>QQ群：682707942</a>")
        about_label.setOpenExternalLinks(True)
        self.statusBar().addPermanentWidget(about_label)
        
        # 更新状态信息
        self.update_status_info()
        
    def update_status_info(self):
        """更新状态栏信息"""
        mods_path = self.config.get_mods_path()
        if mods_path:
            self.path_label.setText(f'MOD目录: {mods_path}')
            
        mods = self.config.get_mods()
        enabled_count = sum(1 for mod in mods.values() if mod.get('enabled', False))
        self.mod_count_label.setText(f'已加载MOD: {enabled_count}/{len(mods)}')
        
    def on_search_text_changed(self, text):
        """搜索框输入时同步刷新C区MOD列表"""
        self.filter_mods(text)
        self.refresh_mod_list(search_text=text)
        
    def filter_mods(self, text):
        """根据搜索文本过滤MOD（支持名称和real_name）"""
        text = text.lower()
        self.tree.blockSignals(True)
        for i in range(self.tree.topLevelItemCount()):
            category_item = self.tree.topLevelItem(i)
            has_visible_mods = False
            for j in range(category_item.childCount()):
                mod_item = category_item.child(j)
                mod_info = mod_item.data(0, Qt.ItemDataRole.UserRole)['info']
                name_match = text in mod_info.get('name', '').lower()
                real_name_match = text in mod_info.get('real_name', '').lower()
                mod_item.setHidden(not (name_match or real_name_match))
                if not mod_item.isHidden():
                    has_visible_mods = True
            category_item.setHidden(not has_visible_mods)
        self.tree.blockSignals(False)
        
    def refresh_mods(self):
        """刷新MOD列表"""
        print("[调试] refresh_mods: 开始刷新MOD列表")
        
        # 保存当前选中的分类
        current_category = None
        current_item = self.tree.currentItem()
        if current_item:
            data = current_item.data(0, Qt.ItemDataRole.UserRole)
            if data:
                if data['type'] == 'category':
                    current_category = data['name']
                elif data['type'] == 'subcategory':
                    current_category = data['full_path']
                print(f"[调试] refresh_mods: 保存当前选中的分类: {current_category}")
                    
        # 清理无效的MOD
        cleaned_count = self.config.clean_invalid_mods()
        if cleaned_count:
            print(f"[调试] 已清理 {cleaned_count} 个无效的MOD记录")
        
        # 搜索现有MOD
        found_mods = self.mod_manager.scan_mods_directory()
        print(f"[调试] 扫描到 {len(found_mods)} 个MOD文件")
        
        # 重新加载分类和MOD列表
        self.load_categories()
        self.load_mods()
        
        # 尝试恢复之前选中的分类
        if current_category:
            found = False
            print(f"[调试] refresh_mods: 尝试恢复选中的分类: {current_category}")
            
            for i in range(self.tree.topLevelItemCount()):
                item = self.tree.topLevelItem(i)
                data = item.data(0, Qt.ItemDataRole.UserRole)
                
                # 如果是一级分类
                if data['type'] == 'category' and data['name'] == current_category:
                    self.tree.setCurrentItem(item)
                    found = True
                    print(f"[调试] refresh_mods: 恢复选中一级分类: {current_category}")
                    break
                    
                # 如果是二级分类，检查所有子项
                if '/' in current_category and current_category.startswith(data['name'] + '/'):
                    sub_name = current_category.split('/', 1)[1]
                    for j in range(item.childCount()):
                        child = item.child(j)
                        child_data = child.data(0, Qt.ItemDataRole.UserRole)
                        if child_data['type'] == 'subcategory' and child_data['name'] == sub_name:
                            self.tree.setCurrentItem(child)
                            found = True
                            print(f"[调试] refresh_mods: 恢复选中二级分类: {current_category}")
                            break
                if found:
                    break
            
            if not found:
                print(f"[警告] refresh_mods: 无法恢复选中的分类: {current_category}")
        
        # 刷新MOD列表
        self.refresh_mod_list()
        
        self.update_status_info()
        self.statusBar().showMessage(self.tr('MOD列表已刷新'), 3000)
        
    def clear_tree(self):
        """清空树形控件，但保留所有数据"""
        self.tree.clear()
        
    def show_settings(self):
        """显示设置对话框"""
        # TODO: 实现设置对话框
        pass
        
    def load_categories(self):
        """加载分类到目录树"""
        self.tree.clear()
        
        # 获取按时间戳排序的分类列表
        categories = self.config.get_categories()
        print(f"[调试] load_categories: 加载分类列表: {categories}")
        
        # 先加载所有一级分类
        primary_categories = []
        sub_categories = {}
        
        # 分离一级分类和二级分类
        for category in categories:
            if '/' in category:
                main_cat, sub_cat = category.split('/', 1)
                if main_cat not in sub_categories:
                    sub_categories[main_cat] = []
                sub_categories[main_cat].append(sub_cat)
                if main_cat not in primary_categories:
                    primary_categories.append(main_cat)
            else:
                if category not in primary_categories:
                    primary_categories.append(category)
        
        # 确保默认分类在列表中
        default_category_name = self.config.default_category_name
        if default_category_name not in primary_categories:
            primary_categories.insert(0, default_category_name)
        
        # 按照配置中的顺序添加一级分类（已经按时间戳排序）
        ordered_primary_categories = []
        for category in categories:
            if '/' not in category and category not in ordered_primary_categories:
                ordered_primary_categories.append(category)
        
        # 添加任何可能遗漏的一级分类
        for category in primary_categories:
            if category not in ordered_primary_categories:
                ordered_primary_categories.append(category)
        
        # 添加一级分类到树形控件
        for category in ordered_primary_categories:
            item = QTreeWidgetItem([category])
            item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': category})
            item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
            self.tree.addTopLevelItem(item)
            
            # 添加该一级分类下的二级分类
            if category in sub_categories:
                for sub_cat in sub_categories[category]:
                    sub_item = QTreeWidgetItem([sub_cat])
                    full_path = f"{category}/{sub_cat}"
                    sub_item.setData(0, Qt.ItemDataRole.UserRole, {
                        'type': 'subcategory', 
                        'name': sub_cat,
                        'full_path': full_path
                    })
                    sub_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
                    item.addChild(sub_item)
        
        print(f"[调试] load_categories: 加载完成，一级分类: {ordered_primary_categories}，二级分类: {sub_categories}")
        
    def load_mods(self):
        """加载分类，但不在左侧树中显示MOD"""
        self.tree.blockSignals(True)
        
        # 第一遍：创建所有二级分类
        sub_categories = {}
        mods = self.config.get_mods()
        
        for mod_id, mod_info in mods.items():
            category = mod_info.get('category', self.config.default_category_name)
            # 检查是否包含 / 分隔符，表示二级分类
            if '/' in category:
                main_cat, sub_cat = category.split('/', 1)
                if main_cat not in sub_categories:
                    sub_categories[main_cat] = set()
                sub_categories[main_cat].add(sub_cat)
        
        # 创建二级分类项
        for main_cat, sub_cats in sub_categories.items():
            main_item = self.find_category_item(main_cat)
            if main_item:
                for sub_cat in sub_cats:
                    sub_item = QTreeWidgetItem([sub_cat])
                    sub_item.setData(0, Qt.ItemDataRole.UserRole, {
                        'type': 'subcategory', 
                        'name': sub_cat,
                        'full_path': f"{main_cat}/{sub_cat}"
                    })
                    sub_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
                    main_item.addChild(sub_item)
        
        self.tree.blockSignals(False)
        
    def find_category_item(self, category_name):
        """查找分类项"""
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            if item.data(0, Qt.ItemDataRole.UserRole)['name'] == category_name:
                return item
        return None
        
    def show_tree_context_menu(self, position):
        """显示目录树右键菜单"""
        item = self.tree.itemAt(position)
        if not item:
            return
            
        menu = QMenu()
        data = item.data(0, Qt.ItemDataRole.UserRole)
        
        if data['type'] == 'category':
            rename_action = QAction('重命名分类', self)
            rename_action.triggered.connect(lambda: self.rename_category(item))
            menu.addAction(rename_action)
            
            # 移除对默认分类的特殊处理，所有分类都可以删除
            if data['name'] != self.config.default_category_name:
                delete_action = QAction('删除分类', self)
                delete_action.triggered.connect(lambda: self.delete_category(item))
                menu.addAction(delete_action)
            
            add_subcategory_action = QAction('添加子分类', self)
            add_subcategory_action.triggered.connect(lambda: self.add_subcategory(item))
            menu.addAction(add_subcategory_action)
                
            add_action = QAction('添加分类', self)
            add_action.triggered.connect(self.add_category)
            menu.addAction(add_action)
            
        elif data['type'] == 'subcategory':
            rename_action = QAction('重命名子分类', self)
            rename_action.triggered.connect(lambda: self.rename_subcategory(item))
            menu.addAction(rename_action)
            
            delete_action = QAction('删除子分类', self)
            delete_action.triggered.connect(lambda: self.delete_subcategory(item))
            menu.addAction(delete_action)
            
        elif data['type'] == 'mod':
            edit_action = QAction('编辑信息', self)
            edit_action.triggered.connect(lambda: self.edit_mod_info(item))
            menu.addAction(edit_action)
            
            if data['info'].get('enabled', False):
                disable_action = QAction('禁用MOD', self)
                disable_action.triggered.connect(lambda: self.toggle_mod())
            else:
                enable_action = QAction('启用MOD', self)
                enable_action.triggered.connect(lambda: self.toggle_mod())
            menu.addAction(disable_action if data['info'].get('enabled', False) else enable_action)
            
            delete_action = QAction('删除MOD', self)
            delete_action.triggered.connect(lambda: self.delete_mod())
            menu.addAction(delete_action)
            
        menu.exec(self.tree.mapToGlobal(position))
        
    def add_category(self):
        """添加新分类"""
        name, ok = self.input_dialog('添加分类', '请输入分类名称：')
        if ok and name:
            # 检查分类是否已存在
            categories = self.config.get_categories()
            if name in categories:
                self.show_message('提示', f'分类 "{name}" 已存在！')
                return
                
            # 添加到配置
            self.config.add_category(name)
            
            # 添加到树控件
            item = QTreeWidgetItem([name])
            item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': name})
            item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
            self.tree.addTopLevelItem(item)
            
            # 保存分类顺序
            self.save_category_order()
            
            # 重新加载分类树，确保正确的排序
            self.load_categories()
            
            # 选中新添加的分类
            self.select_category_by_name(name)
            
            # 刷新MOD列表
            self.refresh_mod_list()
            
            print(f"[调试] add_category: 添加分类 {name} 成功")
    
    def add_subcategory(self, parent_item):
        """添加子分类"""
        parent_name = parent_item.data(0, Qt.ItemDataRole.UserRole)['name']
        name, ok = self.input_dialog('添加子分类', '请输入子分类名称：')
        if ok and name:
            # 创建完整分类路径
            full_path = f"{parent_name}/{name}"
            
            # 检查分类是否已存在
            categories = self.config.get_categories()
            if full_path in categories:
                self.show_message('提示', f'子分类 "{name}" 已存在！')
                return
                
            # 添加到配置
            categories.append(full_path)
            self.config.set_categories(categories)
            
            # 添加到树形控件
            sub_item = QTreeWidgetItem([name])
            sub_item.setData(0, Qt.ItemDataRole.UserRole, {
                'type': 'subcategory', 
                'name': name,
                'full_path': full_path
            })
            sub_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
            parent_item.addChild(sub_item)
            
            # 展开父分类并选中新添加的子分类
            self.tree.expandItem(parent_item)
            self.tree.setCurrentItem(sub_item)
            
            # 保存分类顺序
            self.save_category_order()
            
            # 刷新MOD列表
            self.refresh_mod_list()

    def rename_category(self, item):
        """重命名分类"""
        old_name = item.data(0, Qt.ItemDataRole.UserRole)['name']
        print(f"[调试] rename_category: 准备重命名分类 {old_name}")
        print(f"[调试] rename_category: 当前默认分类名称: {self.config.default_category_name}")
        
        new_name, ok = self.input_dialog('重命名分类', '请输入新的分类名称：', old_name)
        
        if ok and new_name and new_name != old_name:
            print(f"[调试] rename_category: 重命名分类 {old_name} -> {new_name}")
            
            # 更新config中的分类名称
            self.config.rename_category(old_name, new_name)
            
            # 更新所有MOD的分类信息
            mods = self.config.get_mods()
            updated_count = 0
            for mod_id, mod_info in mods.items():
                if mod_info.get('category') == old_name:
                    print(f"[调试] rename_category: 更新MOD {mod_id} 的分类")
                    mod_info['category'] = new_name
                    self.config.update_mod(mod_id, mod_info)
                    updated_count += 1
                # 同时更新二级分类
                elif '/' in mod_info.get('category', '') and mod_info.get('category', '').startswith(old_name + '/'):
                    old_subcat = mod_info.get('category')
                    new_subcat = old_subcat.replace(old_name + '/', new_name + '/', 1)
                    print(f"[调试] rename_category: 更新MOD {mod_id} 的子分类 {old_subcat} -> {new_subcat}")
                    mod_info['category'] = new_subcat
                    self.config.update_mod(mod_id, mod_info)
                    updated_count += 1
            
            # 更新分类列表中的二级分类路径
            categories = self.config.get_categories()
            updated_categories = []
            for cat in categories:
                if '/' in cat and cat.startswith(old_name + '/'):
                    new_cat = cat.replace(old_name + '/', new_name + '/', 1)
                    updated_categories.append(new_cat)
                elif cat == old_name:
                    updated_categories.append(new_name)
                else:
                    updated_categories.append(cat)
            
            # 确保默认分类始终存在
            if '默认分类' not in updated_categories:
                updated_categories.append('默认分类')
                
            self.config.set_categories(updated_categories)
            
            print(f"[调试] rename_category: 更新了 {updated_count} 个MOD的分类")
            
            # 刷新UI
            self.load_categories()
            self.load_mods()
            
            # 确保选中重命名后的分类
            found = False
            for i in range(self.tree.topLevelItemCount()):
                item = self.tree.topLevelItem(i)
                if item.data(0, Qt.ItemDataRole.UserRole)['name'] == new_name:
                    print(f"[调试] rename_category: 选中新分类 {new_name}")
                    self.tree.setCurrentItem(item)
                    self.tree.setCurrentItem(item)  # 双重设置确保触发选择事件
                    found = True
                    break
            
            if not found:
                # 如果找不到重命名后的分类，选择默认分类
                self.select_default_category()
                    
            # 强制刷新MOD列表
            self.refresh_mod_list(keep_selected=False)
            
            # 确保MOD列表显示
            self.on_item_clicked(self.tree.currentItem())
            print("[调试] rename_category: 完成分类重命名")
            
    def rename_subcategory(self, item):
        """重命名子分类"""
        data = item.data(0, Qt.ItemDataRole.UserRole)
        old_name = data['name']
        full_path = data['full_path']
        parent_name = full_path.split('/', 1)[0]
        
        new_name, ok = self.input_dialog('重命名子分类', '请输入新的子分类名称：', old_name)
        
        if ok and new_name and new_name != old_name:
            print(f"[调试] rename_subcategory: 重命名子分类 {old_name} -> {new_name}")
            
            new_full_path = f"{parent_name}/{new_name}"
            
            # 更新所有MOD的分类信息
            mods = self.config.get_mods()
            updated_count = 0
            for mod_id, mod_info in mods.items():
                if mod_info.get('category') == full_path:
                    print(f"[调试] rename_subcategory: 更新MOD {mod_id} 的分类")
                    mod_info['category'] = new_full_path
                    self.config.update_mod(mod_id, mod_info)
                    updated_count += 1
            
            # 更新分类列表
            categories = self.config.get_categories()
            if full_path in categories:
                idx = categories.index(full_path)
                categories[idx] = new_full_path
                self.config.set_categories(categories)
            
            print(f"[调试] rename_subcategory: 更新了 {updated_count} 个MOD的分类")
            
            # 刷新UI
            self.load_categories()
            self.load_mods()
            
            # 强制刷新MOD列表
            self.refresh_mod_list()
            print("[调试] rename_subcategory: 完成子分类重命名")
            
    def delete_category(self, item):
        """删除分类"""
        name = item.data(0, Qt.ItemDataRole.UserRole)['name']
        default_category_name = self.config.default_category_name
        if name == default_category_name:
            return
            
        reply = self.msgbox_question_zh('确认删除', f'确定要删除分类"{name}"吗？\n该分类下的MOD将移至{default_category_name}分类。')
        
        if reply == QMessageBox.StandardButton.Yes:
            # 更新所有MOD的分类信息
            mods = self.config.get_mods()
            for mod_id, mod_info in mods.items():
                if mod_info.get('category') == name:
                    mod_info['category'] = default_category_name
                    self.config.update_mod(mod_id, mod_info)
                # 同时处理二级分类
                elif '/' in mod_info.get('category', '') and mod_info.get('category', '').startswith(name + '/'):
                    mod_info['category'] = default_category_name
                    self.config.update_mod(mod_id, mod_info)
            
            # 从分类列表中删除所有相关分类
            categories = self.config.get_categories()
            updated_categories = []
            for cat in categories:
                if cat != name and not (cat.startswith(name + '/')):
                    updated_categories.append(cat)
            
            self.config.set_categories(updated_categories)
            self.load_categories()
            self.load_mods()
    
    def delete_subcategory(self, item):
        """删除子分类"""
        data = item.data(0, Qt.ItemDataRole.UserRole)
        full_path = data['full_path']
        name = data['name']
        parent_name = full_path.split('/', 1)[0]
        
        reply = self.msgbox_question_zh('确认删除', f'确定要删除子分类"{name}"吗？\n该子分类下的MOD将移至上级分类。')
        
        if reply == QMessageBox.StandardButton.Yes:
            # 更新所有MOD的分类信息
            mods = self.config.get_mods()
            for mod_id, mod_info in mods.items():
                if mod_info.get('category') == full_path:
                    mod_info['category'] = parent_name
                    self.config.update_mod(mod_id, mod_info)
            
            # 从分类列表中删除
            categories = self.config.get_categories()
            if full_path in categories:
                categories.remove(full_path)
                self.config.set_categories(categories)
            
            self.load_categories()
            self.load_mods()

    def auto_scan_mods(self):
        """自动扫描MOD目录并加载MOD"""
        mods_path = self.config.get_mods_path()
        if not mods_path:
            return
            
        print(f"[调试] auto_scan_mods: 开始扫描MOD目录: {mods_path}")
        
        # 确保备份路径存在
        backup_path = self.config.get_backup_path()
        if not backup_path or not str(backup_path).strip():
            default_backup_path = os.path.join(os.getcwd(), "modbackup")
            print(f"[调试] auto_scan_mods: 设置默认备份路径: {default_backup_path}")
            self.config.set_backup_path(default_backup_path)
            backup_path = default_backup_path
        
        backup_dir = Path(backup_path)
        if not backup_dir.exists():
            print(f"[调试] auto_scan_mods: 创建备份目录: {backup_dir}")
            backup_dir.mkdir(parents=True, exist_ok=True)
        
        try:
            # 扫描MOD目录
            found_mods = self.mod_manager.scan_mods_directory()
            print(f"[调试] auto_scan_mods: 找到 {len(found_mods)} 个MOD")
            
            # 获取现有MOD列表
            existing_mods = self.config.get_mods()
            existing_mod_names = set([mod.get('name', '') for mod in existing_mods.values()])
            
            # 导入新发现的MOD
            new_mods_count = 0
            for mod_info in found_mods:
                mod_name = mod_info.get('name', '')
                if mod_name not in existing_mod_names:
                    print(f"[调试] auto_scan_mods: 导入新MOD: {mod_name}")
                    # 备份MOD文件
                    self.mod_manager.backup_mod(mod_name, mod_info)
                    new_mods_count += 1
            
            if new_mods_count > 0:
                print(f"[调试] auto_scan_mods: 导入了 {new_mods_count} 个新MOD")
                self.statusBar().showMessage(f'已导入 {new_mods_count} 个新MOD', 3000)
            
            # 加载分类和MOD
            self.load_categories()
            self.load_mods()
            
        except Exception as e:
            print(f"[错误] auto_scan_mods: 扫描MOD目录失败: {str(e)}")
            import traceback
            traceback.print_exc()

    def import_mod(self):
        """导入MOD"""
        file_path, _ = QFileDialog.getOpenFileName(
            self,
            '选择MOD文件',
            '',
            '压缩文件 (*.zip *.rar *.7z)'
        )
        if file_path:
            # 检查文件是否存在
            if not os.path.exists(file_path):
                self.show_message(self.tr('导入失败'), self.tr(f'文件不存在: {file_path}'), QMessageBox.Critical)
                return
                
            # 检查文件大小
            try:
                file_size = os.path.getsize(file_path)
                size_mb = file_size / (1024 * 1024)
                if size_mb > 300:  # 警告超过300MB的文件
                    reply = self.msgbox_question_zh(
                        '文件较大', 
                        f'选择的文件大小为 {size_mb:.2f}MB，较大的文件可能需要更长的处理时间。\n是否继续导入？'
                    )
                    if reply != QMessageBox.StandardButton.Yes:
                        return
            except Exception as e:
                print(f"[警告] 检查文件大小失败: {e}")
            
            # 显示进度对话框
            progress_dialog = QProgressDialog("正在导入MOD...", "取消", 0, 0, self)
            progress_dialog.setWindowTitle("导入MOD")
            progress_dialog.setWindowModality(Qt.WindowModal)
            progress_dialog.setMinimumDuration(500)  # 显示对话框前的延迟时间（毫秒）
            progress_dialog.setAutoClose(False)
            progress_dialog.setCancelButton(None)  # 禁用取消按钮
            
            # 更新状态栏
            self.statusBar().showMessage(self.tr('正在导入MOD...'))
            
            # 创建并启动导入线程
            self.import_thread = ImportModThread(self.mod_manager, file_path)
            self.import_thread.finished.connect(lambda mod_info, error: self.on_import_mod_finished(mod_info, error, progress_dialog))
            self.import_thread.start()

    def on_import_mod_finished(self, mod_info, error, progress_dialog=None):
        """处理MOD导入完成事件"""
        # 关闭进度对话框
        if progress_dialog is not None:
            progress_dialog.close()
            
        if error:
            self.statusBar().showMessage(self.tr('导入MOD失败'), 3000)
            self.show_message(self.tr('导入MOD失败'), error, QMessageBox.Critical)
            return
                
        if not mod_info:
            self.statusBar().showMessage(self.tr('导入失败，未找到有效MOD文件！'), 3000)
            self.show_message(self.tr('错误'), self.tr('导入失败，未找到有效MOD文件！'), QMessageBox.Warning)
            return
                
        imported_mod_ids = []
            
        # 获取当前选中的分类
        current_category = '默认分类'
        current_item = self.tree.currentItem()
        if current_item:
            data = current_item.data(0, Qt.ItemDataRole.UserRole)
            if data:
                if data['type'] == 'category':
                    current_category = data['name']
                elif data['type'] == 'subcategory':
                    current_category = data['full_path']
                        
        print(f"[调试] on_import_mod_finished: 当前选中的分类: {current_category}")
            
        # 处理单个MOD或MOD列表
        if isinstance(mod_info, list):
            mod_infos = mod_info
        else:
            mod_infos = [mod_info]
                
        # 报告错误计数
        error_count = 0
                
        # 导入所有MOD
        for info in mod_infos:
            try:
                # 获取MOD ID
                mod_id = info.get('name', str(uuid.uuid4()))
                    
                # 设置MOD分类为当前选中的分类
                info['category'] = current_category
                print(f"[调试] on_import_mod_finished: 设置MOD {mod_id} 分类为: {current_category}")
                    
                # 添加MOD到配置
                self.config.add_mod(mod_id, info)
                    
                # 启用MOD
                try:
                    enable_result = self.mod_manager.enable_mod(mod_id)
                    if not enable_result:
                        print(f"[警告] on_import_mod_finished: 启用MOD {mod_id} 失败")
                        error_count += 1
                except Exception as e:
                    print(f"[错误] on_import_mod_finished: 启用MOD {mod_id} 时出错: {e}")
                    error_count += 1
                        
                imported_mod_ids.append(mod_id)
            except Exception as e:
                print(f"[错误] on_import_mod_finished: 处理MOD导入结果时出错: {e}")
                import traceback
                traceback.print_exc()
                error_count += 1
                    
        # 刷新MOD列表
        self.refresh_mod_list()
            
        # 如果导入了MOD，选中第一个
        if imported_mod_ids:
            for i in range(self.mod_list.count()):
                item = self.mod_list.item(i)
                if item.data(Qt.UserRole) == imported_mod_ids[0]:
                    self.mod_list.setCurrentRow(i)
                    self.on_mod_list_clicked(item)
                    break
                
            # 显示成功消息
            if len(imported_mod_ids) == 1:
                if error_count > 0:
                    self.statusBar().showMessage(self.tr('MOD导入成功，但启用过程中有错误。'), 3000)
                    self.show_message(self.tr('警告'), self.tr('MOD导入成功，但启用过程中有错误。'), QMessageBox.Warning)
                else:
                    self.statusBar().showMessage(self.tr('MOD导入并已启用！'), 3000)
                    self.show_message(self.tr('成功'), self.tr('MOD导入并已启用！'))
            else:
                if error_count > 0:
                    self.statusBar().showMessage(self.tr(f'成功导入 {len(imported_mod_ids)} 个MOD，但有 {error_count} 个MOD启用失败！'), 3000)
                    self.show_message(self.tr('警告'), self.tr(f'成功导入 {len(imported_mod_ids)} 个MOD，但有 {error_count} 个MOD启用失败！'), QMessageBox.Warning)
                else:
                    self.statusBar().showMessage(self.tr(f'成功导入 {len(imported_mod_ids)} 个MOD！'), 3000)
                    self.show_message(self.tr('成功'), self.tr(f'成功导入 {len(imported_mod_ids)} 个MOD！'))
        else:
            self.statusBar().showMessage(self.tr('导入失败，未找到有效MOD文件！'), 3000)
            self.show_message(self.tr('错误'), self.tr('导入失败，未找到有效MOD文件！'), QMessageBox.Warning)

    def toggle_mod(self):
        """启用/禁用MOD（以C区选中为准）"""
        item = self.mod_list.currentItem()
        if not item:
            print("[调试] toggle_mod: C区未选中任何项")
            return
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            print("[调试] toggle_mod: 未找到mod_info")
            return
        print(f"[调试] toggle_mod: mod_id={mod_id}, enabled={mod_info.get('enabled', False)}")
        try:
            self.statusBar().showMessage('正在处理...')
            if mod_info.get('enabled', False):
                print(f"[调试] 禁用MOD，调用disable_mod，目标文件：{mod_info.get('files', [])}")
                result = self.mod_manager.disable_mod(mod_id)
                print(f"[调试] disable_mod结果: {result}")
                mod_info['enabled'] = False
                self.enable_btn.setText('启用MOD')
                self.enable_btn.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
                self.statusBar().showMessage('MOD已禁用', 3000)
            else:
                print(f"[调试] 启用MOD，调用enable_mod，目标文件：{mod_info.get('files', [])}")
                result = self.mod_manager.enable_mod(mod_id)
                print(f"[调试] enable_mod结果: {result}")
                mod_info['enabled'] = True
                self.enable_btn.setText('禁用MOD')
                self.enable_btn.setIcon(QIcon(resource_path('icons/禁用.svg')))
                self.statusBar().showMessage('MOD已启用', 3000)
            self.config.update_mod(mod_id, mod_info)
            print(f"[调试] toggle_mod: 更新config，enabled={mod_info['enabled']}")
            
            # 保存当前标签页状态
            current_tab = self.active_tab
            
            # 如果当前在"已启用"或"已禁用"标签页，并且MOD状态改变，则可能会导致MOD从列表中消失
            # 在这种情况下，我们需要切换到"全部"标签页以确保用户能看到MOD
            if (current_tab == "enabled" and not mod_info['enabled']) or (current_tab == "disabled" and mod_info['enabled']):
                self.active_tab = "all"
                self.all_tab.setChecked(True)
                self.enabled_tab.setChecked(False)
                self.disabled_tab.setChecked(False)
            
            self.refresh_mod_list(search_text=self.search_box.text())
            self.update_status_info()
            
            # 更新信息面板
            self.show_mod_info(mod_info)
            
        except Exception as e:
            print(f"[调试] toggle_mod: 操作失败: {e}")
            self.statusBar().showMessage('操作失败！', 3000)
            self.show_message(self.tr('错误'), f'操作失败：{str(e)}')

    def on_item_clicked(self, item):
        """处理树形控件项目点击事件"""
        data = item.data(0, Qt.ItemDataRole.UserRole)
        if data['type'] == 'category' or data['type'] == 'subcategory':
            self.refresh_mod_list()
            # 自动选中第一个MOD并展示信息卡片
            if self.mod_list.count() > 0:
                self.mod_list.setCurrentRow(0)
                self.on_mod_list_clicked(self.mod_list.item(0))
            else:
                self.clear_info_panel()
        else:
            self.clear_info_panel()
            
    def clear_info_panel(self):
        """清空右侧信息面板"""
        # 清空MOD信息
        self.mod_name_label.setText("")
        
        # 清空预览图
        self.preview_label.clear()
        self.preview_label.setText("无预览图")
        self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px; color: #bdbdbd; font-size: 12px;')
        
        # 清除字段信息
        for i in reversed(range(self.mod_fields_layout.count())):
            widget = self.mod_fields_layout.itemAt(i)
            if widget:
                if widget.widget():
                    widget.widget().setParent(None)
                elif widget.layout():
                    # 如果是布局，需要递归清除其中的所有控件
                    layout = widget.layout()
                    for j in reversed(range(layout.count())):
                        if layout.itemAt(j).widget():
                            layout.itemAt(j).widget().setParent(None)
        
        # 显示提示信息
        self.mod_fields_frame.hide()
        self.info_label.show()
        
        # 禁用操作按钮
        self.enable_btn.setEnabled(False)
        self.delete_btn.setEnabled(False)
        self.rename_mod_btn.setEnabled(False)
        self.change_preview_btn.setEnabled(False)
        
    def show_mod_info(self, mod_info):
        """显示MOD信息（名称上方，字段竖排）"""
        print(f"[调试] show_mod_info: 显示MOD信息: {mod_info.get('name', '未知')}")
        
        real_name = mod_info.get('real_name', '')
        name = mod_info.get('name', '未命名MOD')
        if real_name and real_name != name:
            show_name = f"{name}（{real_name}）"
        else:
            show_name = name
        self.mod_name_label.setText(show_name)
        self.mod_name_label.show()
        
        # 显示预览图
        preview_image = mod_info.get('preview_image', '')
        print(f"[调试] show_mod_info: 预览图路径: {preview_image}")
        
        # 检查预览图路径是否存在
        if preview_image:
            # 如果预览图路径不存在，尝试在备份目录中查找
            if not os.path.exists(preview_image):
                print(f"[警告] show_mod_info: 预览图不存在: {preview_image}，尝试查找替代路径")
                
                # 获取备份路径
                backup_path = self.config.get_backup_path()
                if not backup_path:
                    backup_path = os.path.join(os.getcwd(), "modbackup")
                backup_path = Path(backup_path)
                
                # 检查MOD备份目录
                mod_id = mod_info.get('name', '')
                mod_backup_dir = backup_path / mod_id
                
                # 查找预览图文件
                if mod_backup_dir.exists():
                    preview_files = list(mod_backup_dir.glob("preview.*"))
                    if preview_files:
                        preview_image = str(preview_files[0])
                        print(f"[调试] show_mod_info: 找到替代预览图: {preview_image}")
                        # 更新MOD信息中的预览图路径
                        mod_info['preview_image'] = preview_image
                        self.config.update_mod(mod_id, mod_info)
                    else:
                        print(f"[警告] show_mod_info: 未找到替代预览图")
                else:
                    print(f"[警告] show_mod_info: MOD备份目录不存在: {mod_backup_dir}")
        
        if preview_image and os.path.exists(preview_image):
            try:
                print(f"[调试] show_mod_info: 尝试加载预览图: {preview_image}")
                pixmap = QPixmap(preview_image)
                if not pixmap.isNull():
                    self.preview_label.setPixmap(pixmap.scaled(
                        250, 180, Qt.AspectRatioMode.KeepAspectRatio,
                        Qt.TransformationMode.SmoothTransformation
                    ))
                    # 清除文本和样式
                    self.preview_label.setText("")
                    self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px;')
                    print(f"[调试] show_mod_info: 成功加载预览图: {preview_image}")
                else:
                    # 如果无法加载预览图，显示提示文字
                    self.preview_label.clear()
                    self.preview_label.setText("无法加载预览图")
                    self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px; color: #bdbdbd; font-size: 12px;')
                    print(f"[警告] show_mod_info: 无法加载预览图: {preview_image}")
            except Exception as e:
                self.preview_label.clear()
                self.preview_label.setText("加载预览图失败")
                self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px; color: #bdbdbd; font-size: 12px;')
                print(f"[错误] show_mod_info: 加载预览图失败: {e}")
                import traceback
                traceback.print_exc()
        else:
            # 如果没有预览图，显示默认提示
            self.preview_label.clear()
            self.preview_label.setText("无预览图")
            self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px; color: #bdbdbd; font-size: 12px;')
            if preview_image:
                print(f"[警告] show_mod_info: 预览图路径存在但文件不存在: {preview_image}")
            else:
                print(f"[调试] show_mod_info: 没有设置预览图路径")
        
        # 确保鼠标指针始终为手型
        self.preview_label.setCursor(Qt.PointingHandCursor)
        
        # 清除之前的字段信息
        for i in reversed(range(self.mod_fields_layout.count())):
            widget = self.mod_fields_layout.itemAt(i)
            if widget:
                if widget.widget():
                    widget.widget().setParent(None)
                elif widget.layout():
                    # 如果是布局，需要递归清除其中的所有控件
                    layout = widget.layout()
                    for j in reversed(range(layout.count())):
                        if layout.itemAt(j).widget():
                            layout.itemAt(j).widget().setParent(None)
        
        # 显示MOD信息
        self.mod_fields_frame.show()
        self.info_label.hide()
        
        # 添加MOD信息字段
        fields = [
            ('状态', '已启用' if mod_info.get('enabled', False) else '已禁用', 'color: #4ade80;' if mod_info.get('enabled', False) else 'color: #f87171;'),
            ('大小', f"{mod_info.get('size', '--')} MB", 'color: #e2e8f0;'),
            ('导入日期', mod_info.get('import_date', '--'), 'color: #e2e8f0;'),
            ('描述', mod_info.get('description', '无描述'), 'color: #e2e8f0;')
        ]
        
        for label_text, value_text, style in fields:
            field_layout = QHBoxLayout()
            field_layout.setContentsMargins(0, 0, 0, 0)
            field_layout.setSpacing(5)
            
            label = QLabel(f"{label_text}:")
            label.setStyleSheet('font-weight: bold; color: #cba6f7;')
            field_layout.addWidget(label)
            
            value = QLabel(value_text)
            if style:
                value.setStyleSheet(style)
            field_layout.addWidget(value, 1)
            
            self.mod_fields_layout.addLayout(field_layout)
        
        # 保存当前显示的MOD ID
        self.current_mod_id = mod_info.get('name', '')
        
        # 启用操作按钮
        self.enable_btn.setEnabled(True)
        self.delete_btn.setEnabled(True)
        self.rename_mod_btn.setEnabled(True)
        self.change_preview_btn.setEnabled(True)
        
        # 设置启用/禁用按钮的文本和图标
        if mod_info.get('enabled', False):
            self.enable_btn.setText('禁用MOD')
            self.enable_btn.setIcon(QIcon(resource_path('icons/禁用.svg')))
        else:
            self.enable_btn.setText('启用MOD')
            self.enable_btn.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
        
        print(f"[调试] show_mod_info: 显示完成，已启用操作按钮")

    def set_mods_directory(self):
        """设置MOD文件夹"""
        path = QFileDialog.getExistingDirectory(
            self,
            '选择MOD文件夹',
            str(Path.home())
        )
        
        if path:
            self.config.set_mods_path(path)
            self.mod_manager.mods_path = Path(path)
            
            # 检查是否为特定路径，并且是否为首次设置
            expected_path = "steam\\steamapps\\common\\StellarBlade\\SB\\Content\\Paks\\~mods"
            if expected_path.replace("\\", "/") in str(path).replace("\\", "/") and not self.config.get('mods_path_notified', False):
                self.show_message('提示', f'已成功设置MOD存放路径为:\n{path}\n\n您可以将MOD文件放入此文件夹。')
                self.config.set('mods_path_notified', True)
            
            # 扫描现有MOD
            found_mods = self.mod_manager.scan_mods_directory()
            for mod_info in found_mods:
                mod_id = str(uuid.uuid4())
                self.config.add_mod(mod_id, mod_info)
                # 备份MOD文件
                self.config.backup_mod(mod_id, mod_info)
                
            self.load_mods()
            self.show_message(self.tr('成功'), f'已找到 {len(found_mods)} 个MOD文件')
            
    def edit_mod_info(self, item):
        """编辑MOD信息"""
        data = item.data(0, Qt.ItemDataRole.UserRole)
        if data['type'] != 'mod':
            return
            
        mod_id = data['id']
        mod_info = data['info']
        
        dialog = ModInfoDialog(self, mod_info)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            new_info = dialog.get_mod_info()
            
            # 更新预览图
            if new_info['preview_image'] != mod_info.get('preview_image', ''):
                self.mod_manager.set_preview_image(mod_id, new_info['preview_image'])
                
            # 更新其他信息
            mod_info.update(new_info)
            self.config.update_mod(mod_id, mod_info)
            
            # 更新显示
            self.load_mods()
            self.show_mod_info(mod_info)
            
    def rename_selected_category(self):
        """重命名选中分类"""
        item = self.tree.currentItem()
        if not item or item.data(0, Qt.ItemDataRole.UserRole)['type'] != 'category':
            QMessageBox.warning(self, self.tr('提示'), self.tr('请先选择要重命名的分类'))
            return
        self.rename_category(item)

    def delete_selected_category(self):
        """删除选中分类"""
        item = self.tree.currentItem()
        if not item:
            QMessageBox.warning(self, self.tr('提示'), self.tr('请先选择要删除的分类'))
            return
        
        # 获取数据类型
        data = item.data(0, Qt.ItemDataRole.UserRole)
        if not data or 'type' not in data:
            print("[错误] delete_selected_category: 选中项没有有效的数据")
            QMessageBox.warning(self, self.tr('提示'), self.tr('选中项无效'))
            return
            
        # 检查是否是分类或子分类
        if data['type'] == 'category':
            name = data['name']
            if name == '默认分类':
                QMessageBox.warning(self, self.tr('提示'), self.tr('默认分类无法删除'))
                return
                
            reply = self.msgbox_question_zh(self.tr('确认删除'), self.tr(f'确定要删除分类"{name}"吗？\n该分类下的MOD将移至默认分类。'))
            if reply == QMessageBox.StandardButton.Yes:
                print(f"[调试] delete_selected_category: 删除分类: {name}")
                # 更新所有MOD的分类信息
                mods = self.config.get_mods()
                for mod_id, mod_info in mods.items():
                    if mod_info.get('category') == name:
                        mod_info['category'] = '默认分类'
                        self.config.update_mod(mod_id, mod_info)
                        print(f"[调试] delete_selected_category: 将MOD {mod_id} 从分类 {name} 移至默认分类")
                    # 同时处理二级分类
                    elif '/' in mod_info.get('category', '') and mod_info.get('category', '').startswith(name + '/'):
                        mod_info['category'] = '默认分类'
                        self.config.update_mod(mod_id, mod_info)
                        print(f"[调试] delete_selected_category: 将子分类MOD {mod_id} 从分类 {mod_info.get('category')} 移至默认分类")
                
                # 从分类列表中删除所有相关分类
                categories = self.config.get_categories()
                updated_categories = []
                for cat in categories:
                    if cat != name and not (cat.startswith(name + '/')):
                        updated_categories.append(cat)
                
                self.config.set_categories(updated_categories)
                print(f"[调试] delete_selected_category: 更新分类列表，从 {len(categories)} 个分类减少到 {len(updated_categories)} 个分类")
                
                # 重新加载分类和MOD
                self.load_categories()
                self.load_mods()
                
                # 清空信息面板
                self.clear_info_panel()
                
                # 选中默认分类并刷新MOD列表
                self.select_default_category()
                
        elif data['type'] == 'subcategory':
            # 调用删除子分类的方法
            self.delete_subcategory(item)
        else:
            QMessageBox.warning(self, self.tr('提示'), self.tr('请先选择要删除的分类'))
            
    def select_default_category(self):
        """选中默认分类"""
        # 获取默认分类名称
        default_category_name = self.config.default_category_name
        
        # 查找默认分类
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            data = item.data(0, Qt.ItemDataRole.UserRole)
            if data['type'] == 'category' and data['name'] == default_category_name:
                self.tree.setCurrentItem(item)
                self.refresh_mod_list()
                return True
        return False

    def toggle_language(self):
        """切换中英文"""
        if getattr(self, '_lang', 'zh') == 'zh':
            self._lang = 'en'
            self.set_language('en')
        else:
            self._lang = 'zh'
            self.set_language('zh')

    def set_language(self, lang):
        """切换界面所有文字"""
        zh2en = {
            '爱酱剑星MOD管理器': 'StellarBlade MOD Manager',
            '剑星MOD管理器': 'StellarBlade MOD Manager',
            '轻松管理你的游戏MOD': 'Easily Manage Your Game MODs',
            'MOD分类': 'MOD Categories',
            '新增': 'Add',
            '重命名': 'Rename',
            '删除': 'Delete',
            '搜索:': 'Search:',
            '输入MOD名称或描述...': 'Enter MOD name or description...',
            '导入MOD': 'Import MOD',
            '启用MOD': 'Enable MOD',
            '禁用MOD': 'Disable MOD',
            '删除MOD': 'Delete MOD',
            '自定义名称': 'Custom Name',
            '修改预览图': 'Change Preview',
            '修改预览图（建议比例1:1或16:9）': 'Change Preview (1:1 or 16:9 recommended)',
            '修改名称': 'Rename',
            '设置': 'Settings',
            '语言切换': 'Switch Language',
            '切换MOD路径': 'Change MOD Path',
            '设置游戏路径': 'Set Game Path',
            '设置备份路径': 'Set Backup Path',
            '默认分类': 'Default',
            '提示': 'Tip',
            '请先选择要重命名的分类': 'Please select a category to rename',
            '请先选择要删除的分类': 'Please select a category to delete',
            '请先选择一个MOD，然后再修改预览图': 'Please select a MOD first, then change the preview image',
            '默认分类无法删除': 'Default category cannot be deleted',
            '确认删除': 'Confirm Delete',
            '确定要删除分类': 'Are you sure to delete category',
            '该分类下的MOD将一并删除。': 'All MODs under this category will be deleted.',
            '确定': 'OK',
            '取消': 'Cancel',
            '成功': 'Success',
            '错误': 'Error',
            '导入MOD失败': 'Import MOD Failed',
            'MOD导入并已启用！': 'MOD imported and enabled!',
            '导入预览图': 'Import Preview',
            'MOD导入成功，是否需要导入预览图？': 'MOD imported successfully, import preview image?',
            '选择MOD文件': 'Select MOD File',
            '压缩文件 (*.zip *.rar *.7z)': 'Archive Files (*.zip *.rar *.7z)',
            '选择预览图': 'Select Preview Image',
            '图片文件 (*.png *.jpg *.jpeg)': 'Image Files (*.png *.jpg *.jpeg)',
            '使用说明': 'Help',
            '关于爱酱剑星MOD管理器': 'About MOD Manager',
            '备份目录已修改': 'Backup path updated',
            '选择备份目录（用于存放MOD压缩包和解压文件）': 'Select backup directory (for MOD archives)',
            '已找到': 'Found',
            '个MOD文件': 'MOD files',
            '选择MOD文件夹': 'Select MOD Folder',
            '请输入新的MOD名称：': 'Enter new MOD name:',
            '修改MOD名称': 'Rename MOD',
            '选择要重命名的分类': 'Select category to rename',
            '选择要删除的分类': 'Select category to delete',
            '默认分类无法删除': 'Default category cannot be deleted',
            '导入失败！': 'Import failed!',
            'MOD已删除': 'MOD deleted',
            '操作失败！': 'Operation failed!',
            'MOD列表已刷新': 'MOD list refreshed',
            'MOD目录:': 'MOD Path:',
            '已加载MOD:': 'Loaded MODs:',
            '选择MOD查看详细信息': 'Select a MOD to view details',
            '启动游戏': 'Launch Game',
            '所有': 'All',
            '已启用': 'Enabled',
            '已禁用': 'Disabled',
            '刷新': 'Refresh',
            '名称：': 'Name:',
            '大小：': 'Size:',
            '导入时间：': 'Import Date:',
            '状态：': 'Status:',
            '已启用': 'Enabled',
            '已禁用': 'Disabled',
            '分类：': 'Category:',
            '描述：': 'Description:',
            '预览：': 'Preview:',
            '文件：': 'Files:',
            '未命名MOD': 'Unnamed MOD',
            '是否设置游戏路径以便直接启动游戏？': 'Set game path to launch the game directly?',
            '游戏可执行文件名为SB-Win64-Shipping.exe': 'Game executable is SB-Win64-Shipping.exe',
            '是': 'Yes',
            '否': 'No',
            '如果对你有帮助，可以请我喝一杯咖啡~': 'If this helps you, you can buy me a coffee~',
            '请导入预览图\n(推荐使用1:1或16:9的图片)': 'Import preview image\n(1:1 or 16:9 ratio recommended)'
        }
        
        # 创建反向映射（英文->中文）
        en2zh = {v: k for k, v in zh2en.items()}
        
        # 保存当前语言设置
        self._lang = lang
        
        def tr(text):
            if lang == 'en':
                # 中文 -> 英文
                return zh2en.get(text, text)
            else:
                # 英文 -> 中文
                return en2zh.get(text, text)
            
        # 更新窗口标题
        self.setWindowTitle(tr('爱酱剑星MOD管理器'))
        
        # 更新顶部区域
        main_title = self.findChild(QLabel, "mainTitle")
        if main_title:
            main_title.setText(tr('爱酱剑星MOD管理器'))
        
        # 查找并更新副标题
        for label in self.findChildren(QLabel):
            if label.text() == '轻松管理你的游戏MOD' or label.text() == 'Easily Manage Your Game MODs':
                label.setText(tr('轻松管理你的游戏MOD'))
                break
        
        # 更新分类区域
        for label in self.findChildren(QLabel):
            if label.text() == 'MOD分类' or label.text() == 'MOD Categories':
                label.setText(tr('MOD分类'))
                break
                
        self.add_cat_btn.setText(tr('新增'))
        self.rename_cat_btn.setText(tr('重命名'))
        self.del_cat_btn.setText(tr('删除'))
        
        # 更新搜索框
        search_label = self.findChild(QLabel, 'searchLabel')
        if search_label:
            search_label.setText(tr('搜索:'))
        self.search_box.setPlaceholderText(tr('输入MOD名称或描述...'))
        
        # 更新按钮
        self.import_btn.setText(tr('导入MOD'))
        self.enable_btn.setText(tr('启用MOD') if not self.enable_btn.isChecked() else tr('禁用MOD'))
        self.delete_btn.setText(tr('删除MOD'))
        self.rename_mod_btn.setText(tr('修改名称'))
        self.change_preview_btn.setText(tr('修改预览图（建议比例1:1或16:9）'))
        self.launch_game_btn.setText(tr('启动游戏'))
        
        # 更新标签页
        self.all_tab.setText(tr('所有'))
        self.enabled_tab.setText(tr('已启用'))
        self.disabled_tab.setText(tr('已禁用'))
        
        # 更新刷新按钮（通过ID查找）
        refresh_btn = self.findChild(QPushButton, "refreshBtn")
        if refresh_btn:
            refresh_btn.setText(tr('刷新'))
        
        # 更新MOD信息面板标签
        for label in self.findChildren(QLabel):
            if label.text() == '名称：' or label.text() == 'Name:':
                label.setText(tr('名称：'))
            elif label.text() == '大小：' or label.text() == 'Size:':
                label.setText(tr('大小：'))
            elif label.text() == '导入时间：' or label.text() == 'Import Date:':
                label.setText(tr('导入时间：'))
            elif label.text() == '状态：' or label.text() == 'Status:':
                label.setText(tr('状态：'))
            elif label.text() == '分类：' or label.text() == 'Category:':
                label.setText(tr('分类：'))
            elif label.text() == '描述：' or label.text() == 'Description:':
                label.setText(tr('描述：'))
            elif label.text() == '预览：' or label.text() == 'Preview:':
                label.setText(tr('预览：'))
            elif label.text() == '文件：' or label.text() == 'Files:':
                label.setText(tr('文件：'))
            elif label.text() == '请导入预览图\n(推荐使用1:1或16:9的图片)' or label.text() == 'Import preview image\n(1:1 or 16:9 ratio recommended)':
                label.setText(tr('请导入预览图\n(推荐使用1:1或16:9的图片)'))
            elif label.text() == '选择MOD查看详细信息' or label.text() == 'Select a MOD to view details':
                label.setText(tr('选择MOD查看详细信息'))
        
        # 更新状态栏
        self.path_label.setText(tr('MOD目录:') + ' ' + self.config.get_mods_path())
        mods = self.config.get_mods()
        enabled_count = sum(1 for mod in mods.values() if mod.get('enabled', False))
        self.mod_count_label.setText(tr('已加载MOD:') + f' {enabled_count}/{len(mods)}')
        
        # 更新菜单项
        for action in self.findChildren(QAction):
            if action.text() == '设置' or action.text() == 'Settings':
                action.setText(tr('设置'))
            elif action.text() == '语言切换' or action.text() == 'Switch Language':
                action.setText(tr('语言切换'))
            elif action.text() == '切换MOD路径' or action.text() == 'Change MOD Path':
                action.setText(tr('切换MOD路径'))
            elif action.text() == '设置游戏路径' or action.text() == 'Set Game Path':
                action.setText(tr('设置游戏路径'))
            elif action.text() == '设置备份路径' or action.text() == 'Set Backup Path':
                action.setText(tr('设置备份路径'))
            elif action.text() == '使用说明' or action.text() == 'Help':
                action.setText(tr('使用说明'))
            elif action.text() == '关于爱酱剑星MOD管理器' or action.text() == 'About MOD Manager':
                action.setText(tr('关于爱酱剑星MOD管理器'))
        
        # 重新应用样式表以保持按钮样式
        self.load_style()
        
        # 确保C3区按钮保持正确的样式
        self.import_btn.setObjectName('primaryButton')
        self.delete_btn.setObjectName('dangerButton')
        self.rename_mod_btn.setObjectName('primaryButton')
        self.change_preview_btn.setObjectName('primaryButton')
        
        # 刷新MOD列表以应用新的语言设置
        self.refresh_mod_list()

    def expand_and_select_mod(self, mod_id):
        """展开分类并选中MOD"""
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            return
            
        # 获取MOD所在的分类
        category = mod_info.get('category', '默认分类')
        
        # 选中分类
        self.select_category_by_name(category)
        
        # 选中MOD
        self.select_mod_by_id(mod_id)
        
    def select_mod_by_id(self, mod_id):
        """在MOD列表中选中指定ID的MOD"""
        # 遍历MOD列表查找对应ID的项
        for i in range(self.mod_list.count()):
            item = self.mod_list.item(i)
            if item and item.data(Qt.UserRole) == mod_id:
                # 选中该项
                self.mod_list.setCurrentItem(item)
                # 确保该项可见
                self.mod_list.scrollToItem(item, QAbstractItemView.PositionAtCenter)
                # 显示MOD信息
                self.on_mod_list_clicked(item)
                return True
        return False
        
    def set_backup_directory(self):
        """设置备份目录"""
        # 获取当前备份目录或默认目录
        current_backup_path = self.config.get_backup_path()
        if not current_backup_path:
            current_backup_path = os.path.join(os.getcwd(), "modbackup")
            
        # 询问用户是否使用默认路径
        reply = self.msgbox_question_zh('备份目录', f'是否使用默认备份目录？\n{current_backup_path}')
        if reply == QMessageBox.StandardButton.Yes:
            # 确保目录存在
            backup_dir = Path(current_backup_path)
            if not backup_dir.exists():
                backup_dir.mkdir(parents=True, exist_ok=True)
            self.config.set_backup_path(str(backup_dir))
            self.show_message(self.tr('成功'), f'备份目录已设置为: {current_backup_path}')
        else:
            # 用户选择自定义目录
            backup_path = QFileDialog.getExistingDirectory(self, self.tr('选择备份目录（用于存放MOD压缩包和解压文件）'))
            if backup_path:
                self.config.set_backup_path(backup_path)
                self.show_message(self.tr('成功'), f'备份目录已设置为: {backup_path}')

    def on_tab_clicked(self, tab_name):
        """处理标签页点击事件"""
        # 更新标签页状态
        self.all_tab.setChecked(tab_name == "all")
        self.enabled_tab.setChecked(tab_name == "enabled")
        self.disabled_tab.setChecked(tab_name == "disabled")
        
        # 保存当前激活的标签
        self.active_tab = tab_name
        
        # 刷新MOD列表
        self.refresh_mod_list()
        
    def refresh_mod_list(self, search_text=None, keep_selected=True):
        """刷新C区MOD列表"""
        print("[调试] refresh_mod_list: 开始刷新C区MOD列表")
        
        # 保存当前选中的MOD
        selected_mod_id = None
        if keep_selected and self.mod_list.currentItem():
            selected_mod_id = self.mod_list.currentItem().data(Qt.UserRole)
            print(f"[调试] refresh_mod_list: 当前选中的MOD: {selected_mod_id}")
        
        # 清空列表
        self.mod_list.clear()
        
        # 获取当前分类信息
        current_item = self.tree.currentItem()
        if not current_item:
            print("[调试] refresh_mod_list: 没有选中的分类")
            return
            
        data = current_item.data(0, Qt.ItemDataRole.UserRole)
        if not data:
            print("[调试] refresh_mod_list: 选中项没有关联数据")
            return
            
        cat_type = data.get('type', '')
        
        # 获取所有MOD
        mods = self.config.get_mods()
        print(f"[调试] refresh_mod_list: 当前MOD总数: {len(mods)}")
        
        # 记录所有MOD的分类情况，用于调试
        mod_categories = {}
        for mod_id, mod_info in mods.items():
            mod_categories[mod_id] = mod_info.get('category', self.config.default_category_name)
        print(f"[调试] refresh_mod_list: 当前所有MOD的分类：")
        for mod_id, category in mod_categories.items():
            print(f"  - {mod_id}: {category}")
        
        # 获取当前选中的分类
        selected_category = None
        if cat_type == 'category':
            selected_category = data['name']
            print(f"[调试] refresh_mod_list: 当前选中一级分类: {selected_category}")
        elif cat_type == 'subcategory':
            selected_category = data['full_path']
            print(f"[调试] refresh_mod_list: 当前选中二级分类: {selected_category}")
        elif cat_type == 'mod':
            # 如果选中的是MOD，则获取其所在的分类
            parent = current_item.parent()
            if parent:
                parent_data = parent.data(0, Qt.ItemDataRole.UserRole)
                if parent_data['type'] == 'category':
                    selected_category = parent_data['name']
                    print(f"[调试] refresh_mod_list: 当前选中MOD所在一级分类: {selected_category}")
                elif parent_data['type'] == 'subcategory':
                    selected_category = parent_data['full_path']
                    print(f"[调试] refresh_mod_list: 当前选中MOD所在二级分类: {selected_category}")
        
        # 如果没有选中分类，则返回
        if not selected_category:
            print("[调试] refresh_mod_list: 未能确定选中的分类")
            return
            
        # 获取当前选项卡
        current_tab = "all_tab"  # 默认显示全部
        if hasattr(self, 'active_tab'):
            if self.active_tab == "enabled":
                current_tab = "enabled_tab"
            elif self.active_tab == "disabled":
                current_tab = "disabled_tab"
        
        print(f"[调试] refresh_mod_list: 当前选项卡: {current_tab}")
        
        # 创建一个集合来跟踪已添加的MOD ID，防止重复添加
        added_mod_ids = set()
        
        # 检查是否处于编辑模式
        is_edit_mode = self.edit_mode_cb.isChecked()
        
        # 添加符合条件的MOD到列表
        for mod_id, mod_info in mods.items():
            # 跳过已添加的MOD
            if mod_id in added_mod_ids:
                continue
                
            # 检查分类
            mod_category = mod_info.get('category', self.config.default_category_name)
            if mod_category != selected_category:
                continue
                
            # 检查启用状态
            is_enabled = mod_info.get('enabled', False)
            if current_tab == 'enabled_tab' and not is_enabled:
                continue
            if current_tab == 'disabled_tab' and is_enabled:
                continue
                
            # 检查搜索文本
            if search_text:
                # 使用display_name和mod_name进行搜索
                display_name = mod_info.get('display_name', '')
                mod_name = mod_info.get('mod_name', '')
                folder_name = mod_info.get('folder_name', '')
                real_name = mod_info.get('real_name', '')
                name = mod_info.get('name', '')
                if (search_text.lower() not in display_name.lower() and 
                    search_text.lower() not in mod_name.lower() and 
                    search_text.lower() not in folder_name.lower() and
                    search_text.lower() not in real_name.lower() and
                    search_text.lower() not in name.lower()):
                    continue
            
            # 添加到列表
            item = QListWidgetItem()
            # 使用name作为显示名称（备注名称），如果没有则使用display_name或mod_name或mod_id
            display_name = mod_info.get('name', mod_info.get('display_name', mod_info.get('mod_name', mod_id)))
            item.setText(display_name)
            item.setData(Qt.UserRole, mod_id)
            item.setToolTip(f"{display_name}\n{mod_info.get('size','--')} MB")
            
            # 如果处于编辑模式，添加复选框
            if is_edit_mode:
                item.setFlags(item.flags() | Qt.ItemIsUserCheckable)
                item.setCheckState(Qt.Unchecked)
                # 设置复选框样式
                item.setFont(QFont("Microsoft YaHei UI", 9))
            
            print(f"[调试] refresh_mod_list: 添加MOD到列表: {mod_id}, 显示名称: {display_name}")
            
            # 设置图标
            if is_enabled:
                item.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
            else:
                item.setIcon(QIcon(resource_path('icons/关闭-关闭.svg')))
                
            self.mod_list.addItem(item)
            
            # 记录已添加的MOD ID
            added_mod_ids.add(mod_id)
        
        print(f"[调试] refresh_mod_list: 已添加 {len(added_mod_ids)} 个MOD到列表")
        
        # 恢复选中状态
        if selected_mod_id:
            found = False
            for i in range(self.mod_list.count()):
                item = self.mod_list.item(i)
                if item.data(Qt.UserRole) == selected_mod_id:
                    self.mod_list.setCurrentItem(item)
                    found = True
                    print(f"[调试] refresh_mod_list: 恢复选中MOD: {selected_mod_id}")
                    break
            
            if not found:
                print(f"[警告] refresh_mod_list: 未能找到之前选中的MOD: {selected_mod_id}")
        
        # 更新状态栏
        self.update_status_info()
        
    def on_mod_list_clicked(self, item):
        """处理MOD列表项点击事件"""
        if not item:
            self.clear_info_panel()
            return
            
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        
        if mod_info:
            print(f"[调试] on_mod_list_clicked: 显示MOD信息: {mod_id}")
            self.show_mod_info(mod_info)
        else:
            print(f"[警告] on_mod_list_clicked: 找不到MOD信息: {mod_id}")
            self.clear_info_panel()

    def rename_mod(self):
        item = self.mod_list.currentItem()
        if not item:
            return
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            print(f"[错误] rename_mod: 找不到MOD信息: {mod_id}")
            return
            
        print(f"[调试] rename_mod: 开始重命名MOD {mod_id}, 当前名称: {mod_info.get('name', mod_id)}")
        
        new_name, ok = self.input_dialog(self.tr('修改MOD名称'), self.tr('请输入新的MOD名称：'), mod_info.get('name',''))
        if ok and new_name and new_name != mod_info.get('name',''):
            print(f"[调试] rename_mod: 用户输入新名称: {new_name}")
            
            # 保存原始名称，如果还没有保存过
            if not mod_info.get('real_name'):
                mod_info['real_name'] = mod_info.get('name', mod_id)
                print(f"[调试] rename_mod: 保存原始名称: {mod_info['real_name']}")
            
            # 保存预览图路径，以便后续检查
            old_preview_image = mod_info.get('preview_image', '')
            if old_preview_image:
                print(f"[调试] rename_mod: 记录原预览图路径: {old_preview_image}")
            
            # 备份当前MOD信息，以防更新MOD ID后找不到
            old_mod_info = mod_info.copy()
            
            # 更新显示名称
            mod_info['name'] = new_name
            print(f"[调试] rename_mod: 更新MOD名称: {mod_id} -> {new_name}")
            
            # 更新MOD信息（这里会处理MOD ID的变更）
            self.config.update_mod(mod_id, mod_info)
            
            # 重新获取MOD列表，因为MOD ID可能已经改变
            mods = self.config.get_mods()
            print(f"[调试] rename_mod: 更新后的MOD列表: {list(mods.keys())}")
            
            # 如果MOD ID更改了，需要使用新ID
            new_mod_id = new_name
            if new_mod_id not in mods:
                print(f"[警告] rename_mod: 新MOD ID {new_mod_id} 不在MOD列表中，尝试查找...")
                # 尝试通过原始名称找到MOD
                for mid, minfo in mods.items():
                    if minfo.get('real_name') == mod_info.get('real_name'):
                        new_mod_id = mid
                        print(f"[调试] rename_mod: 找到匹配的MOD ID: {new_mod_id}")
                        break
            
            # 获取更新后的MOD信息
            updated_mod_info = mods.get(new_mod_id)
            if updated_mod_info:
                # 检查预览图路径是否已更新
                new_preview_image = updated_mod_info.get('preview_image', '')
                if old_preview_image and old_preview_image != new_preview_image:
                    print(f"[调试] rename_mod: 预览图路径已更新: {old_preview_image} -> {new_preview_image}")
                
                # 检查新预览图路径是否存在
                if new_preview_image and not os.path.exists(new_preview_image):
                    print(f"[警告] rename_mod: 新预览图路径不存在: {new_preview_image}，尝试修复")
                    
                    # 获取备份路径
                    backup_path = self.config.get_backup_path()
                    if not backup_path:
                        backup_path = os.path.join(os.getcwd(), "modbackup")
                    backup_path = Path(backup_path)
                    
                    # 检查新MOD备份目录中的预览图
                    mod_backup_dir = backup_path / new_mod_id
                    if mod_backup_dir.exists():
                        preview_files = list(mod_backup_dir.glob("preview.*"))
                        if preview_files:
                            fixed_preview_image = str(preview_files[0])
                            print(f"[调试] rename_mod: 找到正确的预览图: {fixed_preview_image}")
                            updated_mod_info['preview_image'] = fixed_preview_image
                            self.config.update_mod(new_mod_id, updated_mod_info)
            else:
                print(f"[警告] rename_mod: 无法获取更新后的MOD信息: {new_mod_id}")
            
            # 刷新显示
            self.refresh_mod_list()
            
            # 选中刚刚重命名的MOD
            found = False
            for i in range(self.mod_list.count()):
                list_item = self.mod_list.item(i)
                if list_item.data(Qt.UserRole) == new_mod_id:
                    self.mod_list.setCurrentItem(list_item)
                    self.on_mod_list_clicked(list_item)
                    found = True
                    print(f"[调试] rename_mod: 已选中重命名后的MOD: {new_mod_id}")
                    break
            
            if not found:
                print(f"[警告] rename_mod: 无法在列表中找到重命名后的MOD: {new_mod_id}")
                # 尝试刷新整个MOD列表
                self.load_mods()
                self.refresh_mod_list()

    def change_mod_preview(self):
        item = self.mod_list.currentItem()
        if not item:
            return
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            print(f"[错误] change_mod_preview: 找不到MOD信息: {mod_id}")
            return
            
        print(f"[调试] change_mod_preview: 为MOD {mod_id} 添加预览图")
        
        file_path, _ = QFileDialog.getOpenFileName(self, self.tr('选择预览图'), '', '图片文件 (*.png *.jpg *.jpeg)')
        if file_path:
            try:
                print(f"[调试] change_mod_preview: 选择的图片路径: {file_path}")
                
                # 使用mod_manager的方法设置预览图，确保图片被备份
                result = self.mod_manager.set_preview_image(mod_id, file_path)
                if not result:
                    raise ValueError("设置预览图失败")
                
                # 重新获取更新后的mod_info
                mods = self.config.get_mods()
                mod_info = mods.get(mod_id, {})
                
                print(f"[调试] change_mod_preview: 更新后的MOD信息: {mod_info}")
                
                # 确保预览图路径存在
                preview_image = mod_info.get('preview_image', '')
                if preview_image and not os.path.exists(preview_image):
                    print(f"[警告] change_mod_preview: 预览图路径不存在: {preview_image}，尝试修复")
                    
                    # 获取备份路径
                    backup_path = Path(self.config.get_backup_path())
                    if not backup_path or not str(backup_path).strip():
                        backup_path = Path(os.getcwd()) / "modbackup"
                    
                    # 检查MOD备份目录
                    mod_backup_dir = backup_path / mod_id
                    if mod_backup_dir.exists():
                        preview_files = list(mod_backup_dir.glob("preview.*"))
                        if preview_files:
                            preview_image = str(preview_files[0])
                            print(f"[调试] change_mod_preview: 找到正确的预览图: {preview_image}")
                            mod_info['preview_image'] = preview_image
                            self.config.update_mod(mod_id, mod_info)
                
                # 刷新显示
                self.show_mod_info(mod_info)
                self.statusBar().showMessage('预览图已更新', 3000)
                
                # 确保备份目录中有预览图
                backup_path = Path(self.config.get_backup_path())
                if not backup_path or not str(backup_path).strip():
                    backup_path = Path(os.getcwd()) / "modbackup"
                
                mod_backup_dir = backup_path / mod_id
                if not mod_backup_dir.exists():
                    print(f"[调试] change_mod_preview: 创建MOD备份目录: {mod_backup_dir}")
                    mod_backup_dir.mkdir(parents=True, exist_ok=True)
                
                # 复制预览图到备份目录
                preview_path = mod_backup_dir / f"preview{Path(file_path).suffix}"
                if not preview_path.exists() or not preview_path.samefile(file_path):
                    print(f"[调试] change_mod_preview: 复制预览图到备份目录: {preview_path}")
                    shutil.copy2(file_path, preview_path)
                
                # 更新MOD信息中的预览图路径
                mod_info['preview_image'] = str(preview_path)
                self.config.update_mod(mod_id, mod_info)
                
                # 再次刷新显示，确保预览图正确显示
                self.refresh_mod_list(keep_selected=True)
                
            except Exception as e:
                print(f"[错误] change_mod_preview: 更新预览图失败: {e}")
                import traceback
                traceback.print_exc()
                self.statusBar().showMessage('更新预览图失败！', 3000)
                self.show_message(self.tr('错误'), f'更新预览图失败：{str(e)}')

    def show_about(self):
        # 创建自定义对话框
        about_dialog = QDialog(self)
        about_dialog.setObjectName("aboutDialog")  # 设置对象名称以便应用样式
        about_dialog.setWindowTitle(self.tr('关于爱酱剑星MOD管理器'))
        about_dialog.setMinimumWidth(400)
        
        # 主布局
        layout = QVBoxLayout(about_dialog)
        
        # 信息文本
        info_text = f"""
        <div style='text-align:center;'>
        <b>爱酱剑星MOD管理器</b> v1.6.3 (20250620)<br>
        本管理器完全免费<br>
        作者：爱酱<br>
        QQ群：<a href='https://qm.qq.com/q/Ej0DqPPa9i'>788566495</a> (<a href='https://qm.qq.com/q/2rU31GUAKE'>682707942</a>)<br>
        <span style='color:#bdbdbd'>欢迎加入QQ群获取最新MOD和反馈建议！</span>
        </div>
        """
        
        info_label = QLabel(info_text)
        info_label.setTextFormat(Qt.RichText)
        info_label.setOpenExternalLinks(True)
        info_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(info_label)
        
        # 添加分隔线
        line = QFrame()
        line.setFrameShape(QFrame.HLine)
        line.setFrameShadow(QFrame.Sunken)
        layout.addWidget(line)
        
        # 添加捐赠图片
        donation_img = QLabel()
        pixmap = QPixmap(resource_path('捐赠.png'))
        if not pixmap.isNull():
            donation_img.setPixmap(pixmap.scaled(
                200, 200, Qt.AspectRatioMode.KeepAspectRatio,
                Qt.TransformationMode.SmoothTransformation
            ))
            donation_img.setAlignment(Qt.AlignCenter)
            layout.addWidget(donation_img)
        
        # 添加捐赠文字
        donation_text = QLabel(self.tr('如果对你有帮助，可以请我喝一杯蜜雪冰城~\n\n捐赠感谢：\n胖虎、YUki\n春告鳥、蘭\n神秘不保底男\n文铭、阪、……、林墨\n爱酱游戏群全体群友'))
        donation_text.setAlignment(Qt.AlignCenter)
        layout.addWidget(donation_text)
        
        # 添加确定按钮
        button_box = QDialogButtonBox(QDialogButtonBox.Ok)
        button_box.accepted.connect(about_dialog.accept)
        button_box.button(QDialogButtonBox.Ok).setText(self.tr('确定'))
        layout.addWidget(button_box)
        
        # 显示对话框
        about_dialog.exec()

    def msgbox_question_zh(self, *args, **kwargs):
        from PySide6.QtWidgets import QMessageBox
        box = QMessageBox(self)
        box.setIcon(QMessageBox.Question)
        if len(args) >= 2:
            box.setWindowTitle(args[0])
            box.setText(f"<div style='text-align:center;width:300px'>{args[1]}</div>")
        if len(args) >= 3:
            box.setInformativeText(f"<div style='text-align:center;width:300px'>{args[2]}</div>")
        box.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
        box.button(QMessageBox.Yes).setText('是' if getattr(self, '_lang', 'zh') == 'zh' else 'Yes')
        box.button(QMessageBox.No).setText('否' if getattr(self, '_lang', 'zh') == 'zh' else 'No')
        box.setStyleSheet(box.styleSheet() + "\nQPushButton { qproperty-alignment: AlignCenter; }")
        return box.exec()

    def show_message(self, title, text, icon=QMessageBox.Information):
        from PySide6.QtWidgets import QDialog, QVBoxLayout, QLabel, QPushButton, QScrollArea
        from PySide6.QtCore import Qt
        
        # 创建自定义对话框，而不是使用QMessageBox
        dialog = QDialog(self)
        dialog.setWindowTitle(title)
        dialog.setMinimumWidth(500)
        dialog.setMinimumHeight(400)
        dialog.resize(550, 450)
        
        # 创建垂直布局
        layout = QVBoxLayout(dialog)
        
        # 创建滚动区域
        scroll_area = QScrollArea()
        scroll_area.setWidgetResizable(True)
        scroll_area.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        scroll_area.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        
        # 创建内容标签
        content = QLabel()
        content.setTextFormat(Qt.RichText)
        content.setText(text)
        content.setWordWrap(True)
        content.setAlignment(Qt.AlignLeft | Qt.AlignTop)
        content.setStyleSheet("padding: 10px;")
        
        # 将内容标签添加到滚动区域
        scroll_area.setWidget(content)
        
        # 添加滚动区域到布局
        layout.addWidget(scroll_area)
        
        # 创建确定按钮
        ok_button = QPushButton('确定' if getattr(self, '_lang', 'zh') == 'zh' else 'OK')
        ok_button.setMaximumWidth(100)
        ok_button.clicked.connect(dialog.accept)
        
        # 添加按钮到布局，并设置居中对齐
        button_layout = QVBoxLayout()
        button_layout.addWidget(ok_button, 0, Qt.AlignCenter)
        layout.addLayout(button_layout)
        
        # 显示对话框
        return dialog.exec()

    def on_tree_drop_event(self, event):
        """处理目录树的拖放事件"""
        # 检查是否是从MOD列表拖拽过来的
        if event.mimeData().hasFormat('application/x-qabstractitemmodeldatalist') or event.mimeData().hasText():
            # 处理从MOD列表拖拽过来的MOD
            drop_position = event.pos()
            target_item = self.tree.itemAt(drop_position)

            # 如果没有目标项或者目标项是根节点
            if not target_item:
                print("[调试] on_tree_drop_event: 没有有效的目标项，拒绝拖放操作")
                event.ignore()
                return
                
            # 获取目标分类
            target_data = target_item.data(0, Qt.ItemDataRole.UserRole)
            if not target_data:
                print("[调试] on_tree_drop_event: 目标项没有关联数据，拒绝拖放操作")
                event.ignore()
                return
                
            target_type = target_data['type']
            target_category = ""
            
            # 根据目标类型确定目标分类
            if target_type == 'category':
                target_category = target_data['name']
                print(f"[调试] on_tree_drop_event: 目标是一级分类: {target_category}")
            elif target_type == 'subcategory':
                target_category = target_data['full_path']
                print(f"[调试] on_tree_drop_event: 目标是二级分类: {target_category}")
            else:
                # 如果拖放到MOD上，使用其父分类
                parent_item = target_item.parent()
                if parent_item:
                    parent_data = parent_item.data(0, Qt.ItemDataRole.UserRole)
                    if parent_data['type'] == 'subcategory':
                        target_category = parent_data['full_path']
                        print(f"[调试] on_tree_drop_event: 目标是MOD，使用其父级二级分类: {target_category}")
                    else:
                        target_category = parent_item.text(0)
                        print(f"[调试] on_tree_drop_event: 目标是MOD，使用其父级一级分类: {target_category}")
                else:
                    print("[调试] on_tree_drop_event: 目标是MOD，但找不到其父分类，拒绝拖放操作")
                    event.ignore()
                    return
            
            # 获取拖拽的MOD ID列表
            mod_ids = []
            
            # 检查是否是批量拖拽（通过自定义拖拽）
            if event.mimeData().hasText():
                # 从MIME数据中获取MOD ID列表
                mod_ids_str = event.mimeData().text()
                mod_ids = mod_ids_str.split(',')
                print(f"[调试] on_tree_drop_event: 从自定义拖拽获取MOD ID列表: {mod_ids}")
            else:
                # 从标准拖拽获取选中的MOD
                selected_items = self.mod_list.selectedItems()
                if not selected_items:
                    print("[调试] on_tree_drop_event: 没有选中的MOD，拒绝拖放操作")
                    event.ignore()
                    return
                mod_ids = [item.data(Qt.UserRole) for item in selected_items]
                print(f"[调试] on_tree_drop_event: 从标准拖拽获取MOD ID列表: {mod_ids}")
            
            if not mod_ids:
                print("[调试] on_tree_drop_event: 没有有效的MOD ID，拒绝拖放操作")
                event.ignore()
                return
                
            mod_count = len(mod_ids)
            processed_count = 0
            
            print(f"[调试] on_tree_drop_event: 准备将 {mod_count} 个MOD移动到分类 {target_category}")
            
            # 批量更新MOD分类
            for mod_id in mod_ids:
                if self.config.set_mod_category(mod_id, target_category):
                    processed_count += 1
                    print(f"[调试] on_tree_drop_event: 成功将MOD {mod_id} 移动到分类 {target_category}")
                else:
                    print(f"[警告] on_tree_drop_event: 无法将MOD {mod_id} 移动到分类 {target_category}")
            
            # 刷新UI
            self.load_mods()  # 重新加载B区树
            
            # 切换到目标分类并选中
            self.select_category_by_name(target_category)
            
            # 刷新MOD列表并选中刚移动的MOD
            self.refresh_mod_list()
            
            # 如果只移动了一个MOD，则选中它
            if len(mod_ids) == 1:
                self.select_mod_by_id(mod_ids[0])
            
            self.statusBar().showMessage(f'已将 {processed_count}/{mod_count} 个MOD移动到 "{target_category}" 分类', 3000)
            
            event.accept()
            return
        
        # 否则处理目录树内部拖拽
        # 记录拖拽前的信息
        source_item = self.tree.currentItem()
        if not source_item:
            print("[调试] on_tree_drop_event: 没有选中的源项，拒绝拖拽")
            event.ignore()
            return
            
        source_data = source_item.data(0, Qt.ItemDataRole.UserRole)
        if not source_data:
            print("[调试] on_tree_drop_event: 源项没有关联数据，拒绝拖拽")
            event.ignore()
            return
            
        source_type = source_data.get('type')
        source_name = source_data.get('name', '')
        
        # 检查是否为默认分类，不允许拖拽默认分类
        if source_type == 'category' and source_name == '默认分类':
            print("[调试] on_tree_drop_event: 默认分类不允许拖拽")
            self.show_message('提示', '默认分类不允许拖拽')
            event.ignore()
            return
            
        print(f"[调试] on_tree_drop_event: 拖拽项: 类型={source_type}, 名称={source_name}")
        
        # 获取放置目标
        drop_position = event.pos()
        target_item = self.tree.itemAt(drop_position)
        if not target_item:
            print("[调试] on_tree_drop_event: 没有有效的目标项，拒绝拖放操作")
            event.ignore()
            return
            
        target_data = target_item.data(0, Qt.ItemDataRole.UserRole)
        if not target_data:
            print("[调试] on_tree_drop_event: 目标项没有关联数据，拒绝拖放操作")
            event.ignore()
            return
            
        target_type = target_data.get('type')
        target_name = target_data.get('name', '')
        
        # 保存所有当前分类和MOD的分类关系
        all_categories = []
        mod_categories = {}
        
        # 保存所有分类
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            cat_name = item.text(0)
            all_categories.append(cat_name)
            
            # 收集该一级分类下的所有二级分类
            for j in range(item.childCount()):
                child = item.child(j)
                if child.data(0, Qt.ItemDataRole.UserRole)['type'] == 'subcategory':
                    sub_cat_name = child.data(0, Qt.ItemDataRole.UserRole)['name']
                    full_path = f"{cat_name}/{sub_cat_name}"
                    all_categories.append(full_path)
        
        # 保存所有MOD的分类关系
        mods = self.config.get_mods()
        for mod_id, mod_info in mods.items():
            mod_categories[mod_id] = mod_info.get('category', '默认分类')
        
        print(f"[调试] on_tree_drop_event: 拖拽前的分类列表: {all_categories}")
        print(f"[调试] on_tree_drop_event: 保存了 {len(mod_categories)} 个MOD的分类关系")
        
        # 执行标准的拖放操作
        QTreeWidget.dropEvent(self.tree, event)
        
        # 立即更新树项数据，确保所有项都有正确的数据
        self.update_tree_items_data()
        
        # 确保默认分类存在
        default_category_exists = False
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            if item.text(0) == '默认分类':
                default_category_exists = True
                break
                
        if not default_category_exists:
            print("[调试] on_tree_drop_event: 恢复默认分类")
            default_item = QTreeWidgetItem(['默认分类'])
            default_item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': '默认分类'})
            default_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
            self.tree.insertTopLevelItem(0, default_item)  # 插入到列表最前面
        
        # 保存分类顺序，这会触发按时间戳排序
        self.save_category_order()
        
        # 刷新分类树，确保分类按时间戳排序显示
        self.load_categories()
        
        # 如果是将分类拖入其他分类（成为子分类），需要特殊处理
        if source_type == 'category':
            # 检查源分类是否还在顶层
            found_in_top_level = False
            for i in range(self.tree.topLevelItemCount()):
                if self.tree.topLevelItem(i).text(0) == source_name:
                    found_in_top_level = True
                    break
            
            # 如果不在顶层，说明被拖入了其他分类，需要将其转为二级分类
            if not found_in_top_level:
                print(f"[调试] on_tree_drop_event: 分类 {source_name} 被拖入其他分类，转为二级分类")
                
                # 查找该分类现在的位置
                for i in range(self.tree.topLevelItemCount()):
                    parent_item = self.tree.topLevelItem(i)
                    parent_name = parent_item.text(0)
                    
                    # 检查是否是默认分类，不允许子分类下有默认分类
                    if source_name == '默认分类':
                        # 将默认分类还原为顶级分类
                        for j in range(parent_item.childCount()):
                            child = parent_item.child(j)
                            if child.text(0) == '默认分类':
                                parent_item.removeChild(child)
                                
                        default_item = QTreeWidgetItem(['默认分类'])
                        default_item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': '默认分类'})
                        default_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
                        self.tree.insertTopLevelItem(0, default_item)
                        print("[调试] on_tree_drop_event: 将默认分类恢复为顶级分类")
                        continue
                        
                    for j in range(parent_item.childCount()):
                        child = parent_item.child(j)
                        if child.text(0) == source_name and child.data(0, Qt.ItemDataRole.UserRole)['type'] == 'category':
                            # 找到了被拖入的分类，将其转为二级分类
                            full_path = f"{parent_name}/{source_name}"
                            
                            # 更新数据
                            child.setData(0, Qt.ItemDataRole.UserRole, {
                                'type': 'subcategory',
                                'name': source_name,
                                'full_path': full_path
                            })
                            
                            # 更新该分类下所有MOD的分类信息
                            updated_count = 0
                            for mod_id, category in mod_categories.items():
                                if category == source_name:
                                    mod_info = mods.get(mod_id)
                                    if mod_info:
                                        mod_info['category'] = full_path
                                        self.config.update_mod(mod_id, mod_info)
                                        updated_count += 1
                                        print(f"[调试] on_tree_drop_event: 更新MOD {mod_id} 的分类为 {full_path}")
                            
                            print(f"[调试] on_tree_drop_event: 已更新 {updated_count} 个MOD的分类信息")
                            break
        
        # 刷新MOD列表
        self.refresh_mod_list()
    
    def select_category_by_name(self, category_name):
        """根据分类名称选择分类"""
        if not category_name:
            return False
            
        # 检查是否是二级分类
        if '/' in category_name:
            parent_name, sub_name = category_name.split('/', 1)
            # 查找父分类
            for i in range(self.tree.topLevelItemCount()):
                parent_item = self.tree.topLevelItem(i)
                if parent_item.text(0) == parent_name:
                    # 展开父分类
                    self.tree.expandItem(parent_item)
                    # 查找子分类
                    for j in range(parent_item.childCount()):
                        child = parent_item.child(j)
                        if child.data(0, Qt.ItemDataRole.UserRole)['type'] == 'subcategory' and child.text(0) == sub_name:
                            self.tree.setCurrentItem(child)
                            print(f"[调试] select_category_by_name: 选中二级分类 {category_name}")
                            return True
        else:
            # 查找一级分类
            for i in range(self.tree.topLevelItemCount()):
                item = self.tree.topLevelItem(i)
                if item.text(0) == category_name:
                    self.tree.setCurrentItem(item)
                    print(f"[调试] select_category_by_name: 选中一级分类 {category_name}")
                    return True
                    
        print(f"[警告] select_category_by_name: 找不到分类 {category_name}")
        return False

    def update_tree_items_data(self):
        """更新目录树中所有项目的数据"""
        print("[调试] update_tree_items_data: 开始更新树形控件数据")
        
        # 保存当前分类列表，用于检测是否有分类被删除
        current_categories = set()
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            current_categories.add(item.text(0))
            
            # 检查二级分类
            for j in range(item.childCount()):
                child = item.child(j)
                child_data = child.data(0, Qt.ItemDataRole.UserRole)
                if child_data and child_data.get('type') == 'subcategory':
                    full_path = f"{item.text(0)}/{child.text(0)}"
                    current_categories.add(full_path)
        
        # 获取配置中的所有分类
        config_categories = set(self.config.get_categories())
        
        # 检查是否有分类在树中消失
        missing_categories = config_categories - current_categories
        if missing_categories:
            print(f"[警告] update_tree_items_data: 发现消失的分类: {missing_categories}")
            
            # 恢复消失的分类
            for cat in missing_categories:
                if '/' in cat:
                    # 二级分类
                    main_cat, sub_cat = cat.split('/', 1)
                    # 查找父分类
                    parent_item = None
                    for i in range(self.tree.topLevelItemCount()):
                        if self.tree.topLevelItem(i).text(0) == main_cat:
                            parent_item = self.tree.topLevelItem(i)
                            break
                    
                    # 如果父分类存在，添加子分类
                    if parent_item:
                        sub_item = QTreeWidgetItem([sub_cat])
                        sub_item.setData(0, Qt.ItemDataRole.UserRole, {
                            'type': 'subcategory', 
                            'name': sub_cat,
                            'full_path': cat
                        })
                        sub_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
                        parent_item.addChild(sub_item)
                        print(f"[调试] update_tree_items_data: 恢复二级分类 {cat}")
                    else:
                        # 如果父分类不存在，先创建父分类
                        parent_item = QTreeWidgetItem([main_cat])
                        parent_item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': main_cat})
                        parent_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
                        self.tree.addTopLevelItem(parent_item)
                        
                        # 然后添加子分类
                        sub_item = QTreeWidgetItem([sub_cat])
                        sub_item.setData(0, Qt.ItemDataRole.UserRole, {
                            'type': 'subcategory', 
                            'name': sub_cat,
                            'full_path': cat
                        })
                        sub_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
                        parent_item.addChild(sub_item)
                        print(f"[调试] update_tree_items_data: 恢复一级分类 {main_cat} 和二级分类 {cat}")
                else:
                    # 一级分类
                    item = QTreeWidgetItem([cat])
                    item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': cat})
                    item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
                    self.tree.addTopLevelItem(item)
                    print(f"[调试] update_tree_items_data: 恢复一级分类 {cat}")
        
        # 更新一级分类
        default_category_exists = False
        
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            cat_name = item.text(0)
            
            # 检查是否是默认分类
            if cat_name == '默认分类':
                default_category_exists = True
            
            # 确保一级分类有正确的数据
            item.setData(0, Qt.ItemDataRole.UserRole, {
                'type': 'category', 
                'name': cat_name
            })
            
            # 更新二级分类
            for j in range(item.childCount()):
                child = item.child(j)
                child_data = child.data(0, Qt.ItemDataRole.UserRole)
                child_type = child_data.get('type') if child_data else None
                
                # 检查是否是默认分类被拖为二级分类
                if child.text(0) == '默认分类':
                    # 将这个默认分类子项移除
                    item.removeChild(child)
                    j -= 1  # 调整循环计数
                    continue
                
                # 如果是二级分类或者被拖拽的一级分类
                if child_type in ['subcategory', 'category']:
                    sub_cat_name = child.text(0)
                    # 更新二级分类数据
                    full_path = f"{cat_name}/{sub_cat_name}"
                    child.setData(0, Qt.ItemDataRole.UserRole, {
                        'type': 'subcategory',
                        'name': sub_cat_name,
                        'full_path': full_path
                    })
                    
                    # 更新该二级分类下的所有MOD
                    for k in range(child.childCount()):
                        mod_item = child.child(k)
                        mod_data = mod_item.data(0, Qt.ItemDataRole.UserRole)
                        if mod_data and mod_data.get('type') == 'mod':
                            mod_id = mod_data.get('id')
                            mod_info = mod_data.get('info', {})
                            # 更新MOD的分类信息
                            mod_info['category'] = full_path
                            self.config.update_mod(mod_id, mod_info)
                            # 更新MOD项的数据
                            mod_item.setData(0, Qt.ItemDataRole.UserRole, {
                                'type': 'mod',
                                'id': mod_id,
                                'info': mod_info
                            })
                
                # 如果是MOD
                elif child_type == 'mod':
                    mod_id = child_data.get('id')
                    mod_info = child_data.get('info', {})
                    # 更新MOD的分类信息
                    mod_info['category'] = cat_name
                    self.config.update_mod(mod_id, mod_info)
                    # 更新MOD项的数据
                    child.setData(0, Qt.ItemDataRole.UserRole, {
                        'type': 'mod',
                        'id': mod_id,
                        'info': mod_info
                    })
        
        # 如果默认分类不存在，添加默认分类
        if not default_category_exists:
            print("[调试] update_tree_items_data: 添加默认分类")
            default_item = QTreeWidgetItem(['默认分类'])
            default_item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': '默认分类'})
            default_item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
            self.tree.insertTopLevelItem(0, default_item)
            
        print("[调试] update_tree_items_data: 更新完成")

    def save_category_order(self):
        """保存当前目录树结构到配置"""
        # 获取所有一级分类和二级分类
        categories = []
        all_categories = []
        
        # 收集所有一级分类
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            cat_name = item.text(0)
            categories.append(cat_name)
            all_categories.append(cat_name)
            
            # 收集该一级分类下的所有二级分类
            for j in range(item.childCount()):
                child = item.child(j)
                child_data = child.data(0, Qt.ItemDataRole.UserRole)
                if child_data and child_data.get('type') == 'subcategory':
                    sub_cat_name = child_data.get('name')
                    full_path = child_data.get('full_path')
                    if not full_path:  # 如果没有完整路径，则构造一个
                        full_path = f"{cat_name}/{sub_cat_name}"
                    all_categories.append(full_path)
        
        # 确保默认分类始终存在
        if '默认分类' not in all_categories:
            all_categories.insert(0, '默认分类')
            
        # 获取现有配置中的分类，保持原有的时间戳顺序
        existing_categories = self.config.get_categories()
        
        # 创建一个合并后的分类列表，保持时间戳顺序
        merged_categories = []
        
        # 先添加现有配置中的分类（保持顺序）
        for cat in existing_categories:
            if cat in all_categories:
                merged_categories.append(cat)
                
        # 再添加新的分类
        for cat in all_categories:
            if cat not in merged_categories:
                merged_categories.append(cat)
        
        # 确保默认分类在最前面
        if '默认分类' in merged_categories:
            merged_categories.remove('默认分类')
            merged_categories.insert(0, '默认分类')
        
        # 保存完整分类列表到配置文件
        print(f"[调试] save_category_order: 保存分类列表: {merged_categories}")
        self.config.set_categories(merged_categories)
        
        # 将当前分类列表保存到临时文件，确保刷新时能恢复
        temp_config_file = Path(self.config.config_dir) / "temp_categories.json"
        try:
            with open(temp_config_file, 'w', encoding='utf-8') as f:
                import json
                json.dump(merged_categories, f, ensure_ascii=False)
            print(f"[调试] save_category_order: 已保存临时分类列表到 {temp_config_file}")
        except Exception as e:
            print(f"[调试] save_category_order: 保存临时分类列表失败: {e}")
        
        # 更新所有MOD的分类信息，确保它们的分类路径正确
        self.update_mod_categories()

    def update_mod_categories(self):
        """更新所有MOD的分类信息，确保它们的分类路径正确"""
        # 收集所有有效的分类路径
        valid_categories = set()
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            cat_name = item.text(0)
            valid_categories.add(cat_name)
            
            # 添加二级分类
            for j in range(item.childCount()):
                child = item.child(j)
                if child.data(0, Qt.ItemDataRole.UserRole)['type'] == 'subcategory':
                    sub_cat_name = child.data(0, Qt.ItemDataRole.UserRole)['name']
                    full_path = f"{cat_name}/{sub_cat_name}"
                    valid_categories.add(full_path)
        
        # 确保默认分类始终存在
        valid_categories.add('默认分类')
        print(f"[调试] update_mod_categories: 有效分类列表: {valid_categories}")
        
        # 更新配置中的分类列表
        self.config.set_categories(list(valid_categories))
        
        # 委托给ConfigManager处理MOD分类更新
        if hasattr(self.config, 'update_mod_categories'):
            updated_count = self.config.update_mod_categories(valid_categories)
            print(f"[调试] update_mod_categories: 配置管理器更新了 {updated_count} 个MOD的分类信息")
        else:
            # 如果ConfigManager没有实现该方法，保留原来的逻辑
            mods = self.config.get_mods()
            updated_count = 0
            for mod_id, mod_info in mods.items():
                current_category = mod_info.get('category', '默认分类')
                if current_category not in valid_categories:
                    # 如果分类不存在，检查父级分类是否存在
                    parent_category = None
                    if '/' in current_category:
                        parent_name = current_category.split('/', 1)[0]
                        if parent_name in valid_categories:
                            parent_category = parent_name
                    
                    # 如果父级分类存在，将MOD移至父级分类；否则移至默认分类
                    old_category = current_category
                    if parent_category:
                        mod_info['category'] = parent_category
                        print(f"[调试] update_mod_categories: MOD {mod_id} 的分类从 {old_category} 更新为父级分类 {parent_category}")
                    else:
                        mod_info['category'] = '默认分类'
                        print(f"[调试] update_mod_categories: MOD {mod_id} 的分类从 {old_category} 更新为默认分类")
                    
                    self.config.update_mod(mod_id, mod_info)
                    updated_count += 1
            
            if updated_count > 0:
                print(f"[调试] update_mod_categories: 已更新 {updated_count} 个MOD的分类信息")
        
        return updated_count

    def show_help(self):
        # 创建帮助对话框
        help_dialog = QDialog(self)
        help_dialog.setObjectName("helpDialog")  # 设置对象名称以便应用样式
        help_dialog.setWindowTitle(self.tr('使用说明'))
        help_dialog.setMinimumSize(800, 600)
        
        # 主布局
        layout = QVBoxLayout(help_dialog)
        
        # 创建滚动区域
        scroll_area = QScrollArea()
        scroll_area.setWidgetResizable(True)
        
        # 创建内容窗口
        content_widget = QWidget()
        content_layout = QVBoxLayout(content_widget)
        
        # 添加帮助内容
        help_text = f"""
        <h1>爱酱剑星MOD管理器使用说明</h1>
        
        <h2>1. 基本功能</h2>
        <p>本软件用于管理《剑星》游戏的MOD文件，支持导入、启用、禁用和分类管理MOD。</p>
        
        <h2>2. 初始设置</h2>
        <p>首次运行时，需要设置游戏路径和MOD文件夹：</p>
        <ul>
            <li>游戏路径：指向游戏可执行文件(SB-Win64-Shipping.exe)</li>
            <li>MOD路径：通常是游戏目录下的SB/Content/Paks/~mods文件夹</li>
            <li>备份路径：用于存储MOD备份的文件夹</li>
        </ul>
        
        <h2>3. 导入MOD</h2>
        <p>点击顶部工具栏的"导入MOD"按钮，选择MOD压缩包(ZIP/RAR/7Z)进行导入。</p>
        <p>导入后，MOD将自动解压到游戏的MOD目录中，并在管理器中显示。</p>
        
        <h2>4. 管理MOD</h2>
        <p><b>启用/禁用MOD</b>：点击MOD列表中的开关按钮或右键菜单选择。</p>
        <p><b>删除MOD</b>：右键点击MOD，选择"删除"选项。</p>
        <p><b>查看MOD信息</b>：点击MOD列表中的MOD项目，右侧会显示详细信息。</p>
        
        <h2>5. 分类管理</h2>
        <p>左侧树形菜单可以创建和管理MOD分类：</p>
        <ul>
            <li>右键点击空白处可以添加新分类</li>
            <li>右键点击分类可以添加子分类、重命名或删除</li>
            <li>拖拽MOD到分类上可以将MOD移动到该分类</li>
        </ul>
        
        <h2>6. 搜索功能</h2>
        <p>使用顶部的搜索框可以快速查找MOD。</p>
        
        <h2>7. 启动游戏</h2>
        <p>设置好游戏路径后，可以直接通过管理器启动游戏。</p>
        
        <h2>8. 备份与恢复</h2>
        <p>MOD启用时会自动创建备份，禁用时会从备份恢复。</p>
        <p>备份文件存储在设置的备份目录中。</p>
        
        <h2>9. 快捷操作</h2>
        <ul>
            <li><b>刷新</b>：重新扫描MOD目录</li>
            <li><b>设置</b>：修改游戏路径、MOD路径和备份路径</li>
            <li><b>批量操作</b>：按住Ctrl或Shift可以选择多个MOD进行批量操作</li>
        </ul>
        
        <h2>10. 常见问题</h2>
        <p><b>Q: MOD导入后没有显示？</b></p>
        <p>A: 检查MOD压缩包格式是否正确，应包含.pak、.ucas和.utoc文件。</p>
        
        <p><b>Q: 启用MOD后游戏没有变化？</b></p>
        <p>A: 确认MOD路径设置正确，并且MOD文件格式符合游戏要求。</p>
        
        <p><b>Q: 如何更新MOD？</b></p>
        <p>A: 先删除旧版MOD，然后导入新版MOD。</p>
        """
        
        # 创建标签显示帮助内容
        help_label = QLabel(help_text)
        help_label.setTextFormat(Qt.RichText)
        help_label.setWordWrap(True)
        help_label.setOpenExternalLinks(True)
        help_label.setStyleSheet("padding: 20px;")
        
        content_layout.addWidget(help_label)
        scroll_area.setWidget(content_widget)
        layout.addWidget(scroll_area)
        
        # 添加确定按钮
        button_box = QDialogButtonBox(QDialogButtonBox.Ok)
        button_box.accepted.connect(help_dialog.accept)
        button_box.button(QDialogButtonBox.Ok).setText(self.tr('确定'))
        layout.addWidget(button_box)
        
        # 显示对话框
        help_dialog.exec()

    def input_dialog(self, title, label, text=''):
        from PySide6.QtWidgets import QInputDialog
        dlg = QInputDialog(self)
        dlg.setWindowTitle(title)
        dlg.setLabelText(f"<div style='text-align:center;width:300px'>{label}</div>")
        dlg.setTextValue(text)
        dlg.setOkButtonText('确定' if getattr(self, '_lang', 'zh') == 'zh' else 'OK')
        dlg.setCancelButtonText('取消' if getattr(self, '_lang', 'zh') == 'zh' else 'Cancel')
        dlg.setStyleSheet(dlg.styleSheet() + "\nQPushButton { qproperty-alignment: AlignCenter; }")
        ok = dlg.exec()
        return dlg.textValue(), ok == QInputDialog.Accepted 

    def launch_game(self):
        """启动游戏"""
        game_path = self.config.get_game_path()
        if not game_path:
            reply = self.msgbox_question_zh('游戏路径未设置', '游戏路径未设置，是否现在设置？')
            if reply == QMessageBox.StandardButton.Yes:
                self.set_game_path()
            return
            
        try:
            # 尝试启动游戏
            import subprocess
            import os
            
            game_exe = Path(game_path)
            if not game_exe.exists():
                self.show_message('错误', f'游戏可执行文件不存在: {game_path}')
                return
                
            # 使用subprocess启动游戏
            subprocess.Popen(str(game_exe), cwd=str(game_exe.parent))
            self.statusBar().showMessage('游戏已启动', 3000)
        except Exception as e:
            self.show_message('错误', f'启动游戏失败: {str(e)}')
            
    def set_game_path(self):
        """设置游戏路径，并自动设置MOD文件夹"""
        # 尝试自动查找游戏路径
        game_paths = self.find_game_executable()
        
        if game_paths:
            # 如果找到了游戏路径，询问用户是否使用
            paths_str = "\n".join([f"{i+1}. {path}" for i, path in enumerate(game_paths)])
            reply = self.msgbox_question_zh('找到游戏', f'找到以下可能的游戏路径，是否使用？\n{paths_str}')
            
            if reply == QMessageBox.StandardButton.Yes:
                if len(game_paths) == 1:
                    # 只有一个路径，直接使用
                    self.config.set_game_path(game_paths[0])
                    self.setup_mods_folder(game_paths[0])
                    self.show_message('成功', f'游戏路径已设置为: {game_paths[0]}')
                    self.update_launch_button()
                    return
                else:
                    # 多个路径，让用户选择
                    path, ok = QInputDialog.getItem(
                        self, '选择游戏路径', '请选择游戏路径:', 
                        game_paths, 0, False
                    )
                    if ok and path:
                        self.config.set_game_path(path)
                        self.setup_mods_folder(path)
                        self.show_message('成功', f'游戏路径已设置为: {path}')
                        self.update_launch_button()
                        return
        
        # 手动选择游戏路径
        file_dialog = QFileDialog()
        file_dialog.setFileMode(QFileDialog.ExistingFile)
        file_dialog.setNameFilter("游戏可执行文件 (*.exe)")
        file_dialog.setWindowTitle("选择游戏可执行文件")
        
        if file_dialog.exec():
            selected_files = file_dialog.selectedFiles()
            if selected_files:
                game_path = selected_files[0]
                self.config.set_game_path(game_path)
                self.setup_mods_folder(game_path)
                
                # 检查是否为首次设置SB-Win64-Shipping.exe
                if "SB-Win64-Shipping.exe" in game_path and not self.config.get('game_path_notified', False):
                    self.show_message('提示', f'已成功设置游戏启动路径为:\n{game_path}\n\n现在您可以使用"启动游戏"按钮直接启动游戏。')
                    self.config.set('game_path_notified', True)
                else:
                    self.show_message('成功', f'游戏路径已设置为: {game_path}')
                
                self.update_launch_button()
                
    def setup_mods_folder(self, game_exe_path):
        """根据游戏路径设置MOD文件夹"""
        try:
            # 获取游戏根目录
            game_exe = Path(game_exe_path)
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
                    self.config.set_mods_path(str(mods_path))
                    self.mod_manager.mods_path = mods_path
                    print(f"[调试] MOD文件夹已设置为: {mods_path}")
                    self.statusBar().showMessage(f'MOD文件夹已自动设置为: {mods_path}', 5000)
                    
                    # 检查是否为特定路径，并且是否为首次设置
                    expected_path = "steam\\steamapps\\common\\StellarBlade\\SB\\Content\\Paks\\~mods"
                    if expected_path.replace("\\", "/") in str(mods_path).replace("\\", "/") and not self.config.get('mods_path_notified', False):
                        self.show_message('提示', f'已成功设置MOD存放路径为:\n{mods_path}\n\n您可以将MOD文件放入此文件夹。')
                        self.config.set('mods_path_notified', True)
                    
                    # 刷新MOD列表
                    self.refresh_mods()
                    return True
            
            return False
        except Exception as e:
            print(f"[调试] 设置MOD文件夹失败: {e}")
            return False
        
    def find_game_executable(self):
        """查找游戏可执行文件"""
        mods_path = self.config.get_mods_path()
        if not mods_path:
            return None
            
        game_dir = Path(mods_path).parent
        # 使用MOD管理器提供的游戏可执行文件名称
        game_exe_name = self.mod_manager.get_game_exe_name()
        game_exe = game_dir / game_exe_name
        
        if game_exe.exists():
            return str(game_exe)
        else:
            print(f"[调试] find_game_executable: 游戏可执行文件不存在: {game_exe}")
            return None
            
    def update_launch_button(self):
        """更新启动游戏按钮状态"""
        game_path = self.config.get_game_path()
        if not game_path:
            # 尝试自动查找游戏可执行文件
            game_path = self.find_game_executable()
            if game_path:
                self.config.set_game_path(game_path)
                print(f"[调试] update_launch_button: 自动设置游戏路径: {game_path}")
                
        if game_path:
            self.launch_game_btn.setEnabled(True)
            self.launch_game_btn.setToolTip(f"启动游戏: {game_path}")
        else:
            self.launch_game_btn.setEnabled(False)
            self.launch_game_btn.setToolTip("游戏路径未设置，请先设置游戏路径")
            
    def mod_list_start_drag(self, actions):
        """自定义MOD列表的拖拽开始事件，支持复选框多选拖拽
        
        Args:
            actions: 支持的拖拽动作
        """
        print("[调试] mod_list_start_drag: 开始拖拽")
        
        # 获取所有选中的MOD项（包括复选框选中的）
        selected_items = self.get_selected_mod_items()
        if not selected_items:
            return
            
        # 创建拖拽对象
        drag = QDrag(self.mod_list)
        mime_data = QMimeData()
        
        # 将所有选中项的ID添加到MIME数据
        mod_ids = [item.data(Qt.UserRole) for item in selected_items]
        mod_ids_str = ",".join(mod_ids)
        mime_data.setText(mod_ids_str)
        
        # 同时设置标准MIME类型，以便兼容标准拖拽处理
        mime_data.setData('application/x-qabstractitemmodeldatalist', QByteArray())
        
        # 设置拖拽数据
        drag.setMimeData(mime_data)
        
        # 设置拖拽时的图标
        pixmap = QPixmap(32, 32)
        pixmap.fill(Qt.transparent)
        painter = QPainter(pixmap)
        painter.setFont(QFont("Arial", 10))
        painter.setPen(Qt.white)
        painter.drawText(0, 15, f"{len(selected_items)}个MOD")
        painter.end()
        drag.setPixmap(pixmap)
        
        # 执行拖拽操作
        result = drag.exec_(actions)
        print(f"[调试] mod_list_start_drag: 拖拽完成，结果: {result}, 拖拽的MOD: {mod_ids_str}")
            
    def get_selected_mod_items(self):
        """获取选中的MOD项目（包括复选框选中的项目）
        
        Returns:
            list: 选中的MOD项目列表
        """
        # 获取常规选中的项目
        selected_items = self.mod_list.selectedItems()
        
        # 如果处于编辑模式，检查复选框选中的项目
        if self.edit_mode_cb.isChecked():
            # 获取所有复选框选中的项目
            checked_items = []
            for i in range(self.mod_list.count()):
                item = self.mod_list.item(i)
                if item and item.checkState() == Qt.Checked:
                    checked_items.append(item)
            
            # 如果有复选框选中的项目，使用它们
            if checked_items:
                return checked_items
        
        return selected_items
            
    def open_toolbox(self):
        """打开收集工具箱网页"""
        try:
            url = "https://codepen.io/aigame/full/MYwXoGq"
            webbrowser.open(url)
            self.statusBar().showMessage('已打开收集工具箱', 3000)
        except Exception as e:
            self.show_message('错误', f'打开链接失败: {str(e)}')
            
    def on_preview_label_clicked(self, event):
        """处理预览图标签的点击事件"""
        # 检查是否有选中的MOD
        item = self.mod_list.currentItem()
        if item:
            # 调用修改预览图方法
            self.change_mod_preview()
        else:
            # 如果没有选中的MOD，显示提示信息
            self.show_message(self.tr('提示'), self.tr('请先选择一个MOD，然后再修改预览图'))
        
        # 调用父类的mousePressEvent以保持原有功能
        super(QLabel, self.preview_label).mousePressEvent(event)
        
    def toggle_edit_mode(self, state):
        """切换MOD列表的编辑模式"""
        is_checked = state == Qt.CheckState.Checked
        if is_checked:
            # 启用编辑模式
            self.mod_list.setSelectionMode(QAbstractItemView.ExtendedSelection)
            self.mod_list.setDragEnabled(True)
            self.mod_list.setDragDropMode(QAbstractItemView.DragOnly)
            self.mod_list.setDefaultDropAction(Qt.MoveAction)
            
            # 刷新MOD列表，添加复选框
            self.refresh_mod_list(keep_selected=True)
        else:
            # 禁用编辑模式
            self.mod_list.setSelectionMode(QAbstractItemView.SingleSelection)
            self.mod_list.setDragEnabled(False)
            self.mod_list.setDragDropMode(QAbstractItemView.NoDragDrop)
            
            # 刷新MOD列表，移除复选框
            self.refresh_mod_list(keep_selected=True)
            
        # 无论是启用还是禁用编辑模式，都确保拖拽功能正常
        self.mod_list.setDragEnabled(True)
            
        # 刷新状态栏提示
        if is_checked:
            self.statusBar().showMessage('已启用编辑模式：可多选MOD并拖拽到左侧分类（可直接拖拽MOD到分类树中）', 8000)
            # 显示提示对话框
            self.show_message('编辑模式已启用', '现在您可以:\n1. 多选MOD（使用复选框或按住Ctrl/Shift键）\n2. 将选中的MOD拖拽到左侧分类树\n3. 或使用右键菜单的"移动到分类"功能')
        else:
            self.statusBar().showMessage('已退出编辑模式', 3000)
        
    def show_mod_list_context_menu(self, position):
        """显示MOD列表右键菜单"""
        # 获取选中的项目
        selected_items = self.get_selected_mod_items()
        if not selected_items:
            return
            
        # 创建右键菜单
        context_menu = QMenu(self)
        
        # 添加菜单项
        rename_action = QAction(QIcon(resource_path('icons/12C编辑,重命名.svg')), "重命名", self)
        rename_action.triggered.connect(self.rename_mod)
        
        preview_action = QAction(QIcon(resource_path('icons/图片.svg')), "修改预览图", self)
        preview_action.triggered.connect(self.change_mod_preview)
        
        # 根据选中项目数量决定菜单项
        if len(selected_items) == 1:
            # 单选模式
            context_menu.addAction(rename_action)
            context_menu.addAction(preview_action)
            context_menu.addSeparator()
            
            # 判断当前MOD的启用状态
            mod_id = selected_items[0].data(Qt.UserRole)
            mods = self.config.get_mods()
            mod_info = mods.get(mod_id)
            
            if mod_info and mod_info.get('enabled', False):
                disable_action = QAction(QIcon(resource_path('icons/关闭-关闭.svg')), "禁用MOD", self)
                disable_action.triggered.connect(self.toggle_mod)
                context_menu.addAction(disable_action)
            else:
                enable_action = QAction(QIcon(resource_path('icons/开启-开启.svg')), "启用MOD", self)
                enable_action.triggered.connect(self.toggle_mod)
                context_menu.addAction(enable_action)
        else:
            # 多选模式
            enable_all_action = QAction(QIcon(resource_path('icons/开启-开启.svg')), f"启用所选 ({len(selected_items)}) 个MOD", self)
            enable_all_action.triggered.connect(lambda: self.toggle_selected_mods(True))
            
            disable_all_action = QAction(QIcon(resource_path('icons/关闭-关闭.svg')), f"禁用所选 ({len(selected_items)}) 个MOD", self)
            disable_all_action.triggered.connect(lambda: self.toggle_selected_mods(False))
            
            context_menu.addAction(enable_all_action)
            context_menu.addAction(disable_all_action)
        
        # 添加转移到分类菜单
        context_menu.addSeparator()
        move_menu = QMenu("移动到分类", self)
        move_menu.setIcon(QIcon(resource_path('icons/移动.svg')))
        
        # 添加所有可用分类
        categories = self.config.get_categories()
        root_categories = []  # 一级分类
        sub_categories = {}   # 二级分类 {父分类: [子分类]}
        
        for cat in categories:
            if '/' in cat:
                parent, sub = cat.split('/', 1)
                if parent not in sub_categories:
                    sub_categories[parent] = []
                sub_categories[parent].append(cat)
            else:
                root_categories.append(cat)
        
        # 添加一级分类
        for cat in root_categories:
            cat_action = QAction(cat, self)
            cat_action.triggered.connect(lambda checked=False, c=cat: self.move_selected_mods_to_category(c))
            move_menu.addAction(cat_action)
            
            # 如果有子分类，添加子菜单
            if cat in sub_categories:
                sub_menu = QMenu(cat, self)
                for sub_cat in sub_categories[cat]:
                    sub_action = QAction(sub_cat.split('/', 1)[1], self)
                    sub_action.triggered.connect(lambda checked=False, c=sub_cat: self.move_selected_mods_to_category(c))
                    sub_menu.addAction(sub_action)
                move_menu.addMenu(sub_menu)
        
        context_menu.addMenu(move_menu)
        
        # 添加删除选项
        context_menu.addSeparator()
        if len(selected_items) == 1:
            delete_action = QAction(QIcon(resource_path('icons/卸载.svg')), "删除MOD", self)
        else:
            delete_action = QAction(QIcon(resource_path('icons/卸载.svg')), f"删除所选 ({len(selected_items)}) 个MOD", self)
        delete_action.triggered.connect(self.delete_selected_mods)
        context_menu.addAction(delete_action)
        
        # 显示右键菜单
        context_menu.exec_(self.mod_list.mapToGlobal(position))
        
    def toggle_selected_mods(self, enable):
        """批量启用或禁用选中的MOD"""
        selected_items = self.get_selected_mod_items()
        if not selected_items:
            return
            
        try:
            mods = self.config.get_mods()
            mod_count = len(selected_items)
            processed = 0
            
            self.statusBar().showMessage(f"正在{'启用' if enable else '禁用'}选中的MOD...")
            
            for item in selected_items:
                mod_id = item.data(Qt.UserRole)
                mod_info = mods.get(mod_id)
                if not mod_info:
                    continue
                    
                # 如果状态已经符合目标状态，则跳过
                if mod_info.get('enabled', False) == enable:
                    continue
                    
                # 启用或禁用MOD
                try:
                    if enable:
                        self.mod_manager.enable_mod(mod_id)
                        mod_info['enabled'] = True
                    else:
                        self.mod_manager.disable_mod(mod_id)
                        mod_info['enabled'] = False
                    
                    self.config.update_mod(mod_id, mod_info)
                    processed += 1
                except Exception as e:
                    print(f"[警告] 处理MOD {mod_id} 失败: {str(e)}")
            
            # 刷新界面
            self.load_mods()
            self.refresh_mod_list(search_text=self.search_box.text())
            self.update_status_info()
            
            self.statusBar().showMessage(f"成功{'启用' if enable else '禁用'} {processed}/{mod_count} 个MOD", 3000)
        except Exception as e:
            self.statusBar().showMessage('操作失败！', 3000)
            self.show_message('错误', f'操作失败：{str(e)}')
    
    def delete_mod(self):
        """删除MOD（以C区选中为准）"""
        item = self.mod_list.currentItem()
        if not item:
            print("[调试] delete_mod: C区未选中任何项")
            return
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            print("[调试] delete_mod: 未找到mod_info")
            return
        mod_name = mod_info.get('name', mod_id)
        print(f"[调试] delete_mod: mod_id={mod_id}, name={mod_name}")
        reply = self.msgbox_question_zh('确认删除', f'确定要删除MOD"{mod_name}"吗？')
        if reply == QMessageBox.StandardButton.Yes:
            try:
                self.statusBar().showMessage('正在删除MOD...')
                print(f"[调试] delete_mod: 调用mod_manager.delete_mod")
                self.mod_manager.delete_mod(mod_id)
                print(f"[调试] delete_mod: 调用config.remove_mod")
                self.config.remove_mod(mod_id)
                
                # 刷新UI
                self.load_mods()
                self.refresh_mod_list(search_text=self.search_box.text())
                self.clear_info_panel()
                self.update_status_info()
                self.statusBar().showMessage('MOD已删除', 3000)
            except Exception as e:
                print(f"[调试] delete_mod: 删除失败: {e}")
                if 'MOD不存在' in str(e):
                    self.config.remove_mod(mod_id)
                    self.load_mods()
                    self.refresh_mod_list(search_text=self.search_box.text())
                    self.clear_info_panel()
                    self.update_status_info()
                    self.statusBar().showMessage('MOD已从列表移除', 3000)
                else:
                    self.statusBar().showMessage('删除失败！', 3000)
                    self.show_message(self.tr('错误'), f'删除MOD失败：{str(e)}')
    
    def delete_selected_mods(self):
        """批量删除选中的MOD"""
        selected_items = self.get_selected_mod_items()
        if not selected_items:
            return
            
        mod_count = len(selected_items)
        if mod_count == 1:
            self.delete_mod()
            return
            
        reply = self.msgbox_question_zh('确认删除', f'确定要删除所选的 {mod_count} 个MOD吗？')
        if reply != QMessageBox.StandardButton.Yes:
            return
            
        try:
            self.statusBar().showMessage('正在删除MOD...')
            processed = 0
            failed = 0
            
            for item in selected_items:
                mod_id = item.data(Qt.UserRole)
                try:
                    self.mod_manager.delete_mod(mod_id)
                    self.config.remove_mod(mod_id)
                    processed += 1
                except Exception as e:
                    print(f"[警告] 删除MOD {mod_id} 失败: {str(e)}")
                    # 如果是MOD不存在的错误，仍然从配置中移除
                    if 'MOD不存在' in str(e):
                        self.config.remove_mod(mod_id)
                        processed += 1
                    else:
                        failed += 1
            
            # 刷新界面
            self.load_mods()
            self.refresh_mod_list(search_text=self.search_box.text())
            self.clear_info_panel()
            self.update_status_info()
            
            if failed > 0:
                self.statusBar().showMessage(f'已删除 {processed} 个MOD，{failed} 个MOD删除失败', 3000)
            else:
                self.statusBar().showMessage(f'已成功删除 {processed} 个MOD', 3000)
        except Exception as e:
            self.statusBar().showMessage('批量删除失败！', 3000)
            self.show_message('错误', f'批量删除失败：{str(e)}')
    
    def move_selected_mods_to_category(self, category):
        """移动选中的MOD到指定分类"""
        selected_items = self.get_selected_mod_items()
        if not selected_items:
            return
            
        try:
            mod_count = len(selected_items)
            processed = 0
            moved_mod_ids = []
            
            self.statusBar().showMessage(f"正在移动MOD到分类 {category}...")
            
            for item in selected_items:
                mod_id = item.data(Qt.UserRole)
                if self.config.set_mod_category(mod_id, category):
                    processed += 1
                    moved_mod_ids.append(mod_id)
                    
            # 更新分类的内容
            self.update_mod_categories()
            
            # 切换到目标分类
            self.select_category_by_name(category)
            
            # 刷新MOD列表
            self.refresh_mod_list(search_text=self.search_box.text())
            
            # 如果只移动了一个MOD，则选中它
            if len(moved_mod_ids) == 1:
                self.select_mod_by_id(moved_mod_ids[0])
            
            self.statusBar().showMessage(f"已成功移动 {processed}/{mod_count} 个MOD到分类 {category}", 3000)
        except Exception as e:
            self.statusBar().showMessage('移动MOD失败！', 3000)
            self.show_message('错误', f'移动MOD失败：{str(e)}')
