# Hướng dẫn bài tập WorkoutX

- FE: **Tự tập → Hướng dẫn** bên dưới mỗi bài trong lịch tuần.
- API của ứng dụng: `GET /api/exercise-guides?q=Dumbbell%20Bench%20Press&offset=0`, cần JWT đăng nhập. Mỗi trang tối đa 10 bài; chỉ đọc, không ghi database.
- Backend gọi `https://api.workoutxapp.com/v2/exercises` bằng header `X-WorkoutX-Key`. Tài liệu: https://workoutxapp.com/docs.html (API v2).
- Cấu hình secret `WorkoutX:ApiKey` bằng .NET user-secrets khi development. Trên EC2, thêm `WorkoutX__ApiKey` vào `/var/www/sportscenter-api/.env` (systemd EnvironmentFile hiện có), rồi restart service **sau khi được phép deploy**. Không đưa key vào source, Vite env hay FE bundle.
- FE local vẫn gọi Backend EC2 hiện tại. User-secrets trên máy cá nhân không cấu hình được EC2: cần deploy code Backend này và đặt secret tại EC2 trước khi nút hoạt động trên hệ thống đó. Không cần migration hoặc đổi dữ liệu.
- Chỉ tìm khi mở hướng dẫn/bấm Tìm/chuyển trang. Cache kết quả tối đa 15 phút, không vượt thời điểm hết hạn media; giới hạn 15 request/phút cho mỗi người dùng. Video dùng `preload="none"`, chỉ phát khi bấm; không tải lại hoặc lưu trữ video trên máy chủ.
- Tên bài khác thư viện sẽ hiện danh sách để chọn đúng biến thể/dụng cụ. Hai tên tra cứu tương đương: Barbell Back Squat → Barbell Squat; Conventional Deadlift → Barbell Deadlift. Không tự sửa bài trong lịch hoặc chọn biến thể không khớp.
- WorkoutX hiện chưa có bản hướng dẫn tiếng Việt; UI giữ nguyên nội dung tiếng Anh từ nguồn, không tự sinh kỹ thuật tập.
- API trả thông báo riêng khi chưa cấu hình (503), lỗi provider (502/504) hoặc giới hạn lượt (429). Đóng/mở lại để lấy media mới khi link hết hạn.
