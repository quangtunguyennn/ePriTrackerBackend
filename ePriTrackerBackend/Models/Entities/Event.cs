using System.ComponentModel.DataAnnotations;

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

        public string? Content { get; set; }

        public bool IsPublished { get; set; } = false;

        [MaxLength(100)]
        public string? GroupZone { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
