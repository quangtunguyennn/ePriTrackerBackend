using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace ePriTrackerBackend.Models.Entities
{
    public class PriceHistory
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }
        public Guid ProductId { get; set; }
        [JsonIgnore]
        public Product? Product { get; set; }
        public decimal Price { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }
}
