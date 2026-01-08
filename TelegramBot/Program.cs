using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using StackExchange.Redis;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

//---[ИНИЦИАЛИЗАЦИЯ БОТА И REDIS]---
/* 
 * Настройка основных компонентов:
 * 1. Создание экземпляра Telegram бота
 * 2. Подключение к локальному Redis серверу
 * 3. Обработка ошибок подключения
 */
var botToken = "8226200524:AAF5DzkLNIHr1wjkNhyjhjbymUN3pKHu55I";
var bot = new TelegramBotClient(botToken);

IConnectionMultiplexer redis;
IDatabase db = null!;

try
{
    Console.WriteLine("♻️ Подключаемся к Redis...");
    redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379,abortConnect=false");
    db = redis.GetDatabase();
    Console.WriteLine("✅ Redis готов!");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Ошибка Redis: {ex.Message}");
    Console.WriteLine("Проверьте: запущен ли Redis сервер на localhost:6379?");
    Environment.Exit(1);
}

//---[ПОДГОТОВКА К РАБОТЕ]---
/* 
 * Финальные настройки перед запуском:
 * 1. Очистка старых обновлений от Telegram
 * 2. Удаление существующих вебхуков (если были)
 * 3. Получение информации о боте (имя, ID)
 * 4. Вывод диагностической информации
 */
await ClearOldUpdates(bot);
await bot.DeleteWebhookAsync();
var botInfo = await bot.GetMeAsync();
Console.WriteLine($"✅ Бот @{botInfo.Username} готов к работе!");
Console.WriteLine($"🆔 ID бота: {botInfo.Id}");
Console.WriteLine("🚀 Запускаем основной цикл обработки сообщений...");

//---[ОСНОВНОЙ ЦИКЛ ОБРАБОТКИ СООБЩЕНИЙ]---
/* 
 * Бесконечный цикл для получения и обработки сообщений:
 * 1. Запрос обновлений от Telegram API
 * 2. Обработка каждого обновления по очереди
 * 3. Фильтрация нежелательных сообщений (от других ботов, без текста)
 * 4. Централизованная обработка ошибок
 */
int lastUpdateId = 0;

while (true)
{
    try
    {
        // Получаем новые обновления от Telegram
        var updates = await bot.GetUpdatesAsync(
            offset: lastUpdateId + 1,
            limit: 100,
            timeout: 30,
            allowedUpdates: new[] { UpdateType.Message }
        );

        foreach (var update in updates)
        {
            lastUpdateId = update.Id;

            // Пропускаем сообщения от других ботов
            if (update.Message?.From?.IsBot == true) continue;
            
            // Пропускаем сообщения без текста (аудио, фото, стикеры)
            if (update.Message?.Text == null) continue;

            long chatId = update.Message.Chat.Id;
            string text = update.Message.Text;
            var chatType = update.Message.Chat.Type;
            long userId = update.Message.From!.Id;
            
            Console.WriteLine($"📩 [{chatType}] [{DateTime.Now:HH:mm:ss}] {chatId} (user:{userId}): {text}");

            try
            {
                // Получаем текущее состояние пользователя из Redis
                string? userState = await db.StringGetAsync($"user:{userId}:state");
                string? userName = await db.StringGetAsync($"user:{userId}:name");

                //---[ОБРАБОТКА КОМАНДЫ /START]---
                /* 
                 * Логика обработки команды /start:
                 * 1. Для групп: проверка упоминания бота или ответа на его сообщение
                 * 2. Сброс состояния пользователя перед началом диалога
                 * 3. Проверка, зарегистрирован ли пользователь ранее
                 * 4. Отправка приветственного сообщения
                 * 5. Установка состояния ожидания имени для новых пользователей
                 */
                if (text.ToLower().StartsWith("/start"))
                {
                    // Для групповых чатов: проверяем упоминание или ответ
                    if (chatType != ChatType.Private)
                    {
                        bool hasMention = text.ToLower().Contains($"@{botInfo.Username.ToLower()}");
                        bool isReplyToBot = update.Message.ReplyToMessage?.From?.Id == botInfo.Id;
                        
                        // Если нет упоминания и не ответ боту - игнорируем команду
                        if (!hasMention && !isReplyToBot)
                        {
                            Console.WriteLine($"ℹ️ [{chatId}] Игнорируем /start без упоминания и не в ответ боту");
                            continue;
                        }
                    }

                    // Сбрасываем предыдущее состояние пользователя
                    await db.KeyDeleteAsync($"user:{userId}:state");
                    
                    // Если пользователь уже зарегистрирован
                    if (!string.IsNullOrEmpty(userName))
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"😊 Привет снова, {userName}!\nВас приветствует бот команды Stellvia!"
                        );
                    }
                    // Если пользователь новый
                    else
                    {
                        // Формируем разные инструкции для ЛС и групп
                        string instruction = chatType == ChatType.Private 
                            ? "👋 Привет! Как вас зовут?"
                            : $"👋 Привет! Как вас зовут?\n\nℹ️ Чтобы ответить:\n1. Нажмите «Ответить» на это сообщение\n2. Или напишите: `@{botInfo.Username} Ваше_имя`";
                        
                        // Отправляем сообщение и сохраняем его ID для ответов
                        var sentMessage = await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: instruction,
                            parseMode: ParseMode.Markdown
                        );
                        
                        // Устанавливаем состояние ожидания имени
                        await db.StringSetAsync($"user:{userId}:state", "awaiting_name");
                        Console.WriteLine($"🔄 [{userId}] Установлено состояние: awaiting_name");
                    }
                    
                    Console.WriteLine($"✅ [{chatId}] Команда /start обработана");
                    continue; // Пропускаем остальные проверки после обработки команды
                }
                
                //---[ОБРАБОТКА КОМАНДЫ /HELP]---
                /* 
                 * Логика обработки команды /help:
                 * 1. Подготовка справочной информации
                 * 2. Разные инструкции для ЛС и групповых чатов
                 * 3. Форматирование сообщения с использованием Markdown
                 */
                else if (text.ToLower().StartsWith("/help"))
                {
                    string helpText = "❓ Доступные команды:\n\n";
                    
                    helpText += "👤 Для ЛС:\n" +
                                "`/start` - начать диалог\n" +
                                "`/help` - показать эту справку\n\n";
                    
                    helpText += "👥 Для групп:\n" +
                                $"`/start@{botInfo.Username}` - начать диалог\n" +
                                $"или ответьте на сообщение бота командой `/start`\n" +
                                "`/help` - показать справку";
                    
                    await bot.SendTextMessageAsync(
                        chatId: chatId,
                        text: helpText,
                        parseMode: ParseMode.Markdown
                    );
                    Console.WriteLine($"✅ [{chatId}] Команда /help обработана");
                    continue; // Пропускаем остальные проверки после обработки команды
                }

                //---[ОБРАБОТКА СОСТОЯНИЯ: ОЖИДАНИЕ ИМЕНИ]---
                /* 
                 * Состояние "awaiting_name":
                 * 1. Проверка контекста (ЛС или групповой чат)
                 * 2. Валидация ввода имени
                 * 3. Обработка специальных случаев (команды, неправильный формат)
                 * 4. Сохранение имени в Redis при успехе
                 */
                if (userState == "awaiting_name")
                {
                    Console.WriteLine($"🔄 [{userId}] Обработка состояния: awaiting_name");
                    
                    //---[ПОДБЛОК: ПРОВЕРКА КОНТЕКСТА ГРУППОВОГО ЧАТА]---
                    /* 
                     * Для групповых чатов:
                     * 1. Проверяем, является ли сообщение ответом на сообщение бота
                     * 2. Ищем упоминание бота в тексте
                     * 3. Даем пользователю подсказку, если формат неверный
                     */
                    if (chatType != ChatType.Private)
                    {
                        bool isReplyToBot = update.Message.ReplyToMessage?.From?.Id == botInfo.Id;
                        bool hasMention = text.Contains($"@{botInfo.Username}");
                        
                        // Если сообщение не является ответом и нет упоминания
                        if (!isReplyToBot && !hasMention)
                        {
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: $"ℹ️ Пожалуйста, ответьте на мое сообщение или упомяните бота:\n`@{botInfo.Username} Ваше_имя`",
                                replyToMessageId: update.Message.MessageId,
                                parseMode: ParseMode.Markdown
                            );
                            Console.WriteLine($"ℹ️ [{userId}] Ожидаем ответ или упоминание в группе");
                            continue;
                        }
                        
                        // Удаляем упоминание бота из текста для дальнейшей обработки
                        if (hasMention)
                        {
                            var mention = $"@{botInfo.Username}";
                            text = text.Replace(mention, "").Trim();
                            Console.WriteLine($"🔄 [{userId}] Удалено упоминание: {text}");
                        }
                    }

                    //---[ПОДБЛОК: ВАЛИДАЦИЯ ВВОДА]---
                    /* 
                     * Проверка введенного текста:
                     * 1. Не является ли командой
                     * 2. Соответствует ли длина требованиям (2-50 символов)
                     * 3. Содержит ли допустимые символы
                     */
                    if (text.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ Это команда. Пожалуйста, введите ваше имя."
                        );
                        Console.WriteLine($"ℹ️ [{userId}] Получена команда в состоянии ввода имени");
                        continue;
                    }

                    string trimmedName = text.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length < 2 || trimmedName.Length > 50)
                    {
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "❌ Имя должно содержать от 2 до 50 символов. Попробуйте снова."
                        );
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
                        continue;
                    }

                    //---[ПОДБЛОК: СОХРАНЕНИЕ ИМЕНИ]---
                    /* 
                     * Финальные шаги:
                     * 1. Сохранение имени в Redis
                     * 2. Сброс состояния пользователя
                     * 3. Отправка приветственного сообщения
                     * 4. Обработка возможных ошибок сохранения
                     */
                    try
                    {
                        await db.StringSetAsync($"user:{userId}:name", trimmedName);
                        await db.KeyDeleteAsync($"user:{userId}:state");
                        
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
                        await db.KeyDeleteAsync($"user:{userId}:state");
                    }
                    
                    continue;
                }

                //---[ОБРАБОТКА ОТВЕТОВ В ГРУППАХ]---
                /* 
                 * Дополнительная логика для групповых чатов:
                 * 1. Проверка ответов на сообщения бота
                 * 2. Установка состояния ожидания имени при ответе
                 * 3. Игнорирование ответов, содержащих команды
                 */
                if (chatType != ChatType.Private && update.Message.ReplyToMessage?.From?.Id == botInfo.Id)
                {
                    Console.WriteLine($"↩️ [{chatId}] Получен ответ на сообщение бота");
                    
                    // Игнорируем ответы, содержащие команды (они уже обработаны)
                    if (text.ToLower().StartsWith("/start") || text.ToLower().StartsWith("/help"))
                    {
                        // Команды уже обработаны в соответствующих блоках выше
                    }
                    else
                    {
                        // Устанавливаем состояние ожидания имени для ответа в группе
                        await db.StringSetAsync($"user:{userId}:state", "awaiting_name");
                        Console.WriteLine($"🔄 [{userId}] Установлено состояние из ответа в группе");
                    }
                }
                
                Console.WriteLine($"✅ [{chatId}] Обработка завершена");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                //---[ОБРАБОТКА ОШИБОК TELEGRAM API]---
                /* 
                 * Специфические ошибки Telegram API:
                 * 1. Пользователь заблокировал бота (403)
                 * 2. Нет прав на отправку сообщений в группу (400 CHAT_WRITE_FORBIDDEN)
                 * 3. Другие ошибки API
                 */
                if (ex.ErrorCode == 403)
                    Console.WriteLine($"⚠️ {chatId} заблокировал бота");
                else if (ex.ErrorCode == 400 && ex.Message.Contains("CHAT_WRITE_FORBIDDEN"))
                    Console.WriteLine($"⚠️ {chatId} Бот не имеет прав на отправку сообщений в группу");
                else
                    Console.WriteLine($"❌ Ошибка Telegram API: {ex.Message}");
            }
            catch (Exception ex)
            {
                //---[ОБРАБОТКА ОБЩИХ ОШИБОК]---
                /* 
                 * Обработка неожиданных исключений:
                 * 1. Логирование ошибки
                 * 2. Продолжение работы цикла
                 */
                Console.WriteLine($"❌ Неожиданная ошибка: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        //---[ОБРАБОТКА КРИТИЧЕСКИХ ОШИБОК]---
        /* 
         * Критические ошибки уровня цикла:
         * 1. Логирование полной ошибки
         * 2. Пауза перед повторной попыткой
         * 3. Защита от бесконечного цикла ошибок
         */
        Console.WriteLine($"🚨 Критическая ошибка: {ex.Message}");
        await Task.Delay(5000);
    }
    
    // Небольшая задержка между итерациями для снижения нагрузки
    await Task.Delay(1000);
}

//---[ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ]---
/* 
 * Вспомогательные функции проекта:
 * 1. Очистка старых обновлений (для стабильного запуска)
 */

//---[МЕТОД: ОЧИСТКА СТАРЫХ ОБНОВЛЕНИЙ]---
/* 
 * Очищает очередь обновлений от Telegram:
 * 1. Получает все старые обновления
 * 2. Помечает их как обработанные
 * 3. Логирует результат операции
 */
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