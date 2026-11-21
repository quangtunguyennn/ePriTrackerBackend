using ePriTrackerBackend.Services.Notification;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    public class DelistedNotificationTests : IDisposable
    {
        private readonly TestDb _db = new();

        public void Dispose() => _db.Dispose();

        /// <summary>Không gửi mail thật trong test.</summary>
        private sealed class RecordingEmail : IEmailService
        {
            public List<(string Email, string Product, decimal? LastPrice)> Delisted { get; } = new();
            public Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink)
                => Task.CompletedTask;
            public Task SendProductDelistedEmailAsync(string recipientEmail, string recipientName, string productName, decimal? lastPrice, string productLink)
            {
                lock (Delisted) Delisted.Add((recipientEmail, productName, lastPrice));
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task EveryTrackerGetsOneNotificationAndOneEmail()
        {
            var alice = _db.AddUser("alice@test.vn");
            var bob = _db.AddUser("bob@test.vn");
            _db.AddUser("carol@test.vn"); // không theo dõi
            var product = _db.AddProduct("https://fake.vn/a-p1.html", price: 1500000, name: "Bộ PC GVN Homework i5");
            _db.Track(alice, product);
            _db.Track(bob, product);
            var email = new RecordingEmail();

            await new NotificationService(_db.Context, email, NullLogger<NotificationService>.Instance).ProcessDelistedNotificationsAsync(product);
            await _db.Context.SaveChangesAsync(); // bên gọi (job giá) lưu

            var notifications = _db.Fresh().Notification.ToList();
            Assert.Equal(2, notifications.Count);
            Assert.All(notifications, n =>
            {
                Assert.Equal(product.ProductId, n.ProductId);
                // Dữ liệu để giao diện tự dịch + câu tiếng Anh dự phòng
                Assert.Equal(Models.Entities.NotificationTypes.Delisted, n.Type);
                using var data = System.Text.Json.JsonDocument.Parse(n.Data);
                Assert.Equal("Bộ PC GVN Homework i5", data.RootElement.GetProperty("productName").GetString());
                Assert.Equal(1500000m, data.RootElement.GetProperty("lastPrice").GetDecimal());
                Assert.Equal("Product no longer available", n.Title);
                Assert.Contains("1,500,000 ₫", n.Content);
            });
            Assert.Equal(new[] { "alice@test.vn", "bob@test.vn" }, email.Delisted.Select(e => e.Email).OrderBy(e => e));
            Assert.All(email.Delisted, e => Assert.Equal(1500000m, e.LastPrice));
        }

        [Fact]
        public async Task NoTrackers_NothingSent()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            var email = new RecordingEmail();

            await new NotificationService(_db.Context, email, NullLogger<NotificationService>.Instance).ProcessDelistedNotificationsAsync(product);
            await _db.Context.SaveChangesAsync();

            Assert.Empty(_db.Fresh().Notification);
            Assert.Empty(email.Delisted);
        }
    }
}

namespace ePriTrackerBackend.Tests.Services
{
    using ePriTrackerBackend.Models.Entities;
    using ePriTrackerBackend.Services.Notification;
    using ePriTrackerBackend.Tests.Helpers;
    using Microsoft.Extensions.Logging.Abstractions;

    /// <summary>Thông báo chạm giá mục tiêu: dữ liệu có cấu trúc + báo realtime sau khi lưu.</summary>
    public class TargetReachedNotificationTests : IDisposable
    {
        private readonly TestDb _db = new();
        public void Dispose() => _db.Dispose();

        private sealed class NoEmail : IEmailService
        {
            public Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink) => Task.CompletedTask;
            public Task SendProductDelistedEmailAsync(string recipientEmail, string recipientName, string productName, decimal? lastPrice, string productLink) => Task.CompletedTask;
        }

        private sealed class RecordingPublisher : INotificationPublisher
        {
            public List<Guid> Users { get; } = new();
            public int Calls { get; private set; }
            public Task NotifyChangedAsync(IReadOnlyCollection<Guid> userIds) { Calls++; Users.AddRange(userIds); return Task.CompletedTask; }
        }

        [Fact]
        public async Task TargetReached_StoresTypeAndData_AndPublishesOnlyAfterSave()
        {
            var alice = _db.AddUser("alice@test.vn");
            var bob = _db.AddUser("bob@test.vn");
            var product = _db.AddProduct("https://fake.vn/a-p1.html", price: 1000000, name: "Chuột G502");
            _db.Track(alice, product, targetPrice: 900000);
            _db.Track(bob, product, targetPrice: 500000); // chưa chạm mục tiêu
            var publisher = new RecordingPublisher();
            var service = new NotificationService(_db.Context, new NoEmail(), NullLogger<NotificationService>.Instance, publisher);

            await service.ProcessPriceDropNotificationsAsync(product, 850000);
            Assert.Equal(0, publisher.Calls); // chưa lưu → chưa báo
            await _db.Context.SaveChangesAsync();
            await service.PublishPendingAsync();

            var n = Assert.Single(_db.Fresh().Notification);
            Assert.Equal(alice.UserId, n.UserId);
            Assert.Equal(NotificationTypes.TargetReached, n.Type);
            using var data = System.Text.Json.JsonDocument.Parse(n.Data);
            Assert.Equal("Chuột G502", data.RootElement.GetProperty("productName").GetString());
            Assert.Equal(850000m, data.RootElement.GetProperty("price").GetDecimal());
            Assert.Equal(900000m, data.RootElement.GetProperty("targetPrice").GetDecimal());
            Assert.Equal("Price target reached", n.Title);
            Assert.Equal("Chuột G502 dropped to 850,000 ₫ (your target: 900,000 ₫).", n.Content);

            Assert.Equal(new[] { alice.UserId }, publisher.Users);
            await service.PublishPendingAsync(); // đã báo rồi → không báo lại
            Assert.Equal(1, publisher.Calls);
        }
    }
}
