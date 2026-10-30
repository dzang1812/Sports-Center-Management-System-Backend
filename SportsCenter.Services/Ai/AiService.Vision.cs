using System.Diagnostics;
using System.Text.Json;
using SportsCenter.Services.Auth;

namespace SportsCenter.Services.Ai;

public partial class AiService
{
    private static readonly object StringArray = new { type = "array", items = new { type = "string" } };

    private static object StrictSchema(string name, string[] stringFields, string[] arrayFields, string[]? boolFields = null) => new
    {
        type = "json_schema",
        json_schema = new
        {
            name,
            strict = true,
            schema = new
            {
                type = "object",
                properties = stringFields.ToDictionary(f => f, _ => (object)new { type = "string" })
                    .Concat(arrayFields.ToDictionary(f => f, _ => StringArray))
                    .Concat((boolFields ?? []).ToDictionary(f => f, _ => (object)new { type = "boolean" }))
                    .ToDictionary(x => x.Key, x => x.Value),
                required = stringFields.Concat(arrayFields).Concat(boolFields ?? []).ToArray(),
                additionalProperties = false,
            },
        },
    };

    public async Task<ImageAnalyzeResponseDto> AnalyzeImageAsync(int userId, Stream image, string contentType, string mode, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await image.CopyToAsync(ms, ct);
        var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(ms.ToArray())}";

        var prompt = mode switch
        {
            "equipment_info" => """
Bạn là huấn luyện viên gym. Hãy xem ảnh này và phân tích máy tập hoặc dụng cụ tập luyện.

Yêu cầu:
1. Nhận diện đây có thể là máy tập/dụng cụ gì.
2. Máy này dùng để tập nhóm cơ nào.
3. Gợi ý các bài tập có thể tập với máy/dụng cụ này.
4. Hướng dẫn cách dùng cơ bản.
5. Đưa ra lưu ý an toàn.

Nếu ảnh không rõ hoặc không chắc chắn, hãy nói rõ là không chắc.
Không bịa thông tin.

Trả về JSON đúng schema.
""",

            "form_check" => """
Bạn là huấn luyện viên gym. Hãy xem ảnh này để đánh giá form/tư thế tập luyện.

Yêu cầu:
1. Dự đoán người dùng đang tập bài gì.
2. Nhận xét form đang đúng ở điểm nào.
3. Nhận xét form có thể sai hoặc nguy hiểm ở điểm nào.
4. Gợi ý cách sửa form.
5. Đưa ra lưu ý an toàn.

Không chẩn đoán y tế.
Nếu ảnh không rõ, hãy nói ảnh chưa đủ rõ.
Không đưa ra kết luận tuyệt đối.

Trả về JSON đúng schema.
""",

            "body_check" => """
Bạn là huấn luyện viên gym. Hãy xem ảnh cơ thể người dùng và đưa ra đánh giá thể hình mang tính tham khảo.

Yêu cầu:
1. Nhận xét tổng quan vóc dáng dựa trên ảnh, nói lịch sự và không body-shaming.
2. Không ước lượng chính xác  bệnh lý hoặc chẩn đoán y tế.
3. Cho biết nhóm cơ nào có thể nên ưu tiên cải thiện.
4. Gợi ý bài tập phù hợp cho các nhóm cơ đó.
5. Gợi ý hướng tập luyện tổng quát: tăng cơ, siết cơ, cải thiện tư thế hoặc cân bằng cơ thể.
6. Đưa ra lưu ý an toàn.
7. Phần trăm mỡ, cân nặng, chiều cao chỉ nên ước lượng rất rộng nếu có thể, và phải nói rõ là ước lượng không chính xác.

Ví dụ nhóm cơ có thể gợi ý (muscles) đưa ra 4 cái thôi:
Cơ vai  (Deltoids):
Vai trước (Anterior Deltoid)
Vai giữa (Lateral Deltoid)
Vai sau (Posterior Deltoid)
Cơ chóp xoay(Rotator Cuff)

Tay (Arms):
Tay trước (Biceps):
Biceps Brachii (Cơ nhị đầu)
Brachialis (Cơ cánh tay)

Tay sau (Triceps Brachii)
Đầu dài (Long head)
Đầu ngoài (Lateral head)
Đầu giữa (Medial head)

Cẳng tay (Forearms):
Cơ ngửa/gập (Flexors)
Cơ sấp/duỗi (Extensors & Brachioradialis)

Cơ Lưng (Back):
Lưng xô (Latissimus Dorsi - Lats)
Cầu vai (Trapezius - Traps)
Lưng giữa (Rhomboids)
Tròn lớn (Teres Major)
Dựng cột sống (Erector Spinae)


Cơ Ngực (Chest)
Ngực trên (Clavicular head)
Ngực giữa(Sternal head)
Ngực dưới(Abdominal head)
Ngực nhỏ(Minor)

Chân (Legs & Glutes)
Đùi trước (Quadriceps):
Đùi sau(Hamstrings):

Cơ mông (Glutes)
Cơ mông lớn(Gluteus Maximus)
Cơ mông nhỡ(Gluteus Medius)
Cơ mông nhỏ(Gluteus Minimus)

Cơ khép đùi (Adductors)

Cơ bắp chân (Calves)


Cơ Bụng (Abs & Core)
Cơ thẳng bụng(Rectus Abdominis)
Cơ liên sườn ngoài(External Obliques)
Cơ liên sườn trong(Internal Obliques)
Cơ bụng ngang(Transversus Abdominis)

Nếu ảnh không rõ, che quá nhiều, hoặc góc chụp không đủ, hãy nói cần ảnh rõ hơn.
Trả về JSON đúng schema.
""",

            _ => """
Bạn là huấn luyện viên gym. Hãy phân tích ảnh và đưa ra lời khuyên tập luyện an toàn.
Trả về JSON đúng schema.
"""
        };

        var content = await CompleteAsync(userId, AiFeature.AnalyzeImage, new
        {
            model = VisionModel,
            messages = new object[]
            {
                new { role = "user", content = new object[] { new { type = "text", text = prompt }, new { type = "image_url", image_url = new { url = dataUrl } } } },
            },
            temperature = 0.2,
            response_format = StrictSchema("gym_image_analysis",
                ["mode", "title", "summary"],
                ["detectedItems", "bodyObservations", "muscles", "priorityMuscles", "suggestedExercises", "formFeedback", "trainingAdvice", "warnings"]),
            max_tokens = 1000,
        }, VisionModel, ct);

        var failed = new ImageAnalyzeResponseDto
        {
            Mode = mode, Title = "Không phân tích được ảnh", Summary = content ?? "AI không trả về kết quả.",
            Warnings = string.IsNullOrWhiteSpace(content) ? ["Vui lòng thử lại với ảnh rõ hơn."] : [],
        };
        if (string.IsNullOrWhiteSpace(content)) return failed;
        try { return JsonSerializer.Deserialize<ImageAnalyzeResponseDto>(content, Json) ?? failed; }
        catch (JsonException) { return failed; }
    }

    /// <summary>Needs the ffmpeg binary on PATH: samples up to 12 frames (2 fps) and sends them to the vision model.</summary>
    public async Task<VideoFormAnalyzeResponseDto> AnalyzeFormVideoAsync(int userId, Stream video, string fileName, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sportscenter_video_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, Path.GetFileName(fileName));
            await using (var file = File.Create(input)) await video.CopyToAsync(file, ct);

            using var ffmpeg = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{input}\" -vf \"fps=2,scale=768:-1\" -frames:v 12 \"{Path.Combine(dir, "frame_%03d.jpg")}\"",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            }) ?? throw new AuthException("Không khởi chạy được FFmpeg trên máy chủ.");
            var stderr = ffmpeg.StandardError.ReadToEndAsync(ct);
            await ffmpeg.WaitForExitAsync(ct);
            if (ffmpeg.ExitCode != 0) throw new AuthException("Không thể xử lý video. Vui lòng thử video khác.");

            var frames = Directory.GetFiles(dir, "frame_*.jpg").OrderBy(x => x).Take(12).ToList();
            if (frames.Count == 0) throw new AuthException("Không thể trích xuất frame từ video.");
            return await AnalyzeFramesAsync(userId, frames, ct);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private async Task<VideoFormAnalyzeResponseDto> AnalyzeFramesAsync(int userId, List<string> frames, CancellationToken ct)
    {
        var content = new List<object> { new { type = "text", text = FormAuditPrompt } };
        foreach (var frame in frames)
            content.Add(new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(frame, ct))}", detail = "low" } });

        var aiContent = await CompleteAsync(userId, AiFeature.AnalyzeFormVideo, new
        {
            model = VisionModel,
            messages = new object[] { new { role = "user", content } },
            temperature = 0.1,
            response_format = StrictSchema("video_form_check",
                ["mode", "title", "summary", "targetExercise", "detectedExercise", "confidence", "riskLevel", "overallVerdict", "movementSummary"],
                ["majorIssues", "minorIssues", "correctPoints", "frameObservations", "correctiveCues", "suggestedFixes", "muscles", "warnings"],
                ["isFormAcceptable"]),
            max_tokens = 1600,
        }, VisionModel, ct);

        VideoFormAnalyzeResponseDto Failed(string summary, params string[] warnings) => new()
        {
            Title = "Không phân tích được video", Summary = summary, DetectedExercise = "Không chắc chắn", Confidence = "low",
            OverallVerdict = "Không đủ dữ liệu để đánh giá form.", Warnings = [.. warnings],
        };

        if (string.IsNullOrWhiteSpace(aiContent))
            return Failed("AI không trả về kết quả.", "Vui lòng thử lại với video rõ hơn.", "Kết quả chỉ mang tính tham khảo, không thay thế huấn luyện viên hoặc bác sĩ.");

        VideoFormAnalyzeResponseDto? a;
        try { a = JsonSerializer.Deserialize<VideoFormAnalyzeResponseDto>(aiContent, Json); }
        catch (JsonException) { a = null; }
        if (a is null) return Failed(aiContent, "Kết quả chỉ mang tính tham khảo.");

        a.Mode = "video_form_check";
        a.TargetExercise = "auto_detect";
        if (string.IsNullOrWhiteSpace(a.DetectedExercise)) a.DetectedExercise = "Không chắc chắn";
        if (string.IsNullOrWhiteSpace(a.Confidence)) a.Confidence = "medium";
        if (string.IsNullOrWhiteSpace(a.RiskLevel)) a.RiskLevel = "medium";

        // Guard against an over-positive verdict: errors, low confidence or an unknown exercise cannot be "good form".
        void Reject(string verdict)
        {
            a.IsFormAcceptable = false;
            if (string.IsNullOrWhiteSpace(a.OverallVerdict) || a.OverallVerdict.Contains("tốt", StringComparison.OrdinalIgnoreCase))
                a.OverallVerdict = verdict;
        }
        if (a.MajorIssues.Count > 0)
        {
            if (a.RiskLevel.Equals("low", StringComparison.OrdinalIgnoreCase)) a.RiskLevel = "medium";
            Reject("Form chưa đạt, cần sửa các lỗi kỹ thuật chính.");
        }
        if (a.Confidence.Equals("low", StringComparison.OrdinalIgnoreCase))
            Reject("Chưa đủ dữ liệu rõ ràng để đánh giá form chính xác.");
        if (a.DetectedExercise.Contains("Không chắc", StringComparison.OrdinalIgnoreCase))
        {
            a.Confidence = "low";
            Reject("Chưa nhận diện chắc chắn bài tập nên không thể kết luận form tốt.");
        }
        if (a.Warnings.Count == 0)
            a.Warnings.Add("Kết quả chỉ mang tính tham khảo, không thay thế huấn luyện viên hoặc bác sĩ.");
        return a;
    }

    public async Task<WorkoutEvaluationNarrativeDto> EvaluateWorkoutAsync(int userId, WorkoutEvaluationMetricsDto metrics, CancellationToken ct = default)
    {
        var content = await CompleteAsync(userId, AiFeature.EvaluateWorkout, new
        {
            model = TextModel,
            messages = new object[]
            {
                new { role = "system", content = EvaluationPrompt },
                new { role = "user", content = $"Số liệu buổi tập:\n{JsonSerializer.Serialize(metrics, Json)}" },
            },
            temperature = 0.6,
            max_tokens = 500,
            response_format = StrictSchema("workout_evaluation_narrative",
                ["narrativeSummary", "mealSuggestion", "suggestedNextWorkout", "motivationalMessage"], []),
        }, TextModel, ct);

        var fallback = new WorkoutEvaluationNarrativeDto
        {
            NarrativeSummary = $"Bạn đạt {metrics.Score} điểm ({metrics.Grade}) cho buổi tập {metrics.Focus}.",
            MealSuggestion = "Bổ sung đạm nạc và rau xanh sau buổi tập.",
            SuggestedNextWorkout = metrics.NextPlannedSession ?? "Nghỉ ngơi hoặc tập nhẹ nhàng nhóm cơ khác.",
            MotivationalMessage = "Tiếp tục duy trì phong độ này nhé!",
        };
        if (string.IsNullOrWhiteSpace(content)) return fallback;
        try { return JsonSerializer.Deserialize<WorkoutEvaluationNarrativeDto>(content, Json) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    private const string FormAuditPrompt =
"""
Bạn là AI Form Auditor trong app Sports Center. Bạn đang phân tích video tập gym đã được tách thành các frame theo thứ tự thời gian.

BẠN KHÔNG ĐƯỢC BIẾT TRƯỚC BÀI TẬP.
Bạn phải tự nhận diện bài tập từ video nếu có thể.

NHIỆM VỤ CHÍNH:
1. Tự nhận diện bài tập trong video.
2. Nếu không chắc bài tập là gì, detectedExercise phải ghi "Không chắc chắn".
3. Nếu không chắc bài tập là gì, confidence = "low".
4. Sau khi nhận diện bài tập, kiểm tra form có ĐẠT hay KHÔNG ĐẠT.
5. Không được khen chung chung nếu có lỗi rõ ràng.

QUY TẮC BẮT BUỘC:
1. Ưu tiên tìm lỗi form trước, sau đó mới nói điểm đúng.
2. Nếu thấy lỗi nguy hiểm, riskLevel = "high".
3. Nếu lỗi form có thể gây chấn thương, isFormAcceptable = false.
4. Nếu video không đủ rõ, confidence = "low" và isFormAcceptable = false.
5. Nếu không thấy đủ chuyển động để xác định bài tập, confidence = "low" và isFormAcceptable = false.
6. Không chẩn đoán y tế.
7. Không nói "form tốt" nếu vẫn có lỗi lớn.
8. Không được viết quá tích cực nếu video thể hiện form sai.
9. Chỉ nhận xét dựa trên những gì thấy được trong frame.
10. Trả lời bằng tiếng Việt.
11. Nếu không nhìn rõ toàn bộ động tác, không được pass form.
12. Nếu có majorIssues thì isFormAcceptable bắt buộc là false.

TIÊU CHÍ ĐÁNH GIÁ CHUNG:
- Cột sống/lưng có giữ trung lập không?
- Vai có bị nhún hoặc cuộn về trước không?
- Core có được kiểm soát không?
- Có dùng đà quá nhiều không?
- Biên độ chuyển động có hợp lý không?
- Tốc độ động tác có kiểm soát không?
- Đường đi của khớp có an toàn không?
- Có dấu hiệu mất kiểm soát tạ/máy không?

CÁCH CHẤM:
- Nếu có từ 1 lỗi nguy hiểm trở lên: riskLevel = "high", isFormAcceptable = false.
- Nếu có nhiều lỗi kỹ thuật nhưng chưa quá nguy hiểm: riskLevel = "medium", isFormAcceptable = false.
- Nếu chỉ có lỗi nhỏ và video đủ rõ: riskLevel = "low", isFormAcceptable = true.
- Nếu không đủ rõ để đánh giá: confidence = "low", isFormAcceptable = false.
- Nếu không nhận diện chắc bài tập: confidence = "low", isFormAcceptable = false.

YÊU CẦU OUTPUT:
- targetExercise = "auto_detect".
- detectedExercise = bài tập bạn quan sát được, hoặc "Không chắc chắn".
- overallVerdict phải nói rõ: "Form chưa đạt", "Form tạm ổn", hoặc "Form tốt".
- Nếu isFormAcceptable = false thì overallVerdict không được là "Form tốt".
- majorIssues phải liệt kê lỗi lớn nếu có.
- minorIssues liệt kê lỗi nhỏ nếu có.
- correctPoints chỉ liệt kê điểm đúng thật sự quan sát được.
- correctiveCues phải là câu hướng dẫn sửa ngắn gọn, dễ làm.
- warnings phải nhắc kết quả chỉ mang tính tham khảo.

Trả về JSON đúng schema.
""";

    private const string EvaluationPrompt =
"""
Bạn là AI Coach viết nhận xét ngắn gọn cho báo cáo buổi tập. Toàn bộ số liệu
(score, grade, summary, highlights, improvements, recovery, dinh dưỡng, số buổi tập 14 ngày gần đây)
ĐÃ được tính toán chính xác sẵn - TUYỆT ĐỐI không tự tính lại, không bịa thêm số liệu mới.
Nhiệm vụ của bạn CHỈ là viết 4 đoạn văn ngắn, tự nhiên, cá nhân hoá dựa đúng trên
các số liệu được cung cấp:
- narrativeSummary: 2-3 câu tóm tắt buổi tập, nêu bật điểm số/hạng và PHẢI nhắc tới nội dung trong comparisonSummary (so với lần trước tập cùng buổi) một cách tự nhiên.
- mealSuggestion: 1 câu gợi ý bữa ăn phục hồi cụ thể (món ăn thật, không chung chung), dựa trên lượng protein/nước đã tính.
- suggestedNextWorkout: 1 câu gợi ý buổi tập tiếp theo, ưu tiên dùng gợi ý buổi kế hoạch nếu có, hoặc dựa vào tình trạng phục hồi nhóm cơ.
- motivationalMessage: 1-2 câu động viên chân thành, nhắc tới số liệu thật (số buổi tập gần đây, điểm số...) thay vì lời khen chung chung.
Không chẩn đoán y tế. Trả lời bằng tiếng Việt, giọng văn ấm áp như bạn tập gym.
""";
}
