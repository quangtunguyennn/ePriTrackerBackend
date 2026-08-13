namespace ePriTrackerBackend.Models.Entities
{
    public class EventProduct
    {
        public Guid EventId { get; set; }
        public Event Event { get; set; } = null!;

        public Guid ProductId { get; set; }
        public Product Product { get; set; } = null!;
    }
}
