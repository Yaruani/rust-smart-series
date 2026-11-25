using System;
using System.Collections.Generic;
using System.Linq;
using System.IO; // ファイル操作用
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Smart Smelt", "Yaruani", "1.3.2")]
    [Description("Intelligent furnace speed controller with batch processing and safety stops.")]
    class SmartSmelt : RustPlugin
    {
        #region Variables
        private static SmartSmelt _instance;
        private const string PermissionUse = "smartsmelt.use";
        private readonly Dictionary<BaseOven, (bool WasOn, bool Allowed)> pendingToggles =
            new Dictionary<BaseOven, (bool WasOn, bool Allowed)>();
        #endregion

        #region Configuration
        private static Configuration _config;

        private class Configuration
        {
            [JsonProperty("Plugin Enabled")] public bool PluginEnabled = true; // Default ON
            [JsonProperty("Use Permission")] public bool UsePermission = true;
            [JsonProperty("Enable Chat Notifications")] public bool EnableChatNotifications = true;
            
            [JsonProperty("Speed Multipliers")] 
            public Dictionary<string, float> SpeedMultipliers = new Dictionary<string, float> { { "global", 10.0f } };
            
            [JsonProperty("Fuel Usage Speed Multipliers")] 
            public Dictionary<string, float> FuelSpeedMultipliers = new Dictionary<string, float> { { "global", 1.0f } };
            
            [JsonProperty("Fuel Usage Multipliers")] 
            public Dictionary<string, int> FuelUsageMultipliers = new Dictionary<string, int> { { "global", 1 } };
            
            [JsonProperty("Output Multipliers")] 
            public Dictionary<string, Dictionary<string, float>> OutputMultipliers = new Dictionary<string, Dictionary<string, float>> { { "global", new Dictionary<string, float> { { "charcoal", 4.0f } } } };
            
            [JsonProperty("Whitelist")] public Dictionary<string, List<string>> Whitelist = new Dictionary<string, List<string>>();
            [JsonProperty("Blacklist")] public Dictionary<string, List<string>> Blacklist = new Dictionary<string, List<string>>();
            [JsonProperty("Smelting Frequencies")] public Dictionary<string, int> SmeltingFrequencies = new Dictionary<string, int> { { "global", 1 } };
            [JsonProperty("Debug Mode (Logs)")] public bool Debug = false;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                var rawConfig = Config.ReadObject<Dictionary<string, object>>();
                if (rawConfig != null)
                {
                    var json = JsonConvert.SerializeObject(rawConfig);
                    // QuickSmelt時代の設定("furnace.shortname")や、古いSmartSmelt設定を検知
                    if (json.Contains("furnace.shortname"))
                    {
                        PrintWarning("Detected old configuration format. Resetting to SmartSmelt defaults.");
                        LoadDefaultConfig();
                        SaveConfig();
                        return;
                    }
                }

                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();
                SaveConfig();
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
        [ChatCommand("smeltspeed")]
        private void CmdSmeltSpeed(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;
            if (!_config.PluginEnabled) { player.ChatMessage($"<color=#red>[SmartSmelt]</color> Plugin is currently DISABLED in config."); return; }

            if (args.Length == 0)
            {
                float current = 1.0f;
                _config.SpeedMultipliers.TryGetValue("global", out current);
                player.ChatMessage($"<color=#00ffff>[SmartSmelt]</color> Current Global Speed: x{current}");
                return;
            }

            if (float.TryParse(args[0], out float newSpeed))
            {
                if (newSpeed <= 0) newSpeed = 1.0f;
                
                if (_config.SpeedMultipliers.ContainsKey("global")) _config.SpeedMultipliers["global"] = newSpeed;
                else _config.SpeedMultipliers.Add("global", newSpeed);
                
                SaveConfig();

                int count = 0;
                // Optimized search
                foreach (var oven in BaseNetworkable.serverEntities.OfType<BaseOven>())
                {
                    var component = oven.GetComponent<FurnaceController>();
                    if (component != null) { component.UpdateSettings(); count++; }
                }

                string msg = $"<color=#00ffff>[SmartSmelt]</color> Speed updated to x{newSpeed}. Refreshed settings for {count} furnaces.";
                PrintDebug($"Config updated via command. Refreshed settings for {count} controllers.");
                
                if (_config.EnableChatNotifications) Server.Broadcast(msg);
                else player.ChatMessage(msg);
            }
            else
            {
                player.ChatMessage("<color=#red>Error:</color> Invalid number. Usage: /smeltspeed 10");
            }
        }
        #endregion

        #region Hooks
        private void OnServerInitialized()
        {
            _instance = this;
            permission.RegisterPermission(PermissionUse, this);

            if (_config.PluginEnabled) 
                PrintWarning("SmartSmelt is ENABLED.");
            else 
                PrintError("SmartSmelt is DISABLED in config.");

            if (!_config.PluginEnabled) return;

            timer.Once(1f, () => {
                foreach (var oven in BaseNetworkable.serverEntities.OfType<BaseOven>())
                    OnEntitySpawned(oven);
            });
        }

        private void Unload()
        {
            pendingToggles.Clear();
            foreach (var oven in BaseNetworkable.serverEntities.OfType<BaseOven>())
            {
                var component = oven.GetComponent<FurnaceController>();
                if (component != null)
                {
                    if (component.IsCustomCooking)
                    {
                        component.CancelCustomCooking();
                        if (oven.IsOn()) oven.StartCooking();
                    }
                    UnityEngine.Object.Destroy(component);
                }
            }
            _instance = null;
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (!_config.PluginEnabled) return;
            if (entity is BaseOven oven && !(oven is BaseFuelLightSource) && oven.GetComponent<FurnaceController>() == null)
            {
                oven.gameObject.AddComponent<FurnaceController>().Initialize();
            }
        }

        private void OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (!_config.PluginEnabled || oven == null || oven is BaseFuelLightSource) return;

            // The hook precedes Rust's switch checks and state change. Settle the previous request
            // before recording another one for the same oven in this tick.
            if (pendingToggles.ContainsKey(oven)) ApplyOvenToggle(oven);
            else NextTick(() => ApplyOvenToggle(oven));

            bool allowed = !_config.UsePermission ||
                (player != null && permission.UserHasPermission(player.UserIDString, PermissionUse));
            pendingToggles[oven] = (oven.IsOn(), allowed);
        }

        private void ApplyOvenToggle(BaseOven oven)
        {
            if (!pendingToggles.TryGetValue(oven, out var request)) return;
            pendingToggles.Remove(oven);
            if (oven == null || oven.IsDestroyed || oven.IsOn() == request.WasOn) return;

            var component = oven.GetComponent<FurnaceController>();
            if (component == null) return;

            if (oven.IsOn() && request.Allowed) component.StartCooking();
            else component.CancelCustomCooking();
        }
        #endregion

        #region Helpers
        private void PrintDebug(string message)
        {
            if (_config.Debug) Puts($"[Debug] {message}");
        }
        #endregion

        #region Controller
        public class FurnaceController : FacepunchBehaviour
        {
            private BaseOven _oven;
            private int _ticks;
            private float _batchMultiplier;
            private float _fuelRateMultiplier;
            private int _fuelPerItemMultiplier;
            private int _smeltingFrequency;
            private Dictionary<string, float> _outputModifiers;
            private List<string> _blacklist;
            private List<string> _whitelist;
            private SmartSmelt _plugin;

            private BaseOven Furnace => _oven ?? (_oven = GetComponent<BaseOven>());

            private void Awake()
            {
                _plugin = SmartSmelt._instance;
            }

            public void Initialize()
            {
                UpdateSettings();
                if (!SmartSmelt._config.UsePermission && Furnace.IsOn()) StartCooking();
            }

            public void UpdateSettings()
            {
                string name = Furnace.ShortPrefabName;
                _batchMultiplier = GetCfg(SmartSmelt._config.SpeedMultipliers, name, 1.0f);
                _fuelRateMultiplier = GetCfg(SmartSmelt._config.FuelSpeedMultipliers, name, 1.0f);
                _fuelPerItemMultiplier = GetCfg(SmartSmelt._config.FuelUsageMultipliers, name, 1);
                _smeltingFrequency = GetCfg(SmartSmelt._config.SmeltingFrequencies, name, 1);
                
                if (!SmartSmelt._config.OutputMultipliers.TryGetValue(name, out _outputModifiers)) SmartSmelt._config.OutputMultipliers.TryGetValue("global", out _outputModifiers);
                if (!SmartSmelt._config.Blacklist.TryGetValue(name, out _blacklist)) SmartSmelt._config.Blacklist.TryGetValue("global", out _blacklist);
                if (!SmartSmelt._config.Whitelist.TryGetValue(name, out _whitelist)) SmartSmelt._config.Whitelist.TryGetValue("global", out _whitelist);
            }

            public bool IsCustomCooking => Furnace.IsInvoking(Cook);
            public void CancelCustomCooking() => Furnace.CancelInvoke(Cook);

            private T GetCfg<T>(Dictionary<string, T> dict, string key, T def) => dict.TryGetValue(key, out var v) ? v : (dict.TryGetValue("global", out v) ? v : def);

            private float GetOutputMultiplier(string shortname)
            {
                if (_outputModifiers == null) return 1.0f;
                if (_outputModifiers.ContainsKey(shortname)) return _outputModifiers[shortname];
                if (_outputModifiers.ContainsKey("global")) return _outputModifiers["global"];
                return 1.0f;
            }

            public void StartCooking()
            {
                if (FindBurnable() == null) return;
                StopCooking();
                Furnace.inventory.temperature = Furnace.cookingTemperature;
                Furnace.UpdateAttachmentTemperature();
                Furnace.SetFlag(BaseEntity.Flags.On, true);
                Furnace.InvokeRepeating(Cook, 0.5f, 0.5f);
            }

            public void StopCooking() 
            { 
                CancelCustomCooking();
                Furnace.StopCooking(); 
            }

            private void StopAndTurnOff(string reason)
            {
                StopCooking();
                Furnace.SetFlag(BaseEntity.Flags.On, false);
                _plugin?.PrintDebug($"Stopped {Furnace.ShortPrefabName}: {reason}");
            }

            private void Cook()
            {
                if (!Furnace.IsOn())
                {
                    CancelCustomCooking();
                    return;
                }

                var fuel = FindBurnable();
                if (Interface.CallHook("OnOvenCook", this, fuel) != null || fuel == null) { StopCooking(); return; }

                SmeltItems();
                if (!Furnace.IsOn()) return;

                var burnable = fuel.info.GetComponent<ItemModBurnable>();
                // Fuel consumption scales with batch multiplier (burning 10x items uses 10x fuel per tick)
                float consumeAmount = 0.5f * (Furnace.cookingTemperature / 200f) * _fuelRateMultiplier * _batchMultiplier;
                fuel.fuel -= consumeAmount;

                if (!fuel.HasFlag(global::Item.Flag.OnFire)) { fuel.SetFlag(global::Item.Flag.OnFire, true); fuel.MarkDirty(); }

                if (fuel.fuel <= 0f) ConsumeFuel(fuel, burnable);

                _ticks++;
                Interface.CallHook("OnOvenCooked", this, fuel, Furnace.GetSlot(BaseEntity.Slot.FireMod));
            }

            private void ConsumeFuel(Item fuel, ItemModBurnable burnable)
            {
                if (Interface.CallHook("OnFuelConsume", _oven, fuel, burnable) != null) return;

                if (Furnace.allowByproductCreation && burnable.byproductItem != null && Random.value > burnable.byproductChance)
                {
                    float outMod = GetOutputMultiplier(burnable.byproductItem.shortname);
                    int amount = (int)(burnable.byproductAmount * outMod * _batchMultiplier);
                    if (amount > 0)
                    {
                        var item = ItemManager.Create(burnable.byproductItem, amount);
                        if (!item.MoveToContainer(Furnace.inventory)) 
                        { 
                            item.Remove(); 
                            StopAndTurnOff("Output Full (Charcoal)");
                            return;
                        }
                    }
                }

                if (fuel.amount <= _fuelPerItemMultiplier) fuel.Remove();
                else { fuel.UseItem(_fuelPerItemMultiplier); fuel.fuel = burnable.fuelAmount; fuel.MarkDirty(); }
                
                Interface.CallHook("OnFuelConsumed", _oven, fuel, burnable);
            }

            private void SmeltItems()
            {
                if (_ticks % _smeltingFrequency != 0) return;

                for (var i = Furnace.inventory.itemList.Count - 1; i >= 0; i--)
                {
                    var item = Furnace.inventory.itemList[i];
                    var cookable = item.info.GetComponent<ItemModCookable>();
                    if (cookable == null) continue;

                    if ((_blacklist != null && _blacklist.Contains(item.info.shortname)) || 
                        (_whitelist != null && _whitelist.Count > 0 && !_whitelist.Contains(item.info.shortname))) continue;

                    if (item.temperature < cookable.lowTemp || item.temperature > cookable.highTemp)
                    {
                        if (item.HasFlag(global::Item.Flag.Cooking)) { item.SetFlag(global::Item.Flag.Cooking, false); item.MarkDirty(); }
                        continue;
                    }

                    if (cookable.setCookingFlag && !item.HasFlag(global::Item.Flag.Cooking)) { item.SetFlag(global::Item.Flag.Cooking, true); item.MarkDirty(); }

                    int consume = (int)(Furnace.GetSmeltingSpeed() * _batchMultiplier);
                    consume = Math.Min(consume, item.amount);
                    if (consume <= 0) continue;
                    
                    if (cookable.becomeOnCooked != null)
                    {
                        float outMod = GetOutputMultiplier(cookable.becomeOnCooked.shortname);
                        int produce = (int)(cookable.amountOfBecome * consume * outMod);
                        
                        if (produce > 0)
                        {
                            var product = ItemManager.Create(cookable.becomeOnCooked, produce);
                            if (product != null)
                            {
                                if (!product.MoveToContainer(item.parent)) 
                                { 
                                    product.Remove();
                                    StopAndTurnOff("Output Full (Smelted Item)");
                                    return;
                                }
                            }
                        }
                    }

                    if (item.amount > consume) { item.amount -= consume; item.MarkDirty(); }
                    else item.Remove();
                }
            }

            private Item FindBurnable()
            {
                var h = Interface.Call<Item>("OnFindBurnable", _oven);
                if (h != null) return h;
                foreach (var item in Furnace.inventory.itemList) if (Furnace.IsBurnableItem(item)) return item;
                return null;
            }
        }
        #endregion
    }
}
