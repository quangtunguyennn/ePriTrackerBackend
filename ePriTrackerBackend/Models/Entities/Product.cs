using System.ComponentModel.DataAnnotations;

namespace ePriTrackerBackend.Models.Entities
{
    public class Product
    {
        [Key]
        public Guid ProductId { get; set; }
        public string ProductName { get; set; }
        public string ProductLink { get; set; }
        public string ImageURL { get; set; }
        public string? Description { get; set; }
        public Decimal InitialPrice { get; set; }
        public DateTime? AddedAt { get; set; }
    }
}
