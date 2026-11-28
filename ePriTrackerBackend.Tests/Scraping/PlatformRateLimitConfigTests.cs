using System.Text.Json;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// Giới hạn tốc độ theo sàn trong appsettings.json (Scraping:Platforms:{Sàn}:MinIntervalMs) – thiếu là gửi dồn request
    /// và bị sàn chặn (HACOM từng trả 403 cho mọi trang sản phẩm khi chưa có giới hạn).
    /// </summary>
    public class PlatformRateLimitConfigTests
    {
        private static JsonElement Platforms()
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ePriTrackerBackend", "appsettings.json"));
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.GetProperty("Scraping").GetProperty("Platforms").Clone();
        }

        [Theory]
        [InlineData("HACOM", 3000)]
        [InlineData("CellphoneS", 1000)]
        [InlineData("GearVN", 1000)]
        public void ScrapedPlatforms_HaveMinInterval(string platform, int expectedMs)
        {
            // Tên khoá phải trùng PlatformName của scraper (AddPlatformHttpClient đọc theo tên đó)
            Assert.Equal(expectedMs, Platforms().GetProperty(platform).GetProperty("MinIntervalMs").GetInt32());
        }

        [Fact]
        public void HacomKey_MatchesScraperPlatformName()
        {
            Assert.True(Platforms().TryGetProperty(ePriTrackerBackend.Scraping.Hacom.HacomScraper.PlatformName, out _));
        }
    }
}
