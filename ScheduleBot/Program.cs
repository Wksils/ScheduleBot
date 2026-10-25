using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScheduleBot;
using VkNet;
using VkNet.Enums.StringEnums;
using VkNet.Model;
using Document = VkNet.Model.Document;

// ---------- настройки (переменные окружения) ----------
var token = Environment.GetEnvironmentVariable("VK_TOKEN") ?? throw new Exception("Задайте VK_TOKEN");
var groupId = ulong.Parse(Environment.GetEnvironmentVariable("VK_GROUP_ID") ?? throw new Exception("Задайте VK_GROUP_ID"));
var adminId = long.Parse(Environment.GetEnvironmentVariable("VK_ADMIN_ID") ?? throw new Exception("Задайте VK_ADMIN_ID"));

const string ScheduleFile = "schedule.json";
const string UsersFile = "users.json";
const int GroupsPerPage = 27; // 9 рядов по 3 кнопки + ряд навигации (лимит VK — 10 рядов, 40 кнопок)

var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

var schedule = File.Exists(ScheduleFile)
    ? JsonSerializer.Deserialize<Schedule>(File.ReadAllText(ScheduleFile), json)!
    : new Schedule(new(), new());
var users = File.Exists(UsersFile)
    ? JsonSerializer.Deserialize<Dictionary<long, UserState>>(File.ReadAllText(UsersFile), json)!
    : new();

var api = new VkApi();
await api.AuthorizeAsync(new ApiAuthParams { AccessToken = token });
Console.WriteLine("Бот запущен");

// ---------- цикл Long Poll ----------
while (true)
{
    try
    {
        var server = await api.Groups.GetLongPollServerAsync(groupId);
        var ts = server.Ts;
        while (true)
        {
            var poll = await api.Groups.GetBotsLongPollHistoryAsync(new BotsLongPollHistoryParams
            {
                Server = server.Server,
                Key = server.Key,
                Ts = ts,
                Wait = 25
            });
            ts = poll.Ts;
            if (poll.Updates == null) continue;

            foreach (var upd in poll.Updates)
            {
                if (upd.Instance is not MessageNew mn) continue;
                try { await Handle(mn.Message); }
                catch (Exception e) { Console.WriteLine("Ошибка обработки: " + e); }
            }
        }
    }
    catch (Exception e)
    {
        Console.WriteLine("Long Poll упал, переподключение: " + e.Message);
        await Task.Delay(3000);
    }
}

// ---------- обработка сообщений ----------
async Task Handle(Message msg)
{
    long peer = msg.PeerId ?? 0, from = msg.FromId ?? 0;
    var text = (msg.Text ?? "").Trim();
    if (!users.TryGetValue(from, out var user)) users[from] = user = new UserState();

    // 1. нажатие кнопки (приходит с payload вида {"b":"команда|значение"})
    if (!string.IsNullOrEmpty(msg.Payload))
    {
        var p = JsonSerializer.Deserialize<Dictionary<string, string>>(msg.Payload);
        var parts = p != null && p.TryGetValue("b", out var raw) ? raw.Split('|', 2) : null;
        if (parts is { Length: 2 })
        {
            var cmd = parts[0];
            var v = parts[1];
            switch (cmd)
            {
                case "p":
                    await Send(peer, "Выберите группу:", GroupsKeyboard(int.Parse(v)));
                    return;
                case "g":
                    if (!schedule.Groups.Contains(v)) { await Send(peer, "Такой группы нет в расписании."); return; }
                    user.Group = v; user.Mode = "group"; user.Awaiting = "";
                    Save();
                    await Send(peer, $"Группа установлена: {v}\nРасписание: /schedule");
                    return;
                case "s":
                    await Send(peer, BuildSchedule(user, v));
                    return;
            }
        }
    }

    // 2. команды
    if (text.StartsWith('/'))
    {
        user.Awaiting = "";
        switch (text.Split(' ')[0].ToLowerInvariant())
        {
            case "/start":
                await Send(peer, "Привет! Я показываю расписание колледжа.\n\n" +
                    "/setGroup — выбрать группу\n/setTeacher — искать по преподавателю\n/schedule — показать расписание");
                break;
            case "/setgroup":
                if (schedule.Groups.Count == 0) { await Send(peer, "Расписание ещё не загружено."); break; }
                await Send(peer, "Выберите группу:", GroupsKeyboard(0));
                break;
            case "/setteacher":
                if (schedule.Lessons.Count == 0) { await Send(peer, "Расписание ещё не загружено."); break; }
                user.Awaiting = "teacher";
                await Send(peer, "Введите имя преподавателя (Фамилия И.О.)");
                break;
            case "/schedule":
                if (user.Mode == "") { await Send(peer, "Сначала выберите группу (/setGroup) или преподавателя (/setTeacher)."); break; }
                var kb = new KeyboardBuilder().SetOneTime()
                    .AddButton("Сегодня", Payload("s", "today"), KeyboardButtonColor.Primary)
                    .AddButton("Завтра", Payload("s", "tomorrow"), KeyboardButtonColor.Primary)
                    .AddButton("Неделя", Payload("s", "week"), KeyboardButtonColor.Primary)
                    .Build();
                var who = user.Mode == "group" ? $"группа {user.Group}" : $"преподаватель {user.Teacher}";
                await Send(peer, $"Расписание: {who}. Выберите период:", kb);
                break;
            case "/upload":
                if (from != adminId) { await Send(peer, "Нет доступа."); break; }
                user.Awaiting = "upload";
                await Send(peer, "Отправьте файл расписания (.xls)");
                break;
            default:
                await Send(peer, "Неизвестная команда. Начните с /start");
                break;
        }
        Save();
        return;
    }

    // 3. обычное сообщение — зависит от того, чего мы ждём
    if (user.Awaiting == "teacher")
    {
        var wanted = Norm(text);
        var found = schedule.Lessons.Select(l => l.Teacher)
            .FirstOrDefault(t => t != "" && Norm(t) == wanted);
        if (found == null)
        {
            await Send(peer, "Преподаватель не найден. Введите в формате Фамилия И.О., например: Дубинин А. В.");
            return;
        }
        user.Teacher = found; user.Mode = "teacher"; user.Awaiting = "";
        Save();
        await Send(peer, $"Преподаватель установлен: {FormatTeacher(found)}\nРасписание: /schedule");
        return;
    }

    if (user.Awaiting == "upload" && from == adminId)
    {
        var doc = msg.Attachments?.Select(a => a.Instance).OfType<Document>().FirstOrDefault();
        if (doc == null || doc.Ext?.ToLowerInvariant() != "xls")
        {
            await Send(peer, "Нужен файл в формате .xls. Отправьте его или выберите другую команду.");
            return;
        }
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(doc.Uri);
        var parsed = ScheduleParser.Parse(new MemoryStream(bytes));
        if (parsed.Lessons.Count == 0) { await Send(peer, "В файле не найдено ни одного занятия — проверьте файл."); return; }

        schedule = parsed;
        File.WriteAllText(ScheduleFile, JsonSerializer.Serialize(schedule, json));
        user.Awaiting = "";
        Save();
        var d = schedule.Lessons.Select(l => l.Date).Where(x => x != default).ToList();
        await Send(peer, $"Расписание загружено: {d.Min():dd.MM.yyyy} – {d.Max():dd.MM.yyyy}, " +
                         $"групп: {schedule.Groups.Count}, занятий: {schedule.Lessons.Count}");
        return;
    }

    await Send(peer, "Используйте команды: /setGroup, /setTeacher, /schedule");
}

// ---------- расписание ----------
string BuildSchedule(UserState u, string period)
{
    var mine = u.Mode == "group"
        ? schedule.Lessons.Where(l => l.Group == u.Group)
        : schedule.Lessons.Where(l => Norm(l.Teacher) == Norm(u.Teacher));

    var today = DateOnly.FromDateTime(DateTime.Now);
    var dates = period switch
    {
        "today" => new List<DateOnly> { today },
        "tomorrow" => new List<DateOnly> { today.AddDays(1) },
        _ => mine.Select(l => l.Date).Distinct().OrderBy(d => d).ToList()
    };
    if (dates.Count == 0) return "На этой неделе занятий нет.";

    var loaded = schedule.Lessons.Select(l => l.Date).ToHashSet();
    var sb = new System.Text.StringBuilder();
    foreach (var date in dates)
    {
        if (!loaded.Contains(date)) { sb.AppendLine($"{date:dd.MM.yyyy}: расписание на эту дату не загружено\n"); continue; }
        sb.AppendLine($"{DayName(date)} | {date:dd.MM.yyyy}");

        var day = mine.Where(l => l.Date == date).OrderBy(l => l.Num).ToList();
        if (day.Count == 0) sb.AppendLine("Занятий нет");

        // у преподавателя одна пара может идти у нескольких групп сразу — склеиваем в одну строку
        foreach (var g in day.GroupBy(l => (l.Num, l.Time, l.Subject, l.Room)))
        {
            var last = u.Mode == "group"
                ? FormatTeacher(g.First().Teacher)
                : string.Join(", ", g.Select(l => l.Group));
            sb.AppendLine($"{g.Key.Num}. | {g.Key.Time} | {g.Key.Subject} | {(g.Key.Room == "" ? "-" : g.Key.Room)} | {last}");
        }
        sb.AppendLine();
    }
    return sb.ToString().TrimEnd();
}

// ---------- вспомогательное ----------
MessageKeyboard GroupsKeyboard(int page)
{
    var all = schedule.Groups;
    var pages = (all.Count + GroupsPerPage - 1) / GroupsPerPage;
    page = Math.Clamp(page, 0, pages - 1);
    var items = all.Skip(page * GroupsPerPage).Take(GroupsPerPage).ToList();

    var kb = new KeyboardBuilder().SetOneTime();
    for (int i = 0; i < items.Count; i++)
    {
        if (i > 0 && i % 3 == 0) kb.AddLine();
        kb.AddButton(items[i], Payload("g", items[i]));
    }
    if (pages > 1)
    {
        kb.AddLine();
        if (page > 0) kb.AddButton("◀ Назад", Payload("p", (page - 1).ToString()), KeyboardButtonColor.Primary);
        if (page < pages - 1) kb.AddButton("Вперёд ▶", Payload("p", (page + 1).ToString()), KeyboardButtonColor.Primary);
    }
    return kb.Build();
}

// VkNet сам оборачивает это в {"b":"..."}, поэтому кладём просто короткую строку "команда|значение"
string Payload(string cmd, string v) => $"{cmd}|{v}";

// "Мартыненко А.И." и "Мартыненко А. И." считаем одним и тем же
string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е'), @"\s+", "");
string FormatTeacher(string t) => Regex.Replace(t, @"\.(?=\p{L})", ". ");
string DayName(DateOnly d) => new[] { "Воскресенье", "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота" }[(int)d.DayOfWeek];

void Save() => File.WriteAllText(UsersFile, JsonSerializer.Serialize(users, json));

async Task Send(long peer, string text, MessageKeyboard? kb = null) =>
    await api.Messages.SendAsync(new MessagesSendParams
    {
        RandomId = Random.Shared.Next(),
        PeerId = peer,
        Message = text,
        Keyboard = kb
    });