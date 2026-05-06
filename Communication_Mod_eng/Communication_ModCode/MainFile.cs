using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models; 
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Context; 
using MegaCrit.Sts2.Core.Runs; 
using Godot; 
using System; 
using System.Text; 
using System.Text.Json; 
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks; 

namespace Communication_Mod.Communication_ModCode;

public static class AIActionExecutor
{
    public static void ExecuteAICommand(string json, CombatManager combatManager)
    {
        try 
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string actionType = root.GetProperty("Type").GetString();

            var currentState = AIDataScanner.CurrentCombatState;
            if (currentState == null || currentState.Players.Count == 0) return;

            var player = currentState.Players[0];

            if (actionType == "PlayCard")
            {
                int handIndex = root.GetProperty("HandIndex").GetInt32();
                int targetIndex = root.GetProperty("TargetIndex").GetInt32();

                var handCards = player.PlayerCombatState.Hand.Cards;
                var enemies = currentState.Enemies;

                if (handIndex >= 0 && handIndex < handCards.Count)
                {
                    CardModel cardToPlay = handCards[handIndex]; 
                    
                    Creature targetCreature = null; 
                    if (targetIndex >= 0 && targetIndex < enemies.Count)
                    {
                        targetCreature = enemies[targetIndex].Monster?.Creature;
                    }

                    Log.Info($"[AI ACTION] AI wants to play card: {cardToPlay.Id.Entry} on target {targetIndex}");
                    
                    bool requiresTarget;
                    switch (cardToPlay.TargetType)
                    {
                        case TargetType.AnyEnemy:
                        case TargetType.AnyAlly:
                            requiresTarget = true;
                            break;
                        default:
                            requiresTarget = false;
                            break;
                    }

                    bool playSuccess = !requiresTarget ? cardToPlay.TryManualPlay(null) : cardToPlay.TryManualPlay(targetCreature);

                    if (playSuccess)
                    {
                         Log.Info($"[AI ACTION] Card played successfully!");
                    }
                    else
                    {
                         Log.Warn($"[AI ACTION] Card rejected (e.g., not enough energy or invalid target).");
                    }
                }
            }
            else if (actionType == "EndTurn")
            {
                Log.Info("[AI ACTION] AI ends the turn.");
                combatManager.SetReadyToEndTurn(player, false, null);
            }
            else 
            {
                Log.Warn($"[AI ACTION] Unknown command: {actionType}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error executing AI command: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(CombatStateTracker), "NotifyCombatStateChanged")]
public static class AIDataScanner 
{
    public static CombatState CurrentCombatState { get; private set; }
    
    public static string PendingJsonPayload = null;
    public static bool IsSenderRunning = false;

    private static CombatState _lastCombat = null;
    private static string _lastJsonString = ""; 
    private static bool _combatStarted = false; 
    private static string _lastHandSignature = "";
    private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();
    private static readonly string _aiServerUrl = "http://127.0.0.1:5000/update_state"; 
    
    private static bool _isPlayerActionPhase = false; 
    private static int _combatHistoryChangeCounter = 0;
    
    public static void Postfix(string caller, CombatState ____state) 
    {
        CurrentCombatState = ____state;

        if (caller != "OnTurnStarted" && 
            caller != "OnTurnEnded" && 
            caller != "OnCombatHistoryChanged" &&
            caller != "OnCardPileContentsChanged" &&
            caller != "AfterCombatRoomLoaded")
        {
            return; 
        }

        CombatState _ = ____state;
        if (_ == null || _.Players.Count == 0) return;

        var player = _.Players[0];

        if (_lastCombat != _)
        {
            _lastCombat = _; 
            _lastJsonString = ""; 
            _combatStarted = false; 
            _lastHandSignature = ""; 
            _isPlayerActionPhase = false; 
            _combatHistoryChangeCounter = 0; 
        }

        if (!_combatStarted && caller != "OnTurnStarted" && caller != "AfterCombatRoomLoaded") return;

        bool isVeryFirstTurn = (!_combatStarted && caller == "OnTurnStarted");
        if (isVeryFirstTurn) _combatStarted = true; 
        
        if (caller == "OnTurnStarted")
        {
            _isPlayerActionPhase = CombatManager.Instance != null && CombatManager.Instance.IsPartOfPlayerTurn(player);
            _combatHistoryChangeCounter = 0; 
        }
        else if (caller == "OnTurnEnded")
        {
            _isPlayerActionPhase = false;
        }
        
        if (caller == "OnCombatHistoryChanged" || caller == "OnCardPileContentsChanged")
        {
            if (!_isPlayerActionPhase) return;
            _combatHistoryChangeCounter++;
        }
        
        if (!_isPlayerActionPhase && caller != "AfterCombatRoomLoaded")
        {
            return; 
        }
        
        var playerCreature = player.Creature;
        var combatState = player.PlayerCombatState;

        IReadOnlyList<CardModel> handCards = combatState?.Hand?.Cards ?? new List<CardModel>();
        var drawPile = combatState?.DrawPile?.Cards ?? new List<CardModel>();
        var discardPile = combatState?.DiscardPile?.Cards ?? new List<CardModel>();
        var exhaustPile = combatState?.ExhaustPile?.Cards ?? new List<CardModel>();

        string currentHandSignature = string.Join(",", handCards.Select(k => k.Id.Entry));
        bool handHasChanged = currentHandSignature != _lastHandSignature;
        
        bool isFirstInfo = (caller == "AfterCombatRoomLoaded");
        bool mustSendCatalog = (caller == "OnTurnStarted");

        var aiDataPackage = new Dictionary<string, object>
        {
            ["Trigger"] = caller, 
            ["TurnNumber"] = _.RoundNumber,
            ["ActionCount"] = _combatHistoryChangeCounter, 

            ["Player"] = new 
            {
                playerCreature.CurrentHp,
                MaxHp = playerCreature.MaxHp,
                Energy = combatState?.Energy ?? 0,
                MaxEnergy = combatState?.MaxEnergy ?? 0,
                Stars = combatState?.Stars ?? 0,
                Gold = player.Gold,
                MaxPotions = player.MaxPotionCount,
                Potions = player.PotionSlots?.Where(p => p != null).Select(p => p.Id.Entry).ToList() ?? new List<string>(),
                OrbSlots = combatState?.OrbQueue?.Capacity ?? 0,
                Orbs = combatState?.OrbQueue?.Orbs?.Select(o => o.Id.Entry).ToList() ?? new List<string>(),
                Relics = player.Relics.Select(r => new 
                {
                    RelicId = r.Id.Entry,
                    Counter = r.GetType().GetProperty("Counter")?.GetValue(r) ?? r.GetType().GetField("Counter")?.GetValue(r) ?? 0
                }).ToList(),
                Powers = playerCreature.Powers.Select(p => new 
                {
                    PowerId = p.Id.Entry,
                    Type = p.Type.ToString(), 
                    Amount = p.Amount 
                }).ToList()
            },

            ["Piles"] = new {
                DrawCount = drawPile.Count,
                DrawPile = drawPile.Select(k => new
                {
                    Card_Name = k.Id.Entry,
                    Card_ID = k.GetHashCode()
                }).ToList(),
                DiscardCount = discardPile.Count,
                DiscardPile = discardPile.Select(k => new
                {
                    Card_Name = k.Id.Entry,
                    Card_ID = k.GetHashCode()
                }).ToList(),
                HandCount = handCards.Count,
                HandPile = handCards.Select(k => new
                {
                    Card_Name = k.Id.Entry,
                    Card_ID = k.GetHashCode()
                }).ToList(),
                ExhaustCount = exhaustPile.Count,
                ExhaustPile = exhaustPile.Select(k => new
                {
                    Card_Name = k.Id.Entry,
                    Card_ID = k.GetHashCode()
                }).ToList(),
            },

            ["Enemies"] = _.Enemies
                .Where(e => e.Monster != null)
                .Select((e, i) => new 
                {
                    Index = i, 
                    InstanceId = e.GetHashCode().ToString(), 
                    MonsterId = e.Monster!.Id.Entry,
                    CurrentHp = e.CurrentHp,
                    MaxHp = e.MaxHp,
                    Intents = e.Monster.NextMove?.Intents == null ? new List<object>() : 
                        ((IEnumerable<object>)e.Monster.NextMove.Intents).Select(ExtractIntentData).ToList(),
                    Powers = e.Powers.Select(p => new 
                    {
                        PowerId = p.Id.Entry,
                        Type = p.Type.ToString(), 
                        Amount = p.Amount 
                    }).ToList()
                }).ToList()
        };

        if (isFirstInfo)
        {
            aiDataPackage["FirstInfo"] = new 
            {
                RunStatus = new
                {
                    Character = player.Character?.Id.Entry ?? "Unknown",
                    Act = (player.RunState?.CurrentActIndex ?? 0) + 1,
                    Floor = player.RunState?.ActFloor ?? 0,
                    Gold = player.Gold
                },
                RoomInfo = new
                {
                    Type = _.RunState.CurrentMapPoint?.PointType.ToString() ?? "Unknown",
                    X = _.RunState.CurrentMapPoint?.coord.col ?? 0,
                    Y = _.RunState.CurrentMapPoint?.coord.row ?? 0,
                }
            };
        }
        
        if (mustSendCatalog)
        {
            var allCardsInCombat = new List<CardModel>();
            allCardsInCombat.AddRange(handCards);
            allCardsInCombat.AddRange(drawPile);
            allCardsInCombat.AddRange(discardPile);
            allCardsInCombat.AddRange(exhaustPile);

            aiDataPackage["CardCatalog"] = allCardsInCombat
                .GroupBy(k => new { k.Id.Entry, k.IsUpgraded }) 
                .Select(g => {
                    var k = g.First();
                    return new 
                    {
                        CardId = k.Id.Entry,
                        IsUpgraded = k.IsUpgraded,
                        TotalCount = g.Count(), 
                        Cost = k.EnergyCost.CostsX ? "X" : k.EnergyCost.GetResolved().ToString(),
                        TargetType = k.TargetType.ToString(),
                        Damage = GetCardValue(k, x => x.DynamicVars.Damage.IntValue),
                        Block = GetCardValue(k, x => x.DynamicVars.Block.IntValue),
                        DrawsCards = GetCardValue(k, x => x.DynamicVars.Cards.IntValue),
                        GivesEnergy = GetCardValue(k, x => x.DynamicVars.Energy.IntValue),
                        AppliesVulnerable = GetCardValue(k, x => x.DynamicVars.Vulnerable.IntValue),
                        AppliesWeak = GetCardValue(k, x => x.DynamicVars.Weak.IntValue)
                    };
                }).ToList();
                
            _lastHandSignature = currentHandSignature; 
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        string jsonOutput = JsonSerializer.Serialize(aiDataPackage, options);

        if (jsonOutput == _lastJsonString) return; 
        _lastJsonString = jsonOutput;
        
        PendingJsonPayload = jsonOutput;
        
        if (!IsSenderRunning)
        {
            Task.Run(SenderLoop);
        }
    }
    
    private static async Task SenderLoop()
    {
        IsSenderRunning = true;
        try
        {
            while (PendingJsonPayload != null)
            {
                while (true)
                {
                    bool isBusy = false;
                    if (RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction != null)
                    {
                        isBusy = true;
                    }
                    
                    if (!isBusy) break; 
                    await Task.Delay(100);
                }
                
                await Task.Delay(500);
                
                if (RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction != null)
                {
                    continue; 
                }

                
                string payloadToSend = PendingJsonPayload;
                PendingJsonPayload = null;

                if (payloadToSend != null)
                {
                    await SendDataToAI(payloadToSend);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[AI SENDER ERROR]: {ex.Message}");
        }
        finally
        {
            IsSenderRunning = false;
        }
    }

    private static async Task SendDataToAI(string jsonPayload)
    {
        try
        {
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync(_aiServerUrl, content);

            if (response.IsSuccessStatusCode)
            {
                string aiResponse = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(aiResponse) && aiResponse != "OK")
                {
                    
                    Godot.Callable.From(() => {
                        AIActionExecutor.ExecuteAICommand(aiResponse, CombatManager.Instance);
                    }).CallDeferred();
                }
            }
            else
            {
                Log.Warn($"[AI SERVER ERROR]: {response.StatusCode}"); 
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[AI NETWORK ERROR]: Could not connect to local server. Is the AI running? ({ex.Message})");
        }
    }

    private static int GetCardValue(CardModel card, Func<CardModel, int> selector)
    {
        try { return card.DynamicVars == null ? 0 : selector(card); }
        catch { return 0; }
    }

    private static object ExtractIntentData(object intentObj)
    {
        int hits = 1;
        int damage = 0;
        string type = "Unknown";
        try
        {
            var intentType = intentObj.GetType();
            var typeProp = intentType.GetProperty("IntentType");
            if (typeProp != null) type = typeProp.GetValue(intentObj)?.ToString() ?? "Unknown";
            var repeatsProp = intentType.GetProperty("Repeats");
            if (repeatsProp != null) hits = (int)repeatsProp.GetValue(intentObj);
            var dmgCalcProp = intentType.GetProperty("DamageCalc");
            if (dmgCalcProp != null)
            {
                var calcFunc = dmgCalcProp.GetValue(intentObj) as Func<decimal>;
                if (calcFunc != null) damage = (int)calcFunc(); 
            }
        }
        catch { }
        return new { Type = type, Damage = damage, Hits = hits };
    }
}