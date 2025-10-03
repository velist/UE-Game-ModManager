namespace UEModManager.Views
{
    public static class RegisterWindowLocalization
    {
        public static string GetString(string lang, string key)
        {
            var zh = lang == "zh-CN";
            switch (key)
            {
                case "WindowTitle": return zh ? "用户注册 - UEModManager" : "Sign Up - UEModManager";
                case "Subtitle": return zh ? "创建新账户" : "Create New Account";
                case "UsernameLabel": return zh ? "用户名（可选)" : "Username (optional)";
                case "EmailLabel": return zh ? "邮箱地址 *" : "Email Address *";
                case "PasswordLabel": return zh ? "登录密码 *" : "Password *";
                case "ConfirmPasswordLabel": return zh ? "确认密码 *" : "Confirm Password *";
                case "PwdHint": return zh ? "密码至少8位，建议包含字母、数字和特殊字符" : "At least 8 chars, include letters, numbers, and symbols";
                case "AgreePrefix": return zh ? "我已阅读并同意" : "I have read and agree to";
                case "And": return zh ? "和" : "and";
                case "UserAgreement": return zh ? "用户协议" : "User Agreement";
                case "PrivacyPolicy": return zh ? "隐私政策" : "Privacy Policy";
                case "RegisterBtn": return zh ? "创建账户" : "Create Account";
                case "BackToLoginBtn": return zh ? "已有账户？返回登录" : "Already have an account? Back to Sign In";
                case "LoadingCreating": return zh ? "正在创建账户..." : "Creating account...";
                case "PwdVeryWeak": return zh ? "密码强度：很弱 - 至少需要8位字符" : "Strength: Very Weak - at least 8 characters";
                case "PwdWeak": return zh ? "密码强度：较弱 - 建议添加数字和特殊字符" : "Strength: Weak - add digits & symbols";
                case "PwdMedium": return zh ? "密码强度：中等 - 还不错，可以更强" : "Strength: Medium - not bad, can be stronger";
                case "PwdStrong": return zh ? "密码强度：强 - 很好的密码" : "Strength: Strong - good password";
                case "PwdVeryStrong": return zh ? "密码强度：很强 - 非常安全" : "Strength: Very Strong - very safe";
                case "PwdPlease": return zh ? "请输入密码" : "Please enter password";
                case "ErrService": return zh ? "认证服务未初始化，请重新打开窗口" : "Auth service not initialized. Please reopen.";
                case "ErrInputTitle": return zh ? "输入错误" : "Input Error";
                case "ErrEnterEmail": return zh ? "请输入邮箱地址" : "Please enter email";
                case "ErrEmailInvalid": return zh ? "请输入有效的邮箱地址" : "Please enter a valid email";
                case "ErrEnterPwd": return zh ? "请输入密码" : "Please enter password";
                case "ErrPwdShort": return zh ? "密码至少需要8位字符" : "Password must be at least 8 characters";
                case "ErrPwdNotMatch": return zh ? "两次输入的密码不一致" : "Passwords do not match";
                case "ErrUsernameLen": return zh ? "用户名长度应在2-20个字符之间" : "Username length must be 2-20 characters";
                case "ErrAgree": return zh ? "请同意用户协议和隐私政策" : "Please agree to the User Agreement and Privacy Policy";
                case "LoadingEnhanced": return zh ? "正在进行增强注册验证..." : "Performing enhanced checks...";
                case "LoadingFallback": return zh ? "回退到基础注册模式..." : "Falling back to basic registration...";
                case "TitleRegisterSuccess": return zh ? "注册成功" : "Registration Success";
                case "TitleRegisterError": return zh ? "注册错误" : "Registration Error";
                case "TitleLinkError": return zh ? "链接错误" : "Link Error";
            }
            return key;
        }
    }
}
