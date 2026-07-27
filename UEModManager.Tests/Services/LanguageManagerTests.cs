using System;
using System.Collections.Generic;
using UEModManager.Services;

namespace UEModManager.Tests.Services
{
    /// <summary>
    /// LanguageManager 的派发行为测试。
    /// 重点是：某个订阅者抛异常时，排在它后面的订阅者仍然必须收到通知——
    /// 原实现用 <c>LanguageChanged?.Invoke(...)</c> 加一层 try/catch，
    /// 第一个僵尸窗口抛异常就会让主窗口永远收不到语言切换通知。
    /// 注：静态事件是进程级共享状态，本类内的用例串行执行，且每个用例都会退订并复位。
    /// </summary>
    public class LanguageManagerTests
    {
        [Fact]
        public void 某个订阅者抛异常时_后续订阅者仍然收到通知()
        {
            var called = new List<string>();
            Action<bool> first = _ => { called.Add("first"); throw new InvalidOperationException("模拟僵尸窗口对已卸载控件赋值"); };
            Action<bool> second = _ => called.Add("second");
            Action<bool> third = _ => called.Add("third");

            LanguageManager.LanguageChanged += first;
            LanguageManager.LanguageChanged += second;
            LanguageManager.LanguageChanged += third;
            try
            {
                Toggle();
                Assert.Equal(new[] { "first", "second", "third" }, called.ToArray());
            }
            finally
            {
                LanguageManager.LanguageChanged -= first;
                LanguageManager.LanguageChanged -= second;
                LanguageManager.LanguageChanged -= third;
                Reset();
            }
        }

        [Fact]
        public void 订阅者抛异常不会冒泡到调用方()
        {
            Action<bool> boom = _ => throw new InvalidOperationException("boom");
            LanguageManager.LanguageChanged += boom;
            try
            {
                Toggle();   // 不抛即通过
            }
            finally
            {
                LanguageManager.LanguageChanged -= boom;
                Reset();
            }
        }

        [Fact]
        public void 具名handler可以正常退订_退订后不再收到通知()
        {
            var count = 0;
            Action<bool> handler = _ => count++;

            LanguageManager.LanguageChanged += handler;
            Toggle();
            Assert.Equal(1, count);

            LanguageManager.LanguageChanged -= handler;
            Toggle();
            Assert.Equal(1, count);   // 退订生效，计数不再增加

            Reset();
        }

        [Fact]
        public void 语言未发生变化时不派发()
        {
            var count = 0;
            Action<bool> handler = _ => count++;

            LanguageManager.LanguageChanged += handler;
            try
            {
                LanguageManager.SetEnglish(LanguageManager.IsEnglish);
                Assert.Equal(0, count);
            }
            finally
            {
                LanguageManager.LanguageChanged -= handler;
            }
        }

        /// <summary>切换到相反的语言以触发一次派发。</summary>
        private static void Toggle() => LanguageManager.SetEnglish(!LanguageManager.IsEnglish);

        /// <summary>把语言复位为中文，避免污染其它用例。</summary>
        private static void Reset() => LanguageManager.SetEnglish(false);
    }
}
