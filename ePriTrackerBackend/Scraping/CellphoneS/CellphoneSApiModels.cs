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

    /// <summary>Một nhóm hot sale bày trên trang chủ (tab "BẮT NHỊP SĂN DEAL", "HOT SALE CUỐI TUẦN" – cấu hình config_hotsale_flashsale).</summary>
    public sealed class CellphoneSHotSaleGroupRef
    {
        /// <summary>Mã nhóm cho sale_off_group, vd "pk_flash_sale".</summary>
        public string GroupUd { get; init; }

        /// <summary>Tên hiển thị, vd "HOT SALE CUỐI TUẦN – Phụ kiện, PC" hoặc "BẮT NHỊP SĂN DEAL".</summary>
        public string Title { get; init; }
    }

    /// <summary>sale_off_group: nhóm sản phẩm giá giảm có thời hạn (hot sale), giá bán theo miền.</summary>
    public sealed class CellphoneSSaleOffGroup
    {
        public string Ud { get; init; }
        public string Name { get; init; }
        public DateTimeOffset? FromDate { get; init; }
        public DateTimeOffset? ToDate { get; init; }
        public IReadOnlyList<CellphoneSSaleOffItem> Items { get; init; } = Array.Empty<CellphoneSSaleOffItem>();
    }

    public sealed class CellphoneSSaleOffItem
    {
        public long ProductId { get; init; }

        /// <summary>Giá hot sale theo miền của tỉnh mặc định (TP.HCM → miền Nam), thiếu thì miền Bắc.</summary>
        public decimal SalePrice { get; init; }

        /// <summary>Ghi chú / quà kèm (thường rỗng).</summary>
        public string Note { get; init; }
    }
}
