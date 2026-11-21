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

        /// <summary>
        /// Loại thông báo (<see cref="NotificationTypes"/>) – FE dựa vào đây + <see cref="Data"/> để dựng câu theo ngôn ngữ đang chọn.
        /// Null với thông báo rất cũ không suy được loại. Migration AddTypeAndDataToNotification.
        /// </summary>
        [MaxLength(50)]
        public string Type { get; set; }

        /// <summary>Tham số của thông báo dạng JSON (vd tên sản phẩm, giá, giá mục tiêu) – không chứa câu chữ.</summary>
        public string Data { get; set; }

        /// <summary>Câu tiếng Anh dựng sẵn – dự phòng khi FE không biết loại thông báo (và cho thông báo cũ).</summary>
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

    /// <summary>Các loại thông báo. Giữ nguyên chuỗi (lưu dưới DB, FE dùng làm khoá từ điển).</summary>
    public static class NotificationTypes
    {
        /// <summary>Giá giảm tới (hoặc dưới) giá mục tiêu của người dùng. Data: productName, price, targetPrice.</summary>
        public const string TargetReached = "TargetReached";

        /// <summary>Sàn đã gỡ sản phẩm, ngưng cập nhật giá. Data: productName, lastPrice.</summary>
        public const string Delisted = "Delisted";
    }
}
