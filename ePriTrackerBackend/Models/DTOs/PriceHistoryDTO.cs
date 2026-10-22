// Models/DTOs/PriceHistoryDTO.cs
namespace ePriTrackerBackend.Models.DTOs
{
    public class PricePointDTO
    {
        public DateTimeOffset CheckedAt { get; set; }
        public decimal Price { get; set; }
    }

    public class PriceHistoryDTO
    {
        public Guid ProductId { get; set; }
        public string ProductName { get; set; }
        public decimal? CurrentPrice { get; set; }
        public decimal? LowestPrice { get; set; }
        public decimal? HighestPrice { get; set; }
        public List<PricePointDTO> Points { get; set; } = new();
    }
}