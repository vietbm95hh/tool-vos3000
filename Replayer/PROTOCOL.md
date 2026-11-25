# VOS3000 — Giao thức TCP 1202 & thao tác Mapping Caller Prefix

> Project: `Vos3000AgentCSharp/Replayer` — build `VosReplayer.exe` (net8.0)
> Đã kiểm chứng trên server thật `172.29.101.32:1202`, mapping `'cts'` (gatewaymapping_id=223).

## 1. Wire format (đã 100% xác thực)

Mỗi packet client gửi qua TCP 1202:

```
[4 byte zero] [36 byte header] [gzip payload]
```

- **4 byte zero**: bắt buộc. Thiếu → server RST ngay (đã test PB1/PB2).
- Header 36 byte:
  - `[0..7]` = 0
  - `[8..11]` = msg type (6 = hello, 1 = request)
  - `[12..19]` = 0
  - `[20..27]` = magic `00 00 03 FC FF FF FC 03`
  - `[28..31]` = tick (JVM ms)
  - `[32..35]` = **len(payload)** (big-endian)
- Payload = gzip( Java serialization, class `com.kunshi.interfacef.*` ), bắt đầu `1F 8B 08`.
- Server trả greeting dạng text `... /127.0.0.1:62969 ... MBX3000` ngay sau hello (75 + 235 bytes).
- Response của server cũng là `[hdr][gzip(java)]` (không có 4 byte zero phía trước).

## 2. Luồng thao tác prefix (flow "fast" — ~2 giây/cycle)

Chỉ cần 4 packet từ session capture (`s01_c2s.bin`):

| # | Packet | Nội dung |
|---|--------|----------|
| 0 | hello (60 byte payload) | bắt tay, nhận greeting MBX3000 |
| 2 | `ReqSetInfo{InfoUser{loginName="admin", password="admin"}}` | login |
| 17 | `FilterGatewayMapping{name="cts"}` | GET → server trả `RspGetInfo` đầy đủ object |
| 47 | `ReqSetInfoList{modifyInfoList:[ReqSetInfo{InfoGatewayMapping}]}` | SET → server trả `RspSetInfoList` |

**Đọc prefix**: parse các `TC_STRING` (byte `0x74` + len 2 byte + UTF-8) trong object GET,
lấy chuỗi dài nhất có dạng `^[01];[0-9,]*$` → `calloutCallerPrefixes`.

**Ghi prefix**: decompress gzip của packet SET, thay thế chuỗi prefix dài nhất, compress lại,
cập nhật `len` tại `[32..35]`, gửi kèm 4 byte zero. Điểm mạnh: **không cần implement Java
serialization** — chỉ patch string trong bytes.

⚠️ Packet SET dùng template từ capture — ghi đè **toàn bộ 60 trường** của `InfoGatewayMapping`
giống client thật (giá trị capture còn hợp lệ vì chỉ mình tool sửa prefix).

## 3. Định dạng chuỗi prefix

```
1;0901,0912          ← header hằng "1;" + danh sách prefix phân cách bằng ","
1;0088,0889,0901,0912,0999,123456
1;                   ← rỗng (không prefix nào) — server chấp nhận
```

## 4. CLI

```
VosReplayer get                          # đọc prefix hiện tại (fast flow ~2s)
VosReplayer set --prefix "1;0901,0912"   # ghi + verify bằng GET readback
VosReplayer pentest [--quick]            # 20 test case, tự động khôi phục baseline
VosReplayer replay --session s01_c2s.bin [--apply] [--prefix X]   # replay v1
Chung: --host 172.29.101.32 --port 1202
```

## 5. Kết quả pentest (2026-10-29 15:10, FULL)

Report đầy đủ: `bin/Debug/net8.0/pentest/pentest_report_*.txt`

| ID | Test | Kết quả |
|----|------|---------|
| T0.0 | Load session/template (hello#0 login#2 GET#17 SET#47) | PASS |
| T0.1 | Baseline đọc prefix | PASS — `1;0901,0912` |
| PB1 | Hello **thiếu 4 byte zero** | PASS — server RST (bảo vệ đúng) |
| PB2 | 64 byte rác | PASS — server RST |
| PB3 | Hello đúng protocol → greeting | PASS — `MBX3000` |
| PB4 | Header magic hỏng | PASS — server RST |
| PB5 | Server tỉnh táo sau negative test | PASS — prefix nguyên vẹn |
| PC1 | Flow FULL replay 0..GET | PASS |
| PC2 | Replay DRY toàn bộ, không đổi dữ liệu | PASS |
| PD1 | SET `1;0901` (6 byte) | PASS — readback khớp |
| PD2 | SET 6 prefix | PASS — readback khớp |
| PD3 | SET dài 131 ký tự (10 prefix × 12 số) | PASS — readback khớp, **tồn tại ổn định sau 30s** |
| PD4 | SET chứa ký tự đặc biệt `+86ABCtest*` | INFO — server tự từ chối/làm sạch (an toàn) |
| PD5 | SET rỗng `1;` | PASS — server chấp nhận |
| PD6 | SET cùng giá trị 2 lần (idempotence) | PASS |
| PD7 | 3 vòng nhanh SET A→B→A (6 lần SET/GET) | PASS |
| PD8 | Khôi phục baseline | PASS — `1;0901,0912` |
| PE1 | SET cắt đứt 60% packet | PASS — server không ghi, dữ liệu nguyên vẹn |
| PE2 | Fuzz SET chuỗi 0 byte + khôi phục | PASS — trạng thái ổn định |
| PF1 | Trạng thái cuối = baseline | PASS — hoàn toàn khôi phục |

**Tổng: 19 PASS / 0 FAIL / 1 INFO.**

## 6. An toàn đã kiểm chứng

- Tool **chỉ nói chuyện TCP 1202** — không đụng MySQL, không UI automation, không sửa hosts, không cần Admin.
- Mọi test ghi đều **đọc baseline trước, khôi phục baseline sau, xác nhận bằng GET readback**.
- Server tự bảo vệ: RST khi packet sai format; từ chối giá trị prefix chứa ký tự lạ; ghi nửa vời (packet cắt đứt) bị bỏ qua.
- Log đầy đủ: `bin/Debug/net8.0/pentest/pentest_report_*.txt`, replay logs trong `bin/Debug/net8.0/replay/`.
