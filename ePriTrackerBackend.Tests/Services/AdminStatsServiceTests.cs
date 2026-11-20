using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Services.Admin;
using ePriTrackerBackend.Tests.Helpers;

namespace ePriTrackerBackend.Tests.Services
{
    public class AdminStatsServiceTests : IDisposable
    {
        // 10:00 giờ VN ngày 14/11/2026
        private static readonly DateTimeOffset Now = new(2026, 11, 14, 3, 0, 0, TimeSpan.Zero);
        private static readonly DateTime TodayVn = new(2026, 11, 14, 9, 0, 0);

        private sealed class ManualTime : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private readonly TestDb _db = new();
        private readonly FakePlatformScraper _scraper = new();

        public void Dispose() => _db.Dispose();

        private AdminStatsService Create() => new(new AdminRepository(_db.Context), new SingleScraperResolver(_scraper), new ManualTime());

        private void AddHistory(Product product, decimal price, DateTimeOffset at)
        {
            _db.Context.PriceHistory.Add(new PriceHistory { ProductId = product.ProductId, Price = price, CheckedAt = at });
            _db.Context.SaveChanges();
        }

        [Fact]
        public async Task Totals_ByPlatform_AndDailySeries()
        {
            var alice = _db.AddUser("a@test.vn");
            var bob = _db.AddUser("b@test.vn");
            _db.Context.User.Find(alice.UserId)!.CreatedAt = TodayVn;
            _db.Context.User.Find(bob.UserId)!.CreatedAt = TodayVn.AddDays(-20);
            _db.Context.SaveChanges();

            var p1 = _db.AddProduct("https://fake.vn/a-p1.html", addedAt: TodayVn);
            var p2 = _db.AddProduct("https://fake.vn/b-p2.html", addedAt: TodayVn.AddDays(-1));
            _db.AddProduct("https://fake.vn/c-p3.html", addedAt: TodayVn.AddDays(-40)); // không ai theo dõi, ngoài 30 ngày
            var other = _db.AddProduct("https://shop-cu.vn/x", addedAt: TodayVn);
            _db.Track(alice, p1);
            _db.Track(bob, p1);
            _db.Track(alice, p2);
            _db.Track(alice, other);
            _db.Context.Product.Find(p2.ProductId)!.IsInStock = false;

            _db.Context.Notification.Add(new Notification { NotificationId = Guid.NewGuid(), UserId = alice.UserId, Title = "t", Content = "c", CreatedAt = Now.AddHours(-1) });
            _db.Context.Notification.Add(new Notification { NotificationId = Guid.NewGuid(), UserId = alice.UserId, Title = "t", Content = "c", CreatedAt = Now.AddDays(-10) });
            _db.Context.SaveChanges();

            var stats = await Create().GetStatsAsync();

            Assert.Equal(2, stats.TotalUsers);
            Assert.Equal(1, stats.NewUsersLast7Days);
            Assert.Equal(3, stats.TrackedProducts);
            Assert.Equal(4, stats.TotalProducts);
            Assert.Equal(4, stats.TrackingEntries);
            Assert.Equal(1, stats.OutOfStockProducts);
            Assert.Equal(2, stats.TotalNotifications);
            Assert.Equal(1, stats.NotificationsLast7Days);

            Assert.Equal(2, stats.ProductsByPlatform.Single(p => p.Platform == "Fake").Products);
            Assert.Equal(1, stats.ProductsByPlatform.Single(p => p.Platform == AdminMonitoringService.UnknownPlatform).Products);

            Assert.Equal(AdminStatsDTO.AdminStatsDays, stats.Daily.Count);
            var today = stats.Daily[^1];
            Assert.Equal("2026-11-14", today.Date);
            Assert.Equal(2, today.NewProducts);
            Assert.Equal(1, today.NewUsers);
            Assert.Equal(1, today.Notifications);
            Assert.Equal(1, stats.Daily[^2].NewProducts);

            Assert.Equal(p1.ProductId, stats.TopTracked[0].ProductId);
            Assert.Equal(2, stats.TopTracked[0].Trackers);
        }

        [Fact]
        public async Task TopPriceDrops_ComparesWithPriceAtWindowStart()
        {
            var user = _db.AddUser("u@test.vn");
            var dropped = _db.AddProduct("https://fake.vn/a-p1.html", price: 800);
            var newlyAdded = _db.AddProduct("https://fake.vn/b-p2.html", price: 450);
            var raised = _db.AddProduct("https://fake.vn/c-p3.html", price: 1200);
            foreach (var p in new[] { dropped, newlyAdded, raised }) _db.Track(user, p);

            AddHistory(dropped, 1200, Now.AddDays(-20));
            AddHistory(dropped, 1000, Now.AddDays(-8));  // giá lúc đầu cửa sổ 7 ngày
            AddHistory(dropped, 800, Now.AddDays(-1));
            AddHistory(newlyAdded, 500, Now.AddDays(-2)); // thêm trong tuần → so với điểm đầu tiên
            AddHistory(newlyAdded, 450, Now.AddHours(-2));
            AddHistory(raised, 1000, Now.AddDays(-9));

            var drops = (await Create().GetStatsAsync()).TopPriceDrops;

            Assert.Equal(new[] { dropped.ProductId, newlyAdded.ProductId }, drops.Select(d => d.ProductId));
            Assert.Equal(1000m, drops[0].OldPrice);
            Assert.Equal(20.0, drops[0].DropPercent);
            Assert.Equal(10.0, drops[1].DropPercent);
        }

        [Fact]
        public async Task EmptyDatabase_ReturnsZeros()
        {
            var stats = await Create().GetStatsAsync();

            Assert.Equal(0, stats.TotalUsers);
            Assert.Empty(stats.TopTracked);
            Assert.Empty(stats.TopPriceDrops);
            Assert.All(stats.Daily, d => Assert.Equal(0, d.NewProducts));
        }
    }
}
