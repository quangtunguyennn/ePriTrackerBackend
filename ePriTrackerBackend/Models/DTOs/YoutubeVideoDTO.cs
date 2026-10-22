public class YouTubeVideoDto
{
    public string VideoId { get; set; }
    public string Title { get; set; }
    public string ThumbnailUrl { get; set; }
    public string EmbedUrl => $"https://www.youtube.com/embed/{VideoId}";
}