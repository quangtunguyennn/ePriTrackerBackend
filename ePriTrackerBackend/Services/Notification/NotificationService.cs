using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Services.Notification
{
    public interface INotificationService
    {
        Task ProcessPriceDropNotificationsAsync(ePriTrackerContext dbContext, Product product, decimal newPrice);
    }

    public class NotificationService : INotificationService
    {
        private readonly IEmailService _emailService;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(IEmailService emailService, ILogger<NotificationService> logger)
        {
            _emailService = emailService;
            _logger = logger;
        }

        public async Task ProcessPriceDropNotificationsAsync(ePriTrackerContext dbContext, Product product, decimal newPrice)
        {
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
    }
}