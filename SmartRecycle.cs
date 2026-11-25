using System;
using System.Collections.Generic;
using System.Linq;
using System.IO; // ファイル操作用
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Smart Recycle", "Yaruani", "1.3.5")]
    [Description("Intelligent recycler controller with batch processing, safety stops, and visual feedback.")]
    class SmartRecycle : RustPlugin
    {
        #region Configuration

        private static Configuration _config;

        private class Configuration
        {
            [JsonProperty("Plugin Enabled")] public bool PluginEnabled = true;
            [JsonProperty("Use Permission")] public bool UsePermission = true;
            [JsonProperty("Enable Chat Notifications")] public bool EnableChatNotifications = true;
            
            [JsonProperty("Enable Visual Effects")] public bool EnableVisualEffects = true;
            [JsonProperty("Enable Sound Effects")] public bool EnableSoundEffects = true;
            
            [JsonProperty("Visual Effect Prefab")] 
            public string VisualEffectPrefab = "assets/bundled/prefabs/fx/impacts/blunt/metal/metal1.prefab";
            
            [JsonProperty("Sound Effect Prefab")] 
            public string SoundEffectPrefab = "assets/bundled/prefabs/fx/mining/pickaxe_hit_metal_ore.prefab";

            [JsonProperty("Recycle Speed Multiplier (Batch Size)")] public float SpeedMultiplier = 1.0f;
            [JsonProperty("Recycle Efficiency (0.0 - 1.0)")] public float RecycleEfficiency = 0.6f;
            [JsonProperty("Safezone Efficiency (0.0 - 1.0)")] public float SafezoneEfficiency = 0.4f;
            [JsonProperty("Debug Mode (Logs)")] public bool Debug = false;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                // --- 移行ロジック ---
                if (!File.Exists(Config.Filename))
                {
                    string oldConfigPath = Path.Combine(Interface.Oxide.ConfigDirectory, "RecycleManager.json");
                    if (File.Exists(oldConfigPath))
                    {
                        PrintWarning("Detected 'RecycleManager.json'. Resetting to SmartRecycle defaults and Enabling plugin.");
                        _config = new Configuration();
                        _config.PluginEnabled = true;
                        SaveConfig();
                    }
                }

                // Check for old keys inside the current file
                var rawConfig = Config.ReadObject<Dictionary<string, object>>();
                if (rawConfig != null)
                {
                    var json = JsonConvert.SerializeObject(rawConfig);
                    if (json.Contains("Custom recycle speed"))
                    {
                        PrintWarning("Detected legacy configuration inside file. Resetting to defaults.");
                        LoadDefaultConfig();
                        SaveConfig();
                    }
                    // Add missing keys if updated
                    if (!rawConfig.ContainsKey("Visual Effect Prefab"))
                    {
                        SaveConfig();
                    }
                }
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

        #region Variables
        public static SmartRecycle _instance;
        private const string PermissionUse = "smartrecycle.use";
        #endregion

        #region Init
        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PermissionUse, this);
        }

        private void Unload()
        {
            foreach (var recycler in BaseNetworkable.serverEntities.OfType<Recycler>()) {
                var component = recycler.GetComponent<RecycleController>();
                if (component != null) {
                    UnityEngine.Object.Destroy(component);
                    if (recycler.IsOn()) {
                        recycler.CancelInvoke(recycler.RecycleThink);
                        recycler.InvokeRepeating(recycler.RecycleThink, 5f, 5f);
                    }
                }
            }
            _instance = null;
        }
        #endregion

        #region Commands
        [ChatCommand("recyclespeed")]
        private void CmdRecycleSpeed(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (!_config.PluginEnabled) { 
                player.ChatMessage($"<color=#red>[SmartRecycle]</color> Plugin is DISABLED."); 
                return; 
            }

            if (args.Length == 0) {
                player.ChatMessage($"<color=#00ffff>[SmartRecycle]</color> Speed: x{_config.SpeedMultiplier}");
                player.ChatMessage($"<color=#00ffff>[SmartRecycle]</color> Visuals: {(_config.EnableVisualEffects ? "<color=#00ff00>ON</color>" : "<color=#ff0000>OFF</color>")} | Sound: {(_config.EnableSoundEffects ? "<color=#00ff00>ON</color>" : "<color=#ff0000>OFF</color>")}");
                player.ChatMessage($"<color=#00ffff>[SmartRecycle]</color> Usage: /recyclespeed <number> | visual | sound");
                return;
            }

            string arg = args[0].ToLower();

            if (float.TryParse(arg, out float newSpeed)) {
                if (newSpeed < 1.0f) newSpeed = 1.0f;
                _config.SpeedMultiplier = newSpeed;
                SaveConfig();
                RestartAllRecyclers();

                string msg = $"<color=#00ffff>[SmartRecycle]</color> Speed updated to x{newSpeed}.";
                if (_config.EnableChatNotifications) Server.Broadcast(msg);
                else player.ChatMessage(msg);
                return;
            }

            if (arg == "visual" || arg == "v") {
                _config.EnableVisualEffects = !_config.EnableVisualEffects;
                SaveConfig();
                player.ChatMessage($"<color=#00ffff>[SmartRecycle]</color> Visual Effects: {(_config.EnableVisualEffects ? "<color=#00ff00>ENABLED</color>" : "<color=#ff0000>DISABLED</color>")}");
                return;
            }

            if (arg == "sound" || arg == "s") {
                _config.EnableSoundEffects = !_config.EnableSoundEffects;
                SaveConfig();
                player.ChatMessage($"<color=#00ffff>[SmartRecycle]</color> Sound Effects: {(_config.EnableSoundEffects ? "<color=#00ff00>ENABLED</color>" : "<color=#ff0000>DISABLED</color>")}");
                return;
            }

            player.ChatMessage("<color=#red>Error:</color> Invalid argument.");
        }

        private void RestartAllRecyclers()
        {
            foreach (var recycler in BaseNetworkable.serverEntities.OfType<Recycler>()) {
                var component = recycler.GetComponent<RecycleController>();
                if (component != null) component.ForceRestart();
            }
        }
        #endregion

        #region Hooks
        private void OnServerInitialized()
        {
            if (_config.PluginEnabled) PrintWarning("SmartRecycle is ENABLED.");
            else PrintError("SmartRecycle is DISABLED in config.");
            if (!_config.PluginEnabled) return;
            foreach (var recycler in BaseNetworkable.serverEntities.OfType<Recycler>()) {
                if (recycler.IsOn()) OnRecyclerToggle(recycler, null);
            }
        }
        
        private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
        {
            if (!_config.PluginEnabled) return;
            if (recycler == null) return;

            PrintDebug($"Toggle Detected. ID: {recycler.net.ID} | Player: {player?.displayName ?? "null"}");

            if (player != null && _config.UsePermission) {
                if (!permission.UserHasPermission(player.UserIDString, PermissionUse)) {
                    PrintDebug("Permission denied. Using vanilla speed.");
                    return; 
                }
            }

            NextTick(() => {
                if (recycler == null || recycler.IsDestroyed) return;
                var component = recycler.GetComponent<RecycleController>() ?? recycler.gameObject.AddComponent<RecycleController>();
                if (recycler.IsOn()) {
                    PrintDebug("Starting Batch Recycle.");
                    recycler.CancelInvoke(recycler.RecycleThink);
                    component.StartBatchRecycle();
                } else {
                    component.StopBatchRecycle();
                }
            });
        }
        #endregion

        #region Helpers
        private void PrintDebug(string message) {
            if (_config.Debug) Puts($"[Debug] {message}");
        }
        #endregion

        #region Controller
        public class RecycleController : FacepunchBehaviour
        {
            private Recycler _recycler;
            private Recycler RecyclerEntity => _recycler ?? (_recycler = GetComponent<Recycler>());
            private SmartRecycle _plugin;

            private void Awake()
            {
                _plugin = SmartRecycle._instance;
            }

            public void StartBatchRecycle() {
                StopBatchRecycle();
                InvokeRepeating(BatchRecycleProcess, 0.5f, 0.5f);
            }
            public void StopBatchRecycle() => CancelInvoke(BatchRecycleProcess);
            public void ForceRestart() { if (RecyclerEntity.IsOn()) StartBatchRecycle(); }

            private void StopAndTurnOff(string reason) {
                StopBatchRecycle();
                RecyclerEntity.StopRecycling();
                _plugin?.PrintDebug($"Stopped: {reason}");
            }

            private void PlayEffects() {
                if (_plugin == null) return;
                Vector3 pos = RecyclerEntity.transform.position + new Vector3(0, 1.5f, 0);

                if (SmartRecycle._config.EnableVisualEffects && !string.IsNullOrEmpty(SmartRecycle._config.VisualEffectPrefab)) 
                    Effect.server.Run(SmartRecycle._config.VisualEffectPrefab, pos);
                
                if (SmartRecycle._config.EnableSoundEffects && !string.IsNullOrEmpty(SmartRecycle._config.SoundEffectPrefab)) 
                    Effect.server.Run(SmartRecycle._config.SoundEffectPrefab, pos);
            }

            private void BatchRecycleProcess() {
                if (RecyclerEntity == null || !RecyclerEntity.IsOn()) { StopBatchRecycle(); return; }
                Item inputItem = null;
                for (int i = 0; i < 6; i++) {
                    var item = RecyclerEntity.inventory.GetSlot(i);
                    if (item != null) { inputItem = item; break; }
                }
                if (inputItem == null) { StopAndTurnOff("Empty Input"); return; }
                if (inputItem.info.Blueprint == null) { StopAndTurnOff("Item not recyclable"); return; }

                int currentAmount = inputItem.amount;
                int desiredBatch = (int)SmartRecycle._config.SpeedMultiplier;
                int actualBatch = Mathf.Min(desiredBatch, currentAmount);
                if (actualBatch <= 0) return;

                float efficiency = RecyclerEntity.IsSafezoneRecycler() ? SmartRecycle._config.SafezoneEfficiency : SmartRecycle._config.RecycleEfficiency;
                var blueprint = inputItem.info.Blueprint;
                List<Item> productsToCreate = new List<Item>();

                foreach (var ingredient in blueprint.ingredients) {
                    float baseAmount = (float)ingredient.amount * (float)actualBatch * efficiency;
                    if (inputItem.hasCondition) baseAmount *= inputItem.conditionNormalized;
                    int resultAmount = Mathf.FloorToInt(baseAmount);
                    if (resultAmount < 1) continue; 
                    Item product = ItemManager.Create(ingredient.itemDef, resultAmount);
                    if (product != null) productsToCreate.Add(product);
                }
                if (blueprint.scrapFromRecycle > 0) {
                    float baseScrap = (float)blueprint.scrapFromRecycle * (float)actualBatch * efficiency;
                    if (inputItem.hasCondition) baseScrap *= inputItem.conditionNormalized;
                    int scrapAmount = Mathf.FloorToInt(baseScrap);
                    if (scrapAmount >= 1) {
                        Item scrap = ItemManager.CreateByName("scrap", scrapAmount);
                        if (scrap != null) productsToCreate.Add(scrap);
                    }
                }

                if (productsToCreate.Count == 0)
                {
                    StopAndTurnOff("No recyclable output");
                    return;
                }

                bool spaceCheckPassed = true;
                foreach (var product in productsToCreate) {
                    if (!RecyclerEntity.MoveItemToOutput(product)) { spaceCheckPassed = false; break; }
                }
                if (!spaceCheckPassed) {
                    foreach (var product in productsToCreate) { if (product != null) product.Remove(); }
                    StopAndTurnOff("Output Full");
                    return;
                }
                if (actualBatch >= inputItem.amount) inputItem.Remove();
                else inputItem.UseItem(actualBatch);
                PlayEffects();
            }
        }
        #endregion
    }
}
