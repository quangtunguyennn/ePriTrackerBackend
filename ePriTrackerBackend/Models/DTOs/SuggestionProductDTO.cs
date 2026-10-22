namespace ePriTrackerBackend.Models.DTOs
{
    public class SuggestionProductDTO
    {
        public string ProductName { get; set; }
        public decimal Price { get; set; }
        public string ImageURL { get; set; }
        public string ProductLink { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }

        /// <summary>Sàn bán sản phẩm gợi ý (vd "Tiki"), suy ra từ link. Null nếu không nhận ra sàn.</summary>
        public string Platform { get; set; }
    }
}
