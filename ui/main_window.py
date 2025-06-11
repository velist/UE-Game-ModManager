from PySide6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, 
    QTreeWidget, QTreeWidgetItem, QLabel, QPushButton,
    QFileDialog, QMessageBox, QInputDialog, QMenu,
    QFrame, QSplitter, QDialog, QLineEdit, QTextEdit,
    QDialogButtonBox, QFormLayout, QToolBar, QToolButton,
    QStatusBar, QProgressBar, QListWidget, QListWidgetItem,
    QScrollArea
)
from PySide6.QtCore import Qt, QSize, Signal
from PySide6.QtGui import QAction, QIcon, QPixmap, QFont, QImage
from utils.mod_manager import ModManager
from utils.config_manager import ConfigManager
import os
import uuid
from pathlib import Path
import sys

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

class MainWindow(QMainWindow):
    def __init__(self, config_manager):
        super().__init__()
        self.config = config_manager
        self.mod_manager = ModManager(config_manager)
        self.load_style()
        self.init_ui()
        
        # 检查是否需要设置MOD文件夹
        if not self.config.get_mods_path():
            self.set_mods_directory()
        
        # 自动扫描并加载MOD
        # self.auto_scan_mods()  # ← 这里注释掉或删除
        
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
        self.setWindowTitle('剑星MOD管理器')
        self.setMinimumSize(1200, 800)
        
        # 创建工具栏
        self.create_toolbar()
        
        # 创建中央部件
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        
        # 创建主布局
        main_layout = QVBoxLayout(central_widget)
        main_layout.setContentsMargins(20, 10, 20, 10)
        main_layout.setSpacing(10)
        
        # 左侧A+B区整体
        left_ab_widget = QWidget()
        left_ab_layout = QVBoxLayout(left_ab_widget)
        left_ab_layout.setContentsMargins(0, 0, 0, 0)
        left_ab_layout.setSpacing(10)
        # A区顶部信息区
        a_top_widget = QWidget()
        a_top_layout = QHBoxLayout(a_top_widget)
        a_top_layout.setSpacing(6)
        a_top_layout.setContentsMargins(0, 0, 0, 0)
        icon_label = QLabel()
        icon_label.setPixmap(QPixmap(resource_path('icons/your_icon.svg')).scaled(48, 48, Qt.KeepAspectRatio, Qt.SmoothTransformation))
        icon_label.setFixedSize(48, 48)
        a_title_box = QVBoxLayout()
        a_title_box.setSpacing(0)
        main_title = QLabel(self.tr('爱酱剑星MOD管理器'))
        main_title.setObjectName('titleLabel')
        main_title.setAlignment(Qt.AlignLeft | Qt.AlignTop)
        a_title_box.addWidget(main_title)
        sub_title = QLabel(self.tr('轻松管理你的游戏MOD'))
        sub_title.setObjectName('subTitleLabel')
        sub_title.setStyleSheet('font-size:15px;line-height:12pt;color:#bdbdbd;')
        a_title_box.addWidget(sub_title)
        a_title_box.addStretch()
        a_top_layout.addWidget(icon_label, alignment=Qt.AlignLeft | Qt.AlignTop)
        a_top_layout.addLayout(a_title_box)
        a_top_layout.addStretch()
        a_top_widget.setLayout(a_top_layout)
        left_ab_layout.addWidget(a_top_widget, alignment=Qt.AlignLeft | Qt.AlignTop)
        # 搜索框
        search_layout = QHBoxLayout()
        search_label = QLabel(self.tr('搜索:'))
        search_label.setObjectName('searchLabel')
        self.search_box = QLineEdit()
        self.search_box.setObjectName('searchBox')
        self.search_box.setPlaceholderText('输入MOD名称或描述...')
        self.search_box.textChanged.connect(self.on_search_text_changed)
        self.search_box.setFixedWidth(320)
        search_layout.addWidget(search_label)
        search_layout.addWidget(self.search_box)
        search_layout.addStretch()
        left_ab_layout.addLayout(search_layout)
        # B区目录树卡片
        left_card = QFrame()
        left_card.setObjectName('leftCard')
        left_card.setStyleSheet('QFrame#leftCard{background:#292a3e;border-radius:16px;}')
        left_layout = QVBoxLayout(left_card)
        left_layout.setContentsMargins(8, 8, 8, 8)
        btn_layout = QHBoxLayout()
        btn_layout.setSpacing(6)
        self.add_cat_btn = QPushButton(self.tr('新增'))
        self.add_cat_btn.setFixedHeight(26)
        self.add_cat_btn.setObjectName('primaryButton')
        self.add_cat_btn.setStyleSheet('font-size:12px;')
        self.add_cat_btn.setIcon(QIcon(resource_path('icons/12C编辑,重命名.svg')))
        self.add_cat_btn.setIconSize(QSize(18, 18))
        self.add_cat_btn.setLayoutDirection(Qt.LeftToRight)
        self.add_cat_btn.setStyleSheet(self.add_cat_btn.styleSheet() + 'text-align:center;')
        self.add_cat_btn.clicked.connect(self.add_category)
        btn_layout.addWidget(self.add_cat_btn)
        self.rename_cat_btn = QPushButton(self.tr('重命名'))
        self.rename_cat_btn.setFixedHeight(26)
        self.rename_cat_btn.setObjectName('primaryButton')
        self.rename_cat_btn.setStyleSheet('font-size:12px;')
        self.rename_cat_btn.setIcon(QIcon(resource_path('icons/12C编辑,重命名.svg')))
        self.rename_cat_btn.setIconSize(QSize(18, 18))
        self.rename_cat_btn.setLayoutDirection(Qt.LeftToRight)
        self.rename_cat_btn.setStyleSheet(self.rename_cat_btn.styleSheet() + 'text-align:center;')
        self.rename_cat_btn.clicked.connect(self.rename_selected_category)
        btn_layout.addWidget(self.rename_cat_btn)
        self.del_cat_btn = QPushButton(self.tr('删除'))
        self.del_cat_btn.setFixedHeight(26)
        self.del_cat_btn.setObjectName('dangerButton')
        self.del_cat_btn.setStyleSheet('font-size:12px;')
        self.del_cat_btn.setIcon(QIcon(resource_path('icons/卸载.svg')))
        self.del_cat_btn.setIconSize(QSize(18, 18))
        self.del_cat_btn.setLayoutDirection(Qt.LeftToRight)
        self.del_cat_btn.setStyleSheet(self.del_cat_btn.styleSheet() + 'text-align:center;')
        self.del_cat_btn.clicked.connect(self.delete_selected_category)
        btn_layout.addWidget(self.del_cat_btn)
        left_layout.addLayout(btn_layout)
        self.category_card = QFrame()
        self.category_card.setObjectName('categoryCard')
        self.category_card.setStyleSheet('QFrame#categoryCard{background:#23243a;border-radius:12px;margin-top:8px;}')
        category_layout = QVBoxLayout(self.category_card)
        category_layout.setContentsMargins(4, 4, 4, 4)
        self.tree = QTreeWidget()
        self.tree.setHeaderHidden(True)
        self.tree.setMinimumWidth(250)
        self.tree.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.tree.customContextMenuRequested.connect(self.show_tree_context_menu)
        # 支持一级分类拖动排序
        self.tree.setDragDropMode(QTreeWidget.InternalMove)
        self.tree.setDragEnabled(True)
        self.tree.setAcceptDrops(True)
        self.tree.setDropIndicatorShown(True)
        self.tree.dropEvent = self.on_tree_drop_event  # 覆盖dropEvent
        category_layout.addWidget(self.tree)
        tree_scroll = QScrollArea()
        tree_scroll.setWidgetResizable(True)
        tree_scroll.setFrameShape(QFrame.NoFrame)
        tree_scroll.setWidget(self.category_card)
        left_layout.addWidget(tree_scroll)
        left_card.setLayout(left_layout)
        left_ab_layout.addWidget(left_card)
        
        # 右侧C区整体
        right_c_widget = QWidget()
        right_c_layout = QVBoxLayout(right_c_widget)
        right_c_layout.setContentsMargins(0, 0, 0, 0)
        right_c_layout.setSpacing(8)
        # MOD信息卡片
        info_frame = QFrame()
        info_frame.setObjectName('infoFrame')
        info_layout = QVBoxLayout(info_frame)
        info_layout.setContentsMargins(0, 0, 0, 0)
        info_layout.setSpacing(6)
        self.mod_name_label = QLabel()
        self.mod_name_label.setObjectName('modNameLabel')
        self.mod_name_label.setAlignment(Qt.AlignHCenter | Qt.AlignVCenter)
        self.mod_name_label.setStyleSheet('font-size:22px;font-weight:bold;color:#cba6f7;padding:4px 0;')
        info_layout.addWidget(self.mod_name_label)
        self.preview_label = QLabel()
        self.preview_label.setMinimumSize(300, 200)
        self.preview_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview_label.setStyleSheet('border: 1px solid #313244; border-radius: 4px;')
        info_layout.addWidget(self.preview_label)
        self.mod_fields_frame = QFrame()
        self.mod_fields_frame.setObjectName('modFieldsFrame')
        self.mod_fields_layout = QHBoxLayout(self.mod_fields_frame)
        self.mod_fields_layout.setContentsMargins(0, 8, 0, 8)
        self.mod_fields_layout.setSpacing(24)
        info_layout.addWidget(self.mod_fields_frame)
        self.info_label = QLabel('选择MOD查看详细信息')
        self.info_label.setObjectName('info_label')
        self.info_label.setAlignment(Qt.AlignmentFlag.AlignTop | Qt.AlignmentFlag.AlignLeft)
        self.info_label.setWordWrap(True)
        info_layout.addWidget(self.info_label)
        right_c_layout.addWidget(info_frame)
        # MOD列表
        self.mod_list = QListWidget()
        self.mod_list.setObjectName('modList')
        self.mod_list.itemClicked.connect(self.on_mod_list_clicked)
        mod_list_scroll = QScrollArea()
        mod_list_scroll.setWidgetResizable(True)
        mod_list_scroll.setFrameShape(QFrame.NoFrame)
        mod_list_scroll.setWidget(self.mod_list)
        right_c_layout.addWidget(mod_list_scroll, stretch=1)
        # 操作按钮面板
        button_frame = QFrame()
        button_frame.setFrameStyle(QFrame.Shape.StyledPanel)
        button_layout = QHBoxLayout(button_frame)
        button_layout.setSpacing(20)
        self.import_btn = QPushButton('导入MOD')
        self.import_btn.setObjectName('primaryButton')
        self.import_btn.setIcon(QIcon(resource_path('icons/下载.svg')))
        self.import_btn.setIconSize(QSize(22, 22))
        self.import_btn.setLayoutDirection(Qt.LeftToRight)
        self.import_btn.setStyleSheet('text-align:center;')
        self.import_btn.clicked.connect(self.import_mod)
        button_layout.addWidget(self.import_btn)
        self.enable_btn = QPushButton('启用MOD')
        self.enable_btn.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
        self.enable_btn.setIconSize(QSize(22, 22))
        self.enable_btn.setLayoutDirection(Qt.LeftToRight)
        self.enable_btn.setStyleSheet('text-align:center;')
        self.enable_btn.clicked.connect(self.toggle_mod)
        self.enable_btn.setEnabled(False)
        button_layout.addWidget(self.enable_btn)
        self.delete_btn = QPushButton('删除MOD')
        self.delete_btn.setObjectName('dangerButton')
        self.delete_btn.setIcon(QIcon(resource_path('icons/卸载.svg')))
        self.delete_btn.setIconSize(QSize(22, 22))
        self.delete_btn.setLayoutDirection(Qt.LeftToRight)
        self.delete_btn.setStyleSheet('text-align:center;')
        self.delete_btn.clicked.connect(self.delete_mod)
        self.delete_btn.setEnabled(False)
        button_layout.addWidget(self.delete_btn)
        right_c_layout.addWidget(button_frame)
        # C区下方按钮
        c_btn_layout = QHBoxLayout()
        self.rename_mod_btn = QPushButton(self.tr('修改名称'))
        self.rename_mod_btn.setFixedHeight(32)
        self.rename_mod_btn.setObjectName('primaryButton')
        self.rename_mod_btn.setStyleSheet('font-size:14px;')
        self.rename_mod_btn.setIcon(QIcon(resource_path('icons/12C编辑,重命名.svg')))
        self.rename_mod_btn.setIconSize(QSize(20, 20))
        self.rename_mod_btn.setLayoutDirection(Qt.LeftToRight)
        self.rename_mod_btn.setStyleSheet(self.rename_mod_btn.styleSheet() + 'text-align:center;')
        self.rename_mod_btn.clicked.connect(self.rename_mod)
        c_btn_layout.addWidget(self.rename_mod_btn)
        self.change_preview_btn = QPushButton(self.tr('修改预览图（建议比例1:1或16:9）'))
        self.change_preview_btn.setFixedHeight(32)
        self.change_preview_btn.setObjectName('primaryButton')
        self.change_preview_btn.setStyleSheet('font-size:14px;')
        self.change_preview_btn.setIcon(QIcon(resource_path('icons/图片.svg')))
        self.change_preview_btn.setIconSize(QSize(20, 20))
        self.change_preview_btn.setLayoutDirection(Qt.LeftToRight)
        self.change_preview_btn.setStyleSheet(self.change_preview_btn.styleSheet() + 'text-align:center;')
        self.change_preview_btn.clicked.connect(self.change_mod_preview)
        c_btn_layout.addWidget(self.change_preview_btn)
        right_c_layout.addLayout(c_btn_layout)

        # 主分割器
        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.setHandleWidth(1)
        splitter.addWidget(left_ab_widget)
        splitter.addWidget(right_c_widget)
        splitter.setStretchFactor(0, 1)
        splitter.setStretchFactor(1, 2)
        main_layout.addWidget(splitter)
        
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
        
    def create_toolbar(self):
        """创建工具栏"""
        toolbar = QToolBar()
        toolbar.setMovable(False)
        toolbar.setIconSize(QSize(24, 24))
        self.addToolBar(toolbar)
        # 刷新按钮
        refresh_action = QAction(QIcon(resource_path('icons/刷新.svg')), self.tr('刷新'), self)
        refresh_action.triggered.connect(self.refresh_mods)
        toolbar.addAction(refresh_action)
        toolbar.addSeparator()
        # 设置按钮
        settings_action = QAction(QIcon(resource_path('icons/icon_设置.svg')), self.tr('设置'), self)
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
        about_action = QAction(self.tr('关于爱酱MOD管理器'), self)
        about_action.triggered.connect(self.show_about)
        settings_menu.addAction(about_action)
        # 新增：使用说明
        help_action = QAction(self.tr('使用说明'), self)
        help_action.triggered.connect(self.show_help)
        settings_menu.addAction(help_action)
        settings_action = QAction(QIcon(resource_path('icons/icon_设置.svg')), self.tr('设置'), self)
        settings_action.setMenu(settings_menu)
        toolbar.addAction(settings_action)
        
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
        
        about_label = QLabel("爱酱MOD管理器 v1.0 (20250601) | 作者：爱酱 | <a href='https://qm.qq.com/q/8vAq0MtyIU'>QQ群：788566495</a>")
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
        print("[调试] refresh_mods: 调用auto_scan_mods")
        self.auto_scan_mods()
        self.update_status_info()
        self.statusBar().showMessage(self.tr('MOD列表已刷新'), 3000)
        
    def show_settings(self):
        """显示设置对话框"""
        # TODO: 实现设置对话框
        pass
        
    def load_categories(self):
        """加载分类到目录树"""
        self.tree.clear()
        categories = self.config.get_categories()
        for category in categories:
            item = QTreeWidgetItem([category])
            item.setData(0, Qt.ItemDataRole.UserRole, {'type': 'category', 'name': category})
            item.setIcon(0, QIcon(resource_path('icons/文件夹.svg')))
            self.tree.addTopLevelItem(item)
            
    def load_mods(self):
        """加载MOD到目录树，只显示唯一同名MOD"""
        self.tree.blockSignals(True)
        for i in range(self.tree.topLevelItemCount()):
            category_item = self.tree.topLevelItem(i)
            category_item.takeChildren()
        mods = self.config.get_mods()
        shown_names = set()
        for mod_id, mod_info in mods.items():
            name = mod_info.get('name', '未命名MOD')
            if name in shown_names:
                continue
            shown_names.add(name)
            category = mod_info.get('category', '默认分类')
            category_item = self.find_category_item(category)
            if category_item:
                mod_item = QTreeWidgetItem([name])
                mod_item.setData(0, Qt.ItemDataRole.UserRole, {
                    'type': 'mod',
                    'id': mod_id,
                    'info': mod_info
                })
                mod_item.setIcon(0, QIcon(resource_path('icons/mod.svg')))
                category_item.addChild(mod_item)
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
                
            add_action = QAction('添加分类', self)
            add_action.triggered.connect(self.add_category)
            menu.addAction(add_action)
            
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
        name, ok = QInputDialog.getText(self, '添加分类', '请输入分类名称：')
        if ok and name:
            self.config.add_category(name)
            self.load_categories()
            self.load_mods()
            
    def rename_category(self, item):
        """重命名分类"""
        old_name = item.data(0, Qt.ItemDataRole.UserRole)['name']
        new_name, ok = QInputDialog.getText(
            self, '重命名分类', 
            '请输入新的分类名称：',
            text=old_name
        )
        
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
            
    def delete_category(self, item):
        """删除分类"""
        name = item.data(0, Qt.ItemDataRole.UserRole)['name']
        if name == '默认分类':
            return
            
        reply = self.msgbox_question_zh('确认删除', f'确定要删除分类"{name}"吗？\n该分类下的MOD将移至默认分类。')
        
        if reply == QMessageBox.StandardButton.Yes:
            self.config.delete_category(name)
            self.load_categories()
            self.load_mods()
            
    def auto_scan_mods(self):
        """自动扫描mods目录并写入config，刷新UI"""
        print("[调试] auto_scan_mods: 开始扫描mods目录")
        self.load_categories()  # 先加载分类，确保有分类可用
        if self.tree.topLevelItemCount() == 0:
            self.config.add_category('剑星')
            self.load_categories()
        found_mods = self.mod_manager.scan_mods_directory()
        print(f"[调试] auto_scan_mods: 扫描到 {len(found_mods)} 个MOD")
        for mod in found_mods:
            base_name = mod['name']
            mod_id = base_name
            # 只修正没有分类的MOD
            if 'category' not in mod or not mod['category']:
                if self.tree.topLevelItemCount() > 0:
                    mod['category'] = self.tree.topLevelItem(0).text(0)
                else:
                    mod['category'] = '默认分类'
            if mod_id not in self.config.get_mods():
                print(f"[调试] auto_scan_mods: 新增MOD {mod_id} 分类 {mod['category']}")
                self.config.add_mod(mod_id, mod)
        # 修正所有MOD的分类
        categories = [self.tree.topLevelItem(i).text(0) for i in range(self.tree.topLevelItemCount())]
        mods = self.config.get_mods()
        for mod_id, mod_info in mods.items():
            if mod_info.get('category') not in categories:
                mod_info['category'] = categories[0] if categories else '默认分类'
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
            try:
                self.statusBar().showMessage('正在导入MOD...')
                mod_info = self.mod_manager.import_mod(file_path)
                if mod_info and mod_info['files']:
                    main_stem = Path(mod_info['files'][0]).stem
                    mod_info['name'] = main_stem
                    mod_id = main_stem
                    mod_info['enabled'] = True
                    # 自动归类到当前选中分类
                    cat_item = self.tree.currentItem()
                    categories = [self.tree.topLevelItem(i).text(0) for i in range(self.tree.topLevelItemCount())]
                    if cat_item and cat_item.data(0, Qt.ItemDataRole.UserRole)['type'] == 'category':
                        mod_info['category'] = cat_item.data(0, Qt.ItemDataRole.UserRole)['name']
                    else:
                        mod_info['category'] = categories[0] if categories else '默认分类'
                    self.config.add_mod(mod_id, mod_info)
                    self.mod_manager.enable_mod(mod_id, parent_widget=self)
                    self.load_categories()
                    self.load_mods()
                    # 自动选中该MOD所属分类
                    category = mod_info.get('category', '默认分类')
                    for i in range(self.tree.topLevelItemCount()):
                        cat_item = self.tree.topLevelItem(i)
                        if cat_item.data(0, Qt.ItemDataRole.UserRole)['name'] == category:
                            self.tree.setCurrentItem(cat_item)
                            break
                    else:
                        if self.tree.topLevelItemCount() > 0:
                            self.tree.setCurrentItem(self.tree.topLevelItem(0))
                    self.refresh_mod_list()
                    self.statusBar().showMessage('MOD导入并已启用！', 3000)
                    # 新增：导入成功后询问是否导入预览图
                    reply = self.msgbox_question_zh('导入预览图', 'MOD导入成功，是否需要导入预览图？')
                    if reply == QMessageBox.StandardButton.Yes:
                        file_path, _ = QFileDialog.getOpenFileName(self, '选择预览图', '', '图片文件 (*.png *.jpg *.jpeg)')
                        if file_path:
                            mod_info['preview_image'] = file_path
                            self.config.update_mod(mod_id, mod_info)
                            self.show_mod_info(mod_info)
                    QMessageBox.information(self, '成功', 'MOD导入并已启用！')
            except Exception as e:
                self.statusBar().showMessage('导入失败！', 3000)
                QMessageBox.critical(self, '错误', f'导入MOD失败：{str(e)}')
                
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
            self.load_mods()
            self.refresh_mod_list(search_text=self.search_box.text())
            self.update_status_info()
        except Exception as e:
            print(f"[调试] toggle_mod: 操作失败: {e}")
            self.statusBar().showMessage('操作失败！', 3000)
            QMessageBox.critical(self, '错误', f'操作失败：{str(e)}')
            
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
                    QMessageBox.critical(self, '错误', f'删除MOD失败：{str(e)}')
                
    def on_item_clicked(self, item):
        """处理项目点击事件"""
        data = item.data(0, Qt.ItemDataRole.UserRole)
        if data['type'] == 'category':
            self.refresh_mod_list()
            # 自动选中第一个MOD并展示信息卡片
            if self.mod_list.count() > 0:
                self.mod_list.setCurrentRow(0)
                self.on_mod_list_clicked(self.mod_list.item(0))
            else:
                self.clear_info_panel()
        elif data['type'] == 'mod':
            self.show_mod_info(data['info'])
            self.enable_btn.setEnabled(True)
            self.delete_btn.setEnabled(True)
            enabled = data['info'].get('enabled', False)
            if enabled:
                self.enable_btn.setText('禁用MOD')
                self.enable_btn.setIcon(QIcon(resource_path('icons/禁用.svg')))
            else:
                self.enable_btn.setText('启用MOD')
                self.enable_btn.setIcon(QIcon(resource_path('icons/开启-开启.svg')))
        else:
            self.clear_info_panel()
            
    def show_mod_info(self, mod_info):
        """显示MOD信息（名称上方，字段横排）"""
        real_name = mod_info.get('real_name', '')
        name = mod_info.get('name', '未命名MOD')
        if real_name and real_name != name:
            show_name = f"{name}（{real_name}）"
        else:
            show_name = name
        self.mod_name_label.setText(show_name)
        self.mod_name_label.show()
        if mod_info.get('preview_image'):
            pixmap = QPixmap(mod_info['preview_image'])
            if not pixmap.isNull():
                self.preview_label.setPixmap(pixmap.scaled(
                    300, 200, Qt.AspectRatioMode.KeepAspectRatio,
                    Qt.TransformationMode.SmoothTransformation
                ))
        else:
            self.preview_label.clear()
        for i in reversed(range(self.mod_fields_layout.count())):
            w = self.mod_fields_layout.itemAt(i).widget()
            if w: w.setParent(None)
        cat = mod_info.get('category', '默认分类')
        cat_label = QLabel(f"分类：{cat}")
        cat_label.setStyleSheet('background:#313244;color:#b4befe;padding:4px 12px;border-radius:8px;font-size:14px;')
        self.mod_fields_layout.addWidget(cat_label)
        enabled = mod_info.get('enabled', False)
        status_label = QLabel(f"状态：{'已启用' if enabled else '已禁用'}")
        status_label.setStyleSheet(f"background:{'#a6e3a1' if enabled else '#f38ba8'};color:#23243a;padding:4px 12px;border-radius:8px;font-size:14px;")
        self.mod_fields_layout.addWidget(status_label)
        import_date = mod_info.get('import_date', '--')
        # 格式化为YYYY-MM-DD
        if import_date and import_date != '--':
            import_date = str(import_date)[:10]
        date_label = QLabel(f"导入日期：{import_date}")
        date_label.setStyleSheet('background:#45475a;color:#fab387;padding:4px 12px;border-radius:8px;font-size:14px;')
        self.mod_fields_layout.addWidget(date_label)
        self.mod_fields_frame.show()
        self.info_label.hide()
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
        self.preview_label.clear()
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
            QMessageBox.information(
                self,
                '成功',
                f'已找到 {len(found_mods)} 个MOD文件'
            )
            
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
        # 这里只做简单示例，实际可用QTranslator或自定义字典
        zh2en = {
            '剑星MOD管理器': 'StellarBlade MOD Manager',
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
            '设置': 'Settings',
            '语言切换': 'Switch Language',
            '切换MOD路径': 'Change MOD Path',
            '默认分类': 'Default',
            '提示': 'Tip',
            '请先选择要重命名的分类': 'Please select a category to rename',
            '请先选择要删除的分类': 'Please select a category to delete',
            '默认分类无法删除': 'Default category cannot be deleted',
            '确认删除': 'Confirm Delete',
            '确定要删除分类': 'Are you sure to delete category',
            '该分类下的MOD将一并删除。': 'All MODs under this category will be deleted.',
            '确定': 'OK',
            '取消': 'Cancel',
        }
        def tr(text):
            if lang == 'en':
                for k, v in zh2en.items():
                    if text.startswith(k):
                        return text.replace(k, v)
            return text
        # 重新设置所有控件文字
        self.setWindowTitle(tr('剑星MOD管理器'))
        self.findChild(QLabel, 'titleLabel').setText(tr('剑星MOD管理器'))
        self.add_cat_btn.setText(tr('新增'))
        self.rename_cat_btn.setText(tr('重命名'))
        self.del_cat_btn.setText(tr('删除'))
        self.findChild(QLabel, None).setText(tr('MOD分类'))
        self.search_box.setPlaceholderText(tr('输入MOD名称或描述...'))
        # 右侧按钮等可依次补充 

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
            QMessageBox.information(self, self.tr('成功'), self.tr('备份目录已修改')) 

    def refresh_mod_list(self, search_text=None):
        print("[调试] refresh_mod_list: 开始刷新C区MOD列表")
        mods = self.config.get_mods()
        print("[调试] refresh_mod_list: 当前所有MOD的分类：")
        for mod_id, mod_info in mods.items():
            print(f"  - {mod_id}: {mod_info.get('category', '无')}")
        self.mod_list.clear()
        cat_item = self.tree.currentItem()
        if not cat_item or cat_item.data(0, Qt.ItemDataRole.UserRole)['type'] != 'category':
            print("[调试] refresh_mod_list: 未选中分类")
            return
            
        cat_name = cat_item.data(0, Qt.ItemDataRole.UserRole)['name']
        print(f"[调试] refresh_mod_list: 当前选中分类: {cat_name}")
        
        count = 0
        for mod_id, mod_info in mods.items():
            mod_category = mod_info.get('category', '默认分类')
            if mod_category == cat_name:  # 使用精确匹配
                # 搜索过滤
                if search_text:
                    t = search_text.lower()
                    if t not in mod_info.get('name', '').lower() and t not in mod_info.get('real_name', '').lower():
                        continue
                item = QListWidgetItem()
                item.setText(f"{mod_info.get('name','未命名MOD')}")
                item.setData(Qt.UserRole, mod_id)
                item.setToolTip(f"{mod_info.get('name','未命名MOD')}\n{mod_info.get('size','--')} MB")
                self.mod_list.addItem(item)
                count += 1
                
        print(f"[调试] refresh_mod_list: 分类 {cat_name} 下有 {count} 个MOD")
        
        # 自动选中第一个MOD项
        if self.mod_list.count() > 0:
            self.mod_list.setCurrentRow(0)
            self.on_mod_list_clicked(self.mod_list.item(0))
        else:
            self.clear_info_panel()

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
        new_name, ok = QInputDialog.getText(self, self.tr('修改MOD名称'), self.tr('请输入新的MOD名称：'), text=mod_info.get('name',''))
        if ok and new_name and new_name != mod_info.get('name',''):
            mod_info['name'] = new_name
            self.config.update_mod(mod_id, mod_info)
            self.refresh_mod_list()

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
            mod_info['preview_image'] = file_path
            self.config.update_mod(mod_id, mod_info)
            self.show_mod_info(mod_info)

    def show_about(self):
        info = f"""
        <b>爱酱MOD管理器</b> v1.1 (20250611)<br>
        本管理器完全免费<br>
        作者：爱酱<br>
        QQ群：<a href='https://qm.qq.com/q/8vAq0MtyIU'>788566495</a><br>
        <span style='color:#bdbdbd'>欢迎加入QQ群获取最新MOD和反馈建议！</span>
        """
        QMessageBox.about(self, self.tr('关于爱酱MOD管理器'), info)

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
        box.button(QMessageBox.Yes).setText('是')
        box.button(QMessageBox.No).setText('否')
        # 居中按钮
        box.setStyleSheet(box.styleSheet() + "\nQPushButton { qproperty-alignment: AlignCenter; }")
        return box.exec()

    def on_tree_drop_event(self, event):
        # 支持任意层级间拖放
        QTreeWidget.dropEvent(self.tree, event)
        self.save_category_order()

    def save_category_order(self):
        # 获取当前一级分类顺序，保存到config
        categories = []
        for i in range(self.tree.topLevelItemCount()):
            item = self.tree.topLevelItem(i)
            categories.append(item.text(0))
        self.config.set_categories(categories)
        print(f"[调试] 已保存新分类顺序: {categories}") 

    def show_help(self):
        info = """
<b>剑星MOD管理器 使用说明</b><br><br>
1. <b>首次启动</b>：需选择MOD文件夹和备份文件夹，缺一不可，否则无法进入主界面。<br>
2. <b>导入MOD</b>：点击右侧"导入MOD"按钮，选择压缩包文件，导入后可启用/禁用。<br>
3. <b>分类管理</b>：左侧可新建、重命名、删除分类，支持拖动排序和层级调整。<br>
4. <b>MOD管理</b>：支持重命名、修改预览图、删除、启用/禁用等操作。<br>
5. <b>设置</b>：可切换语言、修改MOD/备份路径、查看关于信息。<br>
6. <b>更多</b>：如遇问题请加入QQ群反馈。<br><br>
<span style='color:#bdbdbd'>本工具仅供学习交流，严禁商用。</span>
"""
        QMessageBox.information(self, self.tr('使用说明'), info) 