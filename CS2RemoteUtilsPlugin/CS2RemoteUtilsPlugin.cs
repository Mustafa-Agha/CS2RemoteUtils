using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CS2RemoteUtilsPlugin
{
  public class Match
  {
    public int? CT { get; set; }
    public int? T { get; set; }
    public int? CTRoundWins { get; set; }
    public int? TRoundWins { get; set; }
  }

  public class Player
  {
    public string? ID { get; set; }
    public string? PlayerName { get; set; }
    public string? Xuid { get; set; }
    public bool? Bot { get; set; }
    public string? Team { get; set; }
    public int? Kills { get; set; }
    public int? Assists { get; set; }
    public int? Deaths { get; set; }
  }

  public class Data
  {
    public string? Status { get; set; }
    public bool? Locked { get; set; }
    public bool? HalfTime { get; set; }
    public Match? MatchData { get; set; }
    public List<Player>? Players { get; set; }
    public Player? JoinedPlayer { get; set; }
    public Player? DisconnectedPlayer { get; set; }
    public string? CtName { get; set; }
    public string? TName { get; set; }
    public string? Instruction { get; set; }
  }

  [MinimumApiVersion(28)]
  public class CS2RemoteUtilsPlugin : BasePlugin
  {
    const int CS_TEAM_NONE = 0;       /**< No team yet. */
    const int CS_TEAM_SPECTATOR = 1;  /**< Spectators. */
    const int CS_TEAM_T = 2;          /**< Terrorists. */
    const int CS_TEAM_CT = 3;         /**< Counter-Terrorists. */

    public override string ModuleName => "CS2RemoteUtilsPlugin";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "CODEHUB";
    public override string ModuleDescription => "A plugin used for the CS2 Remote package";

    public FakeConVar<string> GTerrorist = new("sm_teamname_t", "Sets your Terrorist team name", "");
    public FakeConVar<string> GCTerrorist = new("sm_teamname_ct", "Sets your Counter-Terrorist team name", "");

    private HttpClient client = new HttpClient();

    private bool halfTime = false;

    private string url = "http://127.0.0.1:3542";
    private string path = "";

    private Data data = new Data();
    private Dictionary<string, CCSPlayerController> playerControllerDictionary = new Dictionary<string, CCSPlayerController>();

    private bool skipFirstRound;

    public override void Load(bool hotReload)
    {
      this.Log(PluginInfo());
      this.Log(this.ModuleDescription);
      this.PrintToPlayerOrServer("[CS2 Remote] Loaded: CS2 Remote Utils!");

      AddCommand("sm_cs2_remote", "Check to check if this plugin is available", this.Command_CS2_Remote);
      AddCommand("sm_cs2_remote_url", "Set's the CURL Url for API offloading", this.Command_CS2_Remote_URL);

      try
      {
        client.BaseAddress = new Uri(url);
      }
      catch (System.Exception ex)
      {
        this.PrintToPlayerOrServer(ex.Message);
      }

      data.Players = new List<Player>();
      data.MatchData = new Match
      {
        CTRoundWins = 0,
        TRoundWins = 0
      };

      GTerrorist.ValueChanged += (sender, value) =>
      {
        this.PrintToPlayerOrServer($"{sender} GTerrorist: {value}");

        Server.ExecuteCommand($"mp_teamname_2 {value}");
      };

      GCTerrorist.ValueChanged += (sender, value) =>
      {
        this.PrintToPlayerOrServer($"{sender} GCTerrorist: {value}");

        Server.ExecuteCommand($"mp_teamname_1 {value}");
      };

      RegisterEventHandler<EventPlayerConnect>(OnEventPlayerConnect);
      RegisterEventHandler<EventPlayerDisconnect>(OnEventPlayerDisconnect);
      RegisterEventHandler<EventPlayerSpawn>(OnEventPlayerSpawn);

      RegisterEventHandler<EventRoundEnd>(OnEventRoundEnd);
      RegisterEventHandler<EventAnnouncePhaseEnd>(OnEventAnnouncePhaseEnd);
      RegisterEventHandler<EventCsIntermission>(OnEventCsIntermission);
      RegisterEventHandler<EventPlayerDeath>(OnEventPlayerDeath);
      RegisterEventHandler<EventPlayerTeam>(OnEventPlayerTeam);
    }

    private void Command_CS2_Remote(CCSPlayerController? player, CommandInfo commandInfo)
    {
      // The player is null, then the command has been called by the server console.
      if (player == null)
      {
        var status = "OK";
        var enabled = true;

        commandInfo.ReplyToCommand(string.Format(@"{{""status"":""{0}"", ""enabled"":""{1}""}}", status, enabled));

        return;
      }
    }

    private void Command_CS2_Remote_URL(CCSPlayerController? player, CommandInfo commandInfo)
    {
      // The player is null, then the command has been called by the server console.
      if (player == null)
      {
        var url = commandInfo.GetArg(1);
        var path = commandInfo.GetArg(2);
        var status = "OK";

        this.url = url;
        this.path = path;

        try
        {
          client = new HttpClient
          {
            BaseAddress = new Uri(url)
          };
        }
        catch (System.Exception ex)
        {
          this.PrintToPlayerOrServer(ex.Message);
        }

        commandInfo.ReplyToCommand(string.Format(@"{{""status"":""{0}"", ""url"":""{1}"", ""path"":""{2}""}}", status, url, path));

        return;
      }
    }

    private HookResult OnEventPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
      this.PrintToPlayerOrServer($"[CS2 Remote] Player connected! {@event.Name}");

      if (!string.IsNullOrEmpty(@event.Xuid.ToString()) && @event.Xuid.ToString() != "0")
      {
        var player = new Player
        {
          ID = @event.Networkid,
          PlayerName = @event.Name,
          Xuid = @event.Xuid.ToString(),
          Bot = @event.Bot,
          Team = @event.Userid?.Team == CsTeam.Terrorist ? CsTeam.Terrorist.ToString() : CsTeam.CounterTerrorist.ToString(),
          Kills = @event.Userid?.ActionTrackingServices?.MatchStats.Kills ?? 0,
          Assists = @event.Userid?.ActionTrackingServices?.MatchStats.Assists ?? 0,
          Deaths = @event.Userid?.ActionTrackingServices?.MatchStats.Deaths ?? 0,
        };

        data.JoinedPlayer = player;
        data.DisconnectedPlayer = null;

        if (data.Players != null && !data.Players.Contains(player))
        {
          data.Players.Add(player);
        }

        this.EventRoundAndMatchHandler("player_joined");
      }

      return HookResult.Continue;
    }

    private HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
      this.PrintToPlayerOrServer($"[CS2 Remote] Player disconnected! {@event.Name}");

      if (!string.IsNullOrEmpty(@event.Xuid.ToString()) && @event.Xuid.ToString() != "0")
      {
        var player = new Player
        {
          ID = @event.Networkid,
          PlayerName = @event.Name,
          Xuid = @event.Xuid.ToString(),
          Bot = false,
          Team = @event.Userid?.Team == CsTeam.Terrorist ? CsTeam.Terrorist.ToString() : CsTeam.CounterTerrorist.ToString(),
          Kills = @event.Userid?.ActionTrackingServices?.MatchStats.Kills ?? 0,
          Assists = @event.Userid?.ActionTrackingServices?.MatchStats.Assists ?? 0,
          Deaths = @event.Userid?.ActionTrackingServices?.MatchStats.Deaths ?? 0,
        };

        if (data.Players != null)
        {
          for (int i = 0; i < data.Players.Count; i++)
          {
            if (data.Players[i].Xuid == @event.Xuid.ToString())
            {
              data.Players.RemoveAt(i);
            }
          }
        }

        data.JoinedPlayer = null;
        data.DisconnectedPlayer = player;

        this.EventRoundAndMatchHandler("player_disconnect");
      }

      return HookResult.Continue;
    }

    private HookResult OnEventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
      this.PrintToPlayerOrServer($"{@event?.Userid?.PlayerName} connected has UserID {@event?.Userid != null}");

      if (@event?.Userid != null)
      {
        playerControllerDictionary[$"{@event.Userid.SteamID}"] = @event.Userid;
      }

      return HookResult.Continue;
    }

    private HookResult OnEventRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
      if (skipFirstRound)
      {
        this.PrintToPlayerOrServer($"[CS2 Remote] Round End! winner {@event.Winner}");

        if (data.MatchData != null)
        {
          if (@event.Winner == 3)
          {
            data.MatchData.CTRoundWins += 1;
          }

          if (@event.Winner == 2)
          {
            data.MatchData.TRoundWins += 1;
          }
        }

        this.EventRoundAndMatchHandler("round_end");
      }

      skipFirstRound = true;

      return HookResult.Continue;
    }

    private HookResult OnEventAnnouncePhaseEnd(EventAnnouncePhaseEnd @event, GameEventInfo info)
    {
      this.PrintToPlayerOrServer($"[CS2 Remote] Half Time!");

      halfTime = true;

      return HookResult.Continue;
    }

    private HookResult OnEventCsIntermission(EventCsIntermission @event, GameEventInfo info)
    {
      this.PrintToPlayerOrServer($"[CS2 Remote] Match End!");

      this.EventRoundAndMatchHandler("match_end");

      return HookResult.Continue;
    }

    private HookResult OnEventPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
      this.PrintToPlayerOrServer($"[CS2 Remote] Player death!");

      var victim = @event.Userid;
      var attacker = @event.Attacker;
      var assister = @event.Assister;

      this.PrintToPlayerOrServer($"playersDicCount: {playerControllerDictionary.Keys.Count}");

      foreach (var playerController in playerControllerDictionary.Values)
      {
        if (playerController?.PlayerName == victim?.PlayerName)
        {
          if (playerController != null && playerController.ActionTrackingServices != null)
          {
            this.PrintToPlayerOrServer($" victim deaths {victim?.ActionTrackingServices?.MatchStats.Deaths}");

            playerController.ActionTrackingServices.MatchStats.Deaths = victim?.ActionTrackingServices?.MatchStats.Deaths ?? 0;

            this.PrintToPlayerOrServer($"{playerController.PlayerName} deaths: {playerController.ActionTrackingServices.MatchStats.Deaths}");
          }
        }

        if (playerController?.PlayerName == attacker?.PlayerName)
        {
          if (playerController != null && playerController.ActionTrackingServices != null)
          {
            this.PrintToPlayerOrServer($" attacker Kills {attacker?.ActionTrackingServices?.MatchStats.Kills}");

            playerController.ActionTrackingServices.MatchStats.Kills = attacker?.ActionTrackingServices?.MatchStats.Kills ?? 0;

            this.PrintToPlayerOrServer($"{playerController.PlayerName} Kills: {playerController.ActionTrackingServices.MatchStats.Kills}");
          }
        }

        if (playerController?.PlayerName == assister?.PlayerName)
        {
          if (playerController != null && playerController.ActionTrackingServices != null)
          {
            this.PrintToPlayerOrServer($" assister Assists {assister?.ActionTrackingServices?.MatchStats.Assists}");

            playerController.ActionTrackingServices.MatchStats.Assists = assister?.ActionTrackingServices?.MatchStats.Assists ?? 0;

            this.PrintToPlayerOrServer($"{playerController.PlayerName} Assists: {playerController.ActionTrackingServices.MatchStats.Assists}");
          }
        }
      }

      return HookResult.Continue;
    }

    private HookResult OnEventPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
      this.PrintToPlayerOrServer($"[CS2 Remote] Player team! {@event.Team}");

      if (@event.Userid != null && data.Players != null)
      {
        foreach (var player in data.Players)
        {
          if (player.Xuid == @event.Userid.SteamID.ToString())
          {
            player.Team = @event.Team == CS_TEAM_T ? "Terrorist" : "CounterTerrorist";
          }
        }
      }

      return HookResult.Continue;
    }

    private void EventRoundAndMatchHandler(string status)
    {
      if (data.Players != null)
      {
        foreach (var player in data.Players)
        {
          if (playerControllerDictionary.ContainsKey($"{player.Xuid}"))
          {
            var playerController = playerControllerDictionary[$"{player.Xuid}"];

            player.Kills = playerController?.ActionTrackingServices?.MatchStats.Kills;
            player.Assists = playerController?.ActionTrackingServices?.MatchStats.Assists;
            player.Deaths = playerController?.ActionTrackingServices?.MatchStats.Deaths;
          }
        }
      }

      if (data.MatchData != null)
      {
        data.MatchData.CT = GetCSTeamScore(CsTeam.CounterTerrorist);
        data.MatchData.T = GetCSTeamScore(CsTeam.Terrorist);
      }

      var ghCvarTeamName1 = ConVar.Find("mp_teamname_1");
      var ghCvarTeamName2 = ConVar.Find("mp_teamname_2");

      this.PrintToPlayerOrServer(ghCvarTeamName1?.StringValue ?? "");
      this.PrintToPlayerOrServer(ghCvarTeamName2?.StringValue ?? "");

      data.Status = status;
      data.Locked = false;
      data.HalfTime = halfTime;
      data.CtName = ghCvarTeamName1?.StringValue;
      data.TName = ghCvarTeamName2?.StringValue;
      data.Instruction = "round_update";

      this.SendPostRequest();
    }

    private async void SendPostRequest()
    {
      this.PrintToPlayerOrServer($"[CS2 Remote] REST Path: {path}");

      try
      {
        var content = new StringContent(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json");
        var result = await client.PostAsync(path, content);
        var resultContent = await result.Content.ReadAsStringAsync();

        this.PrintToPlayerOrServer(resultContent);
      }
      catch (System.Exception ex)
      {
        this.PrintToPlayerOrServer(ex.Message);
      }
    }

    private int GetCSTeamScore(CsTeam team)
    {
      var teamManagers = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");

      foreach (var teamManager in teamManagers)
      {
        if ((int)team == teamManager.TeamNum)
        {
          return teamManager.Score;
        }
      }

      return 0;
    }

    private string PluginInfo()
    {
      return $"Plugin: {this.ModuleName} - Version: {this.ModuleVersion} by {this.ModuleAuthor}";
    }

    private void PrintToPlayerOrServer(string message, CCSPlayerController? player = null)
    {
      message = $"[{ChatColors.Red}{this.ModuleName}{ChatColors.White}] " + message;

      if (player != null)
      {
        player.PrintToConsole(message);
        player.PrintToChat(message);
      }
      else
      {
        this.Log(message);
      }
    }

    private void Log(string message)
    {
      Console.ForegroundColor = ConsoleColor.Green;
      Console.WriteLine($"[{this.ModuleName}] {message}");
      Console.ResetColor();
    }
  }
}