#!/bin/bash

echo "🚀 设置GitHub Secrets for AI SEO工作流"
echo "=================================="

# 检查是否安装了gh CLI
if ! command -v gh &> /dev/null; then
    echo "❌ 需要安装GitHub CLI (gh)"
    echo "安装方法: https://cli.github.com/manual/installation"
    exit 1
fi

# 检查是否已登录
if ! gh auth status &> /dev/null; then
    echo "❌ 需要先登录GitHub CLI"
    echo "运行: gh auth login"
    exit 1
fi

echo "✅ GitHub CLI已安装并登录"

# 设置API Key
echo "📝 设置OPENAI_API_KEY..."
gh secret set OPENAI_API_KEY --body="sk-kamtijpqszlpexadlbydvcjabsfefzjddhbjpjmxqyifsqom" 2>/dev/null || {
    echo "请手动设置OPENAI_API_KEY:"
    echo "Name: OPENAI_API_KEY"
    echo "Value: sk-kamtijpqszlpexadlbydvcjabsfefzjddhbjpjmxqyifsqom"
}

# 设置Base URL
echo "📝 设置OPENAI_BASE_URL..."
gh secret set OPENAI_BASE_URL --body="https://api.siliconflow.cn/v1" 2>/dev/null || {
    echo "请手动设置OPENAI_BASE_URL:"
    echo "Name: OPENAI_BASE_URL"
    echo "Value: https://api.siliconflow.cn/v1"
}

echo "✅ GitHub Secrets设置完成！"
echo ""
echo "🚀 现在可以运行AI SEO工作流了："
echo "1. 推送代码到GitHub"
echo "2. 工作流将自动运行"
echo "3. 查看Actions页面获取AI分析结果"