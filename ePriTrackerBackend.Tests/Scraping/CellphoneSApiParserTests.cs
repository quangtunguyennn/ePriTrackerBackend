using System.Text.Json;
using ePriTrackerBackend.Scraping.CellphoneS;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>Dữ liệu mẫu rút gọn từ phản hồi thật của api.cellphones.com.vn (khảo sát 22/10/2026).</summary>
    public class CellphoneSApiParserTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void QuickSearch_ReadsProducts_SkipsItemsWithoutUrl()
        {
            var products = CellphoneSApiParser.ParseQuickSearch(Json("""
                {"data":{"quick_search":{"products":[
                  {"product_id":131954,"name":"Laptop ASUS TUF Gaming A16 FA607NUQ-RL007W","product_condition":"Mới","url_path":"laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.html","thumbnail":"/t/e/text_d_i_9_23.png","display_price":35990000,"display_root_price":44490000},
                  {"product_id":2,"name":"Không link","display_price":1}
                ]}}}
                """));

            var p = Assert.Single(products);
            Assert.Equal(131954, p.ProductId);
            Assert.Equal("Mới", p.Condition);
            Assert.Equal(35990000m, p.DisplayPrice);
            Assert.Equal(44490000m, p.RootPrice);
            Assert.Equal("laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.html", p.UrlPath);
        }

        [Fact]
        public void GraphQlErrors_Throw()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => CellphoneSApiParser.ParseQuickSearch(Json(
                """{"errors":[{"message":"Unable to process request","extensions":{"code":"GRAPHQL_VALIDATION_FAILED"}}],"data":null}""")));
            Assert.Contains("GRAPHQL_VALIDATION_FAILED", ex.Message);
        }

        [Fact]
        public void CatalogProducts_ReadsGeneralAndFilterable()
        {
            var products = CellphoneSApiParser.ParseCatalogProducts(Json("""
                {"data":{"products":[
                  {"general":{"product_id":110405,"name":"Máy hút bụi cầm tay thông minh Dreame H12 Flexreach","url_path":"may-hut-bui-cam-tay-dreame-h12-flexreach.html"},
                   "filterable":{"display_price":6490000,"display_root_price":12990000,"thumbnail":"/m/a/may-hut-bui.jpg"}},
                  {"general":{"product_id":0,"url_path":"x.html"}}
                ]}}
                """));

            var p = Assert.Single(products);
            Assert.Equal(110405, p.ProductId);
            Assert.Equal(6490000m, p.DisplayPrice);
            Assert.Equal("/m/a/may-hut-bui.jpg", p.Thumbnail);
        }

        [Theory]
        [InlineData("""{"data":{"dynamic_config":{"value_json":{"flashSaleHome":{"web":[{"groupUd":"flashsale_cuoituan1_t7"},{"groupUd":"flashsale_cuoituan2_cn"},{"groupUd":"flashsale_cuoituan1_t7"}]}}}}}""")]
        [InlineData("""{"data":{"dynamic_config":{"value_json":"{\"flashSaleHome\":{\"web\":[],\"app\":[{\"groupUd\":\"flashsale_cuoituan1_t7\"},{\"groupUd\":\"flashsale_cuoituan2_cn\"}]}}"}}}""")]
        public void HomeFlashSaleConfig_ObjectOrString_WebThenApp_Distinct(string json)
        {
            Assert.Equal(new[] { "flashsale_cuoituan1_t7", "flashsale_cuoituan2_cn" }, CellphoneSApiParser.ParseHomeFlashSaleGroupUds(Json(json)));
        }

        [Fact]
        public void FlashSaleGroup_ReadsPrograms_WithOffsets()
        {
            var group = CellphoneSApiParser.ParseFlashSaleGroup(Json("""
                {"data":{"flash_sale_groups":{"id":44,"ud":"flashsale_cuoituan1_t7","title_desktop":"Flashsale Giá Sốc 2h","list_program":[
                  {"id":460,"title":"12-14h 24/10","is_active":true,"started_at":"2026-10-24T12:00:00+07:00","ended_at":"2026-10-24T14:00:00+07:00"},
                  {"id":461,"title":"20-22h 24/10","is_active":false,"started_at":"2026-10-24T20:00:00+07:00","ended_at":"2026-10-24T22:00:00+07:00"},
                  {"id":462,"title":"thiếu giờ"}
                ]}}}
                """));

            Assert.Equal("Flashsale Giá Sốc 2h", group.Title);
            Assert.Equal(2, group.Programs.Count);
            Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(7)), group.Programs[0].StartsAt);
            Assert.True(group.Programs[0].IsActive);
            Assert.False(group.Programs[1].IsActive);
        }

        [Fact]
        public void FlashSaleSlots_ReadsPricesAndQuota()
        {
            var slots = CellphoneSApiParser.ParseFlashSaleSlots(Json("""
                {"data":{"flash_sale_programs":[{"id":461,"flash_sale_slots":[
                  {"product_id":42337,"max_slot":10,"sold":3,"is_active":true,"product_name":"OPPO Find X5 Pro 5G 12GB 256GB ","product_thumbnail":"/d/o/download.png",
                   "flash_sale_prices":[{"province_id":30,"product_normal_price":15990000,"product_special_price":12990000,"product_flash_sale_price":11990000,"hide_price":"1?.??0.000"}]}
                ]}]}}
                """));

            var s = Assert.Single(slots);
            Assert.Equal("OPPO Find X5 Pro 5G 12GB 256GB", s.Name);
            Assert.Equal(11990000m, s.FlashPrice);
            Assert.Equal(12990000m, s.SpecialPrice);
            Assert.Equal(15990000m, s.NormalPrice);
            Assert.Equal(3, s.Sold);
            Assert.Equal(10, s.MaxSlot);
        }

        [Theory]
        [InlineData("""{"data":{"url_info":{"product_id":131954,"type":"product"}}}""", 131954L)]
        [InlineData("""{"data":{"url_info":{"product_id":0,"type":"category"}}}""", null)]
        [InlineData("""{"data":{"url_info":null}}""", null)]
        public void UrlInfo_OnlyProducts(string json, long? expected)
        {
            Assert.Equal(expected, CellphoneSApiParser.ParseUrlInfoProductId(Json(json)));
        }

        [Theory]
        [InlineData("/t/e/a.png", "https://cdn2.cellphones.com.vn/358x/media/catalog/product/t/e/a.png")]
        [InlineData("t/e/a.png", "https://cdn2.cellphones.com.vn/358x/media/catalog/product/t/e/a.png")]
        [InlineData("https://cdn/a.png", "https://cdn/a.png")]
        [InlineData("", null)]
        public void ImageUrl_RelativePathsUnderCatalog(string thumbnail, string? expected)
        {
            Assert.Equal(expected, CellphoneSApiParser.ImageUrl(thumbnail));
        }
    }
}
