using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
//using CounterStrikeSharp.API.Modules.Players;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
// using CounterStrikeSharp.API.Modules.Events.Enums; // если нужно для HookResult и т.п.
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace LiveCenterHtmlPlugin;

public class LiveCenterHtmlPluginConfig : BasePluginConfig {
    // Path to the HTML file. Adjust for your server layout.
    public string HtmlFilePath { get; set; } = "csgo/addons/counterstrikesharp/ui/ui.html";

    // How often to poll the file for changes (milliseconds).
    public int PollIntervalMs { get; set; } = 1000;

    // Duration in seconds for PrintToCenterHtml.
    public int  DisplayDurationSeconds { get; set; } = 60;
}

public class LiveCenterHtmlPlugin : BasePlugin, IPluginConfig<LiveCenterHtmlPluginConfig> {
    public override string ModuleName => "LiveCenterHtmlPlugin";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "Sergey + ChatGPT";

    public LiveCenterHtmlPluginConfig Config { get => _config; set => _config = value; }

    private LiveCenterHtmlPluginConfig _config = new();
    private Timer? _pollTimer;

    private DateTime _lastWriteTime = DateTime.MinValue;
    private string _currentHtml = string.Empty;

    // Players subscribed to live HTML updates (by SteamID).
    private readonly HashSet<ulong> _subscribedPlayers = new();

    public void OnConfigParsed(LiveCenterHtmlPluginConfig config) {
        _config = config;

        // Если есть подписчики и обновился PollIntervalMs — перезапустим таймер,
        // если подписчиков нет — таймер держать не нужно.
        if (_subscribedPlayers.Count > 0)
            StartPollingIfNeeded();
        else
            StopPolling();

        Logger.LogInformation($"[{ModuleName}] Config parsed. Watching: {_config.HtmlFilePath}");
    }

    public override void Load(bool hotReload) {
        base.Load(hotReload);

        // Chat/console command: !livehtml /livehtml or css_livehtml
        AddCommand("css_livehtml", "Toggle debug CenterHtml from file", OnLiveHtmlCommand);

        // Регистрируем обработчик дисконнекта игрока
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        Logger.LogInformation(
            $"[{ModuleName}] Loaded. Command: !livehtml, file: {_config.HtmlFilePath}, polling disabled until someone subscribes.");
    }

    public override void Unload(bool hotReload) {
        StopPolling();
        _subscribedPlayers.Clear();
        base.Unload(hotReload);
    }

    // ====== Polling control ======

    private void StartPollingIfNeeded() {
        if (_config.PollIntervalMs <= 0) {
            Logger.LogWarning($"[{ModuleName}] PollIntervalMs <= 0, polling disabled.");
            StopPolling();
            return;
        }

        if (_subscribedPlayers.Count == 0) {
            // Некого оповещать — не создаём таймер.
            StopPolling();
            return;
        }

        if (_pollTimer != null)
            return; // уже запущен

        _pollTimer = AddTimer(_config.PollIntervalMs / 1000.0f, TimerCallback, TimerFlags.REPEAT);
        Logger.LogInformation(
            $"[{ModuleName}] Polling started (interval={_config.PollIntervalMs} ms, subscribers={_subscribedPlayers.Count}).");

        // Можно попытаться сразу один раз загрузить файл
        TryInitialLoad();
    }

    private void StopPolling() {
        if (_pollTimer != null) {
            _pollTimer.Kill();
            _pollTimer = null;
            Logger.LogInformation($"[{ModuleName}] Polling stopped (no subscribers or disabled).");
        }
    }

    private void TryInitialLoad() {
        try {
            var path = _config.HtmlFilePath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!File.Exists(path))
                return;

            var info = new FileInfo(path);
            _lastWriteTime = info.LastWriteTimeUtc;
            _currentHtml = File.ReadAllText(path) ?? string.Empty;
            Logger.LogInformation($"[{ModuleName}] Initial HTML loaded from {path}, length={_currentHtml.Length}");
        }
        catch (Exception ex) {
            Logger.LogError(ex, $"[{ModuleName}] Failed to initial-load HTML file.");
        }
    }

    // ====== Timer callback ======

    private void TimerCallback() {
        try {
            if (_subscribedPlayers.Count == 0) {
                StopPolling();
                return;
            }

            var path = _config.HtmlFilePath;

            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!File.Exists(path))
                return;

            var info = new FileInfo(path);
            var writeTime = info.LastWriteTimeUtc;

            // Only update if file write time changed.
            if (writeTime <= _lastWriteTime)
                return;

            _lastWriteTime = writeTime;

            string html;
            try {
                html = File.ReadAllText(path);
            }
            catch (Exception ex) {
                Logger.LogError(ex, $"[{ModuleName}] Failed to read HTML file: {path}");
                return;
            }

            _currentHtml = html ?? string.Empty;

            Logger.LogInformation($"[{ModuleName}] HTML updated from disk ({path}), length={_currentHtml.Length}");

            // Send updated HTML to all subscribed players.
            BroadcastToSubscribedPlayers();
        }
        catch (Exception ex) {
            Logger.LogError(ex, $"[{ModuleName}] TimerCallback error");
        }
    }

    private void BroadcastToSubscribedPlayers() {
        if (_subscribedPlayers.Count == 0)
            return;

        if (string.IsNullOrEmpty(_currentHtml))
            return;

        Server.NextFrame(() => {
            var players = Utilities.GetPlayers()
                .Where(p => p is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.PlayerConnected })
                .ToList();

            float duration = _config.DisplayDurationSeconds > 0
                ? _config.DisplayDurationSeconds
                : 60.0f;

            int sentCount = 0;

            foreach (var player in players) {
                try {
                    ulong steamId = player.SteamID; // Adjust property name if your API differs.
                    if (!_subscribedPlayers.Contains(steamId))
                        continue;

                    player.PrintToCenterHtml(_currentHtml, (int)duration);
                    sentCount++;
                }
                catch (Exception ex) {
                    Logger.LogError(ex, $"[{ModuleName}] Failed to send HTML to player {player.SteamID}");
                }
            }

            if (sentCount > 0) {
                Logger.LogInformation($"[{ModuleName}] Sent updated HTML to {sentCount} subscribed players.");
            }
        });
    }

    // ====== Command handler (!livehtml) ======

    private void OnLiveHtmlCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        // If called from server console.
        if (player == null) {
            Logger.LogInformation($"[{ModuleName}] css_livehtml immediate called from console -> ignoring");
            return;
        }

        if (!player.IsValid || player.IsBot || player.Connected != PlayerConnectedState.PlayerConnected) {
            return;
        }

        // Subcommand: "immediate ..."
        if (commandInfo.ArgCount >= 2) {
            var sub = commandInfo.GetArg(1);
            if (!string.IsNullOrEmpty(sub) &&
                sub.Equals("immediate", StringComparison.OrdinalIgnoreCase)) {
                HandleImmediateCommand(player, commandInfo);
                return; // do not touch subscription
            }
        }

        // ===== normal toggle mode =====
        ulong steamId = player.SteamID;

        if (_subscribedPlayers.Contains(steamId)) {
            _subscribedPlayers.Remove(steamId);

            try {
                player.PrintToCenterHtml(string.Empty, 0);
            }
            catch (Exception ex) {
                Logger.LogError(ex, $"[{ModuleName}] Failed to clear HTML for player {player.SteamID}");
            }

            player.PrintToChat($"[{ModuleName}] Live HTML: OFF");
            Logger.LogInformation($"[{ModuleName}] Player {player.SteamID} unsubscribed from live HTML.");

            if (_subscribedPlayers.Count == 0)
                StopPolling();
        } else {
            _subscribedPlayers.Add(steamId);
            player.PrintToChat($"[{ModuleName}] Live HTML: ON (watching {Path.GetFileName(_config.HtmlFilePath)})");
            Logger.LogInformation($"[{ModuleName}] Player {player.SteamID} subscribed to live HTML.");

            StartPollingIfNeeded();

            if (string.IsNullOrEmpty(_currentHtml))
                TryInitialLoad();

            if (!string.IsNullOrEmpty(_currentHtml)) {
                try {
                    int duration = _config.DisplayDurationSeconds > 0
                        ? _config.DisplayDurationSeconds
                        : 60;

                    player.PrintToCenterHtml(_currentHtml, duration);
                }
                catch (Exception ex) {
                    Logger.LogError(ex, $"[{ModuleName}] Failed to send initial HTML to player {player.SteamID}");
                }
            }
        }
    }


    private void HandleImmediateCommand(CCSPlayerController player, CommandInfo commandInfo) {
        // Pattern 1: css_livehtml immediate 100 Some text here
        // Args:
        //   0: "css_livehtml"
        //   1: "immediate"
        //   2: "100"
        //   3..N: text

        if (commandInfo.ArgCount < 4) {
            player.PrintToChat($"[{ModuleName}] Immediate HTML print error: invalid params count: {commandInfo.ArgCount}");
            return;
        }

        var durationString = commandInfo.GetArg(2);

        if (!int.TryParse(durationString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration)) {
            player.PrintToChat($"[{ModuleName}] Immediate HTML print error: invalid duration value: {durationString}");
            return;
        }

        var textParts = new List<string>();
        for (int i = 3; i < commandInfo.ArgCount; i++) { 
            textParts.Add(commandInfo.GetArg(i));
        }

        string text = string.Join(" ", textParts);
       

        try {
            player.PrintToCenterHtml(text, duration);
            player.PrintToChat($"[{ModuleName}] Immediate HTML printed for {duration.ToString(CultureInfo.InvariantCulture)}s.");
            Logger.LogInformation($"[{ModuleName}] Immediate HTML printed for player {player.SteamID}, duration={duration}, length={text.Length}.");
        }
        catch (Exception ex) {
            Logger.LogError(ex, $"[{ModuleName}] Failed to send immediate HTML to player {player.SteamID}");
        }
    }



    // ====== Player disconnect handler ======

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info) {
        if(@event.Userid != null) {
            try {
                // Обычно в событии есть Userid, через который можно получить игрока.
                var player = @event.Userid;// Utilities.GetPlayerFromUserid(@event.Userid);
                if (player == null || !player.IsValid)
                    return HookResult.Continue;

                ulong steamId = player.SteamID; // Adjust property name if needed.

                if (_subscribedPlayers.Remove(steamId)) {
                    Logger.LogInformation(
                        $"[{ModuleName}] Player {player.SteamID} unsubscribed from live HTML (disconnect).");

                    if (_subscribedPlayers.Count == 0)
                        StopPolling();
                }
            }
            catch (Exception ex) {
                Logger.LogError(ex, $"[{ModuleName}] Error in OnPlayerDisconnect");
            }
        }
       

        return HookResult.Continue;
    }
}
