using System;
using System.Collections.Generic;
using System.Text;

namespace UEModManager.Services.Steam
{
    /// <summary>
    /// Valve KeyValues 文本（<c>libraryfolders.vdf</c>、<c>appmanifest_*.acf</c>）里的一个节点：
    /// 要么是一个字符串值，要么是一组有序的子键。
    /// </summary>
    public sealed class VdfNode
    {
        private readonly List<KeyValuePair<string, VdfNode>> _children = new();

        internal VdfNode(string? value) => Value = value;

        /// <summary>叶子节点的字符串值；对象节点为 null。</summary>
        public string? Value { get; }

        /// <summary>是否是对象节点（<c>{ ... }</c>）。</summary>
        public bool IsObject => Value == null;

        /// <summary>子键，保持文件里的顺序；同名键都保留。</summary>
        public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

        internal void Add(string key, VdfNode child) => _children.Add(new KeyValuePair<string, VdfNode>(key, child));

        /// <summary>按键名取第一个子节点（不区分大小写：Steam 各版本写过 <c>LibraryFolders</c> 与 <c>libraryfolders</c>）。</summary>
        public VdfNode? Child(string key)
        {
            foreach (var pair in _children)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        /// <summary>按键名取字符串值；键不存在或它是对象时返回 null。</summary>
        public string? GetString(string key) => Child(key)?.Value;
    }

    /// <summary>
    /// Valve KeyValues 文本解析器（纯函数）。
    ///
    /// 只做 Steam 清单用得到的那部分语法：带引号/不带引号的记号、<c>{ }</c> 嵌套、<c>//</c> 行注释、
    /// 引号内的 <c>\\ \" \n \t</c> 转义。**对损坏的输入不抛异常**：文件被截断、括号不配对、键后面没有值时，
    /// 返回已经读出来的那部分 —— 清单是别的程序写的，读不全不该让"找游戏"整个失败。
    /// </summary>
    public static class ValveKeyValues
    {
        /// <summary>嵌套层数上限。Steam 清单最多四五层，超出的视为损坏并停止下探。</summary>
        public const int MaxDepth = 32;

        /// <summary>解析文本，返回一个虚拟根节点（顶层的键是它的子键）。null / 空文本返回空根。</summary>
        public static VdfNode Parse(string? text)
        {
            var root = new VdfNode(null);
            if (string.IsNullOrEmpty(text)) return root;

            var stack = new Stack<VdfNode>();
            stack.Push(root);
            string? pendingKey = null;
            var position = 0;

            while (TryReadToken(text, ref position, out var token, out var quoted))
            {
                if (!quoted && token == "{")
                {
                    // 没有键的 { 或层数超限：都无法挂到树上，按损坏处理，到此为止
                    if (pendingKey == null || stack.Count > MaxDepth) break;
                    var child = new VdfNode(null);
                    stack.Peek().Add(pendingKey, child);
                    stack.Push(child);
                    pendingKey = null;
                    continue;
                }

                if (!quoted && token == "}")
                {
                    pendingKey = null;
                    // 顶层多出来的 } 直接忽略
                    if (stack.Count > 1) stack.Pop();
                    continue;
                }

                if (pendingKey == null)
                {
                    // 条件标记（"key" "value" [$WIN32]）不是键
                    if (!quoted && token.StartsWith('[') && token.EndsWith(']')) continue;
                    pendingKey = token;
                }
                else
                {
                    stack.Peek().Add(pendingKey, new VdfNode(token));
                    pendingKey = null;
                }
            }

            return root;
        }

        private static bool TryReadToken(string text, ref int position, out string token, out bool quoted)
        {
            token = string.Empty;
            quoted = false;
            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length) return false;

            var c = text[position];
            if (c == '{' || c == '}')
            {
                position++;
                token = c.ToString();
                return true;
            }

            if (c == '"')
            {
                quoted = true;
                position++;
                var builder = new StringBuilder();
                while (position < text.Length)
                {
                    c = text[position++];
                    if (c == '"')
                    {
                        token = builder.ToString();
                        return true;
                    }
                    if (c == '\\' && position < text.Length)
                    {
                        var next = text[position];
                        switch (next)
                        {
                            case '\\': builder.Append('\\'); position++; continue;
                            case '"': builder.Append('"'); position++; continue;
                            case 'n': builder.Append('\n'); position++; continue;
                            case 't': builder.Append('\t'); position++; continue;
                        }
                        // 不认识的转义：原样保留反斜杠（手改过的文件里会有没转义的路径）
                    }
                    builder.Append(c);
                }
                // 引号没闭合（文件被截断）：把读到的当作这个记号
                token = builder.ToString();
                return true;
            }

            var start = position;
            while (position < text.Length)
            {
                c = text[position];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"') break;
                position++;
            }
            token = text.Substring(start, position - start);
            return true;
        }

        private static void SkipWhitespaceAndComments(string text, ref int position)
        {
            while (position < text.Length)
            {
                var c = text[position];
                if (char.IsWhiteSpace(c) || c == '\0' || c == (char)0xFEFF)
                {
                    position++;
                }
                else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] != '\n') position++;
                }
                else
                {
                    break;
                }
            }
        }
    }
}
