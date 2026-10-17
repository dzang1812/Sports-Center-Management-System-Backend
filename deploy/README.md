# Deploy BE lên EC2 (một lần duy nhất)

Build chạy trên máy ảo của GitHub Actions (free, mạnh), không phải trên EC2 — con `gymsupport-api` chỉ có 1GB RAM và ổ đĩa gần đầy (70%/6.6GB khi kiểm tra), không đủ sức build. Mỗi lần push lên `develop`: GitHub build xong, copy file kết quả qua EC2, rồi restart service. EC2 chỉ cần chạy, không cần build.

## 1. Instance có sẵn: `gymsupport-api` (i-04ebde92411f93820) — đã xong

Đã dùng lại instance này, dành riêng cho Sports Center. Đã xác nhận:
- **Đang Running**, Elastic IP **`46.51.222.230`** (= `EC2_HOST` ở bước 5, cố định dù start/stop).
- Security Group đã mở sẵn port **22, 80, 443**.
- SSH bằng key pair mới (`sportscenter-deploy.pem`) đã hoạt động.
- Đã soi máy: `gymsupport` chạy bằng **Docker**, bind `127.0.0.1:5028`, nginx route riêng theo domain (`api.gsfitness.id.vn`...) — không đụng gì tới port 80 mặc định hay port 5000 mình sẽ dùng. Không cần dọn gì của gymsupport.
- **Ổ đĩa chỉ còn ~2GB trống** → bước 2 dưới đây cố tình cài bản nhẹ nhất (runtime, không phải SDK) để tránh hết ổ.

## 2. Cài môi trường trên EC2 (SSH vào máy rồi chạy)

```bash
# Chỉ cài ASP.NET Core RUNTIME (~150MB) — KHÔNG cài SDK (~1-2GB), vì build không chạy ở đây nữa.
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0 git
rm packages-microsoft-prod.deb   # dọn luôn, đỡ tốn ổ

# nginx đã có sẵn (dùng cho gymsupport) — không cần cài lại, chỉ thêm site mới ở bước 4.

# Clone CHỈ để lấy 3 file mẫu trong deploy/ (service, nginx config, .env.example) — toàn text,
# không build ở đây nên không tốn ổ đáng kể (vài MB, khác hẳn SDK ~1-2GB).
git clone https://github.com/dzang1812/Sports-Center-Management-System-Backend.git ~/sports-center-backend

# Thư mục chạy thật (workflow GitHub Actions sẽ copy file build vào đây mỗi lần deploy)
sudo mkdir -p /var/www/sportscenter-api
sudo chown -R ubuntu:ubuntu /var/www/sportscenter-api
```

## 3. Tạo file secret `.env` trên EC2 (không commit, không nằm trong git)

```bash
cp ~/sports-center-backend/deploy/.env.example /var/www/sportscenter-api/.env
nano /var/www/sportscenter-api/.env   # điền giá trị thật, xem mẫu ở deploy/.env.example
chmod 600 /var/www/sportscenter-api/.env
```

## 4. Đăng ký systemd service + nginx

```bash
sudo cp ~/sports-center-backend/deploy/systemd/sportscenter-api.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now sportscenter-api
sudo systemctl status sportscenter-api   # phải thấy "active (running)"

sudo cp ~/sports-center-backend/deploy/nginx/sportscenter-api.conf /etc/nginx/sites-available/
sudo ln -s /etc/nginx/sites-available/sportscenter-api.conf /etc/nginx/sites-enabled/
sudo rm -f /etc/nginx/sites-enabled/default
sudo nginx -t && sudo systemctl reload nginx
```

Kiểm tra: `curl http://46.51.222.230/swagger/v1/swagger.json` phải trả JSON (nhớ bật Swagger ở Production nếu muốn test bằng Swagger UI — hiện code chỉ bật Swagger khi `ASPNETCORE_ENVIRONMENT=Development`, nói tôi nếu muốn bật cho Production để test).

## 5. Thêm 3 secret vào GitHub repo

Vào repo trên GitHub → **Settings → Secrets and variables → Actions → New repository secret**:

| Tên secret | Giá trị |
|---|---|
| `EC2_HOST` | `46.51.222.230` (Elastic IP cố định của `gymsupport-api`) |
| `EC2_USER` | `ubuntu` (user mặc định của Ubuntu AMI) |
| `EC2_SSH_KEY` | toàn bộ nội dung file `.pem` tải lúc tạo EC2 (kể cả dòng `-----BEGIN...` và `-----END...`) |

Xong bước này, push lên `develop` là workflow `.github/workflows/deploy-backend.yml` tự chạy.

## 6. Đăng ký webhook URL thật với PayOS (chỉ 1 lần, sau khi có domain/IP ổn định)

Login API bằng tài khoản Manager, lấy token, gọi:

```bash
curl -X POST http://46.51.222.230/api/payments/payos/confirm-webhook \
  -H "Authorization: Bearer <manager JWT>" -H "Content-Type: application/json" \
  -d '"http://46.51.222.230/api/payments/payos-webhook"'
```

Sau bước này PayOS mới thực sự gọi được webhook về — vấn đề "thanh toán xong vẫn Pending" sẽ hết, vì webhook giờ có đường về server thật thay vì `localhost`.

## Việc còn thiếu, chưa làm (nói nếu cần)

- **HTTPS thật**: cần 1 domain trỏ về IP của EC2, rồi chạy `sudo certbot --nginx -d your-domain.com`. PayOS khuyến nghị webhook URL dùng HTTPS cho production thật.
- **Bật Swagger ở Production**: hiện code chặn Swagger ngoài môi trường Development.
- **CORS cho FE production**: đã thêm cấu hình `Cors:AllowedOrigins` trong code, nhưng cần set `Cors__AllowedOrigins__0` trong `.env` bằng đúng domain FE sau khi FE cũng được deploy.
