using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using StackExchange.Redis;
using System.Text.RegularExpressions;

var botToken = "8226200524:AAF5DzkLNIHr1wjkNhyjhjbymUN3pKHu55I";
var bot = new TelegramBotClient(botToken);

IConnectionMultiplexer redis;
IDatabase db = null!;

try
{
    Console.WriteLine("♻️ Подключаемся к Redis...");
    redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
    db = redis.GetDatabase();
    Console.WriteLine("✅ Redis готов!");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Ошибка Redis: {ex.Message}");
    Console.WriteLine("Проверьте: запущен ли контейнер 'redis-bot'?");
    Environment.Exit(1);
}

await ClearOldUpdates(bot);
await bot.DeleteWebhookAsync();
var botInfo = await bot.GetMeAsync();
Console.WriteLine($"✅ Бот @{botInfo.Username} готов к работе!");

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
            
            Console.WriteLine($"📩 [{chatType}] [{DateTime.Now:HH:mm:ss}] {chatId} (user:{userId}): {text}");

            try
            {
                string? userState = await db.StringGetAsync($"user:{userId}:state");
                string? userName = await db.StringGetAsync($"user:{userId}:name");

                if (userState == "awaiting_name")
                {
                    string originalText = text;
                    
                    if (chatType != ChatType.Private && update.Message.ReplyToMessage != null)
                    {
                        if (update.Message.ReplyToMessage.From?.Id != botInfo.Id)
                        {
                            Console.WriteLine($"ℹ️ [{userId}] Ответ не на сообщение бота");
                            continue;
                        }
                        Console.WriteLine($"↩️ Обработка ответа на сообщение бота");
                    }
                    else if (chatType != ChatType.Private)
                    {
                        var mention = $"@{botInfo.Username}";
                        if (text.Contains(mention))
                        {
                            text = text.Replace(mention, "").Trim();
                            Console.WriteLine($"🔄 Удалено упоминание: {text}");
                        }
                        else
                        {
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: $"ℹ️ Пожалуйста, ответьте на мое сообщение или упомяните бота:\n`@{botInfo.Username} Ваше_имя`",
                                replyToMessageId: update.Message.MessageId,
                                parseMode: ParseMode.Markdown
                            );
                            // ⭐️ НЕ СБРАСЫВАЕМ СОСТОЯНИЕ! Продолжаем ожидать имя
                            continue;
                        }
                    }

                    if (text.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ Это команда. Пожалуйста, введите ваше имя."
                        );
                        // ⭐️ НЕ СБРАСЫВАЕМ СОСТОЯНИЕ! Продолжаем ожидать имя
                        continue;
                    }

                    string trimmedName = text.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length < 2 || trimmedName.Length > 50)
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ Имя должно содержать от 2 до 50 символов. Попробуйте снова."
                        );
                        // ⭐️ КЛЮЧЕВОЕ ИЗМЕНЕНИЕ: НЕ СБРАСЫВАЕМ СОСТОЯНИЕ!
                        Console.WriteLine($"ℹ️ [{userId}] Имя некорректное, но состояние сохранено");
                        continue;
                    }

                    trimmedName = Regex.Replace(trimmedName, @"[^\w\s\-]", "").Trim();
                    if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length < 2)
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ Недопустимые символы в имени. Используйте только буквы, цифры, пробелы и дефисы."
                        );
                        // ⭐️ НЕ СБРАСЫВАЕМ СОСТОЯНИЕ!
                        continue;
                    }

                    try
                    {
                        await db.StringSetAsync($"user:{userId}:name", trimmedName);
                        await db.KeyDeleteAsync($"user:{userId}:state"); // Сбрасываем ТОЛЬКО при успехе
                        
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"🎉 Отлично, {trimmedName}! Вас приветствует бот команды Stellvia!"
                        );
                        Console.WriteLine($"✅ [{userId}] Имя сохранено: {trimmedName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Ошибка при сохранении имени: {ex.Message}");
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "⚠️ Произошла ошибка при сохранении имени. Попробуйте позже."
                        );
                        await db.KeyDeleteAsync($"user:{userId}:state"); // Сбрасываем при критической ошибке
                    }
                    
                    continue;
                }

                // Обработка команд
                if (text.ToLower().StartsWith("/start"))
                {
                    if (chatType != ChatType.Private)
                    {
                        var commandWithMention = $"/start@{botInfo.Username.ToLower()}";
                        if (!text.ToLower().StartsWith(commandWithMention))
                        {
                            Console.WriteLine($"ℹ️ [{chatId}] Игнорируем /start без упоминания в группе");
                            continue;
                        }
                    }

                    if (!string.IsNullOrEmpty(userName))
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"😊 Привет снова, {userName}!\nВас приветствует бот команды Stellvia!"
                        );
                    }
                    else
                    {
                        string instruction = chatType == ChatType.Private 
                            ? "👋 Привет! Как вас зовут?"
                            : $"👋 Привет! Как вас зовут?\n\nℹ️ Ответьте на это сообщение, написав ваше имя.";
                        
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: instruction,
                            parseMode: ParseMode.Markdown
                        );
                        
                        await db.StringSetAsync($"user:{userId}:state", "awaiting_name");
                    }
                }
                else if (text.ToLower().StartsWith("/help"))
                {
                    string helpText = "❓ Доступные команды:\n\n";
                    
                    helpText += "👤 Для ЛС:\n" +
                                "`/start` - начать диалог\n" +
                                "`/help` - показать эту справку\n\n";
                    
                    helpText += "👥 Для групп:\n" +
                                $"`/start@{botInfo.Username}` - начать диалог\n" +
                                "`/help` - показать справку";
                    
                    await bot.SendTextMessageAsync(
                        chatId: chatId,
                        text: helpText,
                        parseMode: ParseMode.Markdown
                    );
                }
                else if (chatType != ChatType.Private && update.Message.ReplyToMessage?.From?.Id == botInfo.Id)
                {
                    Console.WriteLine($"↩️ Получен ответ на сообщение бота в группе");
                    await db.StringSetAsync($"user:{userId}:state", "awaiting_name");
                }
                else
                {
                    Console.WriteLine($"ℹ️ [{chatId}] Игнорируем сообщение: {text}");
                }
                
                Console.WriteLine($"✅ Ответ отправлен {chatId}");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                if (ex.ErrorCode == 403)
                    Console.WriteLine($"⚠️ {chatId} заблокировал бота");
                else
                    Console.WriteLine($"❌ Ошибка Telegram API: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Неожиданная ошибка: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"🚨 Критическая ошибка: {ex.Message}");
        await Task.Delay(5000);
    }
    
    await Task.Delay(1000);
}

async Task ClearOldUpdates(ITelegramBotClient botClient)
{
    Console.WriteLine("🧹 Очищаем старые обновления...");
    
    try
    {
        var updates = await botClient.GetUpdatesAsync(
            offset: 0,
            limit: 100,
            timeout: 0
        );
        
        if (updates.Length > 0)
        {
            Console.WriteLine($"🗑️ Найдено {updates.Length} старых обновлений");
            await botClient.GetUpdatesAsync(offset: updates[^1].Id + 1);
            Console.WriteLine($"✅ Старые обновления очищены. Последний ID: {updates[^1].Id}");
        }
        else
        {
            Console.WriteLine("✅ Нет старых обновлений для очистки");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Ошибка при очистке: {ex.Message}");
    }
}