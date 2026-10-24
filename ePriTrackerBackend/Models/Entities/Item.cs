using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ePriTrackerBackend.Models.Entities
{
    public class Item
    {
        [Key]
        public Guid ItemId { get; set; }

        [Required]
        public Guid UserId { get; set; }

        [JsonIgnore]
        public User User { get; set; }

        [Required]
        public Guid ProductId { get; set; }

        [JsonIgnore]
        public Product Product { get; set; }

        public decimal? InitialPrice { get; set; }
        public decimal? TargetPrice { get; set; }

        // ==========================================
        // CÁC TRƯỜNG CHỐNG SPAM THÔNG BÁO
        // ==========================================

        /// <summary>
        /// Mức giá gần nhất đã bắn thông báo cho user này.
        /// </summary>
        public decimal? LastNotifiedPrice { get; set; }

        /// <summary>
        /// Thời điểm gần nhất đã bắn thông báo.
        /// </summary>
        public DateTimeOffset? LastNotifiedAt { get; set; }

        /// <summary>
        /// Đã gửi thông báo khi đạt TargetPrice chưa.
        /// </summary>
        public bool IsTargetNotified { get; set; } = false;
    }
}