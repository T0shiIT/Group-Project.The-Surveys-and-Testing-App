using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using StackExchange.Redis;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

//---[ИНИЦИАЛИЗАЦИЯ БОТА И REDIS]---
var botToken = "8226200524:AAF5DzkLNIHr1wjkNhyjhjbymUN3pKHu55I";
var bot = new TelegramBotClient(botToken);

IConnectionMultiplexer redis;
IDatabase db = null!;

try
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ♻️ Подключаемся к Redis...");
    redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379,abortConnect=false");
    db = redis.GetDatabase();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Redis готов!");
}
catch (Exception ex)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка Redis: {ex.Message}");
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Проверьте: запущен ли Redis сервер на localhost:6379?");
    Environment.Exit(1);
}

//---[ПОДГОТОВКА К РАБОТЕ]---
await ClearOldUpdates(bot);
await bot.DeleteWebhookAsync();
var botInfo = await bot.GetMeAsync();
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Бот @{botInfo.Username} готов к работе!");
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚀 Запускаем основной цикл обработки сообщений...");

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

        foreach (var update in updates)
        {
            lastUpdateId = update.Id;

            if (update.Message?.From?.IsBot == true) continue;
            if (update.Message?.Text == null) continue;

            long chatId = update.Message.Chat.Id;
            string text = update.Message.Text;
            var chatType = update.Message.Chat.Type;
            long userId = update.Message.From!.Id;
            
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 📩 [{chatType}] {text}");

            try
            {

                string? userState = await db.StringGetAsync($"user:{userId}:state");
                string? userName = await db.StringGetAsync($"user:{userId}:name");

                //---[ОБРАБОТКА КНОПОК МЕНЮ]---
                if (chatType == ChatType.Private)
                {
                    switch (text)
                    {
                        case "Начать":
                            text = "/start";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Кнопка 'Начать' → /start");
                            break;
                        case "Помощь":
                            text = "/help";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Кнопка 'Помощь' → /help");
                            break;
                        case "Отмена" when userState == "awaiting_name":
                            text = "/cancel";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Кнопка 'Отмена' → /cancel");
                            break;
                        case "Сброс" when !string.IsNullOrEmpty(userName):
                            text = "/reset";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Кнопка 'Сброс' → /reset");
                            break;
                    }
                }

                //---[ФУНКЦИЯ ДЛЯ КЛАВИАТУРЫ]---
                IReplyMarkup GetSafeReplyMarkup(ChatType type, string state = "") => 
                    type != ChatType.Private 
                        ? new ReplyKeyboardRemove { Selective = false } 
                        : state switch
                        {
                            "awaiting_name" => GetCancelKeyboard(),
                            _ => string.IsNullOrEmpty(userName) && state != "just_reset"
                                ? GetStartKeyboard() 
                                : GetMainMenuKeyboard()
                        };

                //---[ОБРАБОТКА КОМАНД]---
                string lowerText = text.ToLower();
                
                switch (lowerText)
                {
                    case var cmd when cmd.StartsWith("/start"):
                        if (chatType != ChatType.Private)
                        {
                            bool hasMention = lowerText.Contains($"@{botInfo.Username.ToLower()}");
                            bool isReplyToBot = update.Message.ReplyToMessage?.From?.Id == botInfo.Id;
                            
                            if (!hasMention && !isReplyToBot)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{chatId}] Игнорируем /start без упоминания");
                                continue;
                            }
                        }

                        await db.KeyDeleteAsync($"user:{userId}:state");
                        
                        if (!string.IsNullOrEmpty(userName))
                        {
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: $"😊 Привет снова, {userName}!\nВас приветствует бот команды Stellvia!",
                                replyMarkup: GetSafeReplyMarkup(chatType)
                            );
                        }
                        else
                        {
                            string instruction = chatType == ChatType.Private 
                                ? "👋 Привет! Как вас зовут?"
                                : $"👋 Привет! Как вас зовут?\n\nℹ️ Ответьте на это сообщение";
                            
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: instruction,
                                parseMode: ParseMode.Markdown,
                                replyMarkup: GetSafeReplyMarkup(chatType, "awaiting_name")
                            );
                            
                            await db.StringSetAsync($"user:{userId}:state", "awaiting_name");
                        }
                        
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] /start обработана");
                        continue;
                    
                    case "/cancel":
                        await db.KeyDeleteAsync($"user:{userId}:state");
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "✅ Действие отменено. Вы можете начать сначала.",
                            replyMarkup: GetSafeReplyMarkup(chatType)
                        );
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Состояние сброшено");
                        continue;
                    
                    case "/reset":
                        if (!string.IsNullOrEmpty(userName))
                        {
                            await db.KeyDeleteAsync($"user:{userId}:name");
                            await db.KeyDeleteAsync($"user:{userId}:state");

                            IReplyMarkup resetMarkup = chatType == ChatType.Private 
                                ? GetStartKeyboard() 
                                : new ReplyKeyboardRemove { Selective = false };
                            
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: chatType == ChatType.Private 
                                    ? "🔄 Ваши данные полностью удалены. Нажмите кнопку, чтобы начать заново:"
                                    : "🔄 Ваши данные полностью удалены. Используйте команду /start",
                                replyMarkup: resetMarkup
                            );
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 [{userId}] Полный сброс данных");
                        }
                        else
                        {
                            IReplyMarkup noDataMarkup = chatType == ChatType.Private 
                                ? GetStartKeyboard() 
                                : new ReplyKeyboardRemove { Selective = false };
                            
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: "ℹ️ У вас нет сохраненных данных для сброса.",
                                replyMarkup: noDataMarkup
                            );
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ℹ️ [{userId}] Попытка сброса без данных");
                        }
                        continue;
                    
                    case var cmd when cmd.StartsWith("/help"):
                        string helpText = "❓ Доступные команды:\n\n";
                        helpText += "👤 Для ЛС:\n`/start`, `/help`, `/cancel`, `/reset`\n\n";
                        helpText += "👥 Для групп:\n`/start@{botInfo.Username}`, `/help`, `/cancel`, `/reset`";
                        
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: helpText.Replace("{botInfo.Username}", botInfo.Username),
                            parseMode: ParseMode.Markdown,
                            replyMarkup: GetSafeReplyMarkup(chatType)
                        );
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] /help обработана");
                        continue;
                }

                //---[БЛОК: ПЕРВОЕ ВЗАИМОДЕЙСТВИЕ В ЛС]---
                if (chatType == ChatType.Private && 
                    string.IsNullOrEmpty(userName) && 
                    string.IsNullOrEmpty(userState) && 
                    !text.StartsWith("/"))
                {
                    await bot.SendTextMessageAsync(
                        chatId: chatId,
                        text: "👋 Добро пожаловать! Нажмите кнопку, чтобы начать:",
                        replyMarkup: GetStartKeyboard()
                    );
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{chatId}] Отправлено приветствие с кнопкой 'Начать'");
                    continue;
                }

                //---[ОБРАБОТКА СОСТОЯНИЯ]---
                if (userState == "awaiting_name")
                {
                    if (chatType != ChatType.Private)
                    {
                        bool isReplyToBot = update.Message.ReplyToMessage?.From?.Id == botInfo.Id;
                        bool hasMention = text.Contains($"@{botInfo.Username}");
                        
                        if (!isReplyToBot && !hasMention)
                        {
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: $"ℹ️ Пожалуйста, ответьте на мое сообщение или упомяните бота:\n`@{botInfo.Username} Ваше_имя`",
                                replyToMessageId: update.Message.MessageId,
                                parseMode: ParseMode.Markdown,
                                replyMarkup: new ReplyKeyboardRemove { Selective = false }
                            );
                            continue;
                        }
                        
                        if (hasMention)
                        {
                            text = text.Replace($"@{botInfo.Username}", "").Trim();
                        }
                    }

                    if (text.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: chatType == ChatType.Private
                                ? "❌ Это команда. Введите имя или нажмите 'Отмена'."
                                : "❌ Это команда. Введите ваше имя.",
                            replyMarkup: GetSafeReplyMarkup(chatType),
                            replyToMessageId: chatType != ChatType.Private ? update.Message.MessageId : null
                        );
                        continue;
                    }

                    string trimmedName = text.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length < 2 || trimmedName.Length > 50)
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: chatType == ChatType.Private
                                ? "❌ Имя должно содержать от 2 до 50 символов. Попробуйте снова."
                                : "❌ Имя должно содержать от 2 до 50 символов.",
                            replyMarkup: GetSafeReplyMarkup(chatType),
                            replyToMessageId: chatType != ChatType.Private ? update.Message.MessageId : null
                        );
                        continue;
                    }

                    trimmedName = Regex.Replace(trimmedName, @"[^\w\s\-]", "").Trim();
                    if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length < 2)
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: chatType == ChatType.Private
                                ? "❌ Недопустимые символы в имени. Используйте буквы, цифры, пробелы и дефисы."
                                : "❌ Недопустимые символы в имени.",
                            replyMarkup: GetSafeReplyMarkup(chatType),
                            replyToMessageId: chatType != ChatType.Private ? update.Message.MessageId : null
                        );
                        continue;
                    }

                    try
                    {
                        await db.StringSetAsync($"user:{userId}:name", trimmedName);
                        await db.KeyDeleteAsync($"user:{userId}:state");
                        
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: chatType == ChatType.Private
                                ? $"🎉 Отлично, {trimmedName}! Теперь вы можете использовать меню для быстрого доступа"
                                : $"🎉 Отлично, {trimmedName}! Вас приветствует бот команды Stellvia!",
                            replyMarkup: GetSafeReplyMarkup(chatType)
                        );
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ [{userId}] Имя сохранено");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка при сохранении имени: {ex.Message}");
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: chatType == ChatType.Private
                                ? "⚠️ Ошибка при сохранении имени. Попробуйте позже или нажмите 'Отмена'."
                                : "⚠️ Ошибка при сохранении имени. Попробуйте позже.",
                            replyMarkup: GetSafeReplyMarkup(chatType),
                            replyToMessageId: chatType != ChatType.Private ? update.Message.MessageId : null
                        );
                        await db.KeyDeleteAsync($"user:{userId}:state");
                    }
                    continue;
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                switch (ex.ErrorCode)
                {
                    case 403:
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ {chatId} заблокировал бота");
                        break;
                    case 400 when ex.Message.Contains("CHAT_WRITE_FORBIDDEN"):
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ {chatId} Бот не имеет прав на отправку сообщений");
                        break;
                    case 400 when ex.Message.Contains("BUTTON_TYPE_INVALID") || 
                                 ex.Message.Contains("can't create inline keyboard"):
                        try
                        {
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

//---[ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ]---
ReplyKeyboardMarkup GetStartKeyboard() => new(new[] { new KeyboardButton("Начать") }) 
{ ResizeKeyboard = true, OneTimeKeyboard = true };

ReplyKeyboardMarkup GetMainMenuKeyboard() => new(new[] 
{
    new KeyboardButton("Начать"),
    new KeyboardButton("Помощь"), 
    new KeyboardButton("Сброс")
}) { ResizeKeyboard = true };

ReplyKeyboardMarkup GetCancelKeyboard() => new(new[] { new KeyboardButton("Отмена") }) 
{ ResizeKeyboard = true, OneTimeKeyboard = true };

async Task ClearOldUpdates(ITelegramBotClient botClient)
{
    try { await botClient.GetUpdatesAsync(offset: -1); }
    catch {}
}