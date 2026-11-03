using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Ai;

/// <summary>
/// Deterministic safety net for the AI: reads what the member said about their health and removes the exercises
/// that load the affected area, instead of trusting the model to follow the prompt rules.
/// </summary>
public static class HealthGuard
{
    private sealed record AreaRule(string Area, string[] Keywords, string[] AvoidNames);

    // Keywords are matched on diacritic-free lowercase text; names are substrings of the exercise library names.
    // shortcut: keyword/name lists are hand-maintained for the seeded library, extend them when exercises are added.
    private static readonly AreaRule[] Areas =
    [
        new("vai", ["vai", "shoulder"],
            ["Overhead", "Shoulder Press", "Arnold", "Landmine", "Dip", "Dumbbell Fly", "Decline", "Pull-Up", "Chin-Up",
             "Dead Hang", "Barbell Bench Press", "Close-Grip Bench", "Front Raise", "Push-Up", "Skull"]),
        new("lưng dưới / cột sống", ["lung", "thoat vi", "dia dem", "cot song", "back pain"],
            ["Conventional Deadlift", "Rack Pull", "Good Morning", "Stiff-Leg", "Romanian", "Sumo Deadlift", "Bent-Over Row",
             "Pendlay", "T-Bar", "Back Extension", "Barbell Back Squat", "Front Squat", "Ab Wheel", "Hanging Leg", "Russian Twist"]),
        new("gối", ["goi", "knee"],
            ["Squat", "Lunge", "Step-Up", "Leg Extension", "Mountain Climber", "Nordic"]),
        new("cổ tay / khuỷu tay", ["co tay", "khuyu", "wrist", "elbow"],
            ["Wrist Curl", "Barbell Curl", "EZ-Bar Curl", "Skull", "Close-Grip Bench", "Push-Up", "Dead Hang", "Farmer", "Preacher"]),
        new("cổ / gáy", ["dau co", "co vai gay", "gay", "cervical", "neck"],
            ["Shrug", "Overhead", "Shoulder Press", "Arnold", "Upright", "Barbell Back Squat"]),
    ];

    // Conditions where only a light plan is acceptable and a doctor must be consulted first.
    private static readonly string[] SeriousKeywords =
    [
        "huyet ap", "tieu duong", "mang thai", "thai ky", "dang bau", "phau thuat", "tim mach", "benh tim", "suy tim",
        "hen suyen", "dong kinh", "heart", "blood pressure", "diabetes", "pregnan", "surgery",
    ];

    public sealed record Assessment(string[] Areas, bool Serious, HashSet<int> BannedExerciseIds, string[] BannedNames)
    {
        public bool HasIssue => Areas.Length > 0 || Serious;
    }

    private static readonly Regex HealthContext = new(@"\b(dau|nhuc|chan thuong|thoat vi|viem|trat|gian day chang|benh|mang thai|thai ky|phau thuat|huyet ap|tieu duong)\b");

    /// <summary>Joins only the messages that talk about pain or illness, so unrelated words cannot trigger the area rules.</summary>
    public static string HealthMessages(IEnumerable<string> userMessages) =>
        string.Join(' ', userMessages.Where(m => HealthContext.IsMatch(Normalize(m))));

    public static Assessment Assess(string? healthText, IEnumerable<AiExerciseRow> exercises)
    {
        var text = Normalize(healthText);
        bool Has(string keyword) => Regex.IsMatch(text, $@"\b{Regex.Escape(keyword)}\b");

        var hit = Array.FindAll(Areas, a => a.Keywords.Any(Has));
        var serious = SeriousKeywords.Any(Has);
        var avoid = hit.SelectMany(a => a.AvoidNames).Distinct().ToArray();
        var banned = exercises.Where(e => avoid.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        return new Assessment(hit.Select(a => a.Area).ToArray(), serious, banned.Select(e => e.Id).ToHashSet(), banned.Select(e => e.Name).ToArray());
    }

    /// <summary>Fixed health notice shown above the plan, written by code so it never depends on the model.</summary>
    public static string Notice(Assessment a, string? declared)
    {
        if (!a.HasIssue)
            return string.IsNullOrWhiteSpace(declared)
                ? "Nếu bạn có chấn thương hoặc bệnh nền, hãy cho mình biết để điều chỉnh lịch cho an toàn."
                : $"**Lưu ý sức khỏe:** đã ghi nhận \"{declared!.Trim()}\". Hãy tập nhẹ nhàng và dừng ngay nếu thấy đau nhói, chóng mặt hoặc khó thở.";

        var sb = new StringBuilder("**Lưu ý sức khỏe**\n");
        if (!string.IsNullOrWhiteSpace(declared)) sb.Append($"- Đã ghi nhận: {declared!.Trim()}.\n");
        if (a.Areas.Length > 0)
            sb.Append($"- Vùng cần bảo vệ: {string.Join(", ", a.Areas)}. Đã loại {a.BannedNames.Length} bài có thể tăng tải lên vùng này khỏi danh sách AI được chọn.\n");
        if (a.Serious)
            sb.Append("- Bạn nên hỏi ý kiến bác sĩ trước khi bắt đầu tập. Lịch chỉ nên giữ cường độ nhẹ, không tập tới mức kiệt sức.\n");
        sb.Append("- Dừng tập và báo huấn luyện viên hoặc bác sĩ nếu đau nhói, chóng mặt, khó thở.");
        return sb.ToString();
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
