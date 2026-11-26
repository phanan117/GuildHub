using GuildHub.Core.Metadata;
using System.Text;
using ClosedXML.Excel;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;

namespace GuildHub.Bot.Services;

public sealed record ExcelExportResult(
    byte[] Content,
    string FileName,
    int ActiveCount,
    int AbsentCount,
    int NotParticipatingCount);

public sealed class EventExcelExporter
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public ExcelExportResult Export(WarEvent ev)
    {
        var activeRoster = ev.Signups
            .Where(s => s.GameClass.HasValue &&
                        s.Status is SignupStatus.Confirmed or SignupStatus.Late or SignupStatus.Bench)
            .OrderBy(s => (int)s.GameClass!.Value)
            .ThenBy(s => s.SignupOrder)
            .ToList();

        var inactiveList = ev.Signups
            .Where(s => s.Status is SignupStatus.Absent or SignupStatus.NotParticipating)
            .OrderBy(s => (int)s.Status)
            .ThenBy(s => s.SignupOrder)
            .ToList();

        var absentCount = inactiveList.Count(s => s.Status == SignupStatus.Absent);
        var notParticipatingCount = inactiveList.Count(s => s.Status == SignupStatus.NotParticipating);

        using var workbook = new XLWorkbook();

        // Sheet 1: Danh sách tham gia (STT, Ingame, Hệ phái, Trạng thái, Ghi chú)
        var mainSheet = workbook.Worksheets.Add("Danh sach tham gia");
        WriteSignupSheet(mainSheet, activeRoster);

        // Sheet 2: Vắng mặt & Không tham gia
        var absentSheet = workbook.Worksheets.Add("Vang - Khong tham gia");
        WriteSignupSheet(absentSheet, inactiveList);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);

        var nowVn = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
        var slug = BuildSafeFileSlug(ev.Title);
        var fileName = $"danh-sach-{slug}-event-{ev.Id}-{nowVn:yyyyMMdd-HHmm}.xlsx";

        return new ExcelExportResult(
            Content: ms.ToArray(),
            FileName: fileName,
            ActiveCount: activeRoster.Count,
            AbsentCount: absentCount,
            NotParticipatingCount: notParticipatingCount);
    }

    private static void WriteSignupSheet(IXLWorksheet sheet, List<EventSignup> rows)
    {
        string[] headers = ["STT", "Ingame", "Hệ phái", "Trạng thái", "Ghi chú"];

        for (var col = 0; col < headers.Length; col++)
        {
            var cell = sheet.Cell(1, col + 1);
            cell.Value = headers[col];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#7B3FE4");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        sheet.Row(1).Height = 24;

        for (var i = 0; i < rows.Count; i++)
        {
            var r = i + 2;
            var item = rows[i];

            var className = item.GameClass.HasValue
                ? ClassMetadata.Get(item.GameClass.Value).DisplayName
                : string.Empty;

            var statusText = item.Status switch
            {
                SignupStatus.Confirmed => "Tham gia",
                SignupStatus.Late => "Đi trễ",
                SignupStatus.Bench => "Dự bị",
                SignupStatus.Absent => "Vắng mặt",
                SignupStatus.NotParticipating => "Không tham gia",
                _ => item.Status.ToString()
            };

            sheet.Cell(r, 1).Value = i + 1;
            sheet.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(r, 2).Value = item.DisplayName;
            sheet.Cell(r, 3).Value = className;
            sheet.Cell(r, 4).Value = statusText;
            sheet.Cell(r, 5).Value = item.Note ?? string.Empty;

            if (i % 2 == 1)
            {
                sheet.Range(r, 1, r, 5).Style.Fill.BackgroundColor = XLColor.FromHtml("#F5F3FF");
            }
        }

        var lastRow = Math.Max(rows.Count + 1, 1);
        var tableRange = sheet.Range(1, 1, lastRow, 5);
        tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
        tableRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#E2E8F0");

        if (rows.Count > 0)
        {
            tableRange.SetAutoFilter();
        }

        // Đặt chiều rộng cột tường minh (không dùng AdjustToContents để chạy mượt trên Linux Docker không cần cài font hệ thống)
        sheet.Column(1).Width = 8;
        sheet.Column(2).Width = 28;
        sheet.Column(3).Width = 18;
        sheet.Column(4).Width = 18;
        sheet.Column(5).Width = 34;

        sheet.SheetView.FreezeRows(1);
    }

    private static string BuildSafeFileSlug(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        var prevDash = false;

        foreach (var ch in normalized)
        {
            var mapped = ch switch
            {
                'đ' or 'Đ' => 'd',
                _ => ch
            };

            if (char.IsAsciiLetterOrDigit(mapped))
            {
                sb.Append(char.ToLowerInvariant(mapped));
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0 && (char.IsWhiteSpace(mapped) || mapped is '-' or '_'))
            {
                sb.Append('-');
                prevDash = true;
            }

            if (sb.Length >= 32)
            {
                break;
            }
        }

        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "su-kien" : slug;
    }
}

