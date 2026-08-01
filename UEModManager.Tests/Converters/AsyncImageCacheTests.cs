using System;
using System.Linq;
using UEModManager.Converters;

namespace UEModManager.Tests.Converters
{
    /// <summary>
    /// 预览图缓存的纯逻辑测试：key 生成、容量核算、LRU 淘汰顺序。
    /// 这三部分不依赖 WPF 类型，可以直接在测试宿主中跑。
    /// </summary>
    public class AsyncImageCacheTests
    {
        // ── key 生成 ──────────────────────────────────────────

        [Fact]
        public void BuildCacheKey_同路径同尺寸不同写入时间_产生不同key()
        {
            // 换预览图的核心场景：ObjectStore 把新图写回同一个 preview.png，路径逐字节相同
            var path = @"C:\packages\foo\preview.png";
            var older = BuildKey(path, 400, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
            var newer = BuildKey(path, 400, new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc));

            Assert.NotEqual(older, newer);
        }

        [Fact]
        public void BuildCacheKey_三要素完全相同_产生相同key()
        {
            var stamp = new DateTime(2026, 7, 1, 12, 30, 0, DateTimeKind.Utc);
            Assert.Equal(
                BuildKey(@"C:\a\preview.png", 400, stamp),
                BuildKey(@"C:\a\preview.png", 400, stamp));
        }

        [Fact]
        public void BuildCacheKey_解码宽度不同_产生不同key()
        {
            var stamp = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.NotEqual(
                BuildKey(@"C:\a\preview.png", 400, stamp),
                BuildKey(@"C:\a\preview.png", 200, stamp));
        }

        [Fact]
        public void BuildCacheKey_路径不同_产生不同key()
        {
            var stamp = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.NotEqual(
                BuildKey(@"C:\a\preview.png", 400, stamp),
                BuildKey(@"C:\b\preview.png", 400, stamp));
        }

        private static string BuildKey(string path, int width, DateTime stamp)
            => AsyncImageConverter.BuildCacheKey(path, width, stamp);

        // ── 容量核算 ──────────────────────────────────────────

        [Fact]
        public void EstimateBitmapBytes_按像素数与像素格式核算()
        {
            // 400×300 的 32bpp 位图 = 480000 字节
            Assert.Equal(480_000L, AsyncImageConverter.EstimateBitmapBytes(400, 300, 32));
        }

        [Fact]
        public void EstimateBitmapBytes_低位深图片占用更小()
        {
            var bpp32 = AsyncImageConverter.EstimateBitmapBytes(400, 300, 32);
            var bpp24 = AsyncImageConverter.EstimateBitmapBytes(400, 300, 24);
            Assert.True(bpp24 < bpp32);
        }

        [Fact]
        public void EstimateBitmapBytes_非法尺寸不产生负数()
        {
            Assert.Equal(0L, AsyncImageConverter.EstimateBitmapBytes(-10, 300, 32));
            Assert.Equal(0L, AsyncImageConverter.EstimateBitmapBytes(400, 0, 32));
        }

        [Fact]
        public void EstimateBitmapBytes_位深不足8也至少按1字节算()
        {
            Assert.Equal(100L, AsyncImageConverter.EstimateBitmapBytes(10, 10, 1));
        }

        [Fact]
        public void EstimateBitmapBytes_大图不会int溢出()
        {
            // 8000×8000×4 = 256,000,000，超过 int 上限的乘法中间值必须走 long
            Assert.Equal(256_000_000L, AsyncImageConverter.EstimateBitmapBytes(8000, 8000, 32));
        }

        // ── LRU 缓存行为 ──────────────────────────────────────

        [Fact]
        public void 缓存_写入后可读回且计入字节数()
        {
            var cache = new SizeLimitedLruCache<string>(1000);
            cache.Set("a", "A", 100);

            Assert.True(cache.TryGet("a", out var value));
            Assert.Equal("A", value);
            Assert.Equal(100, cache.CurrentBytes);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void 缓存_未命中返回false()
        {
            var cache = new SizeLimitedLruCache<string>(1000);
            Assert.False(cache.TryGet("missing", out var value));
            Assert.Null(value);
        }

        [Fact]
        public void 缓存_超过容量时淘汰最久未用的条目()
        {
            var cache = new SizeLimitedLruCache<string>(300);
            cache.Set("a", "A", 100);
            cache.Set("b", "B", 100);
            cache.Set("c", "C", 100);   // 刚好占满
            cache.Set("d", "D", 100);   // 触发淘汰

            Assert.False(cache.TryGet("a", out _));     // a 最久未用，被淘汰
            Assert.True(cache.TryGet("b", out _));
            Assert.True(cache.TryGet("c", out _));
            Assert.True(cache.TryGet("d", out _));
            Assert.Equal(300, cache.CurrentBytes);
        }

        [Fact]
        public void 缓存_读取会把条目提升为最近使用从而免于淘汰()
        {
            var cache = new SizeLimitedLruCache<string>(300);
            cache.Set("a", "A", 100);
            cache.Set("b", "B", 100);
            cache.Set("c", "C", 100);

            cache.TryGet("a", out _);   // a 被提升，b 变成最久未用

            cache.Set("d", "D", 100);

            Assert.True(cache.TryGet("a", out _));
            Assert.False(cache.TryGet("b", out _));
        }

        [Fact]
        public void 缓存_一次写入可连续淘汰多个条目直到回到上限内()
        {
            var cache = new SizeLimitedLruCache<string>(300);
            cache.Set("a", "A", 100);
            cache.Set("b", "B", 100);
            cache.Set("c", "C", 100);
            cache.Set("big", "BIG", 300);   // 需要腾出 300 字节 = 全部淘汰

            Assert.Equal(1, cache.Count);
            Assert.True(cache.TryGet("big", out _));
            Assert.Equal(300, cache.CurrentBytes);
        }

        [Fact]
        public void 缓存_单个条目超过总容量则不缓存且不清空已有内容()
        {
            var cache = new SizeLimitedLruCache<string>(300);
            cache.Set("a", "A", 100);
            cache.Set("huge", "HUGE", 5000);

            Assert.False(cache.TryGet("huge", out _));
            Assert.True(cache.TryGet("a", out _));      // 已有条目不该被一个存不下的图挤掉
            Assert.Equal(100, cache.CurrentBytes);
        }

        [Fact]
        public void 缓存_覆盖同一key时按新大小重新计账而不是累加()
        {
            var cache = new SizeLimitedLruCache<string>(1000);
            cache.Set("a", "A", 100);
            cache.Set("a", "A2", 250);

            Assert.True(cache.TryGet("a", out var value));
            Assert.Equal("A2", value);
            Assert.Equal(250, cache.CurrentBytes);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void 缓存_淘汰顺序严格按最近使用排列()
        {
            var cache = new SizeLimitedLruCache<string>(1000);
            cache.Set("a", "A", 10);
            cache.Set("b", "B", 10);
            cache.Set("c", "C", 10);
            cache.TryGet("a", out _);

            Assert.Equal(new[] { "a", "c", "b" }, cache.KeysMostRecentFirst().ToArray());
        }

        [Fact]
        public void 缓存_Clear清空全部条目与字节计数()
        {
            var cache = new SizeLimitedLruCache<string>(1000);
            cache.Set("a", "A", 100);
            cache.Set("b", "B", 100);

            cache.Clear();

            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.CurrentBytes);
            Assert.False(cache.TryGet("a", out _));
        }

        [Fact]
        public void 缓存_负数大小按0处理()
        {
            var cache = new SizeLimitedLruCache<string>(1000);
            cache.Set("a", "A", -5);

            Assert.True(cache.TryGet("a", out _));
            Assert.Equal(0, cache.CurrentBytes);
        }

        [Fact]
        public void 缓存_容量必须为正数()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SizeLimitedLruCache<string>(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SizeLimitedLruCache<string>(-1));
        }

        [Fact]
        public void 缓存容量上限_按默认预览图尺寸估算约可容纳270张()
        {
            // 注释里声明的依据必须与常量一致：128MB / 480KB ≈ 270
            var oneImage = AsyncImageConverter.EstimateBitmapBytes(400, 300, 32);
            var capacity = AsyncImageConverter.CacheCapacityBytes / oneImage;

            Assert.Equal(128L * 1024 * 1024, AsyncImageConverter.CacheCapacityBytes);
            Assert.InRange(capacity, 250, 300);
        }
    }
}
