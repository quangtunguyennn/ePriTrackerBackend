using System.Security.Claims;
using ePriTrackerBackend.Controllers;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Services.Notification;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Tests.Services
{
    public class NotificationInboxServiceTests : IDisposable
    {
        private static readonly DateTimeOffset T0 = new(2026, 11, 21, 3, 0, 0, TimeSpan.Zero);

        private readonly TestDb _db = new();
        private readonly RecordingPublisher _publisher = new();
        private readonly User _alice;
        private readonly User _bob;
        private readonly Product _product;

        private sealed class RecordingPublisher : INotificationPublisher
        {
            public List<Guid> Users { get; } = new();
            public Task NotifyChangedAsync(IReadOnlyCollection<Guid> userIds) { Users.AddRange(userIds); return Task.CompletedTask; }
        }

        public NotificationInboxServiceTests()
        {
            _alice = _db.AddUser("alice@test.vn");
            _bob = _db.AddUser("bob@test.vn");
            _product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Track(_alice, _product);
        }

        public void Dispose() => _db.Dispose();

        private NotificationInboxService Service() => new(new NotificationRepository(_db.Context), new EfUnitOfWork(_db.Context), _publisher);

        private Notification Add(User user, int minutesAfterT0, bool read = false, Guid? productId = null, string? type = NotificationTypes.TargetReached, string? data = "{\"productName\":\"SP\"}")
        {
            var n = new Notification
            {
                NotificationId = Guid.NewGuid(), UserId = user.UserId, ProductId = productId ?? _product.ProductId, Type = type, Data = data,
                Title = "Price target reached", Content = "SP dropped", IsRead = read, CreatedAt = T0.AddMinutes(minutesAfterT0)
            };
            _db.Context.Notification.Add(n);
            _db.Context.SaveChanges();
            return n;
        }

        [Fact]
        public async Task Page_OnlyOwnNotifications_NewestFirst_WithUnreadCountAndHasMore()
        {
            for (int i = 0; i < 5; i++) Add(_alice, i, read: i == 0);
            Add(_bob, 100);

            var page = await Service().GetPageAsync(_alice.UserId, take: 3, before: null);

            Assert.Equal(new[] { 4, 3, 2 }, page.Items.Select(n => (int)(n.CreatedAt - T0).TotalMinutes));
            Assert.Equal(4, page.UnreadCount);
            Assert.True(page.HasMore);
            Assert.All(page.Items, n => Assert.True(n.ProductTracked));
            Assert.Equal("SP", page.Items[0].Data!.Value.GetProperty("productName").GetString());

            var next = await Service().GetPageAsync(_alice.UserId, take: 3, before: page.Items[^1].CreatedAt);
            Assert.Equal(new[] { 1, 0 }, next.Items.Select(n => (int)(n.CreatedAt - T0).TotalMinutes));
            Assert.False(next.HasMore);
        }

        [Fact]
        public async Task UntrackedProduct_AndBrokenOrMissingData_AreFlagged()
        {
            var other = _db.AddProduct("https://fake.vn/b-p2.html"); // alice không theo dõi
            Add(_alice, 1, productId: other.ProductId, data: "not json");
            Add(_alice, 2, type: null, data: null); // thông báo cũ

            var items = (await Service().GetPageAsync(_alice.UserId, 20, null)).Items;

            Assert.Null(items[0].Type);
            Assert.Null(items[0].Data);
            Assert.False(items[1].ProductTracked);
            Assert.Null(items[1].Data);
        }

        [Fact]
        public async Task MarkRead_OwnOnly_AndNotifiesOtherTabs()
        {
            var mine = Add(_alice, 1);
            var bobs = Add(_bob, 2);

            Assert.True(await Service().MarkReadAsync(_alice.UserId, mine.NotificationId));
            Assert.False(await Service().MarkReadAsync(_alice.UserId, bobs.NotificationId)); // của người khác → như không tồn tại

            using var fresh = _db.Fresh();
            Assert.True(fresh.Notification.Single(n => n.NotificationId == mine.NotificationId).IsRead);
            Assert.False(fresh.Notification.Single(n => n.NotificationId == bobs.NotificationId).IsRead);
            Assert.Equal(new[] { _alice.UserId }, _publisher.Users);
        }

        [Fact]
        public async Task MarkAllRead_OnlyTouchesOwnNotifications()
        {
            Add(_alice, 1);
            Add(_alice, 2);
            Add(_bob, 3);

            await Service().MarkAllReadAsync(_alice.UserId);

            Assert.Equal(0, await Service().GetUnreadCountAsync(_alice.UserId));
            Assert.Equal(1, await Service().GetUnreadCountAsync(_bob.UserId));
        }

        [Fact]
        public async Task Controller_UsesUserIdFromToken()
        {
            Add(_alice, 1);
            var controller = new NotificationController(Service())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, _alice.UserId.ToString()) }, "test"))
                    }
                }
            };

            var ok = Assert.IsType<OkObjectResult>(await controller.GetNotifications());
            Assert.Single(Assert.IsType<NotificationPageDTO>(ok.Value).Items);
            Assert.IsType<NotFoundObjectResult>(await controller.MarkRead(Guid.NewGuid(), default));

            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.IsType<UnauthorizedResult>(await controller.GetUnreadCount(default));
        }
    }
}
