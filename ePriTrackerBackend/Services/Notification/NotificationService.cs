using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Services.Notification
{
    public interface INotificationService
    {
        /// <summary>Thêm thông báo vào DbContext dùng chung (scoped) – bên gọi lưu bằng IUnitOfWork.</summary>
        Task ProcessPriceDropNotificationsAsync(Product product, decimal newPrice);

        /// <summary>
        /// Sản phẩm vừa bị đánh dấu "không còn trên sàn": mỗi người đang theo dõi nhận 1 thông báo + 1 email.
        /// Thêm vào DbContext dùng chung – bên gọi lưu bằng IUnitOfWork.
        /// </summary>
        Task ProcessDelistedNotificationsAsync(Product product);
    }

    public class NotificationService : INotificationService
    {
        private readonly ePriTrackerContext _dbContext;
        private readonly IEmailService _emailService;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(ePriTrackerContext dbContext, IEmailService emailService, ILogger<NotificationService> logger)
        {
            _dbContext = dbContext;
            _emailService = emailService;
            _logger = logger;
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

                // 2. Tạo bản ghi Notification (In-App Bell)
                string formattedPrice = newPrice.ToString("N0") + " đ";
                string formattedTarget = item.TargetPrice.Value.ToString("N0") + " đ";

                notificationsToAdd.Add(new Models.Entities.Notification
                {
                    NotificationId = Guid.NewGuid(),
                    UserId = item.UserId,
                    ProductId = product.ProductId,
                    Title = "🔥 Giá sản phẩm đã giảm!",
                    Content = $"Sản phẩm '{product.ProductName}' đã giảm xuống {formattedPrice} (mục tiêu của bạn là {formattedTarget}).",
                    IsRead = false,
                    CreatedAt = now
                });

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
                        recipientName: item.User.FirstName + item.User.LastName, // hoặc item.User.FullName
                        productName: product.ProductName,
                        newPrice: newPrice,
                        targetPrice: item.TargetPrice.Value,
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
            string lastPrice = product.LatestPrice > 0 ? product.LatestPrice.Value.ToString("N0") + " đ" : "chưa có";

            foreach (var user in users)
            {
                _dbContext.Notification.Add(new Models.Entities.Notification
                {
                    NotificationId = Guid.NewGuid(),
                    UserId = user.UserId,
                    ProductId = product.ProductId,
                    Title = "🚫 Sản phẩm không còn trên sàn",
                    Content = $"Sản phẩm '{product.ProductName}' đã bị gỡ khỏi sàn (giá cuối cùng: {lastPrice}). Hệ thống đã ngưng theo dõi giá – bạn có thể xoá khỏi danh sách hoặc tìm sản phẩm thay thế.",
                    IsRead = false,
                    CreatedAt = now
                });

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
    }
}