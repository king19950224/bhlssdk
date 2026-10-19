using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FxMonitor;

/// <summary>
/// 最小 xlsx（zip + XML），不引第三方库。表头用中文，数字格带 0.00 / 整数格式，方便 Excel 直接筛选。
/// </summary>
internal sealed class XlsxBook : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly string[] _names;
    private int _next;

    private XlsxBook(ZipArchive zip, string[] names)
    {
        _zip = zip;
        _names = names;
    }

    public static void Save(string path, IReadOnlyList<string> sheetNames, Action<XlsxBook> write)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var book = Create(tmp, sheetNames))
                write(book);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public SheetWriter Open(string name, IReadOnlyList<string> headers, IReadOnlyList<double> widths, bool filter = true)
    {
        if (_next >= _names.Length || _names[_next] != name)
            throw new InvalidOperationException("工作表顺序和目录不一致");
        var index = _next++;
        var entry = _zip.CreateEntry($"xl/worksheets/sheet{index + 1}.xml", CompressionLevel.Fastest);
        return new SheetWriter(entry.Open(), headers, widths, filter, selected: index == 0);
    }

    public void Dispose() => _zip.Dispose();

    private static XlsxBook Create(string path, IReadOnlyList<string> sheetNames)
    {
        if (sheetNames.Count == 0) throw new ArgumentException("至少要一个工作表", nameof(sheetNames));
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        try
        {
            var names = sheetNames.ToArray();
            WriteText(zip, "[Content_Types].xml", ContentTypes(names.Length));
            WriteText(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteText(zip, "xl/workbook.xml", Workbook(names));
            WriteText(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(names.Length));
            WriteText(zip, "xl/styles.xml", Styles);
            return new XlsxBook(zip, names);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            """);
        for (var i = 1; i <= sheets; i++)
        {
            sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i)
                .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string Workbook(IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <bookViews><workbookView activeTab="0"/></bookViews>
              <sheets>
            """);
        for (var i = 0; i < names.Count; i++)
        {
            sb.Append("<sheet name=\"").Append(Xml(names[i])).Append("\" sheetId=\"").Append(i + 1)
                .Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
        }

        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private static string WorkbookRels(int sheets)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            """);
        for (var i = 1; i <= sheets; i++)
        {
            sb.Append("<Relationship Id=\"rId").Append(i)
                .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet")
                .Append(i).Append(".xml\"/>");
        }

        sb.Append("<Relationship Id=\"rId").Append(sheets + 1)
            .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2">
            <font><sz val="11"/><color theme="1"/><name val="微软雅黑"/><family val="2"/></font>
            <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="微软雅黑"/><family val="2"/></font>
          </fonts>
          <fills count="3">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E79"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="1">
            <border><left/><right/><top/><bottom/><diagonal/></border>
          </borders>
          <cellStyleXfs count="1">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
          </cellStyleXfs>
          <cellXfs count="4">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1">
            <alignment horizontal="center" vertical="center"/>
          </xf>
            <xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="1" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
          </cellXfs>
          <cellStyles count="1">
            <cellStyle name="Normal" xfId="0" builtinId="0"/>
          </cellStyles>
        </styleSheet>
        """;

    internal static string Xml(string s)
    {
        var sb = new StringBuilder(s.Length);
        AppendXml(sb, s);
        return sb.ToString();
    }

    internal static void AppendXml(StringBuilder sb, string s)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                    sb.Append(c);
                    break;
            }
        }
    }
}

/// <summary>一行一行往当前工作表写。用完必须关掉，才能开下一张表。</summary>
internal sealed class SheetWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly bool _filter;
    private readonly string _lastCol;
    private readonly StringBuilder _row = new();
    private int _rowNum = 1;
    private int _col;
    private int _dataRows;
    private bool _closed;

    public SheetWriter(Stream stream, IReadOnlyList<string> headers, IReadOnlyList<double> widths, bool filter, bool selected)
    {
        if (headers.Count == 0) throw new ArgumentException("表头不能为空", nameof(headers));
        _filter = filter;
        _lastCol = ColName(headers.Count - 1);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 65536);
        _writer.Write("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            """);
        _writer.Write(selected
            ? "<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\">"
            : "<sheetViews><sheetView workbookViewId=\"0\">");
        _writer.Write("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        _writer.Write("<selection pane=\"bottomLeft\" activeCell=\"A2\" sqref=\"A2\"/></sheetView></sheetViews>");
        _writer.Write("<sheetFormatPr defaultRowHeight=\"18\"/>");
        _writer.Write("<cols>");
        for (var i = 0; i < headers.Count; i++)
        {
            var width = i < widths.Count ? widths[i] : 18;
            _writer.Write("<col min=\"");
            _writer.Write(i + 1);
            _writer.Write("\" max=\"");
            _writer.Write(i + 1);
            _writer.Write("\" width=\"");
            _writer.Write(width.ToString("0.##", CultureInfo.InvariantCulture));
            _writer.Write("\" customWidth=\"1\"/>");
        }

        _writer.Write("</cols><sheetData>");
        _row.Append("<row r=\"1\">");
        for (var i = 0; i < headers.Count; i++)
        {
            _row.Append("<c r=\"").Append(ColName(i)).Append("1\" t=\"inlineStr\" s=\"1\"><is><t>");
            XlsxBook.AppendXml(_row, headers[i]);
            _row.Append("</t></is></c>");
        }

        _row.Append("</row>");
        _writer.WriteLine(_row);
    }

    public void BeginRow()
    {
        _rowNum++;
        _col = 0;
        _row.Clear();
        _row.Append("<row r=\"").Append(_rowNum.ToString(CultureInfo.InvariantCulture)).Append("\">");
    }

    public void Text(string? value)
    {
        var col = _col++;
        if (string.IsNullOrEmpty(value)) return;
        _row.Append("<c r=\"").Append(ColName(col)).Append(_rowNum.ToString(CultureInfo.InvariantCulture));
        _row.Append("\" t=\"inlineStr\"");
        if (value[0] == ' ' || value[^1] == ' ') _row.Append(" xml:space=\"preserve\"");
        _row.Append("><is><t>");
        XlsxBook.AppendXml(_row, value);
        _row.Append("</t></is></c>");
    }

    /// <summary>0.00 格式。空值留空。</summary>
    public void Dec(double? value)
    {
        var col = _col++;
        if (value is not { } v || !double.IsFinite(v)) return;
        _row.Append("<c r=\"").Append(ColName(col)).Append(_rowNum.ToString(CultureInfo.InvariantCulture));
        _row.Append("\" s=\"2\"><v>");
        _row.Append(v.ToString("G17", CultureInfo.InvariantCulture));
        _row.Append("</v></c>");
    }

    public void Int(long? value)
    {
        var col = _col++;
        if (value is not { } v) return;
        _row.Append("<c r=\"").Append(ColName(col)).Append(_rowNum.ToString(CultureInfo.InvariantCulture));
        _row.Append("\" s=\"3\"><v>");
        _row.Append(v.ToString(CultureInfo.InvariantCulture));
        _row.Append("</v></c>");
    }

    public void EndRow()
    {
        _row.Append("</row>");
        _writer.WriteLine(_row);
        _dataRows++;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _writer.Write("</sheetData>");
        if (_filter && _dataRows > 0)
        {
            _writer.Write("<autoFilter ref=\"A1:");
            _writer.Write(_lastCol);
            _writer.Write((_dataRows + 1).ToString(CultureInfo.InvariantCulture));
            _writer.Write("\"/>");
        }

        _writer.Write("</worksheet>");
        _writer.Dispose();
    }

    private static string ColName(int index)
    {
        Span<char> buf = stackalloc char[3];
        var n = index + 1;
        var i = buf.Length;
        while (n > 0)
        {
            n--;
            buf[--i] = (char)('A' + n % 26);
            n /= 26;
        }

        return new string(buf[i..]);
    }
}
