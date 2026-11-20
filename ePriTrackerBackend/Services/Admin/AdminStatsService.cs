using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Services.Admin
{
    /// <summary>Tab "Thống kê" của trang Admin: người dùng, sản phẩm, thông báo, top sản phẩm.</summary>
    public interface IAdminStatsService
    {
        Task<AdminStatsDTO> GetStatsAsync(CancellationToken cancellationToken = default);
    }

    public class AdminStatsService : IAdminStatsService
    {
        public const int TopCount = 10;
        public static readonly TimeSpan DropWindow = TimeSpan.FromDays(7);

        private readonly IAdminRepository _admin;
        private readonly IPlatformScraperResolver _resolver;
        private readonly TimeProvider _time;
        private readonly TimeZoneInfo _vietnam;

        public AdminStatsService(IAdminRepository admin, IPlatformScraperResolver resolver, TimeProvider timeProvider = null)
        {
            _admin = admin ?? throw new ArgumentNullException(nameof(admin));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _time = timeProvider ?? TimeProvider.System;
            _vietnam = GetVietnamTimeZone();
        }

        public async Task<AdminStatsDTO> GetStatsAsync(CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow();
            var todayVn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _vietnam).DateTime);
            var weekAgoVn = TimeZoneInfo.ConvertTime(now - DropWindow, _vietnam).DateTime;

            var products = await _admin.GetProductRowsAsync(cancellationToken);
            var userDates = await _admin.GetUserCreatedDatesAsync(cancellationToken);
            var notificationTimes = await _admin.GetNotificationCreatedTimesAsync(cancellationToken);
            int trackingEntries = await _admin.CountTrackedItemsAsync(cancellationToken);

            var tracked = products.Where(p => p.ActiveTrackersCount > 0).ToList();
            var platformOf = products.ToDictionary(p => p.ProductId, p => PlatformOf(p.ProductLink));

            // Giờ lưu trong DB: AddedAt / User.CreatedAt là giờ VN dạng DateTime; Notification.CreatedAt là DateTimeOffset
            var notificationDaysVn = notificationTimes.Select(t => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, _vietnam).DateTime)).ToList();

            var firstDay = todayVn.AddDays(-(AdminStatsDTO.AdminStatsDays - 1));
            var daily = Enumerable.Range(0, AdminStatsDTO.AdminStatsDays)
                .Select(i => firstDay.AddDays(i))
                .Select(day => new DailyStatsDTO
                {
                    Date = day.ToString("yyyy-MM-dd"),
                    NewProducts = products.Count(p => p.AddedAt.HasValue && DateOnly.FromDateTime(p.AddedAt.Value) == day),
                    NewUsers = userDates.Count(d => d.HasValue && DateOnly.FromDateTime(d.Value) == day),
                    Notifications = notificationDaysVn.Count(d => d == day)
                })
                .ToList();

            return new AdminStatsDTO
            {
                GeneratedAt = now,
                TotalUsers = userDates.Count,
                NewUsersLast7Days = userDates.Count(d => d.HasValue && d.Value >= weekAgoVn),
                TrackedProducts = tracked.Count,
                TotalProducts = products.Count,
                TrackingEntries = trackingEntries,
                OutOfStockProducts = tracked.Count(p => !p.IsInStock),
                TotalNotifications = notificationTimes.Count,
                NotificationsLast7Days = notificationTimes.Count(t => now - t <= DropWindow),
                ProductsByPlatform = tracked
                    .GroupBy(p => platformOf[p.ProductId])
                    .Select(g => new PlatformCountDTO { Platform = g.Key, Products = g.Count() })
                    .OrderByDescending(x => x.Products)
                    .ToList(),
                Daily = daily,
                TopTracked = tracked
                    .OrderByDescending(p => p.ActiveTrackersCount).ThenBy(p => p.ProductName)
                    .Take(TopCount)
                    .Select(p => ToTopProduct(p, platformOf[p.ProductId]))
                    .ToList(),
                TopPriceDrops = await GetTopPriceDropsAsync(tracked, platformOf, now, cancellationToken)
            };
        }

        /// <summary>
        /// Giảm sâu nhất trong <see cref="DropWindow"/>: so giá hiện tại với giá lúc đầu cửa sổ (điểm lịch sử gần nhất trước đó;
        /// sản phẩm mới thêm trong cửa sổ → điểm đầu tiên). Chỉ sản phẩm còn hàng, còn người theo dõi.
        /// </summary>
        private async Task<List<TopProductDTO>> GetTopPriceDropsAsync(List<AdminProductRow> tracked, Dictionary<Guid, string> platformOf,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            var candidates = tracked.Where(p => p.IsInStock && p.LatestPrice > 0).ToList();
            var history = (await _admin.GetPriceHistoryAsync(candidates.Select(p => p.ProductId).ToList(), cancellationToken))
                .GroupBy(h => h.ProductId)
                .ToDictionary(g => g.Key, g => g.OrderBy(h => h.CheckedAt).ToList());

            var windowStart = now - DropWindow;
            var drops = new List<TopProductDTO>();
            foreach (var product in candidates)
            {
                if (!history.TryGetValue(product.ProductId, out var points) || points.Count == 0) continue;

                var start = points.LastOrDefault(h => h.CheckedAt <= windowStart) ?? points.FirstOrDefault(h => h.CheckedAt > windowStart);
                if (start == null || start.Price <= 0 || product.LatestPrice >= start.Price) continue;

                var dto = ToTopProduct(product, platformOf[product.ProductId]);
                dto.OldPrice = start.Price;
                dto.DropPercent = Math.Round((double)((start.Price - product.LatestPrice.Value) / start.Price * 100), 1);
                drops.Add(dto);
            }

            return drops.OrderByDescending(d => d.DropPercent).Take(TopCount).ToList();
        }

        private static TopProductDTO ToTopProduct(AdminProductRow p, string platform) => new()
        {
            ProductId = p.ProductId,
            ProductName = p.ProductName,
            ProductLink = p.ProductLink,
            ImageURL = p.ImageURL,
            Platform = platform,
            Trackers = p.ActiveTrackersCount,
            LatestPrice = p.LatestPrice
        };

        private string PlatformOf(string link)
            => !string.IsNullOrWhiteSpace(link) && _resolver.TryResolve(link, out var scraper) ? scraper.Platform : AdminMonitoringService.UnknownPlatform;

        private static TimeZoneInfo GetVietnamTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        }
    }
}
