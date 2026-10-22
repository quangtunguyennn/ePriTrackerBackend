namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>Sản phẩm trả về từ API tìm kiếm / danh mục của CellphoneS.</summary>
    public sealed class CellphoneSApiProduct
    {
        public long ProductId { get; init; }
        public string Name { get; init; }
        /// <summary>Đường dẫn tương đối, vd "laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.html".</summary>
        public string UrlPath { get; init; }
        /// <summary>Giá bán đang hiển thị. 0 = sàn không bán (hết hàng / ngừng kinh doanh).</summary>
        public decimal DisplayPrice { get; init; }
        /// <summary>Giá gốc (trước giảm).</summary>
        public decimal RootPrice { get; init; }
        /// <summary>Đường dẫn ảnh tương đối, vd "/t/e/text_d_i_9_23.png".</summary>
        public string Thumbnail { get; init; }
        /// <summary>"Mới" / "Cũ". Null khi API không trả.</summary>
        public string Condition { get; init; }
    }

    public sealed class CellphoneSFlashSaleProgram
    {
        public int Id { get; init; }
        /// <summary>Tên khung, vd "20-22h 24/10".</summary>
        public string Title { get; init; }
        public bool IsActive { get; init; }
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset EndsAt { get; init; }
    }

    public sealed class CellphoneSFlashSaleGroup
    {
        public string Ud { get; init; }
        /// <summary>Tên chương trình, vd "Flashsale Giá Sốc 2h".</summary>
        public string Title { get; init; }
        public IReadOnlyList<CellphoneSFlashSaleProgram> Programs { get; init; } = Array.Empty<CellphoneSFlashSaleProgram>();
    }

    public sealed class CellphoneSFlashSaleSlot
    {
        public long ProductId { get; init; }
        public string Name { get; init; }
        public string Thumbnail { get; init; }
        public bool IsActive { get; init; }
        public int Sold { get; init; }
        public int MaxSlot { get; init; }
        public decimal NormalPrice { get; init; }
        public decimal SpecialPrice { get; init; }
        /// <summary>Giá flash sale. 0 trước giờ mở bán (CellphoneS giấu giá cho tới khi khung bắt đầu).</summary>
        public decimal FlashPrice { get; init; }
    }
}
