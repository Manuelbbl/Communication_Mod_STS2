using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using System;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Net.Http; // NEW
using System.Text;     // NEW
using System.Threading.Tasks; // NEW

namespace Communication_Mod.Communication_ModCode;

public static class NonCombatScanner
{
    // --- NEW: NETWORK SETUP FOR NON-COMBAT ---
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
                // Use Info in case Warn doesn't exist in the Sts2 Log class
                Log.Info($"[WARNING - AI SERVER ERROR]: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"[ERROR - AI NETWORK]: Could not connect. Is the AI running? ({ex.Message})");
        }
    }


    // --- HELPER METHOD: DETECT PROCEED BUTTON ---
    private static Dictionary<string, object> ReadProceedButton(object roomInstance)
    {
        var result = new Dictionary<string, object>
        {
            { "Exists", false },
            { "Visible", false },
            { "Disabled", false }
        };

        if (roomInstance == null) return result;

        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.FlattenHierarchy;

            var btnInfo = roomInstance.GetType().GetProperty("ProceedButton", flags) as MemberInfo ??
                          roomInstance.GetType().GetField("ProceedButton", flags);

            if (btnInfo == null) return result;

            object btnObj = (btnInfo is PropertyInfo pi)
                ? pi.GetValue(roomInstance)
                : ((FieldInfo)btnInfo).GetValue(roomInstance);
            if (btnObj == null) return result;

            var isHidden = btnObj.GetType().GetProperty("IsHidden", flags)?.GetValue(btnObj) as bool? ?? false;
            var isDisabled = btnObj.GetType().GetProperty("IsDisabled", flags)?.GetValue(btnObj) as bool? ?? false;
            var visible = btnObj.GetType().GetProperty("Visible", flags)?.GetValue(btnObj) as bool? ?? true;

            result["Exists"] = true;
            result["Visible"] = visible && !isHidden;
            result["Disabled"] = isDisabled;
        }
        catch
        {
            result["Error"] = true;
        }

        return result;
    }

    // --- 1. CAMPFIRE SCANNER ---
    [HarmonyPatch(typeof(NRestSiteRoom), "_Ready")]
    public static class CampfireScanner
    {
        public static void Postfix(NRestSiteRoom __instance)
        {
            if (__instance == null || __instance.Options == null) return;

            var options = __instance.Options.Select((opt, index) => new
            {
                CampfireIndex = index,
                InstanceId = opt.GetHashCode().ToString(),
                Type = opt.GetType().Name
            }).ToList();

            var campfireData = new
            {
                Trigger = "Campfire", // NEW added for AI detection
                RoomType = "Campfire",
                Status = ReadPlayerAndRoomStatus(__instance),
                ProceedButton = ReadProceedButton(__instance),
                AvailableOptions = options
            };

            string jsonOutput = JsonSerializer.Serialize(campfireData, new JsonSerializerOptions { WriteIndented = true });
            Task.Run(() => SendDataToAI(jsonOutput));
        }
    }


    // --- 2. SHOP SCANNER ---
    public static class ShopManager
    {
        public static NMerchantRoom CurrentShopRoom = null;

        [HarmonyPatch(typeof(NMerchantRoom), "AfterRoomIsLoaded")]
        public static class ShopScannerInitial
        {
            public static void Postfix(NMerchantRoom __instance)
            {
                CurrentShopRoom = __instance;
                ExecuteShopScan(__instance, "SHOP_INITIAL");
            }
        }

        [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry), "InvokePurchaseCompleted")]
        public static class ShopScannerUpdate
        {
            public static void Postfix()
            {
                if (CurrentShopRoom != null)
                {
                    ExecuteShopScan(CurrentShopRoom, "SHOP_UPDATE");
                }
            }
        }

        public static void ExecuteShopScan(NMerchantRoom __instance, string trigger)
        {
            if (__instance == null) return;

            var inventoryDetails = new Dictionary<string, object>();

            if (__instance.Room != null)
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var coreInventory =
                    __instance.Room.GetType().GetProperty("Inventory", flags)?.GetValue(__instance.Room) ??
                    __instance.Room.GetType().GetField("Inventory", flags)?.GetValue(__instance.Room) ??
                    __instance.Room.GetType().GetField("_inventory", flags)?.GetValue(__instance.Room);

                if (coreInventory != null)
                {
                    ScanShopLists(coreInventory, inventoryDetails);

                    var cardRemoval = coreInventory.GetType().GetProperty("CardRemovalEntry", flags)
                        ?.GetValue(coreInventory);
                    if (cardRemoval != null)
                    {
                        inventoryDetails["CardRemovalEntry"] = ExtractShopItem(cardRemoval);
                    }
                }
            }

            var shopData = new
            {
                Trigger = trigger,
                RoomType = "Shop",
                Status = ReadPlayerAndRoomStatus(__instance),
                ProceedButton = ReadProceedButton(__instance),
                Offerings = inventoryDetails
            };

            string jsonOutput = JsonSerializer.Serialize(shopData, new JsonSerializerOptions { WriteIndented = true });
            Task.Run(() => SendDataToAI(jsonOutput));
        }

        private static void ScanShopLists(object obj, Dictionary<string, object> target)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var prop in obj.GetType().GetProperties(flags))
            {
                try
                {
                    if (prop.Name.Contains("Entries") && prop.GetIndexParameters().Length == 0)
                    {
                        ProcessShopList(prop.Name, prop.GetValue(obj), target);
                    }
                }
                catch { }
            }
        }

        private static void ProcessShopList(string name, object val, Dictionary<string, object> target)
        {
            if (val is System.Collections.IEnumerable list && !(val is string))
            {
                var itemList = new List<object>();
                int index = 0;
                foreach (var item in list)
                {
                    if (item == null) continue;
                    var details = ExtractShopItem(item) as Dictionary<string, object>;
                    if (details != null)
                    {
                        details["ShopIndex"] = index;
                        itemList.Add(details);
                    }
                    index++;
                }
                if (itemList.Count > 0) target[name] = itemList;
            }
        }

        private static object ExtractShopItem(object item)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var details = new Dictionary<string, object>();
            details["RawType"] = item.GetType().Name;

            if (item.GetType().Name.Contains("Removal"))
            {
                details["Id"] = "Service_CardRemoval";
            }
            else
            {
                object model = null;
                var creationResultProp = item.GetType().GetProperty("CreationResult", flags);
                if (creationResultProp != null) model = creationResultProp.GetValue(item);

                if (model == null)
                {
                    string[] targetNames = { "Card", "_card", "card", "Relic", "_relic", "relic", "Potion", "_potion", "potion", "Model", "_model" };
                    foreach (var name in targetNames)
                    {
                        var prop = item.GetType().GetProperty(name, flags);
                        if (prop != null) { model = prop.GetValue(item); if (model != null) break; }
                    }
                    if (model == null)
                    {
                        foreach (var name in targetNames)
                        {
                            var field = item.GetType().GetField(name, flags);
                            if (field != null) { model = field.GetValue(item); if (model != null) break; }
                        }
                    }
                }

                if (model != null && model.GetType().Name.Contains("CreationResult"))
                {
                    object realCard = model.GetType().GetProperty("Card", flags)?.GetValue(model) ??
                                        model.GetType().GetProperty("Model", flags)?.GetValue(model) ??
                                        model.GetType().GetField("_card", flags)?.GetValue(model) ??
                                        model.GetType().GetField("card", flags)?.GetValue(model);
                    if (realCard != null) model = realCard;
                }

                if (model != null)
                {
                    details["ModelType"] = model.GetType().Name;
                    object idVal = null;
                    var idProp = model.GetType().GetProperty("Id", flags) ?? model.GetType().GetProperty("ID", flags);
                    if (idProp != null) idVal = idProp.GetValue(model);
                    else
                    {
                        var idField = model.GetType().GetField("Id", flags) ?? model.GetType().GetField("_id", flags) ?? model.GetType().GetField("ID", flags);
                        if (idField != null) idVal = idField.GetValue(model);
                    }

                    if (idVal != null)
                    {
                        var entryProp = idVal.GetType().GetProperty("Entry", flags);
                        if (entryProp != null) details["Id"] = entryProp.GetValue(idVal)?.ToString();
                        else
                        {
                            var entryField = idVal.GetType().GetField("Entry", flags);
                            if (entryField != null) details["Id"] = entryField.GetValue(idVal)?.ToString();
                            else details["Id"] = idVal.ToString();
                        }
                    }
                    else
                    {
                        var nameProp = model.GetType().GetProperty("Name", flags);
                        details["Id"] = nameProp?.GetValue(model)?.ToString() ?? model.GetType().Name;
                    }
                }
            }

            var costProp = item.GetType().GetProperty("Cost", flags) ?? item.GetType().GetProperty("Price", flags);
            if (costProp != null) details["Price"] = costProp.GetValue(item);
            else
            {
                var costField = item.GetType().GetField("cost", flags) ?? item.GetType().GetField("price", flags) ?? item.GetType().GetField("_cost", flags) ?? item.GetType().GetField("_price", flags);
                if (costField != null) details["Price"] = costField.GetValue(item);
            }

            var soldProp = item.GetType().GetProperty("IsPurchased", flags) ?? item.GetType().GetProperty("IsSoldOut", flags);
            if (soldProp != null) details["IsSold"] = soldProp.GetValue(item);
            else
            {
                var soldField = item.GetType().GetField("isPurchased", flags) ?? item.GetType().GetField("isSoldOut", flags) ?? item.GetType().GetField("_isPurchased", flags) ?? item.GetType().GetField("_isSoldOut", flags);
                if (soldField != null) details["IsSold"] = soldField.GetValue(item);
            }

            var saleProp = item.GetType().GetProperty("IsOnSale", flags);
            if (saleProp != null) details["OnSale"] = saleProp.GetValue(item);

            return details;
        }
    }

    // --- 3. EVENT SCANNER ---
    [HarmonyPatch(typeof(NEventRoom), "SetOptions")]
    public static class EventScanner
    {
        public static void Postfix(NEventRoom __instance, EventModel eventModel, object ____connectedOptions)
        {
            if (__instance == null || eventModel == null) return;

            var optionsList = ____connectedOptions as IEnumerable<object>;

            var eventData = new
            {
                Trigger = "Event",
                RoomType = "Event",
                EventId = eventModel.Id.Entry,
                Status = ReadPlayerAndRoomStatus(__instance),
                Options = optionsList?.Select((opt, index) => ReadOptionPerfectly(opt, index)).ToList() ??
                           new List<object>()
            };

            string jsonOutput = JsonSerializer.Serialize(eventData, new JsonSerializerOptions { WriteIndented = true });
            Task.Run(() => SendDataToAI(jsonOutput));
        }

        private static object ReadOptionPerfectly(object option, int index)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type = option.GetType();

                var textKey = type.GetProperty("TextKey", flags)?.GetValue(option)?.ToString() ?? "No Key";
                var details = new Dictionary<string, object>();

                ScanObject(option, details);
                ScanObject(type.GetProperty("Title", flags)?.GetValue(option), details);
                ScanObject(type.GetProperty("Description", flags)?.GetValue(option), details);

                return new
                {
                    InstanceId = option.GetHashCode().ToString(),
                    Key = textKey,
                };
            }
            catch
            {
                return new { Error = "Error reading", EventIndex = index };
            }
        }

        private static void ScanObject(object obj, Dictionary<string, object> target)
        {
            if (obj == null) return;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var field in obj.GetType().GetFields(flags))
            {
                try { EvaluateValue(field.Name, field.GetValue(obj), target); } catch { }
            }

            foreach (var prop in obj.GetType().GetProperties(flags))
            {
                try { if (prop.GetIndexParameters().Length > 0) continue; EvaluateValue(prop.Name, prop.GetValue(obj), target); } catch { }
            }
        }

        private static void EvaluateValue(string name, object val, Dictionary<string, object> target)
        {
            if (val == null) return;
            string sName = name.Replace("<", "").Replace(">k__BackingField", "").Replace("P", "").TrimStart('_');
            if (sName == "EventModel" || sName == "Room" || target.ContainsKey(sName)) return;

            Type valType = val.GetType();

            if (valType.IsPrimitive || val is string || val is decimal || valType.IsEnum)
            {
                target[sName] = val;
            }
            else if (valType.Name == "LocString" || valType.Name == "StringName")
            {
                target[sName] = val.ToString();
            }
            else if (sName.Contains("Relic") || sName.Contains("Card") || sName.Contains("Potion"))
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                object idVal = valType.GetProperty("Id", flags)?.GetValue(val) ?? valType.GetField("Id", flags)?.GetValue(val) ?? valType.GetField("_id", flags)?.GetValue(val);

                if (idVal != null)
                {
                    var entryProp = idVal.GetType().GetProperty("Entry", flags);
                    if (entryProp != null) target[sName + "_Id"] = entryProp.GetValue(idVal)?.ToString();
                    else target[sName + "_Id"] = idVal.ToString();
                }
            }
        }
    }

    // --- 4. TREASURE ROOM SCANNER ---
    [HarmonyPatch(typeof(NTreasureRoom), "_Ready")]
    public static class TreasureRoomScanner
    {
        public static void Postfix(NTreasureRoom __instance)
        {
            if (__instance == null) return;

            var treasureData = new
            {
                Trigger = "Treasure_Initial",
                RoomType = "Treasure",
                Status = ReadPlayerAndRoomStatus(__instance),
                ProceedButton = ReadProceedButton(__instance),
                Info = "Treasure room entered - Chest can be opened."
            };

            string jsonOutput = JsonSerializer.Serialize(treasureData, new JsonSerializerOptions { WriteIndented = true });
            Task.Run(() => SendDataToAI(jsonOutput));
        }
    }

    // --- 5. TREASURE ROOM UPDATE SCANNER ---
    [HarmonyPatch(typeof(NTreasureRoom), "OnChestButtonReleased")]
    public static class TreasureRoomUpdateScanner
    {
        public static void Postfix(NTreasureRoom __instance)
        {
            if (__instance == null) return;

            var proceedBtn = ReadProceedButton(__instance);

            var treasureData = new
            {
                Trigger = "Treasure_Update",
                RoomType = "Treasure",
                ProceedButton = proceedBtn,
                Action = "ChestOpened",
                Info = "The chest was clicked and loot should now be available."
            };

            string jsonOutput = JsonSerializer.Serialize(treasureData, new JsonSerializerOptions { WriteIndented = true });
            Task.Run(() => SendDataToAI(jsonOutput));
        }
    }

    // --- 6. GLOBAL FOCUS TRACKER ---
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.NGame), "_Input")]
    public static class FocusTracker
    {
        private static Godot.Control _lastFocusedControl = null;
        private static bool _isAttachedToMainLoop = false;

        public static void Postfix(MegaCrit.Sts2.Core.Nodes.NGame __instance)
        {
            if (__instance == null) return;

            if (!_isAttachedToMainLoop)
            {
                __instance.GetTree().ProcessFrame += () =>
                {
                    try
                    {
                        var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                        var viewport = tree?.Root?.GetViewport();
                        if (viewport == null) return;

                        var currentFocus = viewport.GuiGetFocusOwner();

                        if (currentFocus != _lastFocusedControl && currentFocus != null)
                        {
                            _lastFocusedControl = currentFocus;
                            AnalyzeFocus(currentFocus);
                        }
                    }
                    catch { }
                };

                _isAttachedToMainLoop = true;
            }
        }
 
        private static void AnalyzeFocus(Godot.Control currentFocus)
        {
            string nodeName = currentFocus.Name.ToString();
            var focusData = new Dictionary<string, object>
            {
                { "Trigger", "Focus" },
                { "NodeName", nodeName }
            };

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            var textProp = currentFocus.GetType().GetProperty("Text", flags) ??
                           currentFocus.GetType().GetProperty("Title", flags);
            if (textProp != null)
            {
                try { focusData["Label"] = textProp.GetValue(currentFocus)?.ToString(); } catch { }
            }

            string typeName = currentFocus.GetType().Name;
            if (typeName.Contains("MapPoint") || typeName.Contains("MapNode"))
            {
                var pointProp = currentFocus.GetType().GetProperty("Point", flags) ??
                                currentFocus.GetType().GetProperty("MapNode", flags);
                if (pointProp != null)
                {
                    var mapPointObj = pointProp.GetValue(currentFocus);
                    if (mapPointObj != null)
                    {
                        var pType = mapPointObj.GetType().GetProperty("PointType", flags)?.GetValue(mapPointObj) ??
                                    mapPointObj.GetType().GetField("PointType", flags)?.GetValue(mapPointObj);
                        if (pType != null) focusData["MapRoomType"] = pType.ToString();

                        var coordFld = mapPointObj.GetType().GetField("coord", flags) as MemberInfo ??
                                       mapPointObj.GetType().GetProperty("coord", flags);
                        if (coordFld != null)
                        {
                            var coordObj = coordFld is PropertyInfo pi
                                ? pi.GetValue(mapPointObj)
                                : ((FieldInfo)coordFld).GetValue(mapPointObj);
                            if (coordObj != null)
                            {
                                var colFld = coordObj.GetType().GetField("col", flags);
                                var rowFld = coordObj.GetType().GetField("row", flags);
                                if (colFld != null) focusData["MapX"] = colFld.GetValue(coordObj);
                                if (rowFld != null) focusData["MapY"] = rowFld.GetValue(coordObj);
                            }
                        }
                    }
                }
            }

            object hiddenModel = null;
            string[] fieldNames = { "Option", "_option", "Reward", "_reward", "_baseCard", "BaseCard", "Model", "_model", "Card", "_card", "Relic", "_relic", "Potion", "_potion", "Entry", "_entry" };

            foreach (var fn in fieldNames)
            {
                var fld = currentFocus.GetType().GetField(fn, flags);
                if (fld != null) { try { hiddenModel = fld.GetValue(currentFocus); if (hiddenModel != null) break; } catch { } }

                var prop = currentFocus.GetType().GetProperty(fn, flags);
                if (prop != null) { try { hiddenModel = prop.GetValue(currentFocus); if (hiddenModel != null) break; } catch { } }
            }

            if (hiddenModel == null)
            {
                foreach (Godot.Node child in currentFocus.GetChildren())
                {
                    foreach (var fn in fieldNames)
                    {
                        var fld = child.GetType().GetField(fn, flags);
                        if (fld != null) { try { hiddenModel = fld.GetValue(child); if (hiddenModel != null) break; } catch { } }

                        var prop = child.GetType().GetProperty(fn, flags);
                        if (prop != null) { try { hiddenModel = prop.GetValue(child); if (hiddenModel != null) break; } catch { } }
                    }
                    if (hiddenModel != null) break;
                }
            }

            if (hiddenModel != null)
            {
                string modelName = hiddenModel.GetType().Name;

                if (modelName.Contains("Entry"))
                {
                    var creationResultProp = hiddenModel.GetType().GetProperty("CreationResult", flags);
                    if (creationResultProp != null)
                    {
                        object cResult = creationResultProp.GetValue(hiddenModel);
                        if (cResult != null) hiddenModel = cResult;
                    }
                }

                modelName = hiddenModel.GetType().Name;
                if (modelName.Contains("Entry") || modelName.Contains("CreationResult"))
                {
                    object realContent = hiddenModel.GetType().GetProperty("Card", flags)?.GetValue(hiddenModel) ??
                                          hiddenModel.GetType().GetField("_card", flags)?.GetValue(hiddenModel) ??
                                          hiddenModel.GetType().GetProperty("Relic", flags)?.GetValue(hiddenModel) ??
                                          hiddenModel.GetType().GetField("_relic", flags)?.GetValue(hiddenModel) ??
                                          hiddenModel.GetType().GetProperty("Potion", flags)?.GetValue(hiddenModel) ??
                                          hiddenModel.GetType().GetField("_potion", flags)?.GetValue(hiddenModel) ??
                                          hiddenModel.GetType().GetProperty("Model", flags)?.GetValue(hiddenModel) ??
                                          hiddenModel.GetType().GetField("_model", flags)?.GetValue(hiddenModel);
                    if (realContent != null) hiddenModel = realContent;
                }

                focusData["TargetInstanceId"] = hiddenModel.GetHashCode().ToString();

                object idVal = hiddenModel.GetType().GetProperty("Id", flags)?.GetValue(hiddenModel) ??
                               hiddenModel.GetType().GetField("_id", flags)?.GetValue(hiddenModel) ??
                               hiddenModel.GetType().GetField("Id", flags)?.GetValue(hiddenModel);

                if (idVal != null)
                {
                    var entryProp = idVal.GetType().GetProperty("Entry", flags) as MemberInfo ??
                                    idVal.GetType().GetField("Entry", flags);
                    if (entryProp != null)
                    {
                        focusData["TargetId"] = entryProp is PropertyInfo pi
                            ? pi.GetValue(idVal)?.ToString()
                            : ((FieldInfo)entryProp).GetValue(idVal)?.ToString();
                    }
                    else focusData["TargetId"] = idVal.ToString();
                }
                else
                {
                    focusData["TargetId"] = hiddenModel.GetType().GetProperty("Name", flags)?.GetValue(hiddenModel)?.ToString() ?? hiddenModel.GetType().Name;
                }
            }

            if (!focusData.ContainsKey("TargetId") || focusData["TargetId"] == null)
            {
                if (nodeName.Contains("-")) focusData["TargetId"] = nodeName.Substring(nodeName.IndexOf('-') + 1);
            }

            if (nodeName == "Hitbox")
            {
                object backendModel = null;
                Godot.Node parentNode = currentFocus.GetParent();

                while (parentNode != null && parentNode.Name != "EnemyContainer")
                {
                    backendModel = FindBackendModelAggressively(parentNode, flags);
                    if (backendModel != null) break;
                    parentNode = parentNode.GetParent();
                }

                if (backendModel == null && currentFocus.Owner != null) backendModel = FindBackendModelAggressively(currentFocus.Owner, flags);

                if (backendModel != null)
                {
                    focusData["TargetType"] = "Enemy";
                    focusData["TargetInstanceId"] = backendModel.GetHashCode().ToString();

                    object idVal = backendModel.GetType().GetProperty("Id", flags)?.GetValue(backendModel) ??
                                   backendModel.GetType().GetField("Id", flags)?.GetValue(backendModel) ??
                                   backendModel.GetType().GetField("_id", flags)?.GetValue(backendModel);

                    if (idVal != null)
                    {
                        var entryProp = idVal.GetType().GetProperty("Entry", flags) as MemberInfo ??
                                        idVal.GetType().GetField("Entry", flags);
                        if (entryProp != null)
                        {
                            focusData["TargetMonsterId"] = entryProp is PropertyInfo pi
                                ? pi.GetValue(idVal)?.ToString()
                                : ((FieldInfo)entryProp).GetValue(idVal)?.ToString();
                        }
                        else focusData["TargetMonsterId"] = idVal.ToString();
                    }
                }

                if (!focusData.ContainsKey("TargetMonsterId") || focusData["TargetMonsterId"] == null)
                {
                    focusData["TargetType"] = "Enemy";
                    focusData["TargetMonsterIndex"] = currentFocus.GetParent()?.GetIndex();
                }
            }

            string jsonOutput = JsonSerializer.Serialize(focusData, new JsonSerializerOptions { WriteIndented = true });
            Task.Run(() => SendDataToAI(jsonOutput));
        }

        private static object FindBackendModelAggressively(Godot.Node node, BindingFlags flags)
        {
            if (node == null) return null;
            string[] targetNames = { "Creature", "_creature", "creature", "Monster", "_monster", "monster", "Model", "_model" };
            foreach (var name in targetNames)
            {
                object val = node.GetType().GetProperty(name, flags)?.GetValue(node) ??
                             node.GetType().GetField(name, flags)?.GetValue(node);
                if (val != null) return val;
            }

            foreach (Godot.Node child in node.GetChildren())
            {
                foreach (var name in targetNames)
                {
                    object val = child.GetType().GetProperty(name, flags)?.GetValue(child) ??
                                 child.GetType().GetField(name, flags)?.GetValue(child);
                    if (val != null) return val;
                }
            }
            return null;
        }
    }
    
    [HarmonyPatch(typeof(NMerchantRoom), "AfterRoomIsLoaded")]
    public static class PlayerScanner
    {
        public static void Postfix(NMerchantRoom __instance)
        {
            if (__instance == null) return;
            try
            {
                
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var playersField = __instance.GetType().GetField("_players", flags);
                if (playersField != null)
                {
                    var playersList = playersField.GetValue(__instance) as System.Collections.IList;
                    if (playersList != null && playersList.Count > 0)
                    {
                        object playerObj = playersList[0];

                        int gold = (int)(playerObj.GetType().GetProperty("Gold", flags)?.GetValue(playerObj) ?? 0);
                        
                        int maxPotions = (int)(playerObj.GetType().GetProperty("MaxPotionCount", flags)?.GetValue(playerObj) ?? 0);

                        var potionsList = new List<string>();
                        var potionsObj = playerObj.GetType().GetProperty("Potions", flags)?.GetValue(playerObj) as System.Collections.IEnumerable;
                        if (potionsObj != null)
                        {
                            foreach (var p in potionsObj)
                            {
                                if (p == null) continue;
                                potionsList.Add(ExtractId(p) ?? "UnknownPotion");
                            }
                        }

                        var relicsList = new List<object>();
                        var relicsObj = playerObj.GetType().GetProperty("Relics", flags)?.GetValue(playerObj) as System.Collections.IEnumerable;
                        if (relicsObj != null)
                        {
                            foreach (var r in relicsObj)
                            {
                                if (r == null) continue;
                                string rId = ExtractId(r) ?? "UnknownRelic";
                                int counter = (int)(r.GetType().GetProperty("Counter", flags)?.GetValue(r) ??
                                                    r.GetType().GetField("Counter", flags)?.GetValue(r) ?? 0);
                                relicsList.Add(new { RelicId = rId, Counter = counter });
                            }
                        }

                        var deckList = new List<object>();
                        var deckObj = playerObj.GetType().GetProperty("Deck", flags)?.GetValue(playerObj);
                        if (deckObj != null)
                        {
                            var cardsObj = deckObj.GetType().GetProperty("Cards", flags)?.GetValue(deckObj) as System.Collections.IEnumerable;
                            if (cardsObj != null)
                            {
                                foreach (var c in cardsObj)
                                {
                                    if (c == null) continue;
                                    string cId = ExtractId(c) ?? "UnknownCard";
                                    bool isUpgraded = (bool)(c.GetType().GetProperty("IsUpgraded", flags)?.GetValue(c) ?? false);
                                    deckList.Add(new { CardId = cId, IsUpgraded = isUpgraded });
                                }
                            }
                        }

                        var playerData = new
                        {
                            Trigger = "PlayerInventory",
                            Gold = gold,
                            MaxPotionSlots = maxPotions,
                            CurrentPotions = potionsList,
                            Relics = relicsList,
                            Deck = deckList
                        };

                        string jsonOutput = JsonSerializer.Serialize(playerData, new JsonSerializerOptions { WriteIndented = true });
                        Task.Run(() => SendDataToAI(jsonOutput));
                    }
                }
            }
            catch { }
        }

        private static string ExtractId(object model)
        {
            if (model == null) return null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            object idVal = model.GetType().GetProperty("Id", flags)?.GetValue(model) ??
                           model.GetType().GetField("Id", flags)?.GetValue(model) ??
                           model.GetType().GetField("_id", flags)?.GetValue(model);

            if (idVal != null)
            {
                var entryProp = idVal.GetType().GetProperty("Entry", flags) as MemberInfo ??
                                idVal.GetType().GetField("Entry", flags);

                if (entryProp != null)
                {
                    return entryProp is PropertyInfo pi
                        ? pi.GetValue(idVal)?.ToString()
                        : ((FieldInfo)entryProp).GetValue(idVal)?.ToString();
                }
                return idVal.ToString();
            }
            return null;
        }
    }

    private static object ReadPlayerAndRoomStatus(object roomInstance)
    {
        if (roomInstance == null) return null;

        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            
            // 1. Raum Backend extrahieren
            var roomBackend = roomInstance.GetType().GetProperty("Room", flags)?.GetValue(roomInstance) ??
                              roomInstance.GetType().GetField("Room", flags)?.GetValue(roomInstance) ??
                              roomInstance.GetType().GetField("_room", flags)?.GetValue(roomInstance);

            // 2. RunState finden (WICHTIG für Campfire, da es dort direkt in der Godot-Node liegt)
            object runStateObj = roomInstance.GetType().GetProperty("RunState", flags)?.GetValue(roomInstance) ??
                                 roomInstance.GetType().GetField("RunState", flags)?.GetValue(roomInstance) ??
                                 roomInstance.GetType().GetField("_runState", flags)?.GetValue(roomInstance);

            if (runStateObj == null && roomBackend != null)
            {
                runStateObj = roomBackend.GetType().GetProperty("RunState", flags)?.GetValue(roomBackend) ??
                              roomBackend.GetType().GetField("RunState", flags)?.GetValue(roomBackend) ??
                              roomBackend.GetType().GetField("_runState", flags)?.GetValue(roomBackend);
            }

            // 3. Player Objekt finden
            object playerObj = null;

            // Variante A: Direkt aus der Godot-Node (wie beim Shop)
            var playersList = roomInstance.GetType().GetField("_players", flags)?.GetValue(roomInstance) as System.Collections.IList ??
                              roomInstance.GetType().GetProperty("Players", flags)?.GetValue(roomInstance) as System.Collections.IList;

            // Variante B: Aus dem RunState (wie beim Campfire)
            if (playersList == null && runStateObj != null)
            {
                playersList = runStateObj.GetType().GetField("_players", flags)?.GetValue(runStateObj) as System.Collections.IList ??
                              runStateObj.GetType().GetProperty("Players", flags)?.GetValue(runStateObj) as System.Collections.IList;
            }

            if (playersList != null && playersList.Count > 0)
            {
                playerObj = playersList[0];
            }

            // Variante C: Fallback auf Single-Player Eigenschaften
            if (playerObj == null && roomBackend != null)
            {
                playerObj = roomBackend.GetType().GetProperty("Player", flags)?.GetValue(roomBackend) ??
                            roomBackend.GetType().GetField("Player", flags)?.GetValue(roomBackend) ??
                            roomBackend.GetType().GetField("_player", flags)?.GetValue(roomBackend);
            }

            // Falls wir RunState noch nicht haben, aber den Spieler gefunden haben
            if (runStateObj == null && playerObj != null)
            {
                runStateObj = playerObj.GetType().GetProperty("RunState", flags)?.GetValue(playerObj) ??
                              playerObj.GetType().GetField("RunState", flags)?.GetValue(playerObj) ??
                              playerObj.GetType().GetField("_runState", flags)?.GetValue(playerObj);
            }

            // --- MAP & RAUM INFO AUSLESEN ---
            object roomInfo = new { Type = "Unknown", X = 0, Y = 0 };
            if (runStateObj != null)
            {
                var mapPoint = runStateObj.GetType().GetProperty("CurrentMapPoint", flags)?.GetValue(runStateObj) ??
                               runStateObj.GetType().GetField("CurrentMapPoint", flags)?.GetValue(runStateObj);
                
                if (mapPoint != null)
                {
                    var pType = mapPoint.GetType().GetProperty("PointType", flags)?.GetValue(mapPoint)?.ToString() ??
                                mapPoint.GetType().GetField("PointType", flags)?.GetValue(mapPoint)?.ToString();
                                
                    var coord = mapPoint.GetType().GetField("coord", flags)?.GetValue(mapPoint) ??
                                mapPoint.GetType().GetProperty("coord", flags)?.GetValue(mapPoint);
                                
                    if (coord != null)
                    {
                        int x = (int)(coord.GetType().GetField("col", flags)?.GetValue(coord) ?? coord.GetType().GetProperty("col", flags)?.GetValue(coord) ?? 0);
                        int y = (int)(coord.GetType().GetField("row", flags)?.GetValue(coord) ?? coord.GetType().GetProperty("row", flags)?.GetValue(coord) ?? 0);
                        roomInfo = new { Type = pType ?? "Unknown", X = x, Y = y };
                    }
                }
            }
            
            // --- SPIELER STATS (HP, Gold, Deck) AUSLESEN ---
            object deckInfo = new List<string>();
            int gold = 0, hp = 0, maxHp = 0;

            if (playerObj != null)
            {
                gold = (int)(playerObj.GetType().GetProperty("Gold", flags)?.GetValue(playerObj) ?? 
                             playerObj.GetType().GetField("Gold", flags)?.GetValue(playerObj) ?? 0);
                
                var creature = playerObj.GetType().GetProperty("Creature", flags)?.GetValue(playerObj) ??
                               playerObj.GetType().GetField("Creature", flags)?.GetValue(playerObj) ??
                               playerObj.GetType().GetField("_creature", flags)?.GetValue(playerObj);
                               
                if (creature != null)
                {
                    hp = (int)(creature.GetType().GetProperty("CurrentHp", flags)?.GetValue(creature) ?? 
                               creature.GetType().GetField("CurrentHp", flags)?.GetValue(creature) ?? 0);
                    maxHp = (int)(creature.GetType().GetProperty("MaxHp", flags)?.GetValue(creature) ?? 
                                  creature.GetType().GetField("MaxHp", flags)?.GetValue(creature) ?? 0);
                }

                var deckObj = playerObj.GetType().GetProperty("MasterDeck", flags)?.GetValue(playerObj) ??
                              playerObj.GetType().GetField("MasterDeck", flags)?.GetValue(playerObj) ??
                              playerObj.GetType().GetProperty("Deck", flags)?.GetValue(playerObj) ??
                              playerObj.GetType().GetField("Deck", flags)?.GetValue(playerObj);

                if (deckObj != null)
                {
                    var cardsObj = deckObj.GetType().GetProperty("Cards", flags)?.GetValue(deckObj) as System.Collections.IEnumerable ??
                                   deckObj.GetType().GetField("Cards", flags)?.GetValue(deckObj) as System.Collections.IEnumerable ??
                                   deckObj as System.Collections.IEnumerable;

                    if (cardsObj != null)
                    {
                        var cardNames = new List<string>();
                        foreach (var card in cardsObj)
                        {
                            if (card == null) continue;
                            var idVal = card.GetType().GetProperty("Id", flags)?.GetValue(card) ??
                                        card.GetType().GetField("Id", flags)?.GetValue(card) ??
                                        card.GetType().GetField("_id", flags)?.GetValue(card);
                                        
                            if (idVal != null)
                            {
                                var entry = idVal.GetType().GetProperty("Entry", flags)?.GetValue(idVal)?.ToString() ??
                                            idVal.GetType().GetField("Entry", flags)?.GetValue(idVal)?.ToString();
                                            
                                if (entry != null) cardNames.Add(entry);
                                else cardNames.Add(idVal.ToString());
                            }
                        }
                        deckInfo = cardNames;
                    }
                }
            }

            return new
            {
                Room = roomInfo,
                Gold = gold,
                HP = hp,
                MaxHP = maxHp,
                MasterDeck = deckInfo
            };
        }
        catch (Exception ex)
        {
            return new { Error = "Status could not be read: " + ex.Message };
        }
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil), "TryGrabFocus")]
    public static class FocusBugFix
    {
        public static void Prefix(Godot.Control __0)
        {
            if (__0 != null)
            {
                if (__0.FocusMode == Godot.Control.FocusModeEnum.None)
                {
                    __0.FocusMode = Godot.Control.FocusModeEnum.All;
                }
            }
        }
    }

    // --- NEW MAP SCANNER (Direct Hook upon generation) ---
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Map.StandardActMap), "CreateFor")]
    public static class ActMapGenerationScanner
    {
        public static void Postfix(ActMap __result, object runState)
        {
            if (__result == null) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                int currentAct = -1;
                if (runState != null)
                {
                     object actObj = runState.GetType().GetProperty("CurrentActIndex", flags)?.GetValue(runState) ??
                                     runState.GetType().GetField("CurrentActIndex", flags)?.GetValue(runState) ??
                                     runState.GetType().GetField("_currentActIndex", flags)?.GetValue(runState);
                     
                     if (actObj != null) currentAct = (int)actObj;
                }

                var mapData = new
                {
                    Trigger = "MapGenerated",
                    Act = currentAct + 1,
                    RowCount = __result.GetRowCount(),
                    ColumnCount = __result.GetColumnCount(),
                    AllRooms = __result.GetAllMapPoints().Select(room => new
                    {
                        RoomType = room.PointType.ToString(),
                        Column_X = room.coord.col,
                        Row_Y = room.coord.row,
                        NextTargets = room.Children.Select(child => new
                        {
                            TargetColumn_X = child.coord.col,
                            TargetRow_Y = child.coord.row
                        }).ToList()
                    }).ToList()
                };

                string jsonOutput = JsonSerializer.Serialize(mapData, new JsonSerializerOptions { WriteIndented = true });
                Task.Run(() => SendDataToAI(jsonOutput));
            }
            catch (Exception ex)
            {
                Log.Info($"[ERROR - MAP SCANNER]: {ex.Message}");
            }
        }
    }
}