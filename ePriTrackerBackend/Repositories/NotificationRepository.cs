using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Repositories
{
    public interface INotificationRepository
    {
        /// <summary>Mọi thông báo của một người (không theo dõi thay đổi). Sắp xếp/phân trang ở tầng trên (SQLite của test không so sánh được DateTimeOffset).</summary>
        Task<List<Notification>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default);

        Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>Thông báo của đúng người này (có theo dõi để sửa); null nếu không có hoặc của người khác.</summary>
        Task<Notification> FindForUserAsync(Guid notificationId, Guid userId, CancellationToken cancellationToken = default);

        /// <returns>Số thông báo vừa được đánh dấu đã đọc.</returns>
        Task<int> MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>Các sản phẩm người này đang theo dõi – để biết thông báo nào còn mở được trang sản phẩm.</summary>
        Task<HashSet<Guid>> GetTrackedProductIdsAsync(Guid userId, CancellationToken cancellationToken = default);
    }

    public class NotificationRepository : INotificationRepository
    {
        private readonly ePriTrackerContext _context;

        public NotificationRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<List<Notification>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
            => _context.Notification.AsNoTracking().Where(n => n.UserId == userId).ToListAsync(cancellationToken);

        public Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken = default)
            => _context.Notification.CountAsync(n => n.UserId == userId && !n.IsRead, cancellationToken);

        public Task<Notification> FindForUserAsync(Guid notificationId, Guid userId, CancellationToken cancellationToken = default)
            => _context.Notification.FirstOrDefaultAsync(n => n.NotificationId == notificationId && n.UserId == userId, cancellationToken);

        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
            => _context.Notification
                .Where(n => n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), cancellationToken);

        public async Task<HashSet<Guid>> GetTrackedProductIdsAsync(Guid userId, CancellationToken cancellationToken = default)
            => (await _context.Item.AsNoTracking().Where(i => i.UserId == userId).Select(i => i.ProductId).ToListAsync(cancellationToken)).ToHashSet();
    }
}
