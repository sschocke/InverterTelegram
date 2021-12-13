using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace InverterTelegram;

public class Worker : BackgroundService
{
    private string TelegramBotId;
    private long TelegramGroupId;
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _config;
    private string currentStatus = "Line";
    private InverterMon.InverterStatus? current = null;
    private int lastUpdateId = 0;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        TelegramBotId = _config.GetValue<string>("TelegramBotId");
        TelegramGroupId = _config.GetValue<long>("TelegramGroupId");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var botClient = new TelegramBotClient(TelegramBotId);
        var me = await botClient.GetMeAsync();
        // botClient.StartReceiving();
        // botClient.OnMessage += BotMessage;
        var batteryCommand = new BotCommand()
        {
            Command = "/battery",
            Description = "Returns current battery percentage"
        };
        var statusCommand = new BotCommand()
        {
            Command = "/status",
            Description = "Returns current line status mode"
        };
        var infoCommand = new BotCommand()
        {
            Command = "/info",
            Description = "Returns summary of current data"
        };
        await botClient.SetMyCommandsAsync(new List<BotCommand> { batteryCommand, infoCommand, statusCommand });

        _logger.LogDebug(
          $"Hello, World! I am user {me.Username} and my name is {me.FirstName}."
        );

        var factory = new ConnectionFactory();
        factory.HostName = _config.GetValue<string>("MQHost");
        factory.UserName = _config.GetValue<string>("MQUser");
        factory.Password = _config.GetValue<string>("MQPassword");
        factory.VirtualHost = "/";

        using (var mq = factory.CreateConnection())
        {
            using (var channel = mq.CreateModel())
            {
                channel.ExchangeDeclare("inverter", ExchangeType.Topic, false, false);

                var queue = channel.QueueDeclare().QueueName;
                channel.QueueBind(queue, "inverter", "status");

                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += async (model, ea) =>
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    var routingKey = ea.RoutingKey;
                    _logger.LogDebug($" [x] Received '{routingKey}':'{message}'");

                    var status = JsonSerializer.Deserialize<InverterMon.InverterStatus>(message);
                    if (status != null)
                    {
                        if (status.Mode != currentStatus)
                        {
                            _logger.LogInformation($"Status change: {currentStatus} => {status.Mode}");
                            currentStatus = status.Mode;
                            await botClient.SendTextMessageAsync(TelegramGroupId, $"Inverter Status Change={currentStatus}");
                        }
                        current = status;
                    }
                };
                channel.BasicConsume(queue, true, consumer);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var updates = await botClient.GetUpdatesAsync(lastUpdateId);
                    foreach (var update in updates)
                    {
                        _logger.LogDebug($"Telegram Update: {update.Id} - {update.Type}");
                        switch (update.Type)
                        {
                            case UpdateType.Message:
                                if (update.Message.Type != MessageType.Text || !update.Message.Text.StartsWith("/"))
                                {
                                    break;
                                }

                                _logger.LogDebug($"Message received: {update.Message.Text}");
                                var cmd = update.Message.Text.ToLower();
                                if (cmd.Contains($"@{me.Username}"))
                                {
                                    cmd = cmd.Replace($"@{me.Username}", "");
                                }
                                switch (cmd)
                                {
                                    case "/battery":
                                        await SendBatteryReply(botClient, update);
                                        break;
                                    case "/status":
                                        await SendStatusReply(botClient, update);
                                        break;
                                    case "/info":
                                        await SendInfoReply(botClient, update);
                                        break;
                                }
                                break;
                        }
                    }
                    if (updates.Any())
                    {
                        lastUpdateId = updates.Max(update => update.Id) + 1;
                    }
                    // _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                    await Task.Delay(1000, stoppingToken);
                }

                channel.Close();
                mq.Close();
            }
        }
    }

    private async Task SendBatteryReply(TelegramBotClient botClient, Update update)
    {
        var reply = current != null ? $"Battery at {current.BatteryCapacity}%" : "Unknown Battery Status";
        await botClient.SendTextMessageAsync(
            update.Message.Chat.Id,
            reply,
            replyToMessageId: update.Message.MessageId);
    }
    private async Task SendStatusReply(TelegramBotClient botClient, Update update)
    {
        var reply = current != null ? $"Online status {current.Mode}" : "Unknown Status";
        await botClient.SendTextMessageAsync(
            update.Message.Chat.Id,
            reply,
            replyToMessageId: update.Message.MessageId);
    }
    private async Task SendInfoReply(TelegramBotClient botClient, Update update)
    {
        var reply = "Unknown Status";
        if (current != null) {
            var lineStatus = $"Online status: {current.Mode}";
            var acIn = $"AC Input: {current.GridVoltage}V {current.GridFrequency}Hz";
            var output = $"Output: {current.OutputVoltage}V {current.OutputFrequency}Hz";
            var battery = $"Battery: {current.BatteryCapacity}% {current.BatteryVoltage}V";
            reply = $"{lineStatus}\r\n{acIn}\r\n{output}\r\n{battery}";
        }
        await botClient.SendTextMessageAsync(
            update.Message.Chat.Id,
            reply,
            replyToMessageId: update.Message.MessageId);
    }
}
