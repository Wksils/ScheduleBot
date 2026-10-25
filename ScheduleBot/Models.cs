using System;
using System.Collections.Generic;
using System.Text;

namespace ScheduleBot;

public record Lesson(DateOnly Date, string Day, int Num, string Time,
                     string Subject, string Room, string Teacher, string Group);

public record Schedule(List<Lesson> Lessons, List<string> Groups);

public class UserState
{
    public string Mode { get; set; } = "";     // "group" или "teacher"; пусто = ещё ничего не выбрано
    public string Group { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Awaiting { get; set; } = ""; // "teacher" = ждём ввод ФИО, "upload" = ждём файл от админа
}