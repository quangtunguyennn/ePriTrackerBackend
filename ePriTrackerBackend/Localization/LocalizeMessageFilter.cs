using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Localization;

namespace ePriTrackerBackend.Localization
{
    /// <summary>
    /// Bộ lọc chung: phản hồi dạng <c>new { message = "...", ... }</c> → dịch "message" theo Accept-Language của request.
    /// Controller giữ nguyên câu tiếng Anh; hình dạng JSON không đổi (cùng tên trường, cùng thứ tự). Chỉ xử lý đối tượng ẩn danh
    /// có thuộc tính string "message" – DTO và dữ liệu khác đi qua nguyên vẹn.
    /// </summary>
    public sealed class LocalizeMessageFilter : IAsyncResultFilter
    {
        private readonly IStringLocalizer<SharedMessages> _localizer;

        public LocalizeMessageFilter(IStringLocalizer<SharedMessages> localizer)
        {
            _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        }

        public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            if (context.Result is ObjectResult result && result.Value != null)
            {
                var localized = Localize(result.Value, _localizer);
                if (!ReferenceEquals(localized, result.Value)) result.Value = localized;
            }
            return next();
        }

        /// <summary>Đối tượng ẩn danh có "message" → Dictionary cùng trường với message đã dịch; còn lại trả nguyên.</summary>
        public static object Localize(object value, IStringLocalizer localizer)
        {
            var type = value.GetType();
            if (!IsAnonymous(type)) return value;

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var message = properties.FirstOrDefault(p => p.Name == "message" && p.PropertyType == typeof(string));
            if (message == null) return value;

            var copy = new Dictionary<string, object>(properties.Length);
            foreach (var property in properties)
            {
                object current = property.GetValue(value);
                copy[property.Name] = property == message ? MessageTranslator.Translate(localizer, (string)current) : current;
            }
            return copy;
        }

        private static bool IsAnonymous(Type type)
            => type.IsDefined(typeof(CompilerGeneratedAttribute), false) && type.Name.Contains("AnonymousType");
    }
}
