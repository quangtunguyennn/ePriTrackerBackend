using System.ComponentModel.DataAnnotations;

namespace ePriTrackerBackend.Models.Entities
{
    public class Product
    {
        [Key]
        public Guid ProductId { get; set; }
        [Required]
        public string ProductName { get; set; }
        [Required]
        public string ProductLink { get; set; }
        [Required]
        public string ImageURL { get; set; }
        public string? Description { get; set; }
        [Required]
        public Decimal InitialPrice { get; set; }
        public Decimal? LatestPrice { get; set; }
        public DateTime? AddedAt { get; set; }
        public DateTimeOffset? LastUpdatedAt { get; set; }
    }
}
