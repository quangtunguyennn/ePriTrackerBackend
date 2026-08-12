using System.ComponentModel.DataAnnotations;

namespace ePriTrackerBackend.Models.Entities
{
    public class Event
    {
        [Key]
        public Guid EventId { get; set; } = Guid.NewGuid(); // Khóa chính nội bộ của hệ thống

        public int TikiEventId { get; set; } // ID gốc lấy từ JSON của Tiki để tránh lưu trùng lặp

        [Required]
        [MaxLength(255)]
        public string Title { get; set; } = string.Empty; // Tên sự kiện (được bóc tách từ URL)

        [Required]
        public string ImageUrl { get; set; } = string.Empty; // Link ảnh banner

        [Required]
        public string EventLink { get; set; } = string.Empty; // Link dẫn tới trang sự kiện trên Tiki

        public string? Content { get; set; } // Nội dung phụ (có thể null)

        [MaxLength(100)]
        public string? GroupZone { get; set; } // Nhóm hiển thị (VD: banner_carousel_2_8)

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow; // Thời gian cào dữ liệu
    }
}
