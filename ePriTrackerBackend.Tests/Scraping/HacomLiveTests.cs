using System.Diagnostics;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using ePriTrackerBackend.Scraping.Hacom;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// Chạy THẬT tìm kiếm trên hacom.vn (mạng + Playwright): trang tìm kiếm nạp kết quả bằng JavaScript nên luôn phải render.
    /// Đo thời gian lần đầu (khởi động Chromium) và các lần sau. Mặc định bỏ qua; bật bằng EPRI_LIVE_TESTS=1.
    /// </summary>
    public class HacomLiveTests
    {
        private static bool Live => Environment.GetEnvironmentVariable("EPRI_LIVE_TESTS") == "1";

        private readonly ITestOutputHelper _output;
        public HacomLiveTests(ITestOutputHelper output) => _output = output;

        private sealed class NoFetcher : IHybridFetcher
        {
            public Task<System.Text.Json.JsonElement> FetchJsonAsync(string path, FetchOptions? options = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
        }

        [Fact]
        public async Task Search_ColdThenWarm_Timings()
        {
            if (!Live) return;

            await using var renderer = new PlaywrightPageRenderer(NullLogger<PlaywrightPageRenderer>.Instance);
            if (Environment.GetEnvironmentVariable("EPRI_LIVE_DUMP") is { Length: > 0 } dumpPath) // lưu HTML đã render để soi khi bộ đọc sai
                await File.WriteAllTextAsync(dumpPath, await renderer.RenderAsync(
                    Environment.GetEnvironmentVariable("EPRI_LIVE_DUMP_URL") ?? "https://hacom.vn/tim?q=chu%E1%BB%99t%20logitech%20mx%20master%203s", PageRenderOptions.Search));

            var scraper = new HacomScraper(new NoFetcher(), renderer, new ConfigurationBuilder().Build(), NullLogger<HacomScraper>.Instance);

            foreach (var keyword in new[] { "chuột logitech mx master 3s", "bàn phím cơ asus rog", "laptop asus tuf gaming" })
            {
                var sw = Stopwatch.StartNew();
                var results = await scraper.SearchAsync(keyword, 10);
                _output.WriteLine($"'{keyword}': {results.Count} kết quả trong {sw.ElapsedMilliseconds} ms");
                foreach (var p in results) _output.WriteLine($"   {p.Price,12:N0} | {p.Name} | {p.Url}");
                Assert.NotEmpty(results);
                Assert.All(results, p => Assert.DoesNotContain("Xem chi tiết", p.Name));
            }
        }
    }
}
