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
    private readonly string TelegramBotId;
    private readonly long TelegramGroupId;
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _config;
    private string currentMode = "Line";
    private InverterMon.InverterStatus? current = null;
    private int lastUpdateId = 0;
    private DateTime lastStatusChange = DateTime.MinValue;
    private bool inverterMonOnline = false;

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
        var me = await botClient.GetMeAsync(stoppingToken);
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
        await botClient.SetMyCommandsAsync(new List<BotCommand> { batteryCommand, infoCommand, statusCommand }, stoppingToken);

        _logger.LogDebug(
          "Hello, World! I am user {Username} and my name is {FirstName}.",
          me.Username,
          me.FirstName
        );

        var factory = new ConnectionFactory
        {
            HostName = _config.GetValue<string>("MQHost"),
            UserName = _config.GetValue<string>("MQUser"),
            Password = _config.GetValue<string>("MQPassword"),
            VirtualHost = "/"
        };

        using var mq = factory.CreateConnection();
        using var channel = mq.CreateModel();

        channel.ExchangeDeclare("inverter", ExchangeType.Topic, false, false);

        var queue = channel.QueueDeclare().QueueName;
        channel.QueueBind(queue, "inverter", "status");

        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            var routingKey = ea.RoutingKey;
            _logger.LogDebug(" [x] Received '{RoutingKey}':'{Message}'", routingKey, message);

            var status = JsonSerializer.Deserialize<InverterMon.InverterStatus>(message);
            if (status != null)
            {
                if (status.Mode != currentMode)
                {
                    _logger.LogInformation("Status change: {PreviousMode} => {CurrentMode}", currentMode, status.Mode);
                    currentMode = status.Mode;
                    await botClient.SendTextMessageAsync(TelegramGroupId, $"Inverter Status Change={currentMode}");
                }
                current = status;

                if (!inverterMonOnline)
                {
                    await botClient.SendTextMessageAsync(TelegramGroupId, "Inverter Monitor Online");
                }
                lastStatusChange = DateTime.Now;
                inverterMonOnline = true;
            }
        };
        channel.BasicConsume(queue, true, consumer);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await botClient.GetUpdatesAsync(lastUpdateId, cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    _logger.LogDebug("Telegram Update: {UpdateId} - {UpdateType}", update.Id, update.Type);
                    switch (update.Type)
                    {
                        case UpdateType.Message:
                            if (update.Message.Type != MessageType.Text || !update.Message.Text.StartsWith("/"))
                            {
                                break;
                            }

                            _logger.LogDebug("Message received: {Message}", update.Message.Text);
                            var cmd = update.Message.Text.ToLower();
                            if (cmd.Contains($"@{me.Username}"))
                            {
                                cmd = cmd.Replace($"@{me.Username}", "");
                            }
                            switch (cmd)
                            {
                                case "/battery":
                                    await SendBatteryReply(botClient, update, stoppingToken);
                                    break;
                                case "/status":
                                    await SendStatusReply(botClient, update, stoppingToken);
                                    break;
                                case "/info":
                                    await SendInfoReply(botClient, update, stoppingToken);
                                    break;
                            }
                            break;
                    }
                }

                if (updates.Any())
                {
                    lastUpdateId = updates.Max(update => update.Id) + 1;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Telegram Client");
            }

            if (lastStatusChange < DateTime.Now.AddMinutes(-5) && inverterMonOnline)
            {
                inverterMonOnline = false;
                await botClient.SendTextMessageAsync(TelegramGroupId, "Inverter Monitor Offline", cancellationToken: stoppingToken);
            }

            await Task.Delay(1000, stoppingToken);
        }

        channel.Close();
        mq.Close();
    }

    private async Task SendBatteryReply(TelegramBotClient botClient, Update update, CancellationToken stoppingToken)
    {
        var reply = current != null ? $"Battery is {current.BatteryCapacity}% at {lastStatusChange}" : "Unknown Battery Status";
        await botClient.SendTextMessageAsync(update.Message.Chat.Id, reply, replyToMessageId: update.Message.MessageId, cancellationToken: stoppingToken);
    }
    private async Task SendStatusReply(TelegramBotClient botClient, Update update, CancellationToken stoppingToken)
    {
        var reply = current != null ? $"Online status {current.Mode} at {lastStatusChange}" : "Unknown Status";
        await botClient.SendTextMessageAsync(update.Message.Chat.Id, reply, replyToMessageId: update.Message.MessageId, cancellationToken: stoppingToken);
    }
    private async Task SendInfoReply(TelegramBotClient botClient, Update update, CancellationToken stoppingToken)
    {
        var reply = "Unknown Status";
        if (current != null)
        {
            var onBattery = current.Mode.ToLower() == "battery";
            var lineStatus = $"Online status: {current.Mode}";
            var acIn = $"AC Input: {current.GridVoltage}V {current.GridFrequency}Hz";
            var output = $"Output: {current.OutputVoltage}V {current.OutputFrequency}Hz";
            var load = $"Load: {current.LoadPercentage}% {current.LoadWatt}W";
            var battDischarge = $"Discharging: {current.BatteryDischargeCurrent}A";
            var battCharge = $"Charging: {current.BatteryChargeCurrent}A";
            var battery = $"Battery: {current.BatteryCapacity}% {current.BatteryVoltage}V ({(onBattery ? battDischarge : battCharge)})";
            reply = $"Status at {lastStatusChange}\r\n{lineStatus}\r\n{load}\r\n{acIn}\r\n{output}\r\n{battery}";
        }
        await botClient.SendTextMessageAsync(update.Message.Chat.Id, reply, replyToMessageId: update.Message.MessageId, cancellationToken: stoppingToken);
    }
}
