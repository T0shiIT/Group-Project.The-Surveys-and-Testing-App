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

//---[ИНИЦИАЛИЗАЦИЯ HTTP-СЕРВЕРА ДЛЯ HEALTH CHECK]---
var httpListener = new HttpListener();
httpListener.Prefixes.Add("http://+:5000/");
httpListener.Start();
_ = Task.Run(async () => {
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🌐 HTTP-сервер запущен на порту 5000");
    while (true) {
        try {
            var context = await httpListener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;
            
            if (request.Url?.AbsolutePath == "/health") {
                response.StatusCode = 200;
                response.ContentType = "text/plain";
                using var writer = new StreamWriter(response.OutputStream);
                writer.Write("OK");
                writer.Flush();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ /health запрошен");
            }
            else if (request.Url?.AbsolutePath == "/") {
                response.StatusCode = 200;
                response.ContentType = "text/html";
                using var writer = new StreamWriter(response.OutputStream);
                writer.Write("<h1>✅ Telegram Bot работает!</h1><p>Для взаимодействия используйте Telegram-клиент</p>");
                writer.Flush();
            }
            else {
                response.StatusCode = 404;
                response.ContentType = "text/plain";
                using var writer = new StreamWriter(response.OutputStream);
                writer.Write("Not Found");
                writer.Flush();
            }
            response.Close();
        }
        catch (Exception ex) {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка HTTP-сервера: {ex.Message}");
        }
    }
});

//---[ИНИЦИАЛИЗАЦИЯ БОТА И REDIS]---
var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "8226200524:AAF5DzkLNIHr1wjkNhyjhjbymUN3pKHu55I";
var bot = new TelegramBotClient(botToken);

// Исправленное подключение к Redis с увеличенными таймаутами
var redisOptions = new ConfigurationOptions {
    EndPoints = { "redis:6379" },
    AbortOnConnectFail = false,
    ConnectTimeout = 10000,
    SyncTimeout = 15000,
    AsyncTimeout = 20000,
    ReconnectRetryPolicy = new LinearRetry(5000)
};

IConnectionMultiplexer redis = null!; // Явная инициализация
IDatabase db = null!;

try {
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ♻️ Подключаемся к Redis с увеличенными таймаутами...");
    redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
    db = redis.GetDatabase();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Redis готов!");
}
catch (Exception ex) {
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка Redis: {ex.Message}");
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Проверьте: запущен ли Redis в Docker-сети");
    Environment.Exit(1);
}

//---[НАСТРОЙКИ CloudFlared Tunnel]---
string cloudflareUrl = Environment.GetEnvironmentVariable("CLOUDFLARE_URL") ?? "https://meaning-remain-creative-seriously.trycloudflare.com";

//---[ПОДГОТОВКА К РАБОТЕ]---
await ClearOldUpdates(bot);
await bot.DeleteWebhookAsync();
var botInfo = await bot.GetMeAsync();
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Бот @{botInfo.Username} готов к работе!");
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🌐 CloudFlare URL: {cloudflareUrl}");
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚀 Запускаем основной цикл обработки сообщений...");

//---[ОСНОВНОЙ ЦИКЛ ОБРАБОТКИ СООБЩЕНИЙ]---
int lastUpdateId = 0;
while (true) {
    try {
        var updates = await bot.GetUpdatesAsync(
            offset: lastUpdateId + 1,
            limit: 100,
            timeout: 30,
            allowedUpdates: new[] { UpdateType.Message }
        );
        
        // Фоновая проверка авторизации
        await CheckAuthorizationStatus(bot, db, cloudflareUrl);
        
        foreach (var update in updates) {
            lastUpdateId = update.Id;
            if (update.Message?.From?.IsBot == true) continue;
            if (update.Message?.Text == null) continue;
            
            long chatId = update.Message.Chat.Id;
            string text = update.Message.Text;
            var chatType = update.Message.Chat.Type;
            long userId = update.Message.From!.Id;
            
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 📩 [{chatType}] {text}");
            
            try {
                string? userState = await db.StringGetAsync($"user:{userId}:state");
                string? userName = await db.StringGetAsync($"user:{userId}:name");
                string? accessToken = await db.StringGetAsync($"user:{userId}:access_token");
                string? authType = await db.StringGetAsync($"user:{userId}:auth_type");
                
                //---[ОБРАБОТКА КНОПОК МЕНЮ]---
                if (chatType == ChatType.Private) {
                    switch (text) {
                        case "Начать":
                            text = "/start";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Кнопка 'Начать' → /start");
                            break;
                        case "Помощь":
                            text = "/help";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Кнопка 'Помощь' → /help");
                            break;
                        case "Сброс" when !string.IsNullOrEmpty(userName):
                            text = "/logout";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Кнопка 'Выйти' → /logout");
                            break;
                        case "GitHub" when string.IsNullOrEmpty(accessToken):
                            await HandleAuthChoice(bot, db, chatId, userId, "github", cloudflareUrl);
                            continue;
                        case "Yandex" when string.IsNullOrEmpty(accessToken):
                            await HandleAuthChoice(bot, db, chatId, userId, "yandex", cloudflareUrl);
                            continue;
                    }
                }
                
                //---[ФУНКЦИЯ ДЛЯ КЛАВИАТУРЫ]---
                IReplyMarkup GetSafeReplyMarkup(ChatType type) =>
                    type != ChatType.Private
                        ? new ReplyKeyboardRemove { Selective = false }
                        : string.IsNullOrEmpty(userName)
                            ? GetAuthChoiceKeyboard()
                            : GetMainMenuKeyboard();
                
                //---[ОБРАБОТКА КОМАНД]---
                string lowerText = text.ToLower();
                switch (lowerText) {
                    case var cmd when cmd.StartsWith("/start"):
                        if (chatType != ChatType.Private) {
                            bool hasMention = lowerText.Contains($"@{botInfo.Username.ToLower()}");
                            bool isReplyToBot = update.Message.ReplyToMessage?.From?.Id == botInfo.Id;
                            if (!hasMention && !isReplyToBot) {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{chatId}] Игнорируем /start без упоминания");
                                continue;
                            }
                            
                            // Если пользователь не авторизован в группе, отправляем сообщение о необходимости авторизации в ЛС
                            if (string.IsNullOrEmpty(accessToken)) {
                                await bot.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: $"🔐 Для использования бота необходимо пройти авторизацию.\nПожалуйста, напишите мне в личные сообщения: @{botInfo.Username}",
                                    replyMarkup: new ReplyKeyboardRemove { Selective = false }
                                );
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{chatId}] Пользователь не авторизован, отправлено сообщение о необходимости авторизации в ЛС");
                                continue;
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(accessToken)) {
                            // Пользователь уже авторизован
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: $"😊 Привет снова, {userName}!\nВас приветствует бот команды Stellvia!",
                                replyMarkup: GetSafeReplyMarkup(chatType)
                            );
                        }
                        else {
                            // Показываем выбор метода авторизации
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: "🔐 Выберите способ авторизации:",
                                replyMarkup: GetAuthChoiceKeyboard()
                            );
                        }
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] /start обработана");
                        continue;
                        
                    case "/help":
                        string helpText = "❓ Доступные команды:\n";
                        helpText += "👤 Для ЛС:\n/start, /help, /logout\n";
                        helpText += "👥 Для групп:\n/start@{botInfo.Username}, /help, /logout";
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: helpText.Replace("{botInfo.Username}", botInfo.Username),
                            replyMarkup: GetSafeReplyMarkup(chatType)
                        );
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] /help обработана");
                        continue;
                        
                    case "/logout":
                        await db.KeyDeleteAsync($"user:{userId}:name");
                        await db.KeyDeleteAsync($"user:{userId}:access_token");
                        await db.KeyDeleteAsync($"user:{userId}:state");
                        await db.KeyDeleteAsync($"user:{userId}:auth_type");
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "🔄 Ваши данные полностью удалены. Выберите способ авторизации:",
                            replyMarkup: GetAuthChoiceKeyboard()
                        );
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Полный сброс данных");
                        continue;
                }
                
                //---[ОСНОВНАЯ ЛОГИКА ДЛЯ АВТОРИЗОВАННЫХ ПОЛЬЗОВАТЕЛЕЙ]---
                if (!string.IsNullOrEmpty(accessToken)) {
                    if (text.ToLower().StartsWith("/start") || text == "Начать") {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "🎯 Вы успешно авторизованы! Теперь вы можете:\n• Пройти тесты\n• Создать опрос\n• Проверить результаты\nВыберите действие из меню ниже:",
                            replyMarkup: GetMainMenuKeyboard()
                        );
                    }
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) {
                switch (ex.ErrorCode) {
                    case 403:
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ {chatId} заблокировал бота");
                        break;
                    case 400 when ex.Message.Contains("CHAT_WRITE_FORBIDDEN"):
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ {chatId} Бот не имеет прав на отправку сообщений");
                        break;
                    case 400 when ex.Message.Contains("BUTTON_TYPE_INVALID") ||
                                ex.Message.Contains("can't create inline keyboard"):
                        try {
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: "🔧 Интерфейс обновлен для группового чата",
                                replyMarkup: new ReplyKeyboardRemove { Selective = false }
                            );
                        }
                        catch { }
                        break;
                    default:
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка Telegram API: {ex.Message}");
                        break;
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Неожиданная ошибка: {ex.Message}");
            }
        }
    }
    catch (Exception ex) {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚨 Критическая ошибка: {ex.Message}");
        await Task.Delay(5000);
    }
    await Task.Delay(1000);
}

//---[ОБРАБОТКА ВЫБОРА МЕТОДА АВТОРИЗАЦИИ]---
async Task HandleAuthChoice(ITelegramBotClient bot, IDatabase db, long chatId, long userId, string provider, string cloudflareUrl) {
    string authType = provider.ToLower();
    string state = Guid.NewGuid().ToString();
    
    await db.StringSetAsync($"user:{userId}:state", state);
    await db.StringSetAsync($"user:{userId}:auth_type", authType);
    
    using var httpClient = new HttpClient();
    httpClient.Timeout = TimeSpan.FromSeconds(30);
    try {
        var response = await httpClient.GetAsync($"{cloudflareUrl}/auth/login_request?type={authType}&state={state}");
        if (response.IsSuccessStatusCode) {
            string jsonResponse = await response.Content.ReadAsStringAsync();
            var jsonData = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);
            string realAuthUrl = jsonData["url"];
            
            string providerName = authType == "github" ? "GitHub" : "Yandex";
            string providerIcon = authType == "github" ? "🐱" : "🔵";
            
            await bot.SendTextMessageAsync(
                chatId: chatId,
                text: $"{providerIcon} Для продолжения пройдите авторизацию через {providerName}:\n{realAuthUrl}\n⚠️ После авторизации закройте окно браузера",
                replyMarkup: new ReplyKeyboardRemove { Selective = false }
            );
        } else {
            string errorContent = await response.Content.ReadAsStringAsync();
            await bot.SendTextMessageAsync(
                chatId: chatId,
                text: $"❌ Ошибка получения ссылки авторизации через {provider}. Попробуйте позже.\nКод ошибки: {response.StatusCode}\nДетали: {errorContent}",
                replyMarkup: new ReplyKeyboardRemove { Selective = false }
            );
        }
    } catch (Exception ex) {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка при получении ссылки авторизации: {ex.Message}");
        await bot.SendTextMessageAsync(
            chatId: chatId,
            text: $"❌ Ошибка получения ссылки авторизации через {provider}. Попробуйте позже.",
            replyMarkup: new ReplyKeyboardRemove { Selective = false }
        );
    }
}

//---[ФУНКЦИЯ ПРОВЕРКИ СОСТОЯНИЯ АВТОРИЗАЦИИ]---
async Task CheckAuthorizationStatus(ITelegramBotClient bot, IDatabase db, string cloudflareUrl) {
    try {
        var server = redis.GetServer("redis:6379");
        var keys = server.Keys(pattern: "user:*:state", pageSize: 1000);
        foreach (var key in keys) {
            string keyStr = key.ToString();
            string userIdStr = keyStr.Split(':')[1];
            if (!long.TryParse(userIdStr, out long userId)) continue;
            
            string state = await db.StringGetAsync(key);
            if (string.IsNullOrEmpty(state) || state == "pending") continue;
            
            try {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var response = await client.GetAsync($"{cloudflareUrl}/auth/check_state?state={state}");
                
                // Обработка статуса 202 (Accepted) - авторизация еще в процессе
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted) {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{userId}] Авторизация еще в процессе для state: {state}");
                    continue;
                }
                
                if (!response.IsSuccessStatusCode) {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [{userId}] Ошибка сервера авторизации: {response.StatusCode}\n{errorContent}");
                    continue;
                }
                
                string responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 📋 [{userId}] Raw response: {responseContent}");
                
                // Проверяем Content-Type и содержимое перед парсингом
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                bool isJson = contentType.Contains("application/json") || 
                             (responseContent.Trim().StartsWith("{") && responseContent.Trim().EndsWith("}"));
                
                if (isJson) {
                    try {
                        var tokens = JsonConvert.DeserializeObject<JObject>(responseContent);
                        string accessToken = tokens?["access_token"]?.ToString() ?? "";
                        
                        if (!string.IsNullOrEmpty(accessToken)) {
                            await db.StringSetAsync($"user:{userId}:access_token", accessToken);
                            
                            // Корректное декодирование JWT токена
                            string userName = "Пользователь";
                            string authType = await db.StringGetAsync($"user:{userId}:auth_type");
                            
                            // Определяем провайдера ДО использования в сообщении
                            string providerName;
                            string providerIcon;
                            
                            if (string.IsNullOrEmpty(authType)) {
                                providerName = "GitHub";
                                providerIcon = "🐱";
                            } else if (authType.ToLower() == "yandex") {
                                providerName = "Yandex";
                                providerIcon = "🔵";
                            } else {
                                providerName = "GitHub";
                                providerIcon = "🐱";
                            }
                            
                            try {
                                // Проверяем, является ли токен JWT
                                if (accessToken.Contains('.') && accessToken.Split('.').Length == 3) {
                                    var tokenParts = accessToken.Split('.');
                                    if (tokenParts.Length >= 2) {
                                        // Декодируем payload часть JWT
                                        string payloadBase64 = tokenParts[1]
                                            .Replace('-', '+')
                                            .Replace('_', '/');
                                        
                                        // Добавляем padding если нужно
                                        switch (payloadBase64.Length % 4) {
                                            case 2: payloadBase64 += "=="; break;
                                            case 3: payloadBase64 += "="; break;
                                        }
                                        
                                        try {
                                            var payloadBytes = Convert.FromBase64String(payloadBase64);
                                            string payloadJson = Encoding.UTF8.GetString(payloadBytes);
                                            var payload = JsonConvert.DeserializeObject<JObject>(payloadJson);
                                            
                                            if (payload != null && payload.ContainsKey("email") && 
                                                !string.IsNullOrEmpty(payload["email"].ToString())) {
                                                userName = payload["email"].ToString().Split('@')[0];
                                            }
                                        }
                                        catch (FormatException) {
                                            // Если не удалось декодировать как base64, пробуем парсить как обычный JSON
                                            try {
                                                var payload = JsonConvert.DeserializeObject<JObject>(payloadBase64);
                                                if (payload != null && payload.ContainsKey("email") && 
                                                    !string.IsNullOrEmpty(payload["email"].ToString())) {
                                                    userName = payload["email"].ToString().Split('@')[0];
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                            catch (Exception decodeEx) {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Ошибка декодирования токена: {decodeEx.Message}");
                            }
                            
                            await db.StringSetAsync($"user:{userId}:name", userName);
                            await db.KeyDeleteAsync($"user:{userId}:state");
                            await db.KeyDeleteAsync($"user:{userId}:auth_type");
                            
                            // Улучшенное сообщение об успешной авторизации
                            string successMessage = $"🎉 Поздравляем, {userName}!\n" +
                                $"{providerIcon} Авторизация через {providerName} прошла успешно!";
                            
                            try {
                                await bot.SendTextMessageAsync(
                                    chatId: userId,
                                    text: successMessage,
                                    replyMarkup: GetMainMenuKeyboard()
                                );
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{userId}] Отправлено сообщение об успешной авторизации через {providerName}");
                            }
                            catch (Exception sendEx) {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Не удалось отправить сообщение об авторизации: {sendEx.Message}");
                                // Если не удалось отправить с кастомной клавиатурой, отправляем простое сообщение
                                try {
                                    await bot.SendTextMessageAsync(
                                        chatId: userId,
                                        text: $"🎉 {userName}, авторизация через {providerName} успешна! Теперь вы можете использовать все функции бота.",
                                        replyMarkup: GetMainMenuKeyboard()
                                    );
                                }
                                catch (Exception fallbackEx) {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Не удалось отправить fallback сообщение: {fallbackEx.Message}");
                                }
                            }
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{userId}] Авторизация завершена для пользователя {userName} через {providerName}");
                        }
                        else {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{userId}] Получен JSON без access_token");
                        }
                    }
                    catch (Exception parseEx) {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка парсинга JSON: {parseEx.Message}");
                    }
                }
                else {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{userId}] Получен не-JSON ответ: {responseContent}");
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка при проверке состояния авторизации: {ex.Message}");
            }
        }
    }
    catch (Exception ex) {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка проверки авторизации: {ex.Message}");
    }
}

//---[ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ]---
ReplyKeyboardMarkup GetAuthChoiceKeyboard() => new(new[]
{
    new KeyboardButton("GitHub"),
    new KeyboardButton("Yandex")
}) { ResizeKeyboard = true, OneTimeKeyboard = true };

ReplyKeyboardMarkup GetStartKeyboard() => new(new[] { new KeyboardButton("Начать") }) { ResizeKeyboard = true, OneTimeKeyboard = true };

ReplyKeyboardMarkup GetMainMenuKeyboard() => new(new[]
{
    new KeyboardButton("Помощь"),
    new KeyboardButton("Выйти")
}) { ResizeKeyboard = true };

async Task ClearOldUpdates(ITelegramBotClient botClient) {
    try { await botClient.GetUpdatesAsync(offset: -1); }
    catch {}
}