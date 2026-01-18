using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using StackExchange.Redis;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System;

//---[ИНИЦИАЛИЗАЦИЯ HTTP-СЕРВЕРА ДЛЯ HEALTH CHECK]---
var httpListener = new HttpListener();
httpListener.Prefixes.Add("http://+:5000/");
httpListener.Start();
_ = Task.Run(async () =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🌐 HTTP-сервер запущен на порту 5000");
    while (true)
    {
        try
        {
            var context = await httpListener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;
            if (request.Url?.AbsolutePath == "/health")
            {
                response.StatusCode = 200;
                response.ContentType = "text/plain";
                using var writer = new StreamWriter(response.OutputStream);
                writer.Write("OK");
                writer.Flush();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ /health запрошен");
            }
            else if (request.Url?.AbsolutePath == "/")
            {
                response.StatusCode = 200;
                response.ContentType = "text/html";
                using var writer = new StreamWriter(response.OutputStream);
                writer.Write("<h1>✅ Telegram Bot работает!</h1><p>Для взаимодействия используйте Telegram-клиент</p>");
                writer.Flush();
            }
            else
            {
                response.StatusCode = 404;
                response.ContentType = "text/plain";
                using var writer = new StreamWriter(response.OutputStream);
                writer.Write("Not Found");
                writer.Flush();
            }
            response.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка HTTP-сервера: {ex.Message}");
        }
    }
});

//---[ИНИЦИАЛИЗАЦИЯ БОТА И REDIS]---
var botToken = "8226200524:AAF5DzkLNIHr1wjkNhyjhjbymUN3pKHu55I"; // зафиксировано в коде
var bot = new TelegramBotClient(botToken);
var redisOptions = new ConfigurationOptions
{
    EndPoints = { "redis:6379" },
    AbortOnConnectFail = false,
    ConnectTimeout = 10000,
    SyncTimeout = 15000,
    AsyncTimeout = 20000,
    ReconnectRetryPolicy = new LinearRetry(5000)
};
IConnectionMultiplexer redis = null!;
IDatabase db = null!;
try
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ♻️ Подключаемся к Redis...");
    redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
    db = redis.GetDatabase();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Redis готов!");
}
catch (Exception ex)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка Redis: {ex.Message}");
    Environment.Exit(1);
}

//---[НАСТРОЙКИ CloudFlare]---
string cloudflareUrl = "https://providing-tee-bath-evolution.trycloudflare.com";

//---[ПОДГОТОВКА К РАБОТЕ]---
await ClearOldUpdates(bot);
await bot.DeleteWebhookAsync();
var botInfo = await bot.GetMeAsync();
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Бот @{botInfo.Username} готов к работе!");
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🌐 CloudFlare URL: {cloudflareUrl}");
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚀 Запускаем основной цикл обработки сообщений...");

//---[НАСТРОЙКИ ЦИКЛИЧЕСКИХ ПРОВЕРОК]---
DateTime lastAuthCheck = DateTime.Now;
DateTime lastNotificationCheck = DateTime.Now;
TimeSpan authCheckInterval = TimeSpan.FromSeconds(10);
TimeSpan notificationCheckInterval = TimeSpan.FromSeconds(30);

//---[ОСНОВНОЙ ЦИКЛ ОБРАБОТКИ СООБЩЕНИЙ]---
int lastUpdateId = 0;
while (true)
{
    try
    {
        var updates = await bot.GetUpdatesAsync(
            offset: lastUpdateId + 1,
            limit: 100,
            timeout: 30,
            allowedUpdates: new[] { UpdateType.Message }
        );

        if (DateTime.Now - lastAuthCheck > authCheckInterval)
        {
            await CheckAuthorizationForAnonymousUsers(bot, db, cloudflareUrl);
            lastAuthCheck = DateTime.Now;
        }

        if (DateTime.Now - lastNotificationCheck > notificationCheckInterval)
        {
            await CheckNotificationsForAuthorizedUsers(bot, db, cloudflareUrl);
            lastNotificationCheck = DateTime.Now;
        }

        foreach (var update in updates)
        {
            lastUpdateId = update.Id;
            if (update.Message?.From?.IsBot == true) continue;
            if (update.Message?.Text == null) continue;

            long chatId = update.Message.Chat.Id;
            string text = update.Message.Text;
            var chatType = update.Message.Chat.Type;
            long userId = update.Message.From!.Id;

            // Обработка только в личных сообщениях
            if (chatType != ChatType.Private) continue;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 📩 [Private] {text}");

            try
            {
                string userStatus = (await db.StringGetAsync($"user:{chatId}:status")).ToString() ?? "Unknown";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{chatId}] Статус пользователя: {userStatus}");

                switch (userStatus)
                {
                    case "Unknown":
                        await HandleUnknownUser(bot, db, chatId, userId, text, botInfo);
                        break;
                    case "Anonymous":
                        await HandleAnonymousUser(bot, db, chatId, userId, text, botInfo, cloudflareUrl);
                        break;
                    case "Authorized":
                        await HandleAuthorizedUser(bot, db, chatId, userId, text, botInfo, cloudflareUrl);
                        break;
                    default:
                        await db.StringSetAsync($"user:{chatId}:status", "Unknown");
                        await HandleUnknownUser(bot, db, chatId, userId, text, botInfo);
                        break;
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                HandleTelegramApiError(ex, chatId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Неожиданная ошибка: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚨 Критическая ошибка: {ex.Message}");
        await Task.Delay(5000);
    }
    await Task.Delay(1000);
}

//---[ОБРАБОТКА НЕИЗВЕСТНОГО ПОЛЬЗОВАТЕЛЯ]---
async Task HandleUnknownUser(ITelegramBotClient bot, IDatabase db, long chatId, long userId, string text, User botInfo)
{
    string lowerText = text.ToLower();
    if (lowerText == "/start" || lowerText == "начать")
    {
        await db.StringSetAsync($"user:{chatId}:status", "Unknown");
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: "🔐 Для использования бота необходимо пройти авторизацию.\nВыберите способ входа:",
            replyMarkup: GetAuthChoiceKeyboard()
        );
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Неизвестный пользователь получил сообщение о необходимости авторизации");
        return;
    }

    if (text == "GitHub" || text == "Yandex")
    {
        string providerType = text.ToLower();
        await StartLoginProcess(bot, db, chatId, userId, providerType, cloudflareUrl);
        return;
    }

    if (lowerText.StartsWith("/login"))
    {
        string providerType = "github";
        var match = Regex.Match(lowerText, @"type=([^\s]+)");
        if (match.Success)
        {
            providerType = match.Groups[1].Value.ToLower();
        }
        await StartLoginProcess(bot, db, chatId, userId, providerType, cloudflareUrl);
        return;
    }

    await bot.SendTextMessageAsync(
        chatId: chatId,
        text: "❌ Неизвестная команда. Напишите /start для начала работы.",
        replyMarkup: new ReplyKeyboardRemove { Selective = false }
    );
}

//---[ОБРАБОТКА АНОНИМНОГО ПОЛЬЗОВАТЕЛЯ]---
async Task HandleAnonymousUser(ITelegramBotClient bot, IDatabase db, long chatId, long userId, string text, User botInfo, string cloudflareUrl)
{
    string lowerText = text.ToLower();
    string loginToken = (await db.StringGetAsync($"user:{chatId}:login_token")).ToString() ?? "";

    if (lowerText.StartsWith("/login") && text.Contains("type="))
    {
        var match = Regex.Match(lowerText, @"type=([^\s]+)");
        if (match.Success)
        {
            string providerType = match.Groups[1].Value.ToLower();
            await StartLoginProcess(bot, db, chatId, userId, providerType, cloudflareUrl);
        }
        return;
    }

    if (lowerText == "/login" || lowerText == "войти")
    {
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: "🔐 Пожалуйста, выберите способ авторизации:",
            replyMarkup: GetAuthChoiceKeyboard()
        );
        return;
    }

    if (lowerText == "/logout" || lowerText == "выйти")
    {
        await db.KeyDeleteAsync($"user:{chatId}:status");
        await db.KeyDeleteAsync($"user:{chatId}:login_token");
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: "🔄 Сеанс завершен. Выберите способ авторизации:",
            replyMarkup: GetAuthChoiceKeyboard()
        );
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{chatId}] Анонимный пользователь вышел из системы");
        return;
    }

    await bot.SendTextMessageAsync(
        chatId: chatId,
        text: "🔐 Пожалуйста, завершите авторизацию или выберите способ входа:",
        replyMarkup: GetAuthChoiceKeyboard()
    );
}

//---[ОБРАБОТКА АВТОРИЗОВАННОГО ПОЛЬЗОВАТЕЛЯ]---
async Task HandleAuthorizedUser(ITelegramBotClient bot, IDatabase db, long chatId, long userId, string text, User botInfo, string cloudflareUrl)
{
    string lowerText = text.ToLower();
    string userName = (await db.StringGetAsync($"user:{chatId}:name")).ToString() ?? "Пользователь";
    string accessToken = (await db.StringGetAsync($"user:{chatId}:access_token")).ToString() ?? "";
    string refreshToken = (await db.StringGetAsync($"user:{chatId}:refresh_token")).ToString() ?? "";

    // --- [ОБРАБОТКА КОМАНД МЕНЮ] ---
    if (lowerText == "/start" || lowerText == "начать")
    {
        string role = (await db.StringGetAsync($"user:{chatId}:role")).ToString() ?? "Student";
        string roleDisplay = role switch
        {
            "Admin" => "Администратор",
            "Teacher" => "Преподаватель",
            "Student" => "Студент",
            _ => "Пользователь"
        };
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: $"✅ Вы уже авторизованы, {userName}!\nВаша роль: {roleDisplay}\nВыберите действие из меню ниже:",
            replyMarkup: GetMainMenuKeyboard()
        );
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Авторизованный пользователь запросил /start");
        return;
    }

    if (lowerText.StartsWith("/login"))
    {
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: $"✅ Вы уже авторизованы, {userName}!",
            replyMarkup: GetMainMenuKeyboard()
        );
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Авторизованный пользователь попытался войти повторно");
        return;
    }

    if ((lowerText == "/logout" || lowerText == "выйти") && !text.Contains("all=true"))
    {
        await PerformLocalLogout(bot, db, chatId);
        return;
    }

    if (lowerText.Contains("/logout") && lowerText.Contains("all=true"))
    {
        if (!string.IsNullOrEmpty(refreshToken))
        {
            await PerformGlobalLogout(bot, db, chatId, refreshToken, cloudflareUrl);
        }
        else
        {
            await PerformLocalLogout(bot, db, chatId);
        }
        return;
    }

    if (lowerText == "/help" || lowerText == "помощь")
    {
        string helpText = "❓ Доступные команды:\n";
        helpText += "👤 Для ЛС:\n/start, /help, /logout\n";
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: helpText,
            replyMarkup: GetMainMenuKeyboard()
        );
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Авторизованный пользователь запросил /help");
        return;
    }

    // --- [СОЗДАНИЕ КУРСА] ---
    if (text == "➕ Создать курс" || lowerText == "/create_course")
    {
        string role = (await db.StringGetAsync($"user:{chatId}:role")).ToString() ?? "Student";
        if (role != "Teacher" && role != "Admin")
        {
            await bot.SendTextMessageAsync(chatId, "❌ Только преподаватели и администраторы могут создавать курсы.");
            return;
        }

        await db.StringSetAsync($"user:{chatId}:flow", "awaiting_course_name");
        await bot.SendTextMessageAsync(chatId, "✏️ Введите название курса:");
        return;
    }

    // --- [СОСТОЯНИЕ: ОЖИДАНИЕ НАЗВАНИЯ] ---
    string flow = (await db.StringGetAsync($"user:{chatId}:flow")).ToString();
    if (flow == "awaiting_course_name")
    {
        string courseName = text.Trim();
        if (string.IsNullOrWhiteSpace(courseName))
        {
            await bot.SendTextMessageAsync(chatId, "❌ Название не может быть пустым. Попробуйте снова:");
            return;
        }
        await db.StringSetAsync($"user:{chatId}:course_name", courseName);
        await db.StringSetAsync($"user:{chatId}:flow", "awaiting_course_desc");
        await bot.SendTextMessageAsync(chatId, "📝 Введите описание курса:");
        return;
    }

    // --- [СОСТОЯНИЕ: ОЖИДАНИЕ ОПИСАНИЯ + ОТПРАВКА] ---
    if (flow == "awaiting_course_desc")
    {
        string description = text.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            await bot.SendTextMessageAsync(chatId, "❌ Описание не может быть пустым. Попробуйте снова:");
            return;
        }

        string courseName = (await db.StringGetAsync($"user:{chatId}:course_name")).ToString();

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var payload = new { name = courseName, description };
        var jsonPayload = JsonConvert.SerializeObject(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        try
        {
            var response = await client.PostAsync($"{cloudflareUrl}/api/courses", content);
            if (response.IsSuccessStatusCode)
            {
                var result = JsonConvert.DeserializeObject<JObject>(await response.Content.ReadAsStringAsync());
                await bot.SendTextMessageAsync(chatId,
                    $"✅ Курс создан!\nID: {result["id"]}\nНазвание: {result["name"]}\nОписание: {result["description"]}");
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await bot.SendTextMessageAsync(chatId, "❌ У вас нет прав на создание курсов.");
            }
            else
            {
                await bot.SendTextMessageAsync(chatId, $"⚠️ Ошибка: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка создания курса: {ex.Message}");
            await bot.SendTextMessageAsync(chatId, "⚠️ Не удалось создать курс.");
        }

        await db.KeyDeleteAsync($"user:{chatId}:flow");
        await db.KeyDeleteAsync($"user:{chatId}:course_name");
        return;
    }

    // --- [ПРОСМОТР КУРСОВ] ---
    if (text == "📚 Доступные курсы" || lowerText == "/courses")
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            var response = await client.GetAsync($"{cloudflareUrl}/api/courses");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var courses = JsonConvert.DeserializeObject<List<JObject>>(json);
                if (courses == null || courses.Count == 0)
                {
                    await bot.SendTextMessageAsync(chatId, "📭 Нет доступных курсов.");
                }
                else
                {
                    var msg = "📚 Доступные курсы:\n\n";
                    foreach (var c in courses)
                        msg += $"🔹 ID: {c["id"]}\n   Название: {c["name"]}\n   Описание: {c["description"]}\n\n";
                    msg += "Чтобы открыть курс, отправьте:\n/course <ID>";
                    await bot.SendTextMessageAsync(chatId, msg);
                }
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await HandleTokenRefresh(bot, db, chatId, refreshToken, cloudflareUrl);
                await bot.SendTextMessageAsync(chatId, "🔄 Токен обновлён. Повторите команду.");
            }
            else
            {
                await bot.SendTextMessageAsync(chatId, $"⚠️ Ошибка загрузки курсов: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка /courses: {ex.Message}");
            await bot.SendTextMessageAsync(chatId, "⚠️ Не удалось получить список курсов.");
        }
        return;
    }

    // --- [ОТКРЫТИЕ КУРСА ПО ID] ---
    if (lowerText.StartsWith("/course ") && int.TryParse(text.Split(' ')[1], out int courseId))
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            var response = await client.GetAsync($"{cloudflareUrl}/api/courses/{courseId}");
            if (response.IsSuccessStatusCode)
            {
                var course = JsonConvert.DeserializeObject<JObject>(await response.Content.ReadAsStringAsync());
                await bot.SendTextMessageAsync(chatId,
                    $"📘 Курс ID: {course["id"]}\n" +
                    $"Название: {course["name"]}\n" +
                    $"Описание: {course["description"]}\n" +
                    $"Преподаватель ID: {course["teacher_id"]}");
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await bot.SendTextMessageAsync(chatId, "❌ Курс не найден.");
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await bot.SendTextMessageAsync(chatId, "❌ У вас нет доступа к этому курсу.");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await HandleTokenRefresh(bot, db, chatId, refreshToken, cloudflareUrl);
                await bot.SendTextMessageAsync(chatId, "🔄 Токен обновлён. Повторите команду.");
            }
            else
            {
                await bot.SendTextMessageAsync(chatId, $"⚠️ Ошибка: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка /course: {ex.Message}");
            await bot.SendTextMessageAsync(chatId, "⚠️ Не удалось загрузить курс.");
        }
        return;
    }

    // --- [РЕАЛИЗАЦИЯ ПУНКТА 14 TASKFLOW] ---
    using var fallbackClient = new HttpClient();
    fallbackClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    fallbackClient.Timeout = TimeSpan.FromSeconds(30);
    try
    {
        var commandJson = JsonConvert.SerializeObject(new { command = text });
        var content = new StringContent(commandJson, Encoding.UTF8, "application/json");
        var response = await fallbackClient.PostAsync($"{cloudflareUrl}/api/command", content);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadAsStringAsync();
            try
            {
                var resultObj = JsonConvert.DeserializeObject<JObject>(result);
                if (resultObj.ContainsKey("message"))
                {
                    await bot.SendTextMessageAsync(chatId, resultObj["message"]?.ToString() ?? result);
                }
                else
                {
                    await bot.SendTextMessageAsync(chatId, result);
                }
            }
            catch
            {
                await bot.SendTextMessageAsync(chatId, result);
            }
        }
        else if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            await bot.SendTextMessageAsync(chatId, "❌ Недостаточно прав для выполнения этой команды.");
        }
        else if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleTokenRefresh(bot, db, chatId, refreshToken, cloudflareUrl);
            await bot.SendTextMessageAsync(chatId, "🔄 Токен обновлён. Повторите команду.");
        }
        else
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Ошибка при выполнении команды: {response.StatusCode}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка при вызове TestAppLogic: {ex.Message}");
        await bot.SendTextMessageAsync(chatId, "⚠️ Ошибка при выполнении команды. Попробуйте позже.");
    }
}

//---[ОСТАЛЬНЫЕ МЕТОДЫ]---
async Task StartLoginProcess(ITelegramBotClient bot, IDatabase db, long chatId, long userId, string providerType, string cloudflareUrl)
{
    string loginToken = Guid.NewGuid().ToString();
    await db.StringSetAsync($"user:{chatId}:status", "Anonymous");
    await db.StringSetAsync($"user:{chatId}:login_token", loginToken);
    using var httpClient = new HttpClient();
    httpClient.Timeout = TimeSpan.FromSeconds(30);
    try
    {
        var response = await httpClient.GetAsync($"{cloudflareUrl}/auth/login_request?type={providerType}&state={loginToken}");
        if (response.IsSuccessStatusCode)
        {
            string jsonResponse = await response.Content.ReadAsStringAsync();
            var jsonData = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);
            string authUrl = jsonData["url"];
            string providerName = providerType == "github" ? "GitHub" : "Yandex";
            string providerIcon = providerType == "github" ? "🐱" : "🔵";
            await bot.SendTextMessageAsync(
                chatId: chatId,
                text: $"{providerIcon} Для продолжения пройдите авторизацию через {providerName}:\n{authUrl}\n⚠️ После авторизации закройте окно браузера",
                replyMarkup: new ReplyKeyboardRemove { Selective = false }
            );
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Начат процесс авторизации через {providerName}");
        }
        else
        {
            await bot.SendTextMessageAsync(
                chatId: chatId,
                text: $"❌ Ошибка получения ссылки авторизации через {providerType}. Попробуйте позже.\nКод ошибки: {response.StatusCode}",
                replyMarkup: GetAuthChoiceKeyboard()
            );
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [{chatId}] Ошибка при получении ссылки авторизации: {response.StatusCode}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка при получении ссылки авторизации: {ex.Message}");
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: $"❌ Ошибка получения ссылки авторизации. Попробуйте позже.",
            replyMarkup: GetAuthChoiceKeyboard()
        );
    }
}

async Task CheckAuthorizationForAnonymousUsers(ITelegramBotClient bot, IDatabase db, string cloudflareUrl)
{
    try
    {
        var server = redis.GetServer("redis:6379");
        var keys = server.Keys(pattern: "user:*:status", pageSize: 1000);
        foreach (var key in keys)
        {
            string keyStr = key.ToString();
            string chatIdStr = keyStr.Split(':')[1];
            if (!long.TryParse(chatIdStr, out long chatId)) continue;
            string status = (await db.StringGetAsync(key)).ToString() ?? "";
            if (status != "Anonymous") continue;
            string loginToken = (await db.StringGetAsync($"user:{chatId}:login_token")).ToString() ?? "";
            if (string.IsNullOrEmpty(loginToken)) continue;
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            try
            {
                var response = await client.GetAsync($"{cloudflareUrl}/auth/check_state?state={loginToken}");
                if (response.StatusCode == HttpStatusCode.Accepted) continue;
                string responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    await db.KeyDeleteAsync($"user:{chatId}:status");
                    await db.KeyDeleteAsync($"user:{chatId}:login_token");
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{chatId}] Анонимный пользователь переведен в статус Unknown (токен недействителен)");
                    continue;
                }
                var tokens = JsonConvert.DeserializeObject<JObject>(responseContent);
                string accessToken = tokens["access_token"]?.ToString() ?? "";
                string refreshToken = tokens["refresh_token"]?.ToString() ?? "";
                string email = tokens["email"]?.ToString() ?? "";
                string userName = email.Split('@')[0];
                string role = tokens["role"]?.ToString() ?? "Student";

                await db.StringSetAsync($"user:{chatId}:status", "Authorized");
                await db.StringSetAsync($"user:{chatId}:access_token", accessToken);
                await db.StringSetAsync($"user:{chatId}:refresh_token", refreshToken);
                await db.StringSetAsync($"user:{chatId}:name", userName);
                await db.StringSetAsync($"user:{chatId}:role", role);
                await db.KeyDeleteAsync($"user:{chatId}:login_token");

                string roleDisplay = role switch
                {
                    "Admin" => "Администратор",
                    "Teacher" => "Преподаватель",
                    "Student" => "Студент",
                    _ => "Пользователь"
                };
                await bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"🎉 Поздравляем, {userName}!\n✅ Авторизация прошла успешно!\nВаша роль: {roleDisplay}\nТеперь вы можете использовать все функции бота.",
                    replyMarkup: GetMainMenuKeyboard()
                );
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Успешная авторизация для пользователя {userName} (роль: {role})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка проверки токена для анонимного пользователя [{chatId}]: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка циклической проверки авторизации: {ex.Message}");
    }
}

async Task CheckNotificationsForAuthorizedUsers(ITelegramBotClient bot, IDatabase db, string cloudflareUrl)
{
    try
    {
        var server = redis.GetServer("redis:6379");
        var keys = server.Keys(pattern: "user:*:status", pageSize: 1000);
        foreach (var key in keys)
        {
            string keyStr = key.ToString();
            string chatIdStr = keyStr.Split(':')[1];
            if (!long.TryParse(chatIdStr, out long chatId)) continue;
            string status = (await db.StringGetAsync(key)).ToString() ?? "";
            if (status != "Authorized") continue;
            string accessToken = (await db.StringGetAsync($"user:{chatId}:access_token")).ToString() ?? "";
            if (string.IsNullOrEmpty(accessToken)) continue;
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            try
            {
                var response = await client.GetAsync($"{cloudflareUrl}/api/notifications");
                if (response.IsSuccessStatusCode)
                {
                    var notifications = JsonConvert.DeserializeObject<List<Notification>>(await response.Content.ReadAsStringAsync());
                    if (notifications != null && notifications.Count > 0)
                    {
                        foreach (var notification in notifications)
                        {
                            await bot.SendTextMessageAsync(chatId: chatId, text: $"🔔 {notification.Message}");
                        }
                        await client.PostAsync($"{cloudflareUrl}/api/notifications/clear", null);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Отправлено {notifications.Count} уведомлений");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка проверки уведомлений для [{chatId}]: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка циклической проверки уведомлений: {ex.Message}");
    }
}

async Task PerformLocalLogout(ITelegramBotClient bot, IDatabase db, long chatId)
{
    await db.KeyDeleteAsync($"user:{chatId}:status");
    await db.KeyDeleteAsync($"user:{chatId}:access_token");
    await db.KeyDeleteAsync($"user:{chatId}:refresh_token");
    await db.KeyDeleteAsync($"user:{chatId}:name");
    await db.KeyDeleteAsync($"user:{chatId}:role");
    await db.KeyDeleteAsync($"user:{chatId}:login_token");
    await bot.SendTextMessageAsync(
        chatId: chatId,
        text: "✅ Сеанс завершен. Вы вышли из системы на этом устройстве.",
        replyMarkup: GetAuthChoiceKeyboard()
    );
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{chatId}] Локальный выход из системы");
}

async Task PerformGlobalLogout(ITelegramBotClient bot, IDatabase db, long chatId, string refreshToken, string cloudflareUrl)
{
    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromSeconds(30);
    try
    {
        var content = new StringContent(JsonConvert.SerializeObject(new { refresh_token = refreshToken }), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{cloudflareUrl}/auth/logout", content);
        if (response.IsSuccessStatusCode)
        {
            await PerformLocalLogout(bot, db, chatId);
            await bot.SendTextMessageAsync(chatId: chatId, text: "✅ Сеанс завершен на всех устройствах.");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🌐 [{chatId}] Глобальный выход из системы");
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ [{chatId}] Не удалось выполнить глобальный выход: {response.StatusCode}");
            await PerformLocalLogout(bot, db, chatId);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка при глобальном выходе: {ex.Message}");
        await PerformLocalLogout(bot, db, chatId);
    }
}

async Task HandleTokenRefresh(ITelegramBotClient bot, IDatabase db, long chatId, string refreshToken, string cloudflareUrl)
{
    if (string.IsNullOrEmpty(refreshToken))
    {
        await PerformLocalLogout(bot, db, chatId);
        return;
    }
    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromSeconds(30);
    try
    {
        var content = new StringContent(JsonConvert.SerializeObject(new { refresh_token = refreshToken }), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{cloudflareUrl}/auth/refresh", content);
        if (response.IsSuccessStatusCode)
        {
            var tokens = JsonConvert.DeserializeObject<JObject>(await response.Content.ReadAsStringAsync());
            string newAccessToken = tokens["access_token"]?.ToString() ?? "";
            string newRefreshToken = tokens["refresh_token"]?.ToString() ?? "";
            string newRole = tokens["role"]?.ToString() ?? "Student";
            await db.StringSetAsync($"user:{chatId}:access_token", newAccessToken);
            await db.StringSetAsync($"user:{chatId}:refresh_token", newRefreshToken);
            await db.StringSetAsync($"user:{chatId}:role", newRole);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{chatId}] Токен успешно обновлен");
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ [{chatId}] Не удалось обновить токен: {response.StatusCode}");
            await PerformLocalLogout(bot, db, chatId);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка при обновлении токена: {ex.Message}");
        await PerformLocalLogout(bot, db, chatId);
    }
}

async Task<bool> ValidateAccessToken(ITelegramBotClient bot, IDatabase db, long chatId, string accessToken, string cloudflareUrl)
{
    try
    {
        var tokenParts = accessToken.Split('.');
        if (tokenParts.Length >= 2)
        {
            string payloadBase64 = tokenParts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            switch (payloadBase64.Length % 4)
            {
                case 2: payloadBase64 += "=="; break;
                case 3: payloadBase64 += "="; break;
            }
            try
            {
                var payloadBytes = Convert.FromBase64String(payloadBase64);
                string payloadJson = Encoding.UTF8.GetString(payloadBytes);
                var payload = JsonConvert.DeserializeObject<JObject>(payloadJson);
                if (payload != null && payload.ContainsKey("exp"))
                {
                    long exp = payload["exp"].Value<long>();
                    DateTime tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(exp).DateTime;
                    if (tokenExpiry < DateTime.Now.AddMinutes(-5))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ [{chatId}] Токен истек");
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }
    catch
    {
        return false;
    }
}

void HandleTelegramApiError(Telegram.Bot.Exceptions.ApiRequestException ex, long chatId)
{
    switch (ex.ErrorCode)
    {
        case 403:
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ {chatId} заблокировал бота");
            break;
        case 400 when ex.Message.Contains("CHAT_WRITE_FORBIDDEN"):
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ {chatId} Бот не имеет прав на отправку сообщений");
            break;
        default:
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка Telegram API: {ex.Message}");
            break;
    }
}

ReplyKeyboardMarkup GetAuthChoiceKeyboard() => new(new[]
{
    new KeyboardButton("GitHub"),
    new KeyboardButton("Yandex")
})
{
    ResizeKeyboard = true,
    OneTimeKeyboard = true
};

ReplyKeyboardMarkup GetMainMenuKeyboard() => new(new[]
{
    new KeyboardButton("📚 Доступные курсы"),
    new KeyboardButton("➕ Создать курс"),
    new KeyboardButton("Помощь"),
    new KeyboardButton("Выйти")
})
{
    ResizeKeyboard = true
};

async Task ClearOldUpdates(ITelegramBotClient botClient)
{
    try
    {
        await botClient.GetUpdatesAsync(offset: -1);
    }
    catch { }
}

public class Notification
{
    public string? Id { get; set; }
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsRead { get; set; }
}