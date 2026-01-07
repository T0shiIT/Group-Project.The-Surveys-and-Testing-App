using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var botToken = "8226200524:AAF5DzkLNIHr1wjkNhyjhjbymUN3pKHu55I";
var bot = new TelegramBotClient(botToken);

// Очищаем старые обновления
await ClearOldUpdates(bot);

await bot.DeleteWebhookAsync();
var botInfo = await bot.GetMeAsync();
Console.WriteLine($"✅ Бот @{botInfo.Username} готов к работе!");

while (true)
{
    try
    {
        // Получаем ВСЕ доступные обновления
        var updates = await bot.GetUpdatesAsync(
            offset: 0,
            limit: 100,
            timeout: 0, // Немедленный ответ без ожидания
            allowedUpdates: new[] { UpdateType.Message }
        );

        foreach (var update in updates)
        {
            // Пропускаем сообщения от ботов
            if (update.Message?.From?.IsBot == true) continue;
            
            // Пропускаем не текстовые сообщения
            if (update.Message?.Text == null) continue;

            long chatId = update.Message.Chat.Id;
            string text = update.Message.Text;
            
            Console.WriteLine($"📩 [{DateTime.Now:HH:mm:ss}] {chatId}: {text}");

            try
            {
                // Отправляем ответ
                await bot.SendTextMessageAsync(chatId, $"Вас приветствует бот команды Stellvia!");
                Console.WriteLine($"✅ Ответ отправлен {chatId}");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                if (ex.ErrorCode == 403)
                    Console.WriteLine($"⚠️ {chatId} заблокировал бота");
                else
                    Console.WriteLine($"❌ Ошибка: {ex.Message}");
            }

            // ⭐️ КЛЮЧЕВОЙ ШАГ: помечаем обновление как обработанное
            await bot.GetUpdatesAsync(offset: update.Id + 1);
            Console.WriteLine($"🔖 Обновление {update.Id} помечено как прочитанное");
        }

        // Если обновлений нет - ждём немного дольше
        if (updates.Length == 0)
        {
            await Task.Delay(2000);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"🚨 Критическая ошибка: {ex.Message}");
        await Task.Delay(5000); // Длительная пауза при ошибках
    }
}

async Task ClearOldUpdates(ITelegramBotClient botClient)
{
    Console.WriteLine("🧹 Очищаем старые обновления...");
    
    try
    {
        // Получаем ВСЕ накопившиеся обновления
        var updates = await botClient.GetUpdatesAsync(
            offset: 0,
            limit: 100,
            timeout: 0
        );
        
        if (updates.Length > 0)
        {
            Console.WriteLine($"🗑️ Найдено {updates.Length} старых обновлений");
            
            // Помечаем последнее обновление как обработанное
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