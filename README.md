# VOS3000 Agent Custom (C#)

Bản C# WinForms của ứng dụng **VOS3000 Agent Custom** (app gốc Python/Tkinter tại
`C:\Users\PC\Desktop\vos3000-agent-custom`).

## Chức năng
- **Polling job** từ API VOS3000 Agent (next / claim / result / heartbeat).
- **Healthcheck API nguồn**: nhập Domain + Endpoint, tự ping mỗi 5 giây (đổi được), hiện OK/FAIL, HTTP code, thời gian phản hồi.
- **Chạy thử (mô phỏng)** cho 4 nghiệp vụ: Khai số, Khóa số, Mở hạn mức, Khóa hạn mức.
- **Add số từ API — chỉ update số đã có**: quét mapping/routing trên VOS3000, số API trả về mà
  server **đã có** (ở bất kỳ account nào) thì chỉ khóa/mở theo API; số **chưa có** thì **không tự
  thêm** mà lưu vào bảng `t_number_missing` của DB tool.
- **Tab “Số chưa có (VOS)”**: xem/tìm danh sách số chưa có trên server, xuất **Excel `.xlsx`**
  (không cần thư viện ngoài), xoá danh sách khi đã xử lý.
- **Live GUI**: điều khiển client VOS3000 thật bằng Windows UI Automation
  (thay cho pywinauto) theo `gui_profiles.json`.
- **Khảo sát client** để tìm control (name / autoId / control type).
- **Monitor Telegram**: sau mỗi vòng gọi API lấy danh sách phone nguồn + update VOS3000, bot gửi
  báo cáo vào group (trạng thái, danh sách số đã xử lý, thời gian hoàn thành).
- Xuất CSV lịch sử.

## Cấu trúc thư mục
| Tệp | Vai trò | Tương ứng app gốc |
| --- | --- | --- |
| `MainForm.cs` | Giao diện WinForms | `ui.py` |
| `VosCustomerReader.cs` | **Đọc** danh sách customer qua TCP 1202 (như client) | _(mới)_ |
| `PollWorker.cs` | Polling job + xử lý | `worker.py` |
| `GuiDriver.cs` | UI Automation điều khiển client | `gui.py` |
| `ApiClient.cs` | Gọi API | `api.py` |
| `HealthChecker.cs` | Ping healthcheck định kỳ | _(mới)_ |
| `Config.cs` | Đọc/ghi cấu hình | `config.py` |
| `ToolDb.cs` | DB của tool (log, thống kê, audit, số chưa có) | — |
| `XlsxWriter.cs` | Xuất Excel `.xlsx` tối giản (không thư viện ngoài) | _(mới)_ |
| `CustomerDb.cs` | Đọc/ghi account customer VOS3000 (`e_customer`) | _(mới)_ |
| `MockServer/` | Server giả lập để test full luồng | — |

## Test full luồng (mock server)
```powershell
# Terminal 1: chạy mock API (http://127.0.0.1:5099)
cd Vos3000AgentCSharp\MockServer
dotnet run

# Terminal 2: chạy app
cd Vos3000AgentCSharp
dotnet run
```
App tự lấy 2 job "Khai số" từ mock, mô phỏng, rồi báo kết quả. Xem kết quả nhận được:
`http://127.0.0.1:5099/debug/results` và `/debug/stats`.


## Dữ liệu cấu hình
App dùng thư mục riêng **`%APPDATA%\Vos3000AgentCSharp`** (không đụng app cũ):
- `settings.json` — cấu hình API, token, chế độ.
- `gui_profiles.json` — profile điều khiển GUI (mặc định `steps` rỗng → Live bị từ chối).
- `history.jsonl` — lịch sử đã xử lý.

### Hai database trong tab Cấu hình
| Ô | Ý nghĩa | Mặc định |
| --- | --- | --- |
| Tên database (tự đặt) | DB riêng của **tool** (log, thống kê, audit) | `vos_tool` |
| DB VOS3000 (e_customer) | DB của **VOS3000** — nguồn bảng `e_customer` | `vos3000db` |

## Tai khoan dang nhap VOS3000 (TCP 1202)

Tab **"Cau hinh"** co 2 o **User / Password VOS3000 (1202)**. Cat phan nay KHONG phai DB:
day la tai khoan dung de dang nhap vao VOS3000 qua TCP 1202 (lay danh sach gateway mapping/routing).

- Tai khoan nay KHONG nam o bien nao de sua truc tiep: no nam trong **packet login** cua file session template
  (`vos_session_capture.bin`). Ban build chuan nhung san `loginName="admin", password="admin"` cua server cu.
- Bam **"Luu tai khoan login"**: tool tao 1 file session moi (chi thay user/pass, giu nguyen moi packet khac)
  tai `%TEMP%\Vos3000AgentCSharp\vos_session_login.bin` va tro settings sang file do.
  File goc va `%APPDATA%` khong bi sua, nen bootstrap khong the ghi de.
- Bam **"Test login"**: gui 1 lan hello + login, KHONG GET/SET du lieu, de biet user/pass co dung khong.
  Neu ket qua la "0 mapping / 0 routing" thi thu Test login truoc khi nghi den filter.
- Bam **"Xem login trong file"**: hien user/pass hien co trong session goc va session da doi login.

CLI tuong duong (khong can app):

```
VosReplayer login --session "C:\...\vos_session_capture.bin" --user admin --pass xxx --test
```

> Neu server co bao nhieu tai khoan nhung tool bao "Doc duoc 0 mapping", thu tu kiem tra:
> (1) Test login, (2) quyen cua tai khoan (admin/super), (3) `ReadAvailable` co bi timeout khong
> (server tra loi/du lieu ve cham hoac du lieu bi cat).

Cùng dùng chung `Server VOS3000`, `Port (DB)`, `User`, `Password`.

## Tab "Số chưa có (VOS)" — số mới: API có mà VOS3000 chưa có

Luồng **Add Số (VOS)** từ nay **không tự thêm số mới** lên VOS3000:

| Trường hợp | Hành động |
| --- | --- |
| Số API trả về đã có trên server (bất kỳ account call-in/call-out) | Chỉ **khóa/mở** theo trạng thái API |
| Số API trả về **không có ở bất kỳ account nào** = **SỐ MỚI** | Lưu vào bảng `t_number_missing` (DB tool), **không add** |

- Bảng `t_number_missing` (tự tạo khi sync/lần đầu mở tab): `num_key`, `num`, `api_status`,
  `first_seen`, `last_seen`, `hits`.
- Bảng giữ **đúng danh sách số đang còn thiếu**: mỗi vòng sync số thiếu được upsert
  (`hits++`, cập nhật `last_seen`); số **nay đã có trên server** hoặc **đã rời server** thì tự xoá.
- An toàn: nếu vòng sync không đọc được danh sách số nào trên VOS (lỗi kết nối/template) thì
  **không phân loại và không xoá dòng nào** — tránh ghi sai "số mới" hàng loạt.
- Tab **“Số chưa có (VOS)”**: tải danh sách, tìm theo số, xem trạng thái API + thời điểm gặp,
  **xuất Excel `.xlsx`**, xoá danh sách (chỉ xoá DB tool, không đụng VOS3000).

## Tab "Số theo account (VOS)" — danh sách số đang có trên server, gom theo account

Sau **mỗi vòng “Add Số (VOS)”** (chạy tay hoặc theo lịch), tool đọc lại toàn bộ
gateway mapping (call-out) + gateway routing (call-in) trên VOS3000, gom số theo từng
account và lưu vào bảng `t_account_number_set` của DB tool:

| Cột DB | Ý nghĩa |
| --- | --- |
| `account_id` / `account` | `e_customer.id` + tên account |
| `direction` | `in` (call-in) hoặc `out` (call-out) |
| `numbers` | JSON array token trên server: `84...` = mở, `#84...` = khóa |
| `num_count` / `locked_count` | tổng số / số đang khóa |
| `updated_at` | thời điểm vòng sync ghi dữ liệu |

- Bảng được **upsert mỗi vòng**; account không còn trên server sẽ bị dọn khi vòng sync OK.
- Tab **“Số theo account (VOS)”**: lọc theo account / hướng (IN-OUT), tìm theo số hoặc
  account, xem trạng thái khóa/mở và thời điểm cập nhật, **xuất Excel `.xlsx`**.
- Tab tự làm mới khi đang mở lúc vòng sync kết thúc; nút “Xoá bảng (DB tool)” chỉ xoá
  dữ liệu tổng hợp trong DB tool, không ảnh hưởng VOS3000.

## Monitor Telegram — báo cáo sau mỗi vòng sync

Cấu hình ở tab **“Cấu hình” → Monitor Telegram**:

| Trường | Ý nghĩa |
| --- | --- |
| Bot token | token lấy từ `@BotFather` |
| Chat ID nhóm | id group dạng `-100…` (thêm bot vào nhóm, gửi 1 tin rồi xem `getUpdates`) |
| Kèm danh sách số | bật/tắt phần liệt kê số đã xử lý trong tin nhắn |
| Số tối đa liệt kê | giới hạn số in ra cho mỗi danh sách (tin tự chia nếu dài) |
| Chỉ bắn khi có thay đổi | mặc định tắt = bắn mọi vòng sync (xem ghi chú dưới) |

Nội dung mỗi tin: **trạng thái** (OK/LỖI), số API trả về, số đã xử lý trên VOS (khóa/mở),
account cập nhật, số chưa có trên VOS, **danh sách số đã xử lý**, **thời gian hoàn thành + thời lượng**.

- Gửi bất đồng bộ — lỗi Telegram **không** ảnh hưởng vòng đồng bộ.
- Chỉ gửi, không nhận lệnh từ Telegram.
- Nút **“📨 Gửi thử”** để kiểm tra token / chat id / quyền của bot trong nhóm.

**Khi nào KHÔNG có tin?** Chỉ đúng 1 trường hợp: bật *“Chỉ bắn khi có số thay đổi”* **và** vòng sync
chạy theo lịch, thành công, nhưng không số nào đổi trạng thái. Khi đó tool ghi rõ trong log tab
“Add số”: `Telegram: bỏ qua — vòng này không có số nào đổi…`.

Luôn có tin bất kể tuỳ chọn trên khi:
- bấm tay **“API: Lấy + Add ngay 1 lần”** (thao tác tay luôn có phản hồi);
- vòng sync **LỖI** (không gọi được API, không kết nối được VOS3000, …).

Trạng thái gửi hiển thị ngay dưới nút “Gửi thử”: `● Đã gửi báo cáo HH:mm:ss` /
`● Bỏ qua — không có số đổi` / `● Gửi lỗi: …`.

## Yêu cầu
- **Máy build**: Windows + .NET 8 SDK (hoặc mới hơn).
- **Máy chạy**: Windows x64 - không cần cài gì (xem mục Đóng gói 1 file .exe bên dưới).

## Build & chạy
```powershell
cd Vos3000AgentCSharp
dotnet build -c Release
dotnet run
```

## Đóng gói 1 file .exe để chạy trên máy khác (portable)

Ra file **`Vos3000Agent.exe`** duy nhất — đã nhúng sẵn .NET 8 runtime, logo, icon,
session capture, template routing. Máy đích **không cần cài .NET**, **không cần copy thư mục nào**.

```powershell
cd Vos3000AgentCSharp

# Lệnh đầy đủ (dùng khi không có publish profile / máy build chỉ có .NET SDK 9):
dotnet publish Vos3000Agent.csproj -c ReleasePortable -r win-x64 --self-contained true `
  -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none

Get-ChildItem .\bin\ReleasePortable\net8.0-windows\win-x64\publish\Vos3000Agent.exe
```
> `-p:SelfContained=true` là bắt buộc: `Vos3000Agent` ProjectReference tới `Replayer`
> (project Exe). Thiếu cờ này sẽ lỗi **NETSDK1150**.

Kết quả: `Vos3000Agent.exe` (~69 MB, có nén) — copy sang máy khác chạy trực tiếp.

### Lần chạy đầu trên máy mới
App tự tạo `%APPDATA%\Vos3000AgentCSharp\` gồm `settings.json`, `gui_profiles.json`, `agent.log`,
`bootstrap.log`, `vos_session_capture.bin`, `vos_routing_c2s.bin`, `assets\logo.png` — **không cần quyền admin**;
có thể đặt exe ở đâu tùy ý (Desktop, USB, ổ D…).

### Máy đích cần gì?
| Hạng mục | Yêu cầu |
| --- | --- |
| OS | Windows 10 / 11 / Server 2016+, **x64** (WinForms) — không chạy được Win7 |
| .NET runtime | **Không cần cài** (đã nhúng trong exe) |
| VC++ redist | Không cần |
| Mạng | Thông ra **API VOSAgent (HTTPS)**, **VOS3000 TCP 1202**, **MySQL 3306** (server non-SSL vẫn kết nối được), Telegram **api.telegram.org** nếu bật monitor |
| Quyền | User thường là đủ. Nếu dùng **Live GUI**: client VOS3000 phải chạy cùng phiên desktop và cùng mức quyền (đừng chạy app elevated khi client là user thường) |
| Chống virus | File tự giải nén runtime (single-file) nên đôi khi Defender/SmartScreen cảnh báo lần đầu → "More info → Run anyway" hoặc thêm vào whitelist |
| Cấu hình | Nhập trong tab Cấu hình (API URL + token, VOS server, DB host/user/pass, Telegram) — `settings.json` **không đi kèm exe** |

## Ghi chú an toàn
- Mặc định `simulation_mode = true` và `live_gui_enabled = false` → không bao giờ sửa VOS3000.
- Chỉ bật **Live GUI** sau khi đã khảo sát client và cấu hình đầy đủ `steps` + `verify_steps`
  trong `gui_profiles.json`.
