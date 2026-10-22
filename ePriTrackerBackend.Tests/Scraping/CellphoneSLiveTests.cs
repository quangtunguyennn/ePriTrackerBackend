using ePriTrackerBackend.Scraping.CellphoneS;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// Chạy THẬT trên trang chủ CellphoneS (Playwright + mạng) – kiểm tra bộ đọc còn khớp giao diện hiện tại.
    /// Mặc định bỏ qua; bật bằng biến môi trường EPRI_LIVE_TESTS=1.
    /// </summary>
    public class CellphoneSLiveTests
    {
        private readonly ITestOutputHelper _output;
        public CellphoneSLiveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task HomePage_RealRender_FindsEventsWithProducts()
        {
            if (Environment.GetEnvironmentVariable("EPRI_LIVE_TESTS") != "1") return;

            await using var renderer = new PlaywrightPageRenderer(NullLogger<PlaywrightPageRenderer>.Instance);
            string html = await renderer.RenderAsync(CellphoneSHomeEventParser.HomeUrl);
            if (Environment.GetEnvironmentVariable("EPRI_LIVE_DUMP") is { Length: > 0 } dumpPath)
                await File.WriteAllTextAsync(dumpPath, html); // lưu HTML đã render để soi khi bộ đọc sai
            var events = CellphoneSHomeEventParser.Parse(html, DateTimeOffset.UtcNow);

            Assert.NotEmpty(events);
            foreach (var evt in events)
            {
                _output.WriteLine($"{evt.Title} | {evt.Url} | hết hạn {evt.EndsAt?.ToString() ?? "(cuốn chiếu)"} | {evt.Products.Count} sản phẩm");
                foreach (var p in evt.Products.Take(3))
                    _output.WriteLine($"   {p.Name} | {p.Price:N0} (gốc {p.OriginalPrice:N0}) | {p.Description} | {p.Url}");
                foreach (var p in evt.Products.Where(p => !IsHttpUrl(p.ImageUrl)))
                    _output.WriteLine($"   ⚠ THIẾU ẢNH: {p.Name} | ảnh = \"{p.ImageUrl}\"");

                Assert.True(evt.Products.Count >= 4, evt.Title);
                Assert.All(evt.Products, p => Assert.True(p.Price > 0));
            }
        }

        private static bool IsHttpUrl(string url) => url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }
}
