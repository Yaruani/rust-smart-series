using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq; // 追加: これが必要です
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Smart Craft", "Yaruani", "1.3.3")]
    [Description("Allows customization of crafting speed (Instant, 2x, 10x, etc) with smart features.")]
    public class SmartCraft : RustPlugin
    {
        #region Variables
        private const string PermissionUse = "smartcraft.use";
        private const string PermissionNormal = "smartcraft.normal";
        #endregion

        #region Configuration
        private static Configuration _config;

        private class Configuration
        {
            [JsonProperty("Plugin Enabled")] public bool PluginEnabled = true;
            [JsonProperty("Use Permission")] public bool UsePermission = true;
            [JsonProperty("Crafting Speed Multiplier (0 = Instant)")] public float CraftingSpeed = 0.0f;
            [JsonProperty("Enable Chat Notifications")] public bool EnableChatNotifications = true;
            [JsonProperty("Instant Mode - Check for free place")] public bool CheckPlace = true;
            [JsonProperty("Instant Mode - Split crafted stacks")] public bool Split = true;
            [JsonProperty("Normal Speed Items (Blacklist)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> NormalSpeedItems = new List<string> { "hammer" };
            [JsonProperty("Blocked Items (Cannot Craft)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedItems = new List<string>();

            [JsonIgnore] public HashSet<int> NormalSpeedItemIds = new HashSet<int>();
            [JsonIgnore] public HashSet<int> BlockedItemIds = new HashSet<int>();
            [JsonProperty("Debug Mode (Logs)")] public bool Debug = false;

            public void Init(SmartCraft plugin)
            {
                ValidateItemShortNames(plugin, NormalSpeedItems, NormalSpeedItemIds);
                ValidateItemShortNames(plugin, BlockedItems, BlockedItemIds);
            }

            private void ValidateItemShortNames(SmartCraft plugin, List<string> shortNames, HashSet<int> idList)
            {
                idList.Clear();
                foreach (var name in shortNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var def = ItemManager.FindItemDefinition(name);
                    if (def != null) idList.Add(def.itemid);
                    else plugin.PrintWarning($"Invalid item shortname in config: {name}");
                }
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                // --- 移行ロジック (InstantCraft -> SmartCraft) ---
                if (!File.Exists(Config.Filename))
                {
                    string oldConfigPath = Path.Combine(Interface.Oxide.ConfigDirectory, "InstantCraft.json");
                    if (File.Exists(oldConfigPath))
                    {
                        PrintWarning("Found old 'InstantCraft.json'. Attempting to migrate settings...");
                        try
                        {
                            var oldConfig = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(oldConfigPath));

                            // "Normal Speed" -> NormalSpeedItems
                            if (oldConfig.ContainsKey("Normal Speed") && oldConfig["Normal Speed"] is JArray normalArray)
                            {
                                _config.NormalSpeedItems = normalArray.ToObject<List<string>>();
                            }

                            // "Blacklist" -> BlockedItems
                            if (oldConfig.ContainsKey("Blacklist") && oldConfig["Blacklist"] is JArray blockArray)
                            {
                                _config.BlockedItems = blockArray.ToObject<List<string>>();
                            }

                            // "Check for free place"
                            if (oldConfig.ContainsKey("Check for free place"))
                                _config.CheckPlace = Convert.ToBoolean(oldConfig["Check for free place"]);

                            // "Split crafted stacks"
                            if (oldConfig.ContainsKey("Split crafted stacks"))
                                _config.Split = Convert.ToBoolean(oldConfig["Split crafted stacks"]);

                            PrintWarning("Migration successful! Settings imported from InstantCraft.");
                            SaveConfig();
                        }
                        catch (Exception ex)
                        {
                            PrintError($"Migration failed: {ex.Message}");
                        }
                    }
                }
                // ------------------------------------------------
            }
            catch
            {
                PrintWarning("Configuration invalid. Resetting to defaults.");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();
        protected override void SaveConfig() => Config.WriteObject(_config);
        #endregion

        #region Commands
        [ChatCommand("smartcraft")]
        private void CmdSmartCraft(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (args.Length == 0)
            {
                player.ChatMessage($"<color=#00ffff>[SmartCraft]</color> Enabled: {_config.PluginEnabled}");
                player.ChatMessage($"<color=#00ffff>[SmartCraft]</color> Current Speed: {FormatSpeed(_config.CraftingSpeed)}");
                player.ChatMessage($"<color=#00ffff>[SmartCraft]</color> /smartcraft toggle - On/Off");
                player.ChatMessage($"<color=#00ffff>[SmartCraft]</color> /smartcraft speed <number> - Set speed (0=Instant)");
                return;
            }

            switch (args[0].ToLower())
            {
                case "toggle":
                    _config.PluginEnabled = !_config.PluginEnabled;
                    SaveConfig();
                    var status = _config.PluginEnabled ? "Enabled" : "Disabled";
                    Msg(player, $"Plugin is now {status}.");
                    break;

                case "speed":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color=#red>Usage:</color> /smartcraft speed 0 (Instant) or 10 (10x)");
                        return;
                    }
                    if (float.TryParse(args[1], out float newSpeed))
                    {
                        if (newSpeed < 0) newSpeed = 0;
                        _config.CraftingSpeed = newSpeed;
                        SaveConfig();
                        Msg(player, $"Crafting Speed set to: {FormatSpeed(newSpeed)}");
                    }
                    else
                    {
                        player.ChatMessage($"<color=#red>Error:</color> Invalid number.");
                    }
                    break;

                case "reload":
                    ReloadSettings(player);
                    break;
            }
        }

        private void ReloadSettings(BasePlayer player)
        {
            try
            {
                var settings = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(Config.Filename));
                if (settings == null || settings.NormalSpeedItems == null || settings.BlockedItems == null ||
                    settings.CraftingSpeed < 0 || float.IsNaN(settings.CraftingSpeed) || float.IsInfinity(settings.CraftingSpeed))
                    throw new JsonSerializationException("Expected item lists and a finite, nonnegative crafting speed.");

                settings.Init(this);
                _config = settings;
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not reload config: {ex.Message}");
                player.ChatMessage("<color=#red>[SmartCraft]</color> Config reload failed. Current settings remain active.");
                return;
            }

            Msg(player, "Config reloaded.");
        }

        private string FormatSpeed(float speed) => speed <= 0.05f ? "INSTANT" : $"{speed}x";
        private void Msg(BasePlayer player, string msg)
        {
            string formatted = $"<color=#00ffff>[SmartCraft]</color> {msg}";
            if (_config.EnableChatNotifications) Server.Broadcast(formatted);
            else player.ChatMessage(formatted);
        }
        #endregion

        #region Hooks
        private void Init()
        {
            permission.RegisterPermission(PermissionUse, this);
            permission.RegisterPermission(PermissionNormal, this);
            _config.Init(this);
        }

        private void OnServerInitialized()
        {
            if (_config.PluginEnabled)
                PrintWarning("SmartCraft is ENABLED.");
            else
                PrintError("SmartCraft is DISABLED in config.");
        }

        private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint blueprint, int amount, bool free)
        {
            if (!_config.PluginEnabled) return null;

            if (_config.BlockedItemIds.Contains(blueprint.targetItem.itemid))
            {
                Message(itemCrafter.baseEntity, "Blocked");
                return false;
            }
            return null;
        }

        private object OnItemCraft(ItemCraftTask task, BasePlayer owner)
        {
            if (!_config.PluginEnabled || task.cancelled) return null;

            // 1. Permission Check
            if (_config.UsePermission)
            {
                if (!permission.UserHasPermission(owner.UserIDString, PermissionUse)) return null;
            }
            
            // 2. Force Normal Check (Permission or Blacklist)
            if (permission.UserHasPermission(owner.UserIDString, PermissionNormal) || 
                _config.NormalSpeedItemIds.Contains(task.blueprint.targetItem.itemid))
            {
                PrintDebug($"Normal speed enforced for {task.blueprint.targetItem.shortname}");
                return null; // Vanilla speed
            }

            // 3. Determine Logic based on Speed setting
            float speed = _config.CraftingSpeed;

            // === MODE A: INSTANT (Speed ~ 0) ===
            if (speed <= 0.05f) 
            {
                return ProcessInstantCraft(task, owner);
            }

            // === MODE B: MULTIPLIER (Speed > 0) ===
            if (speed > 1.0f)
            {
                float originalDuration = task.blueprint.time;
                float newDuration = originalDuration / speed;
                
                // Rust calculates finish time based on startTime + duration.
                task.endTime = UnityEngine.Time.realtimeSinceStartup + newDuration;
                
                return null; // Let vanilla proceed with modified time
            }

            return null; // Normal speed (1.0)
        }
        #endregion

        #region Instant Craft Logic
        private object ProcessInstantCraft(ItemCraftTask task, BasePlayer owner)
        {
            // Determine stacks
            int totalAmount = task.amount * task.blueprint.amountToCreate;
            List<int> stacks = GetStacks(task.blueprint.targetItem, totalAmount);

            // Check space
            if (_config.CheckPlace)
            {
                int freeSlots = FreeSlots(owner);
                int requiredSlots = _config.Split ? stacks.Count : 1;
                
                if (freeSlots < requiredSlots && freeSlots < stacks.Count) 
                {
                    CancelTask(task, owner, "Slots", requiredSlots, freeSlots);
                    return false;
                }
            }

            // Give Items
            if (!GiveItem(task, owner, stacks)) return null;

            return true; // Cancel vanilla task (we handled it)
        }

        public void CancelTask(ItemCraftTask task, BasePlayer owner, string reason, params object[] args)
        {
            task.cancelled = true;
            Message(owner, reason, args);
            // Refund materials
            if (task.takenItems != null)
            {
                foreach (var item in task.takenItems)
                {
                    owner.GiveItem(item);
                }
            }
            Interface.CallHook("OnItemCraftCancelled", task, owner.inventory.crafting);
        }

        public bool GiveItem(ItemCraftTask task, BasePlayer owner, List<int> stacks)
        {
            ulong skin = ItemDefinition.FindSkin(task.blueprint.targetItem.itemid, task.skinID);
            bool success = false;

            if (_config.Split)
            {
                foreach (var amount in stacks)
                {
                    if (Give(task, owner, amount, skin)) success = true;
                }
            }
            else
            {
                int total = stacks.Sum();
                if (Give(task, owner, total, skin)) success = true;
            }

            if (success) task.cancelled = true;
            return success;
        }

        public bool Give(ItemCraftTask task, BasePlayer owner, int amount, ulong skin)
        {
            Item item = ItemManager.CreateByItemID(task.blueprint.targetItem.itemid, amount, skin);
            if (item == null) return false;

            if (item.hasCondition && task.conditionScale != 1f)
            {
                item.maxCondition *= task.conditionScale;
                item.condition = item.maxCondition;
            }

            item.OnVirginSpawn(owner);
            item.SetItemOwnership(owner, ItemOwnershipPhrases.CraftedPhrase);
            if (task.instanceData != null) item.instanceData = task.instanceData;

            Interface.CallHook("OnItemCraftFinished", task, item, owner.inventory.crafting);

            if (owner.inventory.GiveItem(item))
            {
                owner.Command("note.inv", item.info.itemid, amount);
                return true;
            }

            // Inventory Full -> Drop
            item.Drop(owner.transform.position, owner.transform.forward * 2f);
            owner.Command("note.inv", item.info.itemid, amount);
            return true;
        }

        public int FreeSlots(BasePlayer player)
        {
            return (player.inventory.containerMain.capacity - player.inventory.containerMain.itemList.Count) +
                   (player.inventory.containerBelt.capacity - player.inventory.containerBelt.itemList.Count);
        }

        public List<int> GetStacks(ItemDefinition item, int amount)
        {
            var list = new List<int>();
            var maxStack = Math.Max(1, item.stackable);

            while (amount > maxStack)
            {
                list.Add(maxStack);
                amount -= maxStack;
            }
            if (amount > 0) list.Add(amount);
            return list;
        }
        #endregion

        #region Localization
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Blocked", "Crafting of that item is blocked!"},
                {"Slots", "Not enough space! Need {0} slots, have {1}."},
                {"Normal", "Instant craft disabled for this item. Crafting at normal speed."}
            }, this, "en");
        }

        private void Message(BasePlayer player, string key, params object[] args)
        {
            if (player != null) player.ChatMessage(string.Format(lang.GetMessage(key, this, player.UserIDString), args));
        }
        #endregion

        #region Helpers
        private void PrintDebug(string message)
        {
            if (_config.Debug) Puts($"[Debug] {message}");
        }
        #endregion
    }
}
