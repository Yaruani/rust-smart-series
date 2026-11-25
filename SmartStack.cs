using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Smart Stack", "Yaruani", "1.4.8")]
    [Description("Simple and lightweight stack size controller with in-game commands and config cleanup.")]
    class SmartStack : RustPlugin
    {
        #region Configuration
        private static Configuration _config;

        private class Configuration
        {
            [JsonProperty("Plugin Enabled")] public bool PluginEnabled = true;
            [JsonProperty("Global Stack Multiplier")] public float GlobalMultiplier = 10.0f;
            
            [JsonProperty("Category Multipliers (Overrides Global)")] 
            public Dictionary<string, float> CategoryMultipliers = new Dictionary<string, float> {
                { "Component", 10.0f }, { "Resources", 10.0f }, { "Ammunition", 1.0f }, { "Construction", 10.0f },
                { "Medical", 5.0f }, { "Food", 10.0f }, { "Items", 1.0f }, { "Tool", 1.0f },
                { "Weapon", 1.0f }, { "Attire", 1.0f }, { "Misc", 1.0f }, { "Traps", 1.0f }, { "Electrical", 1.0f }, { "Fun", 1.0f }
            };
            
            [JsonProperty("Specific Item Overrides (Shortname: Limit)")] 
            public Dictionary<string, int> ItemOverrides = new Dictionary<string, int>();
            
            [JsonProperty("Debug Mode")] public bool Debug = false;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                // Migration Logic
                if (!File.Exists(Config.Filename))
                {
                    string oldConfigPath = System.IO.Path.Combine(Interface.Oxide.ConfigDirectory, "StackModifier.json");
                    if (File.Exists(oldConfigPath))
                    {
                        PrintWarning("Detected 'StackModifier.json'. Attempting migration...");
                        try {
                            var stackModifierConfig = JsonConvert.DeserializeObject<Dictionary<string, object>>(System.IO.File.ReadAllText(oldConfigPath));
                            if (stackModifierConfig.ContainsKey("Stack Categories")) {
                                var categories = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, Dictionary<string, object>>>>(JsonConvert.SerializeObject(stackModifierConfig["Stack Categories"]));
                                foreach (var cat in categories) {
                                    foreach (var itemEntry in cat.Value) {
                                        if (itemEntry.Value.ContainsKey("ShortName") && itemEntry.Value.ContainsKey("Modified")) {
                                            string shortname = itemEntry.Value["ShortName"].ToString();
                                            int modified = Convert.ToInt32(itemEntry.Value["Modified"]);
                                            if (modified > 1) _config.ItemOverrides[shortname] = modified;
                                        }
                                    }
                                }
                                SaveConfig();
                            }
                        } catch {}
                    }
                }
                CleanUpOverrides(); 
                SaveConfig();
            } catch {
                PrintWarning("Configuration invalid. Resetting to defaults.");
                LoadDefaultConfig();
            }
        }
        protected override void LoadDefaultConfig() => _config = new Configuration();
        protected override void SaveConfig() => Config.WriteObject(_config);
        #endregion

        #region Data & State
        private Dictionary<int, int> _originalStackSizes = new Dictionary<int, int>();
        #endregion

        #region Commands
        [ChatCommand("smartstack")]
        private void CmdSmartStack(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (args.Length == 0)
            {
                SendHelp(player);
                return;
            }

            string sub = args[0].ToLower();

            // --- SET COMMAND ---
            if (sub == "set")
            {
                string targetName = "";
                int targetAmount = 0;

                if (args.Length == 2 && int.TryParse(args[1], out targetAmount))
                {
                    var item = player.GetActiveItem();
                    if (item == null) { player.ChatMessage("<color=#red>Error:</color> You must hold an item in your hand. Use: /stack set <name> <amount>"); return; }
                    targetName = item.info.shortname;
                }
                else if (args.Length == 3 && int.TryParse(args[2], out targetAmount))
                {
                    var def = ItemManager.FindItemDefinition(args[1]);
                    if (def == null) { player.ChatMessage($"<color=#red>Error:</color> Item '{args[1]}' not found."); return; }
                    targetName = def.shortname;
                }
                else { player.ChatMessage("<color=#red>Usage:</color> /stack set <amount> OR /stack set <name> <amount>"); return; }

                if (targetAmount <= 0)
                {
                    player.ChatMessage("<color=#red>Error:</color> Stack limit must be at least 1.");
                    return;
                }

                int originalStack = GetOriginalStackSize(targetName);
                if (originalStack <= 0) originalStack = 1;

                if (CalculateNewStack(ItemManager.FindItemDefinition(targetName), originalStack, true) == targetAmount)
                {
                    if (_config.ItemOverrides.ContainsKey(targetName)) _config.ItemOverrides.Remove(targetName);
                    player.ChatMessage($"<color=#00ff00>[SmartStack]</color> Removed override for <b>{targetName}</b> (Same as default).");
                }
                else
                {
                    _config.ItemOverrides[targetName] = targetAmount;
                    player.ChatMessage($"<color=#00ff00>[SmartStack]</color> Set <b>{targetName}</b> stack limit to <b>{targetAmount}</b>.");
                }
                
                SaveConfig();
                UpdateStackSizes();
                return;
            }

            // --- RESET COMMAND ---
            if (sub == "reset")
            {
                string targetName = "";

                if (args.Length == 1)
                {
                    var item = player.GetActiveItem();
                    if (item == null) { player.ChatMessage("<color=#red>Error:</color> You must hold an item in your hand. Use: /stack reset <name>"); return; }
                    targetName = item.info.shortname;
                }
                else if (args.Length == 2)
                {
                    targetName = args[1];
                }
                else { SendHelp(player); return; }

                if (_config.ItemOverrides.ContainsKey(targetName))
                {
                    _config.ItemOverrides.Remove(targetName);
                    SaveConfig();
                    UpdateStackSizes();
                    player.ChatMessage($"<color=#00ff00>[SmartStack]</color> Reset <b>{targetName}</b> to category/global default.");
                }
                else { player.ChatMessage($"<color=#ffff00>[SmartStack]</color> No custom override found for {targetName}."); return; }
            }

            // --- GLOBAL COMMAND ---
            if (sub == "global" && args.Length >= 2)
            {
                if (float.TryParse(args[1], out float newMulti))
                {
                    if (float.IsNaN(newMulti) || float.IsInfinity(newMulti))
                    {
                        player.ChatMessage("<color=#red>Error:</color> Stack multiplier must be finite.");
                        return;
                    }
                    if (newMulti < 1.0f) newMulti = 1.0f;
                    _config.GlobalMultiplier = newMulti;
                    
                    CleanUpOverrides();
                    
                    SaveConfig();
                    UpdateStackSizes();
                    player.ChatMessage($"<color=#00ffff>[SmartStack]</color> Global Multiplier updated to: x{newMulti}");
                }
                return;
            }
            
            // --- SEARCH COMMAND ---
            if (sub == "search" && args.Length >= 2)
            {
                string query = args[1].ToLower();
                var matches = ItemManager.itemList
                    .Where(x => x.shortname.Contains(query) || x.displayName.english.ToLower().Contains(query))
                    .Take(10)
                    .Select(x => $"{x.displayName.english} (<color=#aaa>{x.shortname}</color>)")
                    .ToList();

                if (matches.Count > 0) player.ChatMessage($"<color=#00ffff>Found items:</color>\n" + string.Join("\n", matches));
                else player.ChatMessage("No items found.");
                return;
            }
            
            // --- STATUS COMMAND ---
            if (sub == "status" || sub == "stat")
            {
                Puts("--- SmartStack Status ---");
                Puts($"Plugin Enabled: {_config.PluginEnabled}");
                Puts($"Global Multiplier: {_config.GlobalMultiplier:0.##}x");
                Puts($"Total Overrides: {_config.ItemOverrides.Count}");
                Puts("--- Overrides List ---");
                foreach (var kvp in _config.ItemOverrides)
                {
                    ItemDefinition def = ItemManager.FindItemDefinition(kvp.Key);
                    if (def != null)
                    {
                        Puts($"- {def.displayName.english} ({kvp.Key}): {kvp.Value}");
                    }
                }
                Puts("-------------------------");
                player.ChatMessage($"<color=#00ffff>[SmartStack]</color> Status logged to server console.");
                return;
            }
            
            SendHelp(player);
        }

        private void SendHelp(BasePlayer player)
        {
            player.ChatMessage($"<color=#00ffff>--- SmartStack Help ---</color>");
            player.ChatMessage($"/stack set <amount> (Held Item)");
            player.ChatMessage($"/stack set <name> <amount> (By Name)");
            player.ChatMessage($"/stack reset [name]");
            player.ChatMessage($"/stack search <name>");
            player.ChatMessage($"/stack global <multiplier>");
            player.ChatMessage($"/stack status - Log current settings to console");
        }
        
        [ChatCommand("stack")]
        private void CmdStack(BasePlayer player, string command, string[] args) => CmdSmartStack(player, command, args);
        #endregion

        #region Hooks
        private void OnServerInitialized()
        {
            if (!_config.PluginEnabled) { PrintWarning("SmartStack is DISABLED."); return; }
            UpdateStackSizes();
        }
        private void Unload() { RevertStackSizes(); }
        #endregion

        #region Core Logic
        
        private void CleanUpOverrides()
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in _config.ItemOverrides)
            {
                if (kvp.Value <= 0)
                {
                    PrintWarning($"Ignoring stack limit {kvp.Value} for {kvp.Key}. Stack limits must be at least 1.");
                    keysToRemove.Add(kvp.Key);
                    continue;
                }

                var def = ItemManager.FindItemDefinition(kvp.Key);
                if (def == null) continue;

                int originalStack = GetOriginalStackSize(def.shortname);
                int calculatedDefault = CalculateNewStack(def, originalStack, true); 

                if (kvp.Value == calculatedDefault)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _config.ItemOverrides.Remove(key);
            }
        }

        private int GetOriginalStackSize(string shortname)
        {
            var def = ItemManager.FindItemDefinition(shortname);
            if (def == null) return 1;

            if (_originalStackSizes.ContainsKey(def.itemid))
            {
                return _originalStackSizes[def.itemid];
            }
            return def.stackable;
        }

        private void UpdateStackSizes()
        {
            CleanUpOverrides();

            if (_originalStackSizes.Count > 0) RevertStackSizes();

            int count = 0;
            foreach (var itemDef in ItemManager.itemList)
            {
                if (itemDef == null) continue;
                if (!_originalStackSizes.ContainsKey(itemDef.itemid)) _originalStackSizes[itemDef.itemid] = itemDef.stackable;
                
                int originalStack = _originalStackSizes[itemDef.itemid];
                bool hasOverride = _config.ItemOverrides.ContainsKey(itemDef.shortname);
                if (originalStack <= 1 && !hasOverride) continue;

                int newStack = CalculateNewStack(itemDef, originalStack, false);

                if (newStack != itemDef.stackable) { itemDef.stackable = newStack; count++; }
            }
            Puts($"[SmartStack] Updated stack sizes for {count} items.");
        }
        
        private int CalculateNewStack(ItemDefinition def, int original, bool useOnlyMultipliers)
        {
            if (!useOnlyMultipliers && _config.ItemOverrides.TryGetValue(def.shortname, out int specificLimit) && specificLimit > 0)
            {
                return specificLimit;
            }

            float multiplier = IsValidMultiplier(_config.GlobalMultiplier) ? _config.GlobalMultiplier : 1f;
            if (_config.CategoryMultipliers.TryGetValue(def.category.ToString(), out float categoryMultiplier) && IsValidMultiplier(categoryMultiplier))
                multiplier = categoryMultiplier;

            if (multiplier == 1f) return Math.Max(1, original);

            return (int)Math.Max(1d, Math.Min(original * multiplier, 2000000000d));
        }

        private static bool IsValidMultiplier(float value) => value > 0f && !float.IsInfinity(value);

        private void RevertStackSizes() {
            foreach (var kvp in _originalStackSizes) {
                var def = ItemManager.FindItemDefinition(kvp.Key);
                if (def != null) def.stackable = kvp.Value;
            }
            _originalStackSizes.Clear();
        }
        #endregion
    }
}
