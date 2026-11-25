// ============================================================
//  DocxGuide - Tạo tài liệu hướng dẫn cài đặt & sử dụng (.docx)
//  Ghi trực tiếp định dạng OpenXML (Office Open XML) bằng ZipArchive
//  → KHÔNG cần cài Microsoft Office, KHÔNG cần thư viện ngoài.
// ============================================================
using System.IO.Compression;
using System.Text;

namespace Vos3000Agent
{
    public static class DocxGuide
    {
        // ---------- tiện ích XML ----------
        private static string Esc(string s) => (s ?? "")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");

        /// Một đoạn văn có style (title, h1, h2, normal, bullet, code, note)
        private static string P(string text, string style = "normal")
        {
            var sid = style switch
            {
                "title" => "Title",
                "h1" => "Heading1",
                "h2" => "Heading2",
                "code" => "Code",
                "note" => "Note",
                _ => "Normal"
            };
            var parts = Esc(text).Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append("<w:r><w:br/></w:r>");
                sb.Append("<w:r><w:t xml:space=\"preserve\">").Append(parts[i]).Append("</w:t></w:r>");
            }
            return $"<w:p><w:pPr><w:pStyle w:val=\"{sid}\"/></w:pPr>{sb}</w:p>";
        }

        private static string Bullet(string text) => P("•  " + text);

        private static string Table(string[] headers, List<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/>")
              .Append("<w:tblW w:w=\"5000\" w:type=\"pct\"/></w:tblPr><w:tblGrid>");
            foreach (var _ in headers) sb.Append("<w:gridCol w:w=\"2200\"/>");
            sb.Append("</w:tblGrid>");

            sb.Append("<w:tr>");
            foreach (var h in headers)
                sb.Append("<w:tc><w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"006935\"/></w:tcPr>")
                  .Append("<w:p><w:pPr><w:pStyle w:val=\"TableHead\"/></w:pPr><w:r><w:t xml:space=\"preserve\">")
                  .Append(Esc(h)).Append("</w:t></w:r></w:p></w:tc>");
            sb.Append("</w:tr>");

            foreach (var r in rows)
            {
                sb.Append("<w:tr>");
                foreach (var c in r)
                    sb.Append("<w:tc><w:p><w:r><w:t xml:space=\"preserve\">")
                      .Append(Esc(c)).Append("</w:t></w:r></w:p></w:tc>");
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");
            sb.Append("<w:p/>");
            return sb.ToString();
        }

        // ---------- nội dung ----------
        private static string BuildDocumentXml(Settings s)
        {
            var sb = new StringBuilder();
            sb.Append(P("VOS3000 Agent — Hướng dẫn cài đặt và sử dụng", "title"));
            sb.Append(P($"Cập nhật: {DateTime.Now:dd/MM/yyyy HH:mm}"));

            sb.Append(P("1. Giới thiệu", "h1"));
            sb.Append(P("VOS3000 Agent là công cụ quản trị Mapping Caller Prefix trên tổng đài VOS3000 thông qua giao thức TCP cổng 1202. " +
                "Công cụ cho phép thêm số, khóa/mở số, đồng bộ danh sách số từ API nguồn và lưu lại toàn bộ lịch sử thao tác."));
            sb.Append(Bullet("Kết nối trực tiếp VOS3000, không cần thao tác tay trên giao diện GUI của client VOS3000."));
            sb.Append(Bullet("Đồng bộ Add-only: Chỉ đồng bộ số từ G-Billing -> VOS3000 theo trạng thái quyết định từ G-Billing."));
            sb.Append(Bullet("Cho phép theo dõi thống kê số liệu , check log trực quan …"));

            sb.Append(P("2. Yêu cầu hệ thống", "h1"));
            sb.Append(Table(new[] { "Thành phần", "Yêu cầu" }, new List<string[]>
            {
                new[] { "Hệ điều hành", "Windows 10 / 11 / Server 2016 trở lên" },
                new[] { "Bộ chạy .NET", ".NET Desktop Runtime 8.0 (x64)" },
                new[] { "Kết nối", "G-Billing  -> Tool -> VOS3000 server" },
                new[] { "Database", "MySQL 5.0 trở lên " },
            }));

            sb.Append(P("3. Cấu hình ban đầu", "h1"));
            sb.Append(P("Mở tab Khác ▾ → Cấu hình, điền các thông số sau rồi bấm “Lưu cấu hình”."));

            sb.Append(P("3.1. Khối Server VOS3000", "h2"));
            sb.Append(Table(new[] { "Trường", "Giá trị hiện tại", "Ghi chú" }, new List<string[]>
            {
                new[] { "Server VOS3000", s?.vos_server ?? "172.29.101.32", "IP máy chủ tổng đài" },
                new[] { "Port (VOS)", (s?.vos_port ?? 1202).ToString(), "Cố định 1202" },
            }));

            sb.Append(P("3.2. Khối Database của tool", "h2"));
            sb.Append(P("Lưu ý: IP database dùng chung với ô Server VOS3000 (không khai báo lại).", "note"));
            sb.Append(Table(new[] { "Trường", "Giá trị hiện tại" }, new List<string[]>
            {
                new[] { "Port (DB)", (s?.db_port ?? 3306).ToString() },
                new[] { "Tên database", s?.db_name ?? "vos_tool" },
                new[] { "User", s?.db_user ?? "toolEtl" },
                new[] { "Password", "********" },
            }));

            sb.Append(P("Thứ tự thao tác:", "h2"));
            sb.Append(Bullet("Bấm “Test kết nối” — phải báo OK."));
            sb.Append(Bullet("Bấm “Khởi tạo DB” — tạo database nếu chưa có."));
            sb.Append(Bullet("Bấm “Gen bảng” — tạo 8 bảng nghiệp vụ."));

            sb.Append(P("3.3. Nguồn API danh sách số", "h2"));
            sb.Append(Table(new[] { "Trường", "Giá trị hiện tại" }, new List<string[]>
            {
                new[] { "URL API", s?.prefix_api_url ?? "http://100.69.140.68:2024/api/oms-phone" },
                new[] { "API key (header api-key)", "********" },
                new[] { "Field JSON (path)", s?.prefix_api_field ?? "data.phone" },
                new[] { "Chu kỳ đồng bộ (giây)", (s?.prefix_interval_seconds ?? 60).ToString() },
            }));
            sb.Append(P("API trả về mảng các số có thể ở dạng 84xxxxx (đang mở) hoặc #84xxxxx (cần khóa)."));

            sb.Append(P("3.4. Monitor Telegram", "h2"));
            sb.Append(Table(new[] { "Trường", "Giá trị hiện tại" }, new List<string[]>
            {
                new[] { "Bật báo cáo", s?.telegram_enabled == true ? "Có" : "Không" },
                new[] { "Bot token", string.IsNullOrWhiteSpace(s?.telegram_bot_token) ? "(chưa có)" : "********" },
                new[] { "Chat ID nhóm", string.IsNullOrWhiteSpace(s?.telegram_chat_id) ? "(chưa có)" : s.telegram_chat_id },
                new[] { "Kèm danh sách số", s?.telegram_include_numbers == true ? "Có" : "Không" },
                new[] { "Số tối đa liệt kê", (s?.telegram_max_numbers ?? 200).ToString() },
            }));
            sb.Append(Bullet("Sau MỖI vòng gọi API lấy danh sách số + update VOS3000, bot gửi 1 tin vào nhóm."));
            sb.Append(Bullet("Nội dung: trạng thái (OK/LỖI), số API trả về, số đã xử lý (khóa/mở), account cập nhật, số chưa có trên VOS, danh sách số đã xử lý, thời gian hoàn thành + thời lượng."));
            sb.Append(Bullet("Lấy Chat ID: thêm bot vào nhóm → gửi 1 tin bất kỳ trong nhóm → mở https://api.telegram.org/bot<TOKEN>/getUpdates → lấy id âm (-100…)."));
            sb.Append(Bullet("Bấm “📨 Gửi thử” để kiểm tra token / chat id trước khi bật."));
            sb.Append(Bullet("Chạy tay “API: Lấy + Add ngay 1 lần” → LUÔN có tin báo, kể cả khi không có số nào đổi."));
            sb.Append(Bullet("Vòng sync LỖI (API/VOS3000) → LUÔN có tin báo lỗi."));
            sb.Append(Bullet("Chỉ khi bật “Chỉ bắn khi có số thay đổi” + chạy theo lịch + không số nào đổi thì mới không gửi (log tab Add số ghi rõ “Telegram: bỏ qua…”)."));
            sb.Append(Bullet("Chỉ gửi — không nhận lệnh từ Telegram; lỗi gửi tin KHÔNG ảnh hưởng vòng đồng bộ."));

            sb.Append(P("4. Các tab chức năng", "h1"));
            sb.Append(Table(new[] { "Tab", "Chức năng" }, new List<string[]>
            {
                new[] { "Tổng quan", "Thẻ thống kê cập nhật tự động 5 giây/lần" },
                new[] { "Add số", "Đọc prefix hiện tại, lấy số từ API và CHỈ cập nhật khóa/mở cho số đã có trên VOS" },
                new[] { "Khóa số (VOS)", "Danh sách số trên server, khóa/mở từng số, khóa/mở NHIỀU số (tích ✔), tìm kiếm, phân trang" },
                new[] { "Số chưa có (VOS)", "Số API trả về nhưng VOS3000 chưa có — xem và xuất Excel (.xlsx)" },
                new[] { "Lịch sử / Lỗi", "Toàn bộ nghiệp vụ đã chạy, xuất CSV" },
                new[] { "Healthcheck API", "Ping API nguồn theo chu kỳ" },
                new[] { "Cấu hình", "Khai báo server, database, API nguồn, monitor Telegram" },
            }));

            sb.Append(P("5. Thao tác thường dùng", "h1"));
            sb.Append(P("5.1. Đồng bộ số từ API", "h2"));
            sb.Append(Bullet("Mở tab “Add số”."));
            sb.Append(Bullet("Bấm “Chạy 1 lần” để chạy thủ công."));
            sb.Append(Bullet("Hoặc bấm “Bắt đầu lịch” để chạy tự động theo chu kỳ đã cấu hình."));
            sb.Append(Bullet("Nút “Tạm dừng lịch” để ngừng tự động."));

            sb.Append(P("Quy tắc đồng bộ:", "h2"));
            sb.Append(Bullet("CHỈ cập nhật trạng thái (khóa/mở) cho số ĐÃ CÓ trên VOS3000 — không tự thêm số mới."));
            sb.Append(Bullet("Số API trả về nhưng VOS3000 chưa có → là SỐ MỚI: KHÔNG add, chỉ lưu bảng t_number_missing (tab “Số chưa có (VOS)”)."));
            sb.Append(Bullet("Số đã xuất hiện trên server ở bất kỳ account nào (call-in/call-out) được coi là “đã có”."));
            sb.Append(Bullet("So khớp theo gốc số: 0123456781 = 840123456781 = #840123456781 là một."));
            sb.Append(Bullet("API trả #84xxxxx → đưa số về trạng thái khóa trên server."));
            sb.Append(Bullet("API trả 84xxxxx → đưa số về trạng thái mở trên server."));
            sb.Append(Bullet("Cùng một số có cả hai trạng thái trong 1 payload → trạng thái đứng sau thắng."));
            sb.Append(Bullet("Danh sách có entry trùng → tự dọn, giữ 1 entry mỗi số."));
            sb.Append(Bullet("Số trong bảng “chưa có” nay đã xuất hiện trên server → tự xoá khỏi bảng."));
            sb.Append(Bullet("Nếu người khác đang sửa prefix cùng lúc → tool tự đọc lại và thử lại (tối đa 3 lần)."));

            sb.Append(P("5.2. Xem / xuất danh sách số chưa có", "h2"));
            sb.Append(Bullet("Mở tab “Số chưa có (VOS)” — danh sách tự tải khi mở tab."));
            sb.Append(Bullet("Tìm theo số, xem trạng thái API (Khóa/Mở), lần thấy đầu/cuối, số lần gặp."));
            sb.Append(Bullet("Bấm “Xuất Excel (.xlsx)” để lưu danh sách ra file Excel."));
            sb.Append(Bullet("“Xoá danh sách” chỉ xoá bảng trong DB tool, không ảnh hưởng VOS3000."));

            sb.Append(P("5.3. Khóa / mở số", "h2"));
            sb.Append(Bullet("Mở tab “Khóa số (VOS)” → “Tải danh sách số”."));
            sb.Append(Bullet("Tìm số trong ô tìm kiếm, hoặc dùng nút chuyển trang."));
            sb.Append(Bullet("Chọn số cần xử lý, bấm “Khóa số ” hoặc “Mở khóa”."));
            sb.Append(Bullet("Tích ✔ ở cột đầu để chọn NHIỀU số, rồi bấm “Khóa số”/“Mở khóa” — áp dụng một lần cho tất cả số đã tích."));
            sb.Append(Bullet("“☑ Chọn trang” / “☐ Bỏ chọn” để tích hoặc bỏ tích toàn bộ số ở trang đang xem; lựa chọn được giữ khi chuyển trang."));
            sb.Append(Bullet("Bấm “Cập nhật theo lưới” để ghi các thay đổi sửa trực tiếp trên bảng."));

            sb.Append(P("Lưu ý quan trọng:", "h2"));
            sb.Append(P("API nguồn là chuẩn cuối cùng cho các số ĐÃ CÓ trên server. Nếu một số bị mở bằng tay " +
                         "nhưng API vẫn trả #, lần đồng bộ kế tiếp sẽ khóa lại số đó.", "note"));
            sb.Append(P("Số chưa có trên VOS3000 sẽ KHÔNG được tự thêm — chỉ lưu vào bảng t_number_missing " +
                         "để người vận hành kiểm tra và xử lý riêng.", "note"));


            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                   "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">\n" +
                   "<w:body>" + sb +
                   "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                   "<w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1418\"/></w:sectPr>" +
                   "</w:body>\n</w:document>";
        }

        private static string BuildStylesXml() => @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:styles xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
<w:docDefaults><w:rPrDefault><w:rPr>
  <w:rFonts w:ascii=""Calibri"" w:hAnsi=""Calibri"" w:cs=""Calibri""/><w:sz w:val=""22""/><w:szCs w:val=""22""/>
</w:rPr></w:rPrDefault></w:docDefaults>
<w:style w:type=""paragraph"" w:styleId=""Normal"" w:default=""1""><w:name w:val=""Normal""/>
  <w:pPr><w:spacing w:after=""120"" w:line=""276"" w:lineRule=""auto""/></w:pPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Title""><w:name w:val=""Title""/><w:basedOn w:val=""Normal""/>
  <w:pPr><w:spacing w:after=""240""/><w:jc w:val=""center""/></w:pPr>
  <w:rPr><w:b/><w:color w:val=""006935""/><w:sz w:val=""44""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Heading1""><w:name w:val=""heading 1""/><w:basedOn w:val=""Normal""/>
  <w:pPr><w:spacing w:before=""320"" w:after=""140""/><w:pBdr><w:bottom w:val=""single"" w:sz=""8"" w:space=""4"" w:color=""00A350""/></w:pBdr></w:pPr>
  <w:rPr><w:b/><w:color w:val=""006935""/><w:sz w:val=""32""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Heading2""><w:name w:val=""heading 2""/><w:basedOn w:val=""Normal""/>
  <w:pPr><w:spacing w:before=""240"" w:after=""100""/></w:pPr>
  <w:rPr><w:b/><w:color w:val=""1F5C3F""/><w:sz w:val=""26""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Code""><w:name w:val=""Code""/><w:basedOn w:val=""Normal""/>
  <w:pPr><w:shd w:val=""clear"" w:color=""auto"" w:fill=""F1F5F2""/><w:ind w:left=""280""/><w:spacing w:after=""120""/></w:pPr>
  <w:rPr><w:rFonts w:ascii=""Consolas"" w:hAnsi=""Consolas""/><w:sz w:val=""20""/><w:color w:val=""1F5C3F""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Note""><w:name w:val=""Note""/><w:basedOn w:val=""Normal""/>
  <w:pPr><w:shd w:val=""clear"" w:color=""auto"" w:fill=""FFF6E5""/><w:pBdr><w:left w:val=""single"" w:sz=""18"" w:space=""6"" w:color=""E90100""/></w:pBdr><w:ind w:left=""280""/></w:pPr>
  <w:rPr><w:color w:val=""7A4B00""/><w:sz w:val=""21""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""TableHead""><w:name w:val=""TableHead""/><w:basedOn w:val=""Normal""/>
  <w:pPr><w:spacing w:after=""0""/></w:pPr><w:rPr><w:b/><w:color w:val=""FFFFFF""/></w:rPr></w:style>
<w:style w:type=""table"" w:styleId=""TableGrid""><w:name w:val=""Table Grid""/>
  <w:tblPr><w:tblBorders>
    <w:top w:val=""single"" w:sz=""4"" w:color=""BFD9CA""/><w:left w:val=""single"" w:sz=""4"" w:color=""BFD9CA""/>
    <w:bottom w:val=""single"" w:sz=""4"" w:color=""BFD9CA""/><w:right w:val=""single"" w:sz=""4"" w:color=""BFD9CA""/>
    <w:insideH w:val=""single"" w:sz=""4"" w:color=""BFD9CA""/><w:insideV w:val=""single"" w:sz=""4"" w:color=""BFD9CA""/>
  </w:tblBorders></w:tblPr></w:style>
</w:styles>";

        // ---------- ghi file .docx ----------
        public static void Write(string path, Settings s)
        {
            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            void Add(string entry, string content)
            {
                var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
                "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
                "</Types>");

            Add("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
                "</Relationships>");

            Add("word/_rels/document.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");

            Add("word/document.xml", BuildDocumentXml(s));
            Add("word/styles.xml", BuildStylesXml());

            Add("docProps/core.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
                "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                "<dc:title>VOS3000 Agent - Huong dan cai dat va su dung</dc:title>" +
                "<dc:creator>VOS3000 Agent</dc:creator>" +
                "<cp:lastModifiedBy>VOS3000 Agent</cp:lastModifiedBy>" +
                "<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:created>" +
                "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:modified>" +
                "</cp:coreProperties>");

            Add("docProps/app.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
                "<Application>VOS3000 Agent</Application></Properties>");
        }
    }
}
