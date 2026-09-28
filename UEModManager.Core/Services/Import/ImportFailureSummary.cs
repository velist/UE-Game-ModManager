using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 一次导入的失败汇总（纯函数）。
    ///
    /// <c>PackageImportService.ImportAsync</c> 是"每个文件各自 try"的结构：
    /// 任何一个文件失败都不会抛，只在返回结果里留一条 ErrorMessage。
    /// 调用方若只统计 Success 的个数，"仓库目录不可写导致索引写不进去"这类失败
    /// 对用户就是"对话框正常关闭、列表里少了几个 MOD，没有任何解释"。
    ///
    /// 这里把"该怎么向用户交代"抽成纯函数，UI 只负责把结果画出来，
    /// 于是这段时序敏感的提示逻辑可以在 xUnit 里直接断言。
    /// </summary>
    public sealed record ImportFailureSummary(
        string HeaderTemplate, int TotalCount, int FailedCount, IReadOnlyList<string> Reasons)
    {
        /// <summary>标题模板的参数（全失败时标题要多一个 "failed" 参数）。</summary>
        public object?[] HeaderArguments
            => HeaderTemplate.Contains("{0}", StringComparison.Ordinal)
                ? [TotalCount, FailedCount]
                : [];

        /// <summary>未本地化的展示全文，供测试与非 UI 调用方使用。</summary>
        public string Message => Format(HeaderTemplate, HeaderArguments)
            + Environment.NewLine + string.Join(Environment.NewLine, Reasons);

        private static string Format(string template, object?[] args)
            => args.Length == 0 ? template : string.Format(template, args);

        /// <summary>
        /// 汇总导入失败。全部成功时返回 <c>null</c>（调用方据此不弹任何窗）。
        /// </summary>
        /// <param name="results">每个文件成功与否 + 失败原因（空白原因按"原因未知"处理）。</param>
        /// <param name="reasonUnknown">原因缺失时的兜底文案，由调用方传入以便本地化。</param>
        public static ImportFailureSummary? Build(
            IEnumerable<(bool Success, string? ErrorMessage)> results, string reasonUnknown)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            var all = results.ToList();
            var reasons = all
                .Where(r => !r.Success)
                .Select(r => string.IsNullOrWhiteSpace(r.ErrorMessage) ? reasonUnknown : r.ErrorMessage!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (reasons.Count == 0) return null;

            var succeeded = all.Count(r => r.Success);
            var failed = all.Count - succeeded;
            // 全失败时不说"N 个里有 N 个失败"，那读起来像绕口令；只说"导入失败："。
            var headerTemplate = succeeded > 0
                ? "{0} 个文件中有 {1} 个导入失败："
                : "导入失败：";
            return new ImportFailureSummary(headerTemplate, all.Count, failed, reasons);
        }
    }
}
