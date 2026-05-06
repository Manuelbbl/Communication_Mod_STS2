using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens; 
using System; 
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http; 
using System.Text;     
using System.Threading.Tasks; 

namespace Communication_Mod.Communication_ModCode;

[HarmonyPatch(typeof(NRewardsScreen), "SetRewards")]
public static class LootScanner 
{
    // --- NEW: NETWORK SETUP FOR LOOT ---
    private static readonly HttpClient _httpClient = new HttpClient();
    private static readonly string _aiServerUrl = "http://127.0.0.1:5000/update_state";

    private static async Task SendDataToAI(string jsonPayload)
    {
        try
        {
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync(_aiServerUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"[WARNING - AI SERVER ERROR (LOOT)]: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"[ERROR - AI NETWORK (LOOT)]: Could not connect. Is the AI running? ({ex.Message})");
        }
    }

    private static int GetCardValue(CardModel card, System.Func<CardModel, int> selector)
    {
        try { return card.DynamicVars == null ? 0 : selector(card); }
        catch { return 0; }
    }

    public static void Postfix(IEnumerable<Reward> __0) 
    {
        if (__0 == null) return;

        var lootData = new 
        {
            Trigger = "Loot", // NEW: So Python knows what this is about!
            Rewards = __0.Select((reward, index) => ExtractReward(reward, index)).ToList()
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        string jsonOutput = JsonSerializer.Serialize(lootData, options);

        // Transmit data to Python in the background
        Task.Run(() => SendDataToAI(jsonOutput));
    }

    private static object ExtractReward(Reward reward, int index)
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
        string typeName = reward.GetType().Name;
        
        string instanceId = reward.GetHashCode().ToString(); 

        switch (reward)
        {
            case GoldReward gold:
                object goldVal = gold.GetType().GetProperty("Amount", flags)?.GetValue(gold) ??
                                 gold.GetType().GetField("Amount", flags)?.GetValue(gold) ??
                                 gold.GetType().GetField("_amount", flags)?.GetValue(gold) ??
                                 gold.GetType().GetField("amount", flags)?.GetValue(gold);
                                 
                int goldAmount = goldVal != null ? (int)goldVal : 0;
                return new { InstanceId = instanceId, Type = "Gold", Amount = goldAmount };

            case PotionReward potion:
                object pModel = potion.GetType().GetProperty("Potion", flags)?.GetValue(potion) ??
                                potion.GetType().GetField("Potion", flags)?.GetValue(potion) ??
                                potion.GetType().GetField("_potion", flags)?.GetValue(potion);
                                
                string pId = ExtractId(pModel) ?? "UnknownPotion";
                return new { InstanceId = instanceId, Type = "Potion", PotionId = pId };

            case RelicReward relic:
                object rModel = relic.GetType().GetProperty("Relic", flags)?.GetValue(relic) ??
                                relic.GetType().GetField("Relic", flags)?.GetValue(relic) ??
                                relic.GetType().GetField("_relic", flags)?.GetValue(relic);
                                
                string rId = ExtractId(rModel) ?? "UnknownRelic";
                return new { InstanceId = instanceId, Type = "Relic", RelicId = rId };

            case CardReward card:
                return new 
                {
                    InstanceId = instanceId, 
                    Type = "Card",
                    Options = card.Cards.Select(k => new 
                    {
                        CardId = k.Id.Entry,
                        Cost = k.EnergyCost.CostsX ? "X" : k.EnergyCost.GetResolved().ToString(),
                        Damage = GetCardValue(k, x => x.DynamicVars.Damage.IntValue),
                        Block = GetCardValue(k, x => x.DynamicVars.Block.IntValue)
                    }).ToList()
                };

            default:
                return new { InstanceId = instanceId, Type = typeName };
        }
    }

    private static string ExtractId(object model)
    {
        if (model == null) return null;
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;

        object idVal = model.GetType().GetProperty("Id", flags)?.GetValue(model) ??
                       model.GetType().GetField("Id", flags)?.GetValue(model) ??
                       model.GetType().GetField("_id", flags)?.GetValue(model);

        if (idVal != null)
        {
            var entryProp = idVal.GetType().GetProperty("Entry", flags) as System.Reflection.MemberInfo ?? 
                            idVal.GetType().GetField("Entry", flags);
                            
            if (entryProp != null)
            {
                return entryProp is System.Reflection.PropertyInfo pi
                    ? pi.GetValue(idVal)?.ToString()
                    : ((System.Reflection.FieldInfo)entryProp).GetValue(idVal)?.ToString();
            }
            
            return idVal.ToString();
        }
        
        return null;
    }
}