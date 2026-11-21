using System.Text.Json;

namespace ePriTrackerBackend.Models.DTOs
{
    /// <summary>Một thông báo trong chuông. FE dựng câu từ <see cref="Type"/> + <see cref="Data"/>; không biết loại thì dùng Title/Content.</summary>
    public class NotificationDTO
    {
        public Guid NotificationId { get; set; }
        public string Type { get; set; }

        /// <summary>Tham số (JSON object) – null với thông báo cũ.</summary>
        public JsonElement? Data { get; set; }

        public string Title { get; set; }
        public string Content { get; set; }
        public Guid? ProductId { get; set; }

        /// <summary>Người dùng còn theo dõi sản phẩm này không (false → FE không mở trang sản phẩm).</summary>
        public bool ProductTracked { get; set; }

        public bool IsRead { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>GET /api/notifications</summary>
    public class NotificationPageDTO
    {
        public List<NotificationDTO> Items { get; set; } = new();
        public int UnreadCount { get; set; }

        /// <summary>Còn thông báo cũ hơn – gọi lại với before = CreatedAt của dòng cuối.</summary>
        public bool HasMore { get; set; }
    }
}
