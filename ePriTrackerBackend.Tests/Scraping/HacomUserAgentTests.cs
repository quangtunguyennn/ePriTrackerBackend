using ePriTrackerBackend.Scraping;
using ePriTrackerBackend.Scraping.Hacom;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// HttpClient tải trang sản phẩm HACOM tự xưng đúng danh tính (không giả trình duyệt):
    /// Cloudflare của HACOM trả 403 cho UA "Chrome" gửi từ HttpClient (28/11/2026).
    /// </summary>
    public class HacomUserAgentTests
    {
        [Fact]
        public void ProductPageClient_UsesHonestUserAgent()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddScraping();

            using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HacomScraper.PlatformName);
            string ua = client.DefaultRequestHeaders.UserAgent.ToString();

            Assert.StartsWith("ePriTracker/", ua);
            Assert.DoesNotContain("Mozilla", ua);
            Assert.DoesNotContain("Chrome", ua);
        }
    }
}
