using ePriTrackerBackend.Services.Suggestions;

namespace ePriTrackerBackend.Tests.Suggestions
{
    /// <summary>
    /// Thuật toán gợi ý sau khi gộp (giai đoạn 2). Expected của keyword/similarity/bỏ dấu GIỮ NGUYÊN từ
    /// characterization test của bản job (SuggestionCrawlService, chụp ngày 17/10/2026) – chứng minh tách class không đổi hành vi.
    /// </summary>
    public class SuggestionMatcherTests
    {
        private readonly ISuggestionMatcher _matcher = new SuggestionMatcher();

        [Theory]
        [InlineData("Điện thoại Apple iPhone 15 Pro Max 256GB - Chính hãng VN/A", "điện thoại apple iphone 15")]
        [InlineData("Tai nghe Bluetooth Sony WH-1000XM5 (Hàng chính hãng)", "tai nghe bluetooth sony wh")]
        [InlineData("Sách - Đắc Nhân Tâm (Tái bản)", "sách đắc nhân tâm tái")]
        [InlineData("Nồi chiên không dầu Philips HD9200/90 4.1L", "nồi chiên không dầu philips")]
        [InlineData("Laptop ASUS Vivobook 15 X1504ZA i5 1235U/8GB/512GB", "laptop asus vivobook 15 x1504za")]
        [InlineData("Ốp lưng iPhone 15 Pro Max silicon trong suốt", "ốp lưng iphone 15 pro")]
        [InlineData("Sữa bột Fonterra Anlene Gold 800g [Mới 100%]", "sữa bột fonterra anlene gold")]
        [InlineData("TV", "tv")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("---", "")]
        // Bug 6b (đã sửa): trước đây trả "" vì phần trước dấu "-" chỉ có từ quảng cáo → giờ dùng cả tên
        [InlineData("Freeship - Bàn phím cơ AKKO 3068B Plus Chính Hãng", "bàn phím cơ akko 3068b")]
        [InlineData("Chính hãng - Nồi chiên không dầu Philips", "nồi chiên không dầu philips")]
        public void ExtractSearchKeyword(string? productName, string expected)
        {
            Assert.Equal(expected, _matcher.ExtractSearchKeyword(productName!));
        }

        [Theory]
        [InlineData("Điện thoại Apple iPhone 15 Pro Max 256GB - Chính hãng VN/A", "Điện thoại iPhone 15 128GB", true)]
        [InlineData("Ốp lưng iPhone 15 Pro Max silicon trong suốt", "Ốp lưng iPhone 15 dẻo", true)]
        [InlineData("Tai nghe Bluetooth Sony WH-1000XM5 (Hàng chính hãng)", "Tai nghe Sony WH-1000XM4", true)]
        [InlineData("Sách - Đắc Nhân Tâm (Tái bản)", "Sách Đắc Nhân Tâm bản đặc biệt", true)]
        [InlineData("Nồi chiên không dầu Philips HD9200/90 4.1L", "Nồi chiên không dầu Lock&Lock 5.2L", true)]
        [InlineData("Điện thoại Apple iPhone 15 Pro Max 256GB - Chính hãng VN/A", "Ốp lưng iPhone 15 Pro Max", false)]
        [InlineData("Điện thoại Apple iPhone 15 Pro Max 256GB - Chính hãng VN/A", "Samsung Galaxy S24 Ultra", false)]
        [InlineData("Tai nghe Bluetooth Sony WH-1000XM5 (Hàng chính hãng)", "Loa Bluetooth Sony SRS-XB13", false)]
        [InlineData("Laptop ASUS Vivobook 15 X1504ZA i5 1235U/8GB/512GB", "Balo laptop 15.6 inch", false)]
        [InlineData("Máy lọc không khí Xiaomi 4 Lite", "Đế tản nhiệt laptop", false)]
        [InlineData("", "abc", false)]
        [InlineData("abc", null, false)]
        // Bug 6c (đã sửa): so khớp ≥ 50% số từ của từ khoá thay vì chỉ từ đầu tiên
        [InlineData("Điện thoại Apple iPhone 15 Pro Max 256GB - Chính hãng VN/A", "Apple iPhone 15 Pro Max 512GB", true)]  // trước: bỏ sót
        [InlineData("Sách - Đắc Nhân Tâm (Tái bản)", "Đắc Nhân Tâm - bìa cứng", true)]                                         // trước: bỏ sót
        [InlineData("Nồi chiên không dầu Philips HD9200/90 4.1L", "Nồi cơm điện Philips HD3132", false)]                       // trước: nhận nhầm
        [InlineData("Ổ cứng SSD Samsung 970", "Ổ cắm điện Điện Quang", false)]                                                // trước: nhận nhầm
        [InlineData("Laptop ASUS Vivobook 15 X1504ZA i5 1235U/8GB/512GB", "Laptop Dell Inspiron 15 3520", false)]              // đánh đổi đã duyệt: khác hãng bị loại
        [InlineData("Laptop ASUS Vivobook 15 X1504ZA i5 1235U/8GB/512GB", "Laptop ASUS Vivobook 14 X1404ZA", true)]
        [InlineData("TV", "Smart TV Samsung 43 inch", true)]
        // Bug 6a (đã sửa): "de tan nhiet" trong blacklist trước đây còn dấu nên không bao giờ khớp
        [InlineData("Laptop ASUS Vivobook 15 X1504ZA", "Đế tản nhiệt Laptop ASUS Vivobook 15", false)]
        public void IsSimilarProduct(string? originalName, string? candidateName, bool expected)
        {
            Assert.Equal(expected, _matcher.IsSimilarProduct(originalName!, candidateName!));
        }

        [Theory]
        [InlineData(500, 1000, true)]    // 50%
        [InlineData(1000, 1000, true)]   // bằng giá gốc
        [InlineData(400, 1000, true)]    // đúng ngưỡng 40% (bản job dùng >=)
        [InlineData(399, 1000, false)]   // dưới ngưỡng
        [InlineData(1001, 1000, false)]  // đắt hơn
        [InlineData(0, 1000, false)]     // không có giá
        [InlineData(0, 0, true)]         // chưa biết giá gốc → không lọc
        [InlineData(999999, 0, true)]
        public void IsPriceAcceptable(decimal candidatePrice, decimal referencePrice, bool expected)
        {
            Assert.Equal(expected, _matcher.IsPriceAcceptable(candidatePrice, referencePrice));
        }

        [Theory]
        [InlineData(1000, 800d, 1000)]  // ưu tiên giá khởi điểm
        [InlineData(0, 800d, 800)]      // chưa có giá khởi điểm → giá mới nhất
        [InlineData(0, null, 0)]
        public void GetReferencePrice(decimal initialPrice, double? latestPrice, decimal expected)
        {
            Assert.Equal(expected, _matcher.GetReferencePrice(initialPrice, (decimal?)latestPrice));
        }

        [Theory]
        [InlineData("Điện thoại Đắc Nhân Tâm", "Dien thoai Dac Nhan Tam")]
        [InlineData("Ốp lưng KÍNH CƯỜNG LỰC", "Op lung KINH CUONG LUC")]
        [InlineData("abc 123", "abc 123")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void VietnameseText_RemoveDiacritics(string? input, string? expected)
        {
            Assert.Equal(expected, VietnameseText.RemoveDiacritics(input!));
        }
    }
}
