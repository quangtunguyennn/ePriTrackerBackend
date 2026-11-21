using System.Globalization;
using System.Text.Json;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Services.Notification
{
    public interface INotificationService
    {
        /// <summary>Thêm thông báo vào DbContext dùng chung (scoped) – bên gọi lưu bằng IUnitOfWork rồi gọi <see cref="PublishPendingAsync"/>.</summary>
        Task ProcessPriceDropNotificationsAsync(Product product, decimal newPrice);

        /// <summary>
        /// Sản phẩm vừa bị đánh dấu "không còn trên sàn": mỗi người đang theo dõi nhận 1 thông báo + 1 email.
        /// Thêm vào DbContext dùng chung – bên gọi lưu bằng IUnitOfWork rồi gọi <see cref="PublishPendingAsync"/>.
        /// </summary>
        Task ProcessDelistedNotificationsAsync(Product product);

        /// <summary>
        /// Báo realtime (SignalR) cho những người vừa có thông báo mới. Gọi SAU KHI đã lưu DB, để trình duyệt tải lại thấy ngay thông báo.
        /// </summary>
        Task PublishPendingAsync();
    }

    /// <summary>
    /// Thông báo lưu DỮ LIỆU (Type + Data JSON) để giao diện tự dựng câu theo ngôn ngữ người dùng chọn;
    /// Title/Content là câu tiếng Anh dự phòng (FE gặp loại lạ, và cho các bản ghi cũ).
    /// </summary>
    public class NotificationService : INotificationService
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
        // Giữ nguyên chữ có dấu trong JSON (dễ đọc khi xem DB); FE đọc bằng JSON.parse, không chèn thẳng vào HTML
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly ePriTrackerContext _dbContext;
        private readonly IEmailService _emailService;
        private readonly ILogger<NotificationService> _logger;
        private readonly INotificationPublisher _publisher;
        private readonly HashSet<Guid> _pendingUsers = new();

        public NotificationService(ePriTrackerContext dbContext, IEmailService emailService, ILogger<NotificationService> logger,
            INotificationPublisher publisher = null)
        {
            _dbContext = dbContext;
            _emailService = emailService;
            _logger = logger;
            _publisher = publisher ?? NullNotificationPublisher.Instance;
        }

        public async Task ProcessPriceDropNotificationsAsync(Product product, decimal newPrice)
        {
            var dbContext = _dbContext;

            if (newPrice <= 0) return;

            // 1. Lấy danh sách Item đạt TargetPrice, INCLUDE thêm thông tin User để lấy Email
            var eligibleItems = await dbContext.Item
                .Include(i => i.User)
                .Where(i => i.ProductId == product.ProductId
                         && i.TargetPrice.HasValue
                         && newPrice <= i.TargetPrice.Value)
                .ToListAsync();

            if (!eligibleItems.Any()) return;

            var now = DateTimeOffset.UtcNow;
            var notificationsToAdd = new List<Models.Entities.Notification>();

            foreach (var item in eligibleItems)
            {
                // LOGIC CHỐNG SPAM: Bỏ qua nếu đã thông báo và giá chưa giảm sâu hơn
                if (item.IsTargetNotified && item.LastNotifiedPrice.HasValue && newPrice >= item.LastNotifiedPrice.Value)
                {
                    continue;
                }

                // 2. Tạo bản ghi Notification (In-App Bell): dữ liệu + câu tiếng Anh dự phòng
                decimal target = item.TargetPrice.Value;
                notificationsToAdd.Add(new Models.Entities.Notification
                {
                    NotificationId = Guid.NewGuid(),
                    UserId = item.UserId,
                    ProductId = product.ProductId,
                    Type = NotificationTypes.TargetReached,
                    Data = Serialize(new { productName = product.ProductName, price = newPrice, targetPrice = target }),
                    Title = "Price target reached",
                    Content = $"{product.ProductName} dropped to {FormatPrice(newPrice)} (your target: {FormatPrice(target)}).",
                    IsRead = false,
                    CreatedAt = now
                });
                _pendingUsers.Add(item.UserId);

                // 3. Cập nhật cờ anti-spam trên bảng Item
                item.IsTargetNotified = true;
                item.LastNotifiedPrice = newPrice;
                item.LastNotifiedAt = now;
                dbContext.Item.Update(item);

                // 4. GỬI EMAIL THÔNG BÁO TRỰC TIẾP
                if (item.User != null && !string.IsNullOrEmpty(item.User.Email))
                {
                    // Chạy gửi mail không chặn tiến trình chính (Fire-and-forget hoặc Task.Run)
                    _ = _emailService.SendPriceDropEmailAsync(
                        recipientEmail: item.User.Email,
                        recipientName: $"{item.User.FirstName} {item.User.LastName}".Trim(),
                        productName: product.ProductName,
                        newPrice: newPrice,
                        targetPrice: target,
                        productLink: product.ProductLink
                    );
                }
            }

            if (notificationsToAdd.Any())
            {
                dbContext.Notification.AddRange(notificationsToAdd);
                _logger.LogInformation("Đã tạo {Count} thông báo và gửi mail cho Sản phẩm ID {ProductId}", notificationsToAdd.Count, product.ProductId);
            }
        }

        public async Task ProcessDelistedNotificationsAsync(Product product)
        {
            // Mỗi người một lần (Item trùng của cùng người – nếu có – chỉ tính một)
            var users = (await _dbContext.Item
                    .Include(i => i.User)
                    .Where(i => i.ProductId == product.ProductId && i.User != null)
                    .Select(i => i.User)
                    .ToListAsync())
                .GroupBy(u => u.UserId)
                .Select(g => g.First())
                .ToList();

            if (users.Count == 0) return;

            var now = DateTimeOffset.UtcNow;
            decimal? lastPrice = product.LatestPrice > 0 ? product.LatestPrice : null;
            string lastPriceText = lastPrice.HasValue ? FormatPrice(lastPrice.Value) : "not available";

            foreach (var user in users)
            {
                _dbContext.Notification.Add(new Models.Entities.Notification
                {
                    NotificationId = Guid.NewGuid(),
                    UserId = user.UserId,
                    ProductId = product.ProductId,
                    Type = NotificationTypes.Delisted,
                    Data = Serialize(new { productName = product.ProductName, lastPrice }),
                    Title = "Product no longer available",
                    Content = $"{product.ProductName} was removed from the platform (last price: {lastPriceText}). Price tracking has stopped – you can remove it from your list or find an alternative.",
                    IsRead = false,
                    CreatedAt = now
                });
                _pendingUsers.Add(user.UserId);

                if (!string.IsNullOrEmpty(user.Email))
                {
                    // Giống mail giảm giá: gửi không chặn job; lỗi gửi chỉ ghi log trong EmailService
                    _ = _emailService.SendProductDelistedEmailAsync(
                        recipientEmail: user.Email,
                        recipientName: $"{user.FirstName} {user.LastName}".Trim(),
                        productName: product.ProductName,
                        lastPrice: product.LatestPrice,
                        productLink: product.ProductLink);
                }
            }

            _logger.LogInformation("Đã tạo {Count} thông báo 'không còn trên sàn' cho Sản phẩm ID {ProductId}", users.Count, product.ProductId);
        }

        public async Task PublishPendingAsync()
        {
            if (_pendingUsers.Count == 0) return;

            var users = _pendingUsers.ToList();
            _pendingUsers.Clear();
            try
            {
                await _publisher.NotifyChangedAsync(users);
            }
            catch (Exception ex)
            {
                // Realtime chỉ là tiện ích: lỗi đẩy không được làm hỏng job – trình duyệt vẫn thấy khi tải lại
                _logger.LogWarning(ex, "Không đẩy được thông báo realtime tới {Count} người dùng.", users.Count);
            }
        }

        /// <summary>"1,500,000 ₫" – định dạng tiếng Anh cho câu dự phòng.</summary>
        public static string FormatPrice(decimal price) => price.ToString("N0", English) + " ₫";

        private static string Serialize(object data) => JsonSerializer.Serialize(data, JsonOptions);
    }
}
