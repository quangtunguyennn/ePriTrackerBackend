using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ePriTrackerBackend.Models.Entities
{
    public class Event
    {
        [Key]
        public Guid EventId { get; set; } = Guid.NewGuid();

        public int TikiEventId { get; set; }

        [Required]
        [MaxLength(255)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string ImageUrl { get; set; } = string.Empty;

        [Required]
        public string EventLink { get; set; } = string.Empty;

        public string Content { get; set; }

        public bool IsPublished { get; set; } = false;

        [MaxLength(100)]
        public string GroupZone { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Sàn của sự kiện. Tiki: Admin duyệt/đăng. Sàn tự đăng (CellphoneS): job đồng bộ tự thêm/xoá, không hiện ở trang Admin.
        /// Migration gán "Tiki" cho các dòng cũ.
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Platform { get; set; } = string.Empty;

        /// <summary>Thời điểm hết hạn (null = không biết, chỉ Tiki). Hết hạn → không còn hiện ở trang người dùng.</summary>
        public DateTimeOffset? EndsAt { get; set; }

        /// <summary>
        /// true: EndsAt là giờ kết thúc thật do sàn công bố (khung flash sale) → FE hiện "Ends at ...".
        /// false: EndsAt chỉ là hạn tạm do hệ thống gia hạn mỗi lần đồng bộ (vd "Deal sốc mỗi ngày") → không hiện.
        /// </summary>
        public bool EndsAtIsExact { get; set; }

        /// <summary>
        /// Tối đa 4 ảnh sản phẩm để FE ghép lưới 2×2 – cho sự kiện không có banner riêng (CellphoneS).
        /// Null với sự kiện Tiki (dùng ImageUrl). EF lưu dạng JSON trong một cột.
        /// </summary>
        public List<string> PreviewImageUrls { get; set; }

        [NotMapped]
        public bool IsExpired { get; set; } = false;
    }
}
