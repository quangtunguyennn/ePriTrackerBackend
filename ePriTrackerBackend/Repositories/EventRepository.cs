using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.DTOs;
using System.Text.Json;

namespace ePriTrackerBackend.Repositories
{
    public class EventRepository : IEventRepository
    {
        private readonly ePriTrackerContext _context;

        public EventRepository(ePriTrackerContext context)
        {
            _context = context;
        }

        public async Task<List<EventDTO>> GetCurrentTikiEvents()
        {
            var eventsList = new List<EventDTO>();
            // Đường link API chứa banner sự kiện của Tiki
            string apiUrl = "https://tka.tiki.vn/widget/api/v1/banners-group?group=banner_carousel_2_8&trackity_id=104f3c51-fe68-747d-0443-93d3bd5d35f0&_rf=rotate_by_ctr";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");

            try
            {
                var response = await client.GetAsync(apiUrl);
                if (!response.IsSuccessStatusCode) return eventsList;

                string jsonResponse = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(jsonResponse);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("data", out JsonElement dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var groupItem in dataElement.EnumerateArray())
                    {
                        if (groupItem.TryGetProperty("banners", out JsonElement bannersElement) && bannersElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in bannersElement.EnumerateArray())
                            {
                                string link = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                                if (!link.StartsWith("http") && item.TryGetProperty("url", out var urlProp))
                                {
                                    link = urlProp.GetString() ?? "";
                                }

                                string eventName = "Sự kiện Tiki";
                                if (!string.IsNullOrEmpty(link) && link.Contains("/"))
                                {
                                    try
                                    {
                                        string lastSegment = link.Split('/').Last();
                                        if (lastSegment.Contains("?")) lastSegment = lastSegment.Split('?')[0];
                                        string cleanName = lastSegment.Replace("-", " ");
                                        eventName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanName.ToLower());
                                    }
                                    catch { }
                                }

                                var tikiEvent = new EventDTO
                                {
                                    Id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0,
                                    Title = eventName,
                                    ImageUrl = item.TryGetProperty("image_url", out var imgProp) ? imgProp.GetString() ?? "" : "",
                                    EventLink = link,
                                    Content = item.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? "" : "",
                                    GroupZone = groupItem.TryGetProperty("group", out var groupProp) ? groupProp.GetString() ?? "Unknown" : "Unknown"
                                };

                                if (!string.IsNullOrEmpty(tikiEvent.EventLink) || !string.IsNullOrEmpty(tikiEvent.ImageUrl))
                                {
                                    eventsList.Add(tikiEvent);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                string detailError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                throw new Exception($"Lỗi khi crawl sự kiện Tiki: {detailError}");
            }

            return eventsList;
        }
    }
}
