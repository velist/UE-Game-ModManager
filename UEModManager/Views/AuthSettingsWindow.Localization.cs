namespace UEModManager.Views
{
    public static class AuthSettingsWindowLocalization
    {
        public static string GetString(string lang, string key)
        {
            var zh = lang == "zh-CN";
            switch (key)
            {
                case "WindowTitle": return zh ? "认证设置" : "Authentication Settings";
                case "Header": return zh ? "选择认证模式" : "Choose Authentication Mode";
                case "HybridTitle": return zh ? "混合模式 (推荐)" : "Hybrid (Recommended)";
                case "HybridDesc": return zh ? "智能结合本地和云端认证，提供最佳的用户体验：" : "Smartly combines local & cloud auth for best experience:";
                case "HybridLine1": return zh ? "• 优先尝试云端登录，获取最新数据和设置" : "• Prefer cloud sign-in for latest data & settings";
                case "HybridLine2": return zh ? "• 网络异常时自动切换到本地认证" : "• Fallback to local when network fails";
                case "HybridLine3": return zh ? "• 支持数据同步和多设备共享" : "• Data sync & multi-device support";
                case "HybridLine4": return zh ? "• 离线时保持完整功能" : "• Full functionality when offline";
                case "OnlineTitle": return zh ? "仅云端模式" : "Cloud Only";
                case "OnlineDesc": return zh ? "所有认证均通过云端服务进行：" : "All authentication via cloud:";
                case "OnlineLine1": return zh ? "• 实时获取最新的用户数据和设置" : "• Real-time latest user data & settings";
                case "OnlineLine2": return zh ? "• 无需担心本地数据丢失" : "• No local data loss concerns";
                case "OnlineLine3": return zh ? "• 多设备自动同步" : "• Auto sync across devices";
                case "OnlineLine4": return zh ? "• 需要稳定网络连接" : "• Requires stable network";
                case "OfflineTitle": return zh ? "仅本地模式" : "Local Only";
                case "OfflineDesc": return zh ? "所有数据存储在本地SQLite数据库中：" : "All data stored locally (SQLite):";
                case "OfflineLine1": return zh ? "• 完全离线运行，无需网络" : "• Fully offline, no network required";
                case "OfflineLine2": return zh ? "• 数据隐私和安全性最高" : "• Highest privacy & security";
                case "OfflineLine3": return zh ? "• 响应速度最快" : "• Fastest response";
                case "OfflineLine4": return zh ? "• 不支持多设备同步" : "• No multi-device sync";
                case "NetworkLabel": return zh ? "网络状态：" : "Network:";
                case "Checking": return zh ? "检查中..." : "Checking...";
                case "Refresh": return zh ? "刷新" : "Refresh";
                case "TestConnection": return zh ? "测试连接" : "Test";
                case "Cancel": return zh ? "取消" : "Cancel";
                case "SaveSettings": return zh ? "保存设置" : "Save";
                case "Saving": return zh ? "正在保存..." : "Saving...";
                case "SaveSuccessTitle": return zh ? "保存成功" : "Saved";
                case "SaveSuccessPrefix": return zh ? "设置已保存！\n当前认证模式: " : "Saved!\nCurrent mode: ";
                case "SaveFailedTitle": return zh ? "保存失败" : "Save Failed";
                case "ErrorTitle": return zh ? "错误" : "Error";
                case "UnknownMode": return zh ? "未知模式" : "Unknown";
                case "ModeHybrid": return zh ? "混合模式" : "Hybrid";
                case "ModeOnline": return zh ? "仅云端模式" : "Cloud Only";
                case "ModeOffline": return zh ? "仅本地模式" : "Local Only";
            }
            return key;
        }
    }
}
