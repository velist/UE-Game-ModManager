from PySide6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, 
    QTreeWidget, QTreeWidgetItem, QLabel, QPushButton,
    QFileDialog, QMessageBox, QInputDialog, QMenu,
    QFrame, QSplitter, QDialog, QLineEdit, QTextEdit,
    QDialogButtonBox, QFormLayout, QToolBar, QToolButton,
    QStatusBar, QProgressBar, QListWidget, QListWidgetItem,
    QScrollArea, QAbstractItemView, QCheckBox
)
from PySide6.QtCore import Qt, QSize, Signal, QThread, QMimeData, QPoint
from PySide6.QtGui import QAction, QIcon, QPixmap, QFont, QImage, QDrag
from utils.mod_manager import ModManager
from utils.config_manager import ConfigManager
import os
import uuid
from pathlib import Path
import sys
import json
import webbrowser  # 导入webbrowser模块用于打开URL

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
        self.result = None
        self.error = None
    def run(self):
        try:
            mod_info = self.mod_manager.import_mod(self.file_path)
            self.result = mod_info
            self.error = None
        except Exception as e:
            self.result = None
            self.error = str(e)
        self.finished.emit(self.result, self.error)

class MainWindow(QMainWindow):
    def __init__(self, config_manager):
        super().__init__()
        self.config = config_manager
        self.mod_manager = ModManager(config_manager)
        self.load_style()
        self.init_ui()
        
        # 更新启动游戏按钮状态
        self.update_launch_button()
        
        # 检查是否需要设置MOD文件夹
        if not self.config.get_mods_path():
            self.set_mods_directory()
            
        # 检查是否需要设置游戏路径
        if self.config.is_initialized() and not self.config.get_game_path():
            reply = self.msgbox_question_zh('设置游戏路径', '是否设置游戏路径以便直接启动游戏？\n游戏可执行文件名为SB.exe或BootstrapPackagedGame-Win64-Shipping.exe')
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
        self.del_cat_btn.clicked.connect(self.delete_selected_category)
        
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
        # 支持一级分类拖动排序
        self.tree.setDragDropMode(QTreeWidget.InternalMove)
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
        
        self.mod_list = QListWidget()
        self.mod_list.setObjectName('modList')
        self.mod_list.itemClicked.connect(self.on_mod_list_clicked)
        self.mod_list.setStyleSheet("QListWidget { background-color: #23243a; border: none; border-radius: 10px; padding: 5px; font-size: 13px; }")  # 减小字号
        
        # 添加右键菜单和拖拽支持
        self.mod_list.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.mod_list.customContextMenuRequested.connect(self.show_mod_list_context_menu)
        self.mod_list.setDragEnabled(True)  # 启用拖拽
        self.mod_list.setAcceptDrops(False)  # MOD列表不接受拖入
        
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
        
        about_label = QLabel("爱酱MOD管理器 v1.55 (20250615) | 作者：爱酱 | <a href='https://qm.qq.com/q/bShcpMFj1Y'>QQ群：682707942</a>")
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
        """刷新MOD列表，重新扫描目标文件夹"""
        print("[调试] refresh_mods: 开始刷新MOD列表")
        
        # 尝试从临时文件加载分类列表
        temp_config_file = Path(self.config.config_dir) / "temp_categories.json"
        original_categories = None
        if temp_config_file.exists():
            try:
                with open(temp_config_file, 'r', encoding='utf-8') as f:
                    original_categories = json.load(f)
                print(f"[调试] refresh_mods: 从临时文件加载分类列表: {original_categories}")
            except Exception as e:
                print(f"[调试] refresh_mods: 加载临时分类列表失败: {e}")
        
        # 如果临时文件不存在或加载失败，则从配置获取
        if not original_categories:
            original_categories = self.config.get_categories()
            print(f"[调试] refresh_mods: 从配置加载分类列表: {original_categories}")
        
        # 保存当前选中的项和展开状态
        current_item = self.tree.currentItem()
        current_path = None
        if current_item:
            data = current_item.data(0, Qt.ItemDataRole.UserRole)
            if data['type'] == 'category':
                current_path = data['name']
            elif data['type'] == 'subcategory':
                current_path = data['full_path']
            elif data['type'] == 'mod':
                parent = current_item.parent()
                if parent:
                    parent_data = parent.data(0, Qt.ItemDataRole.UserRole)
                    if parent_data['type'] == 'subcategory':
                        current_path = parent_data['full_path']
                    else:
                        current_path = parent.text(0)
                else:
                    current_path = '默认分类'
            print(f"[调试] refresh_mods: 保存当前选中路径: {current_path}")
        
        # 保存展开状态
        expanded_categories = []
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            if item.isExpanded():
                expanded_categories.append(item.text(0))
        print(f"[调试] refresh_mods: 保存展开状态: {expanded_categories}")
        
        # 保存现有MOD的信息，包括自定义名称
        existing_mods = {}
        for mod_id, mod_info in self.config.get_mods().items():
            existing_mods[mod_id] = {
                'name': mod_info.get('name', ''),
                'category': mod_info.get('category', '默认分类'),
                'preview_image': mod_info.get('preview_image', ''),
                'description': mod_info.get('description', ''),
                'real_name': mod_info.get('real_name', '')
            }
        print(f"[调试] refresh_mods: 保存了 {len(existing_mods)} 个MOD的自定义信息")
        
        # 执行扫描
        found_mods = self.mod_manager.scan_mods_directory()
        print(f"[调试] refresh_mods: 扫描到 {len(found_mods)} 个MOD")
        
        # 更新MOD信息
        for mod in found_mods:
            base_name = mod['name']
            mod_id = base_name
            
            # 如果MOD已存在，保留其分类和自定义名称等信息
            if mod_id in existing_mods:
                existing_info = existing_mods[mod_id]
                # 保留自定义名称
                mod['name'] = existing_info['name']
                # 保留分类
                mod['category'] = existing_info['category']
                # 保留预览图
                if existing_info['preview_image']:
                    mod['preview_image'] = existing_info['preview_image']
                # 保留描述
                if existing_info['description']:
                    mod['description'] = existing_info['description']
                # 保存原始名称，用于显示
                if not mod.get('real_name'):
                    mod['real_name'] = base_name
            # 否则，对于新MOD，如果没有分类，设置为默认分类
            elif 'category' not in mod or not mod['category']:
                mod['category'] = '默认分类'
                
            # 更新或添加MOD
            self.config.add_mod(mod_id, mod)
        
        # 恢复原始分类列表
        self.config.set_categories(original_categories)
        print(f"[调试] refresh_mods: 恢复分类列表: {original_categories}")
        
        # 重新加载UI
        self.clear_tree()  # 清空树形控件
        self.load_categories()  # 加载分类
        self.load_mods()  # 加载MOD
        
        # 恢复展开状态
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            if item.text(0) in expanded_categories:
                self.tree.expandItem(item)
        
        # 恢复选中状态
        if current_path:
            if '/' in current_path:
                # 二级分类
                main_cat, sub_cat = current_path.split('/', 1)
                for i in range(self.tree.topLevelItemCount()):
                    item = self.tree.topLevelItem(i)
                    if item.text(0) == main_cat:
                        self.tree.expandItem(item)
                        for j in range(item.childCount()):
                            child = item.child(j)
                            if child.data(0, Qt.ItemDataRole.UserRole)['type'] == 'subcategory' and \
                               child.data(0, Qt.ItemDataRole.UserRole)['name'] == sub_cat:
                                self.tree.setCurrentItem(child)
                                break
                        break
            else:
                # 一级分类
                for i in range(self.tree.topLevelItemCount()):
                    item = self.tree.topLevelItem(i)
                    if item.text(0) == current_path:
                        self.tree.setCurrentItem(item)
                        break
        
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
        
        # 添加一级分类到树形控件
        for category in primary_categories:
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
        
        print(f"[调试] load_categories: 加载完成，一级分类: {primary_categories}，二级分类: {sub_categories}")
        
    def load_mods(self):
        """加载分类，但不在左侧树中显示MOD"""
        self.tree.blockSignals(True)
        
        # 第一遍：创建所有二级分类
        sub_categories = {}
        mods = self.config.get_mods()
        
        for mod_id, mod_info in mods.items():
            category = mod_info.get('category', '默认分类')
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
            
            if data['name'] != '默认分类':
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
            
            # 添加到树形控件
            item = QTreeWidgetItem([name])
            item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': name})
            item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
            self.tree.addTopLevelItem(item)
            
            # 选中新添加的分类
            self.tree.setCurrentItem(item)
            
            # 保存分类顺序
            self.save_category_order()
            
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
            self.config.set_categories(updated_categories)
            
            print(f"[调试] rename_category: 更新了 {updated_count} 个MOD的分类")
            
            # 刷新UI
            self.load_categories()
            self.load_mods()
            
            # 确保选中重命名后的分类
            for i in range(self.tree.topLevelItemCount()):
                item = self.tree.topLevelItem(i)
                if item.data(0, Qt.ItemDataRole.UserRole)['name'] == new_name:
                    print(f"[调试] rename_category: 选中新分类 {new_name}")
                    self.tree.setCurrentItem(item)
                    break
                    
            # 强制刷新MOD列表
            self.refresh_mod_list()
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
        if name == '默认分类':
            return
            
        reply = self.msgbox_question_zh('确认删除', f'确定要删除分类"{name}"吗？\n该分类下的MOD将移至默认分类。')
        
        if reply == QMessageBox.StandardButton.Yes:
            # 更新所有MOD的分类信息
            mods = self.config.get_mods()
            for mod_id, mod_info in mods.items():
                if mod_info.get('category') == name:
                    mod_info['category'] = '默认分类'
                    self.config.update_mod(mod_id, mod_info)
                # 同时处理二级分类
                elif '/' in mod_info.get('category', '') and mod_info.get('category', '').startswith(name + '/'):
                    mod_info['category'] = '默认分类'
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
        """自动扫描MOD目录并更新MOD信息"""
        print("[调试] auto_scan_mods: 开始扫描mods目录")
        
        # 保存当前的分类列表
        original_categories = self.config.get_categories()
        print(f"[调试] auto_scan_mods: 原分类列表: {original_categories}")
        
        # 加载分类，但不清除现有分类
        if not self.tree.topLevelItemCount():
            self.load_categories()
            
        # 确保至少有默认分类
        if self.tree.topLevelItemCount() == 0:
            self.config.add_category('默认分类')
            self.load_categories()
            
        # 扫描MOD目录
        found_mods = self.mod_manager.scan_mods_directory()
        print(f"[调试] auto_scan_mods: 扫描到 {len(found_mods)} 个MOD")
        
        # 更新MOD信息
        for mod in found_mods:
            base_name = mod['name']
            mod_id = base_name
            
            # 如果MOD已存在，保留其分类
            if mod_id in self.config.get_mods():
                existing_mod = self.config.get_mods()[mod_id]
                mod['category'] = existing_mod.get('category', '默认分类')
            # 否则，对于新MOD，如果没有分类，设置为默认分类
            elif 'category' not in mod or not mod['category']:
                if self.tree.topLevelItemCount() > 0:
                    mod['category'] = self.tree.topLevelItem(0).text(0)
                else:
                    mod['category'] = '默认分类'
                    
            # 更新或添加MOD
            self.config.add_mod(mod_id, mod)
        
        # 恢复原始分类列表
        self.config.set_categories(original_categories)
        print(f"[调试] auto_scan_mods: 恢复分类列表: {original_categories}")
        
        # 收集所有有效的分类路径
        all_categories = set(original_categories)
        
        # 确保至少有默认分类
        if not all_categories:
            all_categories.add('默认分类')
        
        # 检查每个MOD的分类是否存在
        mods = self.config.get_mods()
        for mod_id, mod_info in mods.items():
            current_category = mod_info.get('category')
            if current_category not in all_categories:
                # 如果分类不存在，则设置为默认分类
                mod_info['category'] = '默认分类'
                self.config.update_mod(mod_id, mod_info)
        
        self.load_mods()
        if self.tree.topLevelItemCount() > 0:
            self.tree.setCurrentItem(self.tree.topLevelItem(0))
        self.refresh_mod_list()
        print("[调试] auto_scan_mods: 当前选中分类：", self.tree.currentItem().text(0) if self.tree.currentItem() else "无")
        print("[调试] auto_scan_mods: 当前MOD总数：", len(self.config.get_mods()))
        
    def import_mod(self):
        """导入MOD"""
        file_path, _ = QFileDialog.getOpenFileName(
            self,
            '选择MOD文件',
            '',
            '压缩文件 (*.zip *.rar *.7z)'
        )
        if file_path:
            self.statusBar().showMessage(self.tr('正在导入MOD...'))
            self.import_thread = ImportModThread(self.mod_manager, file_path)
            self.import_thread.finished.connect(self.on_import_mod_finished)
            self.import_thread.start()

    def on_import_mod_finished(self, mod_info, error):
        if error:
            self.statusBar().showMessage(self.tr('导入失败！'), 3000)
            self.show_message(self.tr('错误'), f'{self.tr("导入MOD失败")}：{error}')
            return
            
        if mod_info:
            # 处理导入的MOD信息
            mod_infos = []
            
            # 检查是否是MOD列表（嵌套压缩包的情况）
            if isinstance(mod_info, list):
                mod_infos = mod_info
            else:
                mod_infos = [mod_info]
                
            # 记录成功导入的MOD ID
            imported_mod_ids = []
            
            # 获取当前选中的分类
            cat_item = self.tree.currentItem()
            category = '默认分类'
            
            # 确定分类
            if cat_item:
                data = cat_item.data(0, Qt.ItemDataRole.UserRole)
                if data['type'] == 'category':
                    category = data['name']
                elif data['type'] == 'subcategory':
                    category = data['full_path']
                elif data['type'] == 'mod':
                    # 如果选中的是MOD，则使用其所属分类
                    parent = cat_item.parent()
                    if parent:
                        parent_data = parent.data(0, Qt.ItemDataRole.UserRole)
                        if parent_data['type'] == 'subcategory':
                            category = parent_data['full_path']
                        else:
                            category = parent.text(0)
                    else:
                        # 如果没有父项，使用第一个分类
                        categories = [self.tree.topLevelItem(i).text(0) for i in range(self.tree.topLevelItemCount())]
                        category = categories[0] if categories else '默认分类'
            else:
                # 如果没有选中项，使用第一个分类
                categories = [self.tree.topLevelItem(i).text(0) for i in range(self.tree.topLevelItemCount())]
                category = categories[0] if categories else '默认分类'
            
            # 处理每个MOD
            for mod in mod_infos:
                # 对于虚拟MOD（RAR文件），使用文件名作为MOD名称
                if mod.get('is_virtual', False):
                    main_stem = mod.get('name', '')
                # 对于标准MOD，使用第一个文件的名称
                elif mod.get('files', []):
                    main_stem = Path(mod['files'][0]).stem
                else:
                    main_stem = str(uuid.uuid4())  # 如果没有文件，使用随机ID
                    
                mod['name'] = main_stem
                mod_id = main_stem
                mod['enabled'] = True
                mod['category'] = category
            
            # 保存MOD信息
            self.config.add_mod(mod_id, mod)
            
            # 启用MOD
            enable_result = self.mod_manager.enable_mod(mod_id, parent_widget=self)
            if not enable_result:
                self.statusBar().showMessage(self.tr(f'MOD {mod_id} 导入成功但启用失败！'), 3000)
                self.show_message(self.tr('警告'), self.tr(f'MOD {mod_id} 导入成功但启用失败！请尝试手动启用。'))
            
            imported_mod_ids.append(mod_id)
            
            # 刷新UI
            self.load_categories()
            self.load_mods()
            
            # 选中MOD所在分类
            if '/' in category:
                # 二级分类
                main_cat, sub_cat = category.split('/', 1)
                # 找到一级分类
                for i in range(self.tree.topLevelItemCount()):
                    cat_item = self.tree.topLevelItem(i)
                    if cat_item.data(0, Qt.ItemDataRole.UserRole)['name'] == main_cat:
                        # 展开一级分类
                        self.tree.expandItem(cat_item)
                        # 找到二级分类
                        for j in range(cat_item.childCount()):
                            sub_item = cat_item.child(j)
                            if sub_item.data(0, Qt.ItemDataRole.UserRole)['type'] == 'subcategory' and \
                               sub_item.data(0, Qt.ItemDataRole.UserRole)['name'] == sub_cat:
                                self.tree.setCurrentItem(sub_item)
                                break
                        break
            else:
                # 一级分类
                for i in range(self.tree.topLevelItemCount()):
                    cat_item = self.tree.topLevelItem(i)
                    if cat_item.data(0, Qt.ItemDataRole.UserRole)['name'] == category:
                        self.tree.setCurrentItem(cat_item)
                        break
                else:
                    if self.tree.topLevelItemCount() > 0:
                        self.tree.setCurrentItem(self.tree.topLevelItem(0))
            
            # 刷新MOD列表
            self.refresh_mod_list()
            
            # 如果只导入了一个MOD，询问是否导入预览图
            if len(imported_mod_ids) == 1:
                mod_id = imported_mod_ids[0]
            reply = self.msgbox_question_zh(self.tr('导入预览图'), self.tr('MOD导入成功，是否需要导入预览图？'))
            if reply == QMessageBox.StandardButton.Yes:
                file_path, _ = QFileDialog.getOpenFileName(self, self.tr('选择预览图'), '', self.tr('图片文件 (*.png *.jpg *.jpeg)'))
                if file_path:
                    # 使用set_preview_image方法，确保预览图被正确备份
                    self.mod_manager.set_preview_image(mod_id, file_path)
                    # 更新MOD信息
                    mod_info = self.config.get_mods().get(mod_id, {})
                    self.refresh_mod_list()
                    # 选中新导入的MOD
                    for i in range(self.mod_list.count()):
                        item = self.mod_list.item(i)
                        if item.data(Qt.UserRole) == mod_id:
                            self.mod_list.setCurrentRow(i)
                            self.on_mod_list_clicked(item)
                            break
            
            # 选中第一个导入的MOD
            if imported_mod_ids:
                for i in range(self.mod_list.count()):
                    item = self.mod_list.item(i)
                    if item.data(Qt.UserRole) == imported_mod_ids[0]:
                        self.mod_list.setCurrentRow(i)
                        self.on_mod_list_clicked(item)
                        break
            
            # 显示成功消息
            if len(imported_mod_ids) == 1:
                self.statusBar().showMessage(self.tr('MOD导入并已启用！'), 3000)
                self.show_message(self.tr('成功'), self.tr('MOD导入并已启用！'))
            else:
                self.statusBar().showMessage(self.tr(f'成功导入 {len(imported_mod_ids)} 个MOD！'), 3000)
                self.show_message(self.tr('成功'), self.tr(f'成功导入 {len(imported_mod_ids)} 个MOD！'))
        else:
            self.statusBar().showMessage(self.tr('导入失败，未找到有效MOD文件！'), 3000)
            self.show_message(self.tr('错误'), self.tr('导入失败，未找到有效MOD文件！'))

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
            
            # 刷新UI
            self.load_mods()
            
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
        """处理项目点击事件"""
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
            
    def show_mod_info(self, mod_info):
        """显示MOD信息（名称上方，字段竖排）"""
        real_name = mod_info.get('real_name', '')
        name = mod_info.get('name', '未命名MOD')
        if real_name and real_name != name:
            show_name = f"{name}（{real_name}）"
        else:
            show_name = name
        self.mod_name_label.setText(show_name)
        self.mod_name_label.show()
        
        # 显示预览图
        if mod_info.get('preview_image'):
            pixmap = QPixmap(mod_info['preview_image'])
            if not pixmap.isNull():
                self.preview_label.setPixmap(pixmap.scaled(
                    250, 180, Qt.AspectRatioMode.KeepAspectRatio,
                    Qt.TransformationMode.SmoothTransformation
                ))
                # 清除文本和样式
                self.preview_label.setText("")
                self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px;')
        else:
            # 如果没有预览图，显示提示文字
            self.preview_label.clear()
            self.preview_label.setText("请导入预览图\n(推荐使用1:1或16:9的图片)")
            self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px; color: #bdbdbd; font-size: 12px;')
        
        # 确保鼠标指针始终为手型
        self.preview_label.setCursor(Qt.PointingHandCursor)
        
        # 清除旧的字段信息
        for i in reversed(range(self.mod_fields_layout.count())):
            w = self.mod_fields_layout.itemAt(i).widget()
            if w: w.setParent(None)
        
        # 添加分类信息（竖排显示）
        cat = mod_info.get('category', '默认分类')
        cat_label = QLabel(f"分类：{cat}")
        cat_label.setStyleSheet('background:#313244;color:#b4befe;padding:4px 8px;border-radius:6px;font-size:12px;')
        self.mod_fields_layout.addWidget(cat_label)
        
        # 添加状态信息
        enabled = mod_info.get('enabled', False)
        status_label = QLabel(f"状态：{'已启用' if enabled else '已禁用'}")
        status_label.setStyleSheet(f"background:{'#a6e3a1' if enabled else '#f38ba8'};color:#23243a;padding:4px 8px;border-radius:6px;font-size:12px;")
        self.mod_fields_layout.addWidget(status_label)
        
        # 添加导入日期
        import_date = mod_info.get('import_date', '--')
        # 格式化为YYYY-MM-DD
        if import_date and import_date != '--':
            import_date = str(import_date)[:10]
        date_label = QLabel(f"导入时间：{import_date}")
        date_label.setStyleSheet('background:#45475a;color:#fab387;padding:4px 8px;border-radius:6px;font-size:12px;')
        self.mod_fields_layout.addWidget(date_label)
        
        # 添加实际文件名
        real_file_name = mod_info.get('real_name', '') or mod_info.get('files', [''])[0] if mod_info.get('files') else ''
        if real_file_name:
            file_label = QLabel(f"实际文件名：{real_file_name}")
            file_label.setStyleSheet('background:#313244;color:#cdd6f4;padding:4px 8px;border-radius:6px;font-size:12px;')
            file_label.setWordWrap(True)
            self.mod_fields_layout.addWidget(file_label)
        
        self.mod_fields_frame.show()
        self.info_label.hide()
        
        # 更新按钮状态
        if enabled:
            self.enable_btn.setText('禁用MOD')
            self.enable_btn.setIcon(QIcon(resource_path('icons/禁用.svg')))
        else:
            self.enable_btn.setText('启用MOD')
            self.enable_btn.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
        self.enable_btn.setEnabled(True)
        self.delete_btn.setEnabled(True)
        self.rename_mod_btn.setEnabled(True)
        self.change_preview_btn.setEnabled(True)
        
    def clear_info_panel(self):
        self.mod_name_label.clear()
        self.mod_name_label.hide()
        
        # 清除预览图并显示提示文字
        self.preview_label.clear()
        self.preview_label.setText("请导入预览图\n(推荐使用1:1或16:9的图片)")
        self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px; color: #bdbdbd; font-size: 12px;')
        
        # 确保鼠标指针始终为手型
        self.preview_label.setCursor(Qt.PointingHandCursor)
        
        for i in reversed(range(self.mod_fields_layout.count())):
            w = self.mod_fields_layout.itemAt(i).widget()
            if w: w.setParent(None)
        self.mod_fields_frame.hide()
        self.info_label.setText('选择MOD查看详细信息')
        self.info_label.show()
        # 所有操作按钮禁用
        self.enable_btn.setEnabled(False)
        self.delete_btn.setEnabled(False)
        self.rename_mod_btn.setEnabled(False)
        self.change_preview_btn.setEnabled(False)
        
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
        if not item or item.data(0, Qt.ItemDataRole.UserRole)['type'] != 'category':
            QMessageBox.warning(self, self.tr('提示'), self.tr('请先选择要删除的分类'))
            return
        name = item.data(0, Qt.ItemDataRole.UserRole)['name']
        if name == '默认分类':
            QMessageBox.warning(self, self.tr('提示'), self.tr('默认分类无法删除'))
            return
        reply = self.msgbox_question_zh(self.tr('确认删除'), self.tr(f'确定要删除分类"{name}"吗？\n该分类下的MOD将一并删除。'))
        if reply == QMessageBox.StandardButton.Yes:
            self.config.delete_category(name)
            self.load_categories()
            self.load_mods()

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
            '游戏可执行文件名为SB.exe或BootstrapPackagedGame-Win64-Shipping.exe': 'Game executable is SB.exe or BootstrapPackagedGame-Win64-Shipping.exe',
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
        """展开并选中刚导入的MOD"""
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            return
        category = mod_info.get('category', '默认分类')
        for i in range(self.tree.topLevelItemCount()):
            cat_item = self.tree.topLevelItem(i)
            if cat_item.data(0, Qt.ItemDataRole.UserRole)['name'] == category:
                self.tree.expandItem(cat_item)
                for j in range(cat_item.childCount()):
                    mod_item = cat_item.child(j)
                    if mod_item.data(0, Qt.ItemDataRole.UserRole)['id'] == mod_id:
                        self.tree.setCurrentItem(mod_item)
                        return

    def set_backup_directory(self):
        """切换备份目录"""
        backup_path = QFileDialog.getExistingDirectory(self, self.tr('选择备份目录（用于存放MOD压缩包和解压文件）'))
        if backup_path:
            self.config.set_backup_path(backup_path)
            self.show_message(self.tr('成功'), self.tr('备份目录已修改')) 

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
        
        # 清空列表
        self.mod_list.clear()
        
        # 获取当前分类信息
        current_item = self.tree.currentItem()
        if not current_item:
            return
            
        data = current_item.data(0, Qt.ItemDataRole.UserRole)
        if not data:
            return
            
        cat_type = data.get('type', '')
        
        # 获取所有MOD
        mods = self.config.get_mods()
        
        # 记录所有MOD的分类情况，用于调试
        mod_categories = {}
        for mod_id, mod_info in mods.items():
            mod_categories[mod_id] = mod_info.get('category', '默认分类')
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
            return
            
        # 获取当前选项卡
        current_tab = "all_tab"  # 默认显示全部
        if hasattr(self, 'active_tab'):
            if self.active_tab == "enabled":
                current_tab = "enabled_tab"
            elif self.active_tab == "disabled":
                current_tab = "disabled_tab"
        
        # 创建一个集合来跟踪已添加的MOD ID，防止重复添加
        added_mod_ids = set()
        
        # 添加符合条件的MOD到列表
        for mod_id, mod_info in mods.items():
            # 跳过已添加的MOD
            if mod_id in added_mod_ids:
                continue
                
            # 检查分类
            mod_category = mod_info.get('category', '默认分类')
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
                mod_name = mod_info.get('name', mod_id)
                if search_text.lower() not in mod_name.lower():
                    continue
            
            # 添加到列表
            item = QListWidgetItem()
            item.setText(mod_info.get('name', mod_id))
            item.setData(Qt.UserRole, mod_id)
            
            # 设置图标
            if is_enabled:
                item.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
            else:
                item.setIcon(QIcon(resource_path('icons/关闭-关闭.svg')))
                
            self.mod_list.addItem(item)
            
            # 记录已添加的MOD ID
            added_mod_ids.add(mod_id)
        
        # 恢复选中状态
        if selected_mod_id:
            for i in range(self.mod_list.count()):
                item = self.mod_list.item(i)
                if item.data(Qt.UserRole) == selected_mod_id:
                    self.mod_list.setCurrentItem(item)
                    break
        
        # 更新状态栏
        self.update_status_info()
        
        # 统计当前分类下的MOD数量
        mod_count = 0
        for mod_id, mod_info in mods.items():
            if mod_info.get('category', '默认分类') == selected_category:
                mod_count += 1
        print(f"[调试] refresh_mod_list: 分类 {selected_category} 下有 {mod_count} 个MOD")

    def on_mod_list_clicked(self, item):
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if mod_info:
            self.show_mod_info(mod_info)

    def rename_mod(self):
        item = self.mod_list.currentItem()
        if not item:
            return
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            return
        new_name, ok = self.input_dialog(self.tr('修改MOD名称'), self.tr('请输入新的MOD名称：'), mod_info.get('name',''))
        if ok and new_name and new_name != mod_info.get('name',''):
            # 保存原始名称，如果还没有保存过
            if not mod_info.get('real_name'):
                mod_info['real_name'] = mod_info.get('name', mod_id)
            
            # 更新显示名称
            mod_info['name'] = new_name
            self.config.update_mod(mod_id, mod_info)
            
            # 刷新显示
            self.refresh_mod_list()
            
            # 选中刚刚重命名的MOD
            for i in range(self.mod_list.count()):
                list_item = self.mod_list.item(i)
                if list_item.data(Qt.UserRole) == mod_id:
                    self.mod_list.setCurrentItem(list_item)
                    self.on_mod_list_clicked(list_item)
                    break

    def change_mod_preview(self):
        item = self.mod_list.currentItem()
        if not item:
            return
        mod_id = item.data(Qt.UserRole)
        mods = self.config.get_mods()
        mod_info = mods.get(mod_id)
        if not mod_info:
            return
        file_path, _ = QFileDialog.getOpenFileName(self, self.tr('选择预览图'), '', '图片文件 (*.png *.jpg *.jpeg)')
        if file_path:
            try:
                # 使用mod_manager的方法设置预览图，确保图片被备份
                self.mod_manager.set_preview_image(mod_id, file_path)
                
                # 重新获取更新后的mod_info
                mod_info = self.config.get_mods().get(mod_id, {})
                
                # 刷新显示
                self.show_mod_info(mod_info)
                self.statusBar().showMessage('预览图已更新', 3000)
            except Exception as e:
                print(f"[调试] change_mod_preview: 更新预览图失败: {e}")
                self.statusBar().showMessage('更新预览图失败！', 3000)
                self.show_message(self.tr('错误'), f'更新预览图失败：{str(e)}')

    def show_about(self):
        # 创建自定义对话框
        about_dialog = QDialog(self)
        about_dialog.setWindowTitle(self.tr('关于爱酱剑星MOD管理器'))
        about_dialog.setMinimumWidth(400)
        
        # 主布局
        layout = QVBoxLayout(about_dialog)
        
        # 信息文本
        info_text = f"""
        <div style='text-align:center;'>
        <b>爱酱剑星MOD管理器</b> v1.55 (20250615)<br>
        本管理器完全免费<br>
        作者：爱酱<br>
        QQ群：<a href='https://qm.qq.com/q/bShcpMFj1Y'>682707942</a><br>
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
        donation_text = QLabel(self.tr('如果对你有帮助，可以请我喝一杯咖啡~'))
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
        # 记录拖拽前的信息
        source_item = self.tree.currentItem()
        source_data = None
        if source_item:
            source_data = source_item.data(0, Qt.ItemDataRole.UserRole)
            source_type = source_data['type']
            source_name = source_data['name']
            print(f"[调试] on_tree_drop_event: 拖拽项: 类型={source_type}, 名称={source_name}")
        
        # 保存所有当前分类
        all_categories = []
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
        
        print(f"[调试] on_tree_drop_event: 拖拽前的分类列表: {all_categories}")
        
        # 执行标准的拖放操作
        QTreeWidget.dropEvent(self.tree, event)
        
        # 确保所有拖拽后的项目都有正确的数据
        self.update_tree_items_data()
        
        # 如果是将分类拖入其他分类（成为子分类），需要特殊处理
        if source_data and source_data['type'] == 'category':
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
                    for j in range(parent_item.childCount()):
                        child = parent_item.child(j)
                        if child.text(0) == source_name and child.data(0, Qt.ItemDataRole.UserRole)['type'] == 'category':
                            # 找到了被拖入的分类，将其转为二级分类
                            parent_name = parent_item.text(0)
                            full_path = f"{parent_name}/{source_name}"
                            
                            # 更新数据
                            child.setData(0, Qt.ItemDataRole.UserRole, {
                                'type': 'subcategory',
                                'name': source_name,
                                'full_path': full_path
                            })
                            
                            # 更新该分类下所有MOD的分类信息
                            mods = self.config.get_mods()
                            for mod_id, mod_info in mods.items():
                                if mod_info.get('category') == source_name:
                                    mod_info['category'] = full_path
                                    self.config.update_mod(mod_id, mod_info)
                                    print(f"[调试] on_tree_drop_event: 更新MOD {mod_id} 的分类为 {full_path}")
                            
                            break
        
        # 保存分类顺序和结构
        self.save_category_order()
        
        # 刷新MOD列表以反映新的分类结构
        self.refresh_mod_list(keep_selected=True)
        
    def update_tree_items_data(self):
        """更新目录树中所有项目的数据"""
        print("[调试] update_tree_items_data: 开始更新树形控件数据")
        # 更新一级分类
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            cat_name = item.text(0)
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
        
        # 保存完整分类列表到配置文件
        print(f"[调试] save_category_order: 保存分类列表: {all_categories}")
        self.config.set_categories(all_categories)
        
        # 将当前分类列表保存到临时文件，确保刷新时能恢复
        temp_config_file = Path(self.config.config_dir) / "temp_categories.json"
        try:
            with open(temp_config_file, 'w', encoding='utf-8') as f:
                import json
                json.dump(all_categories, f, ensure_ascii=False)
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
        
        # 更新MOD的分类信息
        mods = self.config.get_mods()
        updated_count = 0
        for mod_id, mod_info in mods.items():
            category = mod_info.get('category', '默认分类')
            if category not in valid_categories:
                # 如果分类不存在，则设置为默认分类
                old_category = category
                mod_info['category'] = '默认分类'
                self.config.update_mod(mod_id, mod_info)
                updated_count += 1
                print(f"[调试] update_mod_categories: MOD {mod_id} 的分类从 {old_category} 更新为 默认分类")
        
        if updated_count > 0:
            print(f"[调试] update_mod_categories: 已更新 {updated_count} 个MOD的分类信息")

    def show_help(self):
        info = """
<style>
    .section { margin-top: 10px; margin-bottom: 5px; }
    .title { font-weight: bold; color: #cba6f7; }
    .item { margin-left: 15px; margin-top: 5px; }
    .note { color: #bdbdbd; font-style: italic; }
    ul { margin-top: 5px; margin-bottom: 5px; padding-left: 20px; }
    li { margin-bottom: 3px; }
</style>

<div class="title" style="font-size: 16px;">剑星MOD管理器 使用说明</div>

<div class="section">
    <div class="title">基础操作</div>
    <div class="item">1. <b>首次启动</b>
        <ul>
            <li>选择MOD文件夹和备份文件夹，两者缺一不可</li>
            <li>推荐MOD文件夹路径：游戏安装目录\\SB\\Content\\Paks\\~mods</li>
            <li>备份文件夹可以选择任意位置，建议选择空文件夹</li>
        </ul>
    </div>
    <div class="item">2. <b>导入MOD</b>
        <ul>
            <li>点击右侧"导入MOD"按钮</li>
            <li>选择MOD压缩包文件（支持zip、rar、7z格式）</li>
            <li>支持批量选择多个MOD文件同时导入</li>
            <li>支持嵌套压缩包（压缩包内包含其他压缩包）</li>
        </ul>
    </div>
    <div class="item">3. <b>管理MOD</b>
        <ul>
            <li>单击MOD可查看详细信息</li>
            <li>点击"启用/禁用MOD"按钮可切换MOD状态</li>
            <li>右键MOD可显示更多操作选项</li>
        </ul>
    </div>
</div>

<div class="section">
    <div class="title">高级功能</div>
    <div class="item">1. <b>分类管理</b>
        <ul>
            <li>左侧分类树支持右键菜单操作</li>
            <li>可新建、重命名、删除分类</li>
            <li>支持拖动排序和层级调整</li>
            <li>双击分类可展开/收起子分类</li>
        </ul>
    </div>
    <div class="item">2. <b>MOD编辑模式</b>
        <ul>
            <li>点击工具栏"编辑"按钮进入编辑模式</li>
            <li>支持多选MOD（按住Ctrl键点选或Shift键范围选择）</li>
            <li>可批量启用/禁用/删除/移动分类</li>
            <li>拖放功能支持在不同分类间移动MOD</li>
        </ul>
    </div>
    <div class="item">3. <b>预览图管理</b>
        <ul>
            <li>点击MOD信息卡片中的预览图区域可更换预览图</li>
            <li>支持jpg、png、webp等常见图片格式</li>
            <li>推荐使用1:1或16:9比例的图片</li>
        </ul>
    </div>
    <div class="item">4. <b>收藏工具箱</b>
        <ul>
            <li>点击主界面右下角"收藏工具箱"按钮</li>
            <li>可访问在线MOD收藏和管理工具</li>
        </ul>
    </div>
</div>

<div class="section">
    <div class="title">常见问题</div>
    <div class="item">1. <b>MOD无法导入</b>
        <ul>
            <li>检查压缩包格式是否支持</li>
            <li>确认压缩包内包含pak、utoc或ucas文件</li>
            <li>尝试解压后手动复制到MOD文件夹</li>
        </ul>
    </div>
    <div class="item">2. <b>多层嵌套MOD问题</b>
        <ul>
            <li>程序支持最多10层嵌套的压缩包</li>
            <li>每个包含MOD文件的嵌套压缩包会被视为独立MOD</li>
            <li>可单独启用/禁用任意嵌套MOD</li>
        </ul>
    </div>
</div>

<div class="section">
    <div class="title">快捷键</div>
    <ul>
        <li>Ctrl+F：搜索MOD</li>
        <li>Ctrl+E：进入/退出编辑模式</li>
        <li>Delete：删除选中的MOD</li>
        <li>F1：显示帮助</li>
        <li>F5：刷新MOD列表</li>
    </ul>
</div>

<div class="note" style="margin-top: 15px;">
    本工具仅供学习交流，严禁商用。
</div>
"""
        self.show_message(self.tr('使用说明'), info)

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
                    
                    # 刷新MOD列表
                    self.refresh_mods()
                    return True
            
            return False
        except Exception as e:
            print(f"[调试] 设置MOD文件夹失败: {e}")
            return False
        
    def find_game_executable(self):
        """尝试查找游戏可执行文件"""
        possible_paths = []
        
        # 常见的游戏安装路径
        steam_paths = [
            "C:/Program Files (x86)/Steam/steamapps/common/StellarBlade",
            "D:/Steam/steamapps/common/StellarBlade",
            "E:/Steam/steamapps/common/StellarBlade",
            "F:/Steam/steamapps/common/StellarBlade"
        ]
        
        # 游戏可执行文件名
        exe_names = ["SB.exe", "BootstrapPackagedGame-Win64-Shipping.exe"]
        
        # 检查每个可能的路径
        for base_path in steam_paths:
            for exe_name in exe_names:
                full_path = Path(base_path) / exe_name
                if full_path.exists():
                    possible_paths.append(str(full_path))
        
        return possible_paths
        
    def update_launch_button(self):
        """更新启动游戏按钮状态"""
        game_path = self.config.get_game_path()
        if game_path:
            self.launch_game_btn.setEnabled(True)
            self.launch_game_btn.setToolTip(f"启动游戏: {game_path}")
        else:
            self.launch_game_btn.setEnabled(False)
            self.launch_game_btn.setToolTip("游戏路径未设置，请先设置游戏路径")

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
            self.mod_list.setSelectionMode(QAbstractItemView.ExtendedSelection)
            self.mod_list.setDragDropMode(QAbstractItemView.DragOnly)
        else:
            self.mod_list.setSelectionMode(QAbstractItemView.SingleSelection)
            self.mod_list.setDragDropMode(QAbstractItemView.NoDragDrop)
            
        # 刷新状态栏提示
        if is_checked:
            self.statusBar().showMessage('已启用编辑模式：可多选MOD并拖拽到左侧分类', 5000)
        else:
            self.statusBar().showMessage('已退出编辑模式', 3000)
            
    def show_mod_list_context_menu(self, position):
        """显示MOD列表右键菜单"""
        # 获取选中的项目
        selected_items = self.mod_list.selectedItems()
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
        selected_items = self.mod_list.selectedItems()
        if not selected_items:
            return
        
        mods = self.config.get_mods()
        mod_count = len(selected_items)
        processed_count = 0
        
        for item in selected_items:
            mod_id = item.data(Qt.UserRole)
            mod_info = mods.get(mod_id)
            if not mod_info:
                continue
            
            try:
                # 更新配置中的状态
                if enable != mod_info.get('enabled', False):
                    self.mod_manager.toggle_mod(mod_id, enable)
                    processed_count += 1
            except Exception as e:
                self.show_message('错误', f'操作失败: {str(e)}')
                
        # 刷新列表
        self.refresh_mod_list(keep_selected=True)
        self.update_status_info()
        
        # 显示操作结果
        action = "启用" if enable else "禁用"
        self.statusBar().showMessage(f'已{action} {processed_count}/{mod_count} 个MOD', 3000)
        
    def move_selected_mods_to_category(self, category):
        """将选中的MOD移动到指定分类"""
        selected_items = self.mod_list.selectedItems()
        if not selected_items:
            return
        
        mods = self.config.get_mods()
        mod_count = len(selected_items)
        processed_count = 0
        
        for item in selected_items:
            mod_id = item.data(Qt.UserRole)
            mod_info = mods.get(mod_id)
            if not mod_info:
                continue
                
            # 更新MOD分类
            mod_info['category'] = category
            self.config.update_mod(mod_id, mod_info)
            processed_count += 1
            
        # 刷新列表
        self.load_mods()  # 重新加载B区树
        self.refresh_mod_list()  # 刷新C区列表
        
        self.statusBar().showMessage(f'已将 {processed_count}/{mod_count} 个MOD移动到 "{category}" 分类', 3000)
        
    def delete_selected_mods(self):
        """删除选中的MOD"""
        selected_items = self.mod_list.selectedItems()
        if not selected_items:
            return
            
        mod_count = len(selected_items)
        
        # 确认删除
        if mod_count == 1:
            msg = f'确定要删除MOD "{selected_items[0].text()}"吗？'
        else:
            msg = f'确定要删除选中的 {mod_count} 个MOD吗？'
            
        reply = self.msgbox_question_zh('确认删除', msg)
        if reply != QMessageBox.StandardButton.Yes:
            return
            
        # 删除MOD
        processed_count = 0
        failed_count = 0
        
        for item in selected_items:
            mod_id = item.data(Qt.UserRole)
            try:
                self.mod_manager.delete_mod(mod_id)
                self.config.remove_mod(mod_id)
                processed_count += 1
            except Exception as e:
                print(f"[调试] delete_selected_mods: 删除失败: {e}")
                failed_count += 1
                # 如果是"MOD不存在"的错误，仍然从配置中移除
                if 'MOD不存在' in str(e):
                    self.config.remove_mod(mod_id)
        
        # 刷新UI
        self.load_mods()
        self.refresh_mod_list()
        self.clear_info_panel()
        self.update_status_info()
        
        if failed_count > 0:
            self.show_message('删除结果', f'成功删除 {processed_count} 个MOD，{failed_count} 个MOD删除失败')
        else:
            self.statusBar().showMessage(f'已删除 {processed_count} 个MOD', 3000)

    def on_tree_drop_event(self, event):
        # 修改拖放事件处理方法
        # 检查是否是从MOD列表拖拽过来的
        if not event.mimeData().hasFormat('application/x-qabstractitemmodeldatalist'):
            # 目录树内部拖拽处理（保留原功能）
            QTreeWidget.dropEvent(self.tree, event)
            return

        # 处理从MOD列表拖拽过来的MOD
        drop_position = event.pos()
        target_item = self.tree.itemAt(drop_position)

        # 如果没有目标项或者目标项是根节点
        if not target_item:
            return
            
        # 获取目标分类
        target_data = target_item.data(0, Qt.ItemDataRole.UserRole)
        if not target_data:
            return
            
        target_type = target_data['type']
        target_category = ""
        
        # 根据目标类型确定目标分类
        if target_type == 'category':
            target_category = target_data['name']
        elif target_type == 'subcategory':
            target_category = target_data['full_path']
        else:
            # 如果拖放到MOD上，使用其父分类
            parent_item = target_item.parent()
            if parent_item:
                parent_data = parent_item.data(0, Qt.ItemDataRole.UserRole)
                if parent_data['type'] == 'subcategory':
                    target_category = parent_data['full_path']
                else:
                    target_category = parent_item.text(0)
            else:
                return
            
        # 获取选中的MOD
        selected_items = self.mod_list.selectedItems()
        if not selected_items:
            return
        
        mod_count = len(selected_items)
        processed_count = 0
        
        # 批量更新MOD分类
        for item in selected_items:
            mod_id = item.data(Qt.UserRole)
            mods = self.config.get_mods()
            mod_info = mods.get(mod_id)
            if mod_info:
                mod_info['category'] = target_category
                self.config.update_mod(mod_id, mod_info)
                processed_count += 1
                
        # 刷新UI
        self.load_mods()  # 重新加载B区树
        self.refresh_mod_list()  # 刷新C区列表
        
        self.statusBar().showMessage(f'已将 {processed_count}/{mod_count} 个MOD移动到 "{target_category}" 分类', 3000)
        
        event.accept()