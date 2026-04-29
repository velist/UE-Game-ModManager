namespace UEModManager.Views
{
    // 仅负责提供登录窗口的双语文案；避免影响全局。
    public static class LoginWindowLocalization
    {
        public static string GetString(string lang, string key)
        {
            var zh = lang == "zh-CN";
            switch (key)
            {
                case "WindowTitle": return zh ? "用户登录 - UEModManager" : "Sign In - UEModManager";
                case "Subtitle": return zh ? "智能登录" : "Smart Sign-in";
                case "SmartHint": return zh ? "💡 输入邮箱后，系统将自动识别是登录还是注册" : "💡 Enter email, system will auto detect login or register.";
                case "SmartDetect_LoginOrRegister": return zh ? "将自动识别：已注册则登录，未注册则创建账户" : "Auto detect: login if exists, or create an account.";
                case "Email": return zh ? "邮箱地址" : "Email";
                case "Password": return zh ? "密码" : "Password";
                case "NewUserHint": return zh ? "💡 新用户？首次输入密码将自动创建账户" : "💡 New user? First password sets up your account.";
                case "RememberMe": return zh ? "记住我" : "Remember me";
                case "ForgotPwd": return zh ? "忘记密码？" : "Forgot password?";
                case "LoginBtn": return zh ? "登录" : "Sign in";
                case "OtpLoginBtn": return zh ? "使用验证码登录" : "Sign in with code";
                case "OfflineBtn": return zh ? "离线模式" : "Offline";
                case "LoadingLoggingIn": return zh ? "正在登录..." : "Signing in...";
                case "LoadingSendOtp": return zh ? "正在发送验证码..." : "Sending code...";
                case "LoadingVerifyOtp": return zh ? "正在验证验证码..." : "Verifying code...";
                case "LoadingResetPwd": return zh ? "正在发送重置邮件..." : "Sending reset email...";
                case "Tip": return zh ? "提示" : "Tip";
                case "ErrEmailPwdEmpty": return zh ? "请输入邮箱和密码" : "Please enter email and password";
                case "ErrEmailEmpty": return zh ? "请输入邮箱" : "Please enter email";
                case "ErrLoginFailed": return zh ? "登录失败" : "Sign in failed";
                case "ErrSendOtpFailed": return zh ? "发送验证码失败" : "Failed to send code";
                case "InputOtpPrompt": return zh ? "请输入邮箱收到的验证码：" : "Enter the code sent to your email:";
                case "OtpTitle": return zh ? "邮箱验证码" : "Email Code";
                case "ErrVerifyOtpFailed": return zh ? "验证码验证失败" : "Code verification failed";
                case "ErrSetSessionFailed": return zh ? "设置本地会话失败" : "Failed to set local session";
                case "ErrOtpLoginFailed": return zh ? "验证码登录失败" : "Sign in with code failed";
                case "ResetPwdSent": return zh ? "密码重置邮件已发送，请检查邮箱" : "Password reset email sent. Please check your inbox.";
                case "ErrResetPwdFailed": return zh ? "密码重置失败" : "Password reset failed";
                case "PwdWeak": return zh ? "弱" : "Weak";
                case "PwdMedium": return zh ? "中" : "Medium";
                case "PwdStrong": return zh ? "强" : "Strong";
            }
            return key;
        }
    }
}

