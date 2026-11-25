// ============================================================
//  XlsxWriter - ghi file Excel .xlsx tối giản (1 sheet) bằng
//  OpenXML có sẵn trong .NET (System.IO.Compression), KHÔNG
//  cần thư viện ngoài (giữ build portable 1 file như hiện tại).
//
//  Mọi ô đều ghi dạng inline string -> số điện thoại giữ
//  nguyên "0…", "#84…", không bị Excel đổi thành số.
// ============================================================
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Vos3000Agent
{
    public static class XlsxWriter
    {
        const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string NsXml = "http://www.w3.org/XML/1998/namespace";

        /// <summary>
        /// Ghi 1 sheet. headers = dòng tiêu đề (đậm, đóng băng); rows = dữ liệu dạng chuỗi.
        /// columnWidths = độ rộng từng cột (ký tự), có thể null.
        /// </summary>
        public static void Write(string path, string sheetName, IReadOnlyList<string> headers,
            IEnumerable<string[]> rows, IReadOnlyList<double> columnWidths = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Thiếu đường dẫn file Excel.", nameof(path));

            var all = rows?.ToList() ?? new List<string[]>();
            int cols = headers?.Count ?? 0;
            foreach (var r in all) if (r != null && r.Length > cols) cols = r.Length;
            cols = Math.Max(1, cols);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            AddText(zip, "[Content_Types].xml", ContentTypesXml);
            AddText(zip, "_rels/.rels", RootRelsXml);
            AddText(zip, "xl/workbook.xml", WorkbookXml(SafeSheetName(sheetName)));
            AddText(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            AddText(zip, "xl/styles.xml", StylesXml);
            AddSheet(zip, headers, all, cols, columnWidths);
        }

        // ---------- các part tĩnh ----------

        const string ContentTypesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        const string RootRelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        const string WorkbookRelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        const string StylesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\">" +
            "<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "</fonts>" +
            "<fills count=\"2\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "</fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        static string WorkbookXml(string sheetName) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"" + NsMain + "\" xmlns:r=\"" + NsRel + "\">" +
            "<sheets><sheet name=\"" + XmlEscape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>";

        // ---------- sheet ----------

        static void AddSheet(ZipArchive zip, IReadOnlyList<string> headers, List<string[]> rows,
            int cols, IReadOnlyList<double> widths)
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                CloseOutput = false
            });

            xml.WriteStartDocument(true);
            xml.WriteStartElement("worksheet", NsMain);

            // Đóng băng dòng tiêu đề
            xml.WriteStartElement("sheetViews", NsMain);
            xml.WriteStartElement("sheetView", NsMain);
            xml.WriteAttributeString("workbookViewId", "0");
            xml.WriteStartElement("pane", NsMain);
            xml.WriteAttributeString("ySplit", "1");
            xml.WriteAttributeString("topLeftCell", "A2");
            xml.WriteAttributeString("activePane", "bottomLeft");
            xml.WriteAttributeString("state", "frozen");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();

            if (widths != null && widths.Count > 0)
            {
                xml.WriteStartElement("cols", NsMain);
                for (int i = 0; i < widths.Count; i++)
                {
                    xml.WriteStartElement("col", NsMain);
                    xml.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("width", Math.Max(4, widths[i]).ToString("0.#", CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("customWidth", "1");
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }

            xml.WriteStartElement("sheetData", NsMain);

            int rowNum = 1;
            if (headers != null && headers.Count > 0)
                WriteRow(xml, rowNum++, headers, header: true);
            foreach (var r in rows)
                WriteRow(xml, rowNum++, r ?? Array.Empty<string>(), header: false);

            xml.WriteEndElement();   // sheetData

            if (rowNum > 2)
            {
                xml.WriteStartElement("autoFilter", NsMain);
                xml.WriteAttributeString("ref", $"A1:{ColumnLetter(cols)}{rowNum - 1}");
                xml.WriteEndElement();
            }

            xml.WriteEndElement();   // worksheet
            xml.WriteEndDocument();
        }

        static void WriteRow(XmlWriter xml, int rowNum, IReadOnlyList<string> cells, bool header)
        {
            xml.WriteStartElement("row", NsMain);
            xml.WriteAttributeString("r", rowNum.ToString(CultureInfo.InvariantCulture));

            int count = cells?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                xml.WriteStartElement("c", NsMain);
                xml.WriteAttributeString("r", ColumnLetter(i + 1) + rowNum.ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("t", "inlineStr");
                if (header) xml.WriteAttributeString("s", "1");

                xml.WriteStartElement("is", NsMain);
                xml.WriteStartElement("t", NsMain);
                xml.WriteAttributeString("xml", "space", NsXml, "preserve");
                xml.WriteString(SafeText(cells[i]));
                xml.WriteEndElement();   // t
                xml.WriteEndElement();   // is
                xml.WriteEndElement();   // c
            }

            xml.WriteEndElement();       // row
        }

        // ---------- tiện ích ----------

        static void AddText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(text);
        }

        /// <summary>A1, B1, ..., Z1, AA1, ...</summary>
        internal static string ColumnLetter(int index)
        {
            var sb = new StringBuilder(3);
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        static string SafeSheetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Sheet1";
            var sb = new StringBuilder();
            foreach (var ch in name)
                sb.Append(":\\/?*[]".IndexOf(ch) >= 0 ? '_' : ch);
            var s = sb.ToString().Trim('\'', ' ');
            if (s.Length == 0) s = "Sheet1";
            return s.Length > 31 ? s.Substring(0, 31) : s;
        }

        static string XmlEscape(string s) => (s ?? "")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");

        /// <summary>Loại ký tự điều khiển không hợp lệ trong XML; chuẩn hoá CR/LF.</summary>
        static string SafeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch == '\r') continue;
                if (ch == '\n' || ch == '\t' || ch >= ' ') sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
