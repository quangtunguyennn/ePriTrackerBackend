using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using ePriTrackerBackend.Scraping.Tiki;
using ePriTrackerBackend.Services;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping
{
    public static class ScrapingServiceCollectionExtensions
    {
        /// <summary>
        /// Đăng ký toàn bộ hạ tầng scraping: telemetry, các sàn TMĐT (strategy) và bộ chọn sàn.
        /// </summary>
        public static IServiceCollection AddScraping(this IServiceCollection services)
        {
            // Telemetry: một instance duy nhất, cấp ra qua 2 interface (ghi / đọc)
            services.AddSingleton<ScraperMetricsService>();
            services.AddSingleton<IScraperMetrics>(sp => sp.GetRequiredService<ScraperMetricsService>());
            services.AddSingleton<IScraperMetricsReader>(sp => sp.GetRequiredService<ScraperMetricsService>());

            services.AddTikiScraper();
            // services.AddShopeeScraper(); ← thêm sàn mới tại đây

            // Bộ chọn sàn nhận tất cả IPlatformScraper đã đăng ký ở trên
            services.AddSingleton<IPlatformScraperResolver, PlatformScraperResolver>();

            return services;
        }

        private static IServiceCollection AddTikiScraper(this IServiceCollection services)
        {
            // HttpClient cho Tiki qua IHttpClientFactory
            services.AddHttpClient(TikiHttpFetchStrategy.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

            services.AddSingleton<ITikiBrowserService, TikiBrowserService>();
            services.AddSingleton<TikiHttpFetchStrategy>();
            services.AddSingleton<TikiPlaywrightFetchStrategy>();

            // Hybrid Scraping của Tiki – thứ tự mảng = thứ tự fallback: HttpClient (nhanh) → Playwright (tàng hình)
            services.AddKeyedSingleton<IHybridFetcher>(TikiScraper.PlatformName, (sp, _) => new HybridFetcher(
                new IFetchStrategy[]
                {
                    sp.GetRequiredService<TikiHttpFetchStrategy>(),
                    sp.GetRequiredService<TikiPlaywrightFetchStrategy>()
                },
                sp.GetRequiredService<IScraperMetrics>(),
                sp.GetRequiredService<ILogger<HybridFetcher>>()));

            // Một instance TikiScraper phục vụ cả 2 vai trò: strategy sản phẩm và nguồn sự kiện
            services.AddSingleton<TikiScraper>();
            services.AddSingleton<IPlatformScraper>(sp => sp.GetRequiredService<TikiScraper>());
            services.AddSingleton<IEventScraper>(sp => sp.GetRequiredService<TikiScraper>());

            return services;
        }
    }
}
