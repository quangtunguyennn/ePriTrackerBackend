using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ePriTrackerBackend.Models.Entities
{
    public class Item
    {
        [Key]
        public Guid ItemId { get; set; }
        public Guid UserId { get; set; }
        [JsonIgnore]
        public User? User { get; set; }
        public Guid ProductId { get; set; }
        [JsonIgnore]
        public Product? Product { get; set; }
    }
}
