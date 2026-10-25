using System.Globalization;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace ScheduleBot;

public static class ScheduleParser
{
    // Структура файла: строка 4 — названия групп (колонки 4, 6, 8 ...; рядом справа — "Ауд.").
    // С строки 6 идут блоки по 3 строки на пару: 1-я — предмет и аудитория, 3-я — преподаватель.
    // Колонки: 0 — день недели, 1 — дата, 2 — номер пары, 3 — время.
    public static Schedule Parse(Stream xls)
    {
        var sheet = new HSSFWorkbook(xls).GetSheetAt(0);

        string Cell(int r, int c)
        {
            var cell = sheet.GetRow(r)?.GetCell(c);
            if (cell == null) return "";
            return cell.CellType == CellType.Numeric
                ? cell.NumericCellValue.ToString(CultureInfo.InvariantCulture)
                : (cell.ToString() ?? "").Trim();
        }

        var groups = new List<(int Col, string Name)>();
        int lastCol = sheet.GetRow(4).LastCellNum;
        for (int c = 4; c < lastCol; c += 2)
        {
            var name = Cell(4, c);
            if (name != "") groups.Add((c, name));
        }

        var lessons = new List<Lesson>();
        string day = "";
        var date = default(DateOnly);

        for (int r = 6; r + 2 <= sheet.LastRowNum; r += 3)
        {
            if (Cell(r, 0) != "")
            {
                day = Cell(r, 0);
                DateOnly.TryParseExact(Cell(r, 1), "dd.MM.yyyy", out date);
            }
            if (!double.TryParse(Cell(r, 2).Replace(',', '.'), NumberStyles.Any,
                                 CultureInfo.InvariantCulture, out var num)) continue;

            var time = Cell(r, 3);
            foreach (var (col, group) in groups)
            {
                var subject = Cell(r, col);
                if (subject == "") continue;
                lessons.Add(new Lesson(date, day, (int)num, time, subject,
                                       Cell(r, col + 1), Cell(r + 2, col), group));
            }
        }
        return new Schedule(lessons, groups.Select(g => g.Name).ToList());
    }
}