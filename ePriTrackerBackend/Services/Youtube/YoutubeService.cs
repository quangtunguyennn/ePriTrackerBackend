using System.Net;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Configuration;
using GoogleYouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace ePriTrackerBackend.Services.YouTube
{
    public interface IYouTubeService
    {
        Task<List<YouTubeVideoDto>> GetReviewVideosAsync(string productName, int maxResults = 3);
    }

    public class YouTubeService : IYouTubeService
    {
        private readonly string _apiKey;

        public YouTubeService(IConfiguration configuration)
        {
            _apiKey = configuration["YouTubeApiKey"]
                ?? throw new ArgumentNullException("YouTubeApiKey configuration is missing");
        }

        public async Task<List<YouTubeVideoDto>> GetReviewVideosAsync(string productName, int maxResults = 3)
        {
            if (string.IsNullOrWhiteSpace(productName))
                return new List<YouTubeVideoDto>();

            var googleYoutubeService = new GoogleYouTubeService(new BaseClientService.Initializer()
            {
                ApiKey = _apiKey,
                ApplicationName = GetType().ToString()
            });

            var searchListRequest = googleYoutubeService.Search.List("snippet");
            searchListRequest.Q = $"{productName} review đánh giá";
            searchListRequest.MaxResults = maxResults; // Giới hạn đúng 3 video
            searchListRequest.Type = "video";
            searchListRequest.RelevanceLanguage = "vi"; // Ưu tiên video tiếng Việt cho chính xác

            // 🚀 TỐI ƯU TỐC ĐỘ: Chỉ lấy đúng các field cần thiết từ Google API để giảm payload
            searchListRequest.Fields = "items(id/videoId,snippet(title,thumbnails/high/url))";

            var searchListResponse = await searchListRequest.ExecuteAsync();

            var videos = new List<YouTubeVideoDto>();
            if (searchListResponse.Items == null)
                return videos;

            foreach (var searchResult in searchListResponse.Items)
            {
                if (searchResult.Id?.VideoId == null) continue;

                videos.Add(new YouTubeVideoDto
                {
                    VideoId = searchResult.Id.VideoId,
                    Title = WebUtility.HtmlDecode(searchResult.Snippet.Title),
                    ThumbnailUrl = searchResult.Snippet.Thumbnails?.High?.Url
                });
            }

            return videos;
        }
    }
}