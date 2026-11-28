namespace ePriTrackerBackend
{
    /// <summary>
    /// Mốc cho IStringLocalizer: bản dịch nằm ở Resources/SharedMessages.{culture}.resx.
    /// KHOÁ = chính câu tiếng Anh (tiếng Anh là mặc định, không cần file); thiếu bản dịch → giữ câu tiếng Anh.
    /// Lớp đặt ở namespace gốc và KHÔNG nằm cạnh file .resx để tên tài nguyên là "ePriTrackerBackend.Resources.SharedMessages".
    /// </summary>
    public sealed class SharedMessages
    {
    }
}
