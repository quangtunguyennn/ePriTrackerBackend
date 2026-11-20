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
                Assert.Contains("không còn trên sàn", n.Title);
                Assert.Contains("1.500.000", n.Content.Replace(",", "."));
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
