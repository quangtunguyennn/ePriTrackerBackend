using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ePriTrackerBackend.Models.Entities
{
    public class Notification
    {
        [Key]
        public Guid NotificationId { get; set; }

        [Required]
        public Guid UserId { get; set; }

        /// <summary>
        /// Liên kết đến sản phẩm để khi user click vào thông báo sẽ mở ra trang chi tiết.
        /// (Có thể null nếu sau này bạn làm tính năng thông báo chung từ hệ thống)
        /// </summary>
        public Guid? ProductId { get; set; }

        [Required]
        [MaxLength(255)]
        public string Title { get; set; }

        [Required]
        public string Content { get; set; }

        /// <summary>
        /// Trạng thái đã đọc hay chưa (để hiển thị chấm đỏ trên quả chuông)
        /// </summary>
        public bool IsRead { get; set; } = false;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}