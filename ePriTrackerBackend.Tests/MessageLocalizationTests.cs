using System.Globalization;
using ePriTrackerBackend.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ePriTrackerBackend.Tests
{
    /// <summary>Câu "message" trả cho FE được dịch theo ngôn ngữ request (khoá = câu tiếng Anh, bản dịch trong Resources/*.resx thật).</summary>
    public class MessageLocalizationTests
    {
        private static readonly IStringLocalizer Localizer =
            new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance)
                .Create(typeof(SharedMessages));

        private static T WithUiCulture<T>(string culture, Func<T> action)
        {
            var previous = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            try { return action(); }
            finally { CultureInfo.CurrentUICulture = previous; }
        }

        [Theory]
        [InlineData("Product not found.", "Không tìm thấy sản phẩm.")]
        [InlineData("Invalid product link or unsupported platform.", "Link sản phẩm không hợp lệ hoặc sàn chưa được hỗ trợ.")]
        [InlineData("This event's products are still loading from the platform. Please try again in a few minutes.", "Sản phẩm của sự kiện đang được tải từ sàn, vui lòng thử lại sau ít phút.")]
        public void Vietnamese_TranslatesKnownMessages(string english, string vietnamese)
        {
            Assert.Equal(vietnamese, WithUiCulture("vi", () => MessageTranslator.Translate(Localizer, english)));
        }

        [Fact]
        public void English_KeepsOriginal()
        {
            Assert.Equal("Product not found.", WithUiCulture("en", () => MessageTranslator.Translate(Localizer, "Product not found.")));
        }

        [Fact]
        public void CompoundMessage_TranslatedPerSentence_WithNumberTemplate()
        {
            // EventController ghép: câu kết quả + ghi chú số sản phẩm
            string result = WithUiCulture("vi", () => MessageTranslator.Translate(Localizer, "Event published successfully! (5 products)"));
            Assert.Equal("Đã đăng sự kiện thành công! (5 sản phẩm)", result);

            string withNote = WithUiCulture("vi", () => MessageTranslator.Translate(Localizer,
                "Event published successfully! Loading products from Tiki, check the product count again in a few minutes."));
            Assert.Equal("Đã đăng sự kiện thành công! Đang tải sản phẩm từ Tiki, kiểm tra lại số sản phẩm sau ít phút.", withNote);
        }

        [Fact]
        public void UnknownOrDynamicMessage_StaysEnglish()
        {
            Assert.Equal("Tiki returned: {\"data\":[]}", WithUiCulture("vi", () => MessageTranslator.Translate(Localizer, "Tiki returned: {\"data\":[]}")));
            Assert.Null(MessageTranslator.Translate(Localizer, null));
        }

        [Fact]
        public void Filter_LocalizesAnonymousMessage_KeepsOtherFields()
        {
            var localized = WithUiCulture("vi", () => LocalizeMessageFilter.Localize(new { message = "Event published successfully! (3 products)", productCount = 3 }, Localizer));

            var dict = Assert.IsType<Dictionary<string, object>>(localized);
            Assert.Equal(new[] { "message", "productCount" }, dict.Keys);
            Assert.Equal("Đã đăng sự kiện thành công! (3 sản phẩm)", dict["message"]);
            Assert.Equal(3, dict["productCount"]);
        }

        private sealed class MessageDto { public string message { get; set; } = "Product not found."; }

        [Fact]
        public void Filter_LeavesDtosAndAnonymousWithoutMessageUntouched()
        {
            var dto = new MessageDto();
            var count = new { count = 5 };

            Assert.Same(dto, WithUiCulture("vi", () => LocalizeMessageFilter.Localize(dto, Localizer)));
            Assert.Same(count, WithUiCulture("vi", () => LocalizeMessageFilter.Localize(count, Localizer)));
            Assert.Equal("Product not found.", dto.message);
        }
    }
}
