using System;
using System.Collections.Generic;
using System.Linq;
using System.IO; // ファイル操作用
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Smart Conveyor", "Yaruani", "1.3.4")]
    [Description("Intelligent industrial conveyor controller with auto-migration from ConveyorStacks.")]
    class SmartConveyor : RustPlugin
    {
        #region Configuration

        private static Configuration _config;

        private class Configuration
        {
            [JsonProperty("Plugin Enabled")] public bool PluginEnabled = true;
            [JsonProperty("Use Permission")] public bool UsePermission = true;
            [JsonProperty("Enable Chat Notifications")] public bool EnableChatNotifications = true;
            [JsonProperty("Transfer Speed Multiplier")] public float SpeedMultiplier = 100.0f;
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
                // SmartConveyorのコンフィグファイルがまだ存在しない（初回起動）場合のみ実行
                if (!File.Exists(Config.Filename))
                {
                    string oldConfigPath = Path.Combine(Interface.Oxide.ConfigDirectory, "ConveyorStacks.json");
                    if (File.Exists(oldConfigPath))
                    {
                        PrintWarning("Found old 'ConveyorStacks.json'. Attempting to migrate settings...");
                        try
                        {
                            var oldConfig = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(oldConfigPath));
                            
                            if (oldConfig.ContainsKey("Default transfer max stack amount"))
                            {
                                int oldAmount = Convert.ToInt32(oldConfig["Default transfer max stack amount"]);
                                float newMultiplier = (float)oldAmount / 60.0f;
                                if (newMultiplier < 1.0f) newMultiplier = 1.0f;

                                _config.SpeedMultiplier = newMultiplier;
                                // 初回なのでPluginEnabledはデフォルト(true)のまま

                                PrintWarning($"Migration successful! Converted amount {oldAmount} to {newMultiplier:0.##}x multiplier.");
                                SaveConfig();
                            }
                        }
                        catch (Exception ex)
                        {
                            PrintError($"Migration failed: {ex.Message}");
                        }
                    }
                }
                // ------------------
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
        private const string PermissionUse = "smartconveyor.use";
        private readonly Dictionary<IndustrialConveyor, (int Original, int Applied)> changedConveyors =
            new Dictionary<IndustrialConveyor, (int Original, int Applied)>();
        #endregion

        #region Commands
        [ChatCommand("conveyorspeed")]
        private void CmdConveyorSpeed(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (!_config.PluginEnabled)
            {
                player.ChatMessage($"<color=#red>[SmartConveyor]</color> Plugin is DISABLED in config.");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage($"<color=#00ffff>[SmartConveyor]</color> Current Multiplier: x{_config.SpeedMultiplier} (approx. {(int)(60 * _config.SpeedMultiplier)} items/move)");
                return;
            }

            if (float.TryParse(args[0], out float newSpeed) && !float.IsNaN(newSpeed) && !float.IsInfinity(newSpeed))
            {
                if (newSpeed < 1.0f) newSpeed = 1.0f;

                _config.SpeedMultiplier = newSpeed;
                SaveConfig();

                int count = 0;
                foreach (var conveyor in BaseNetworkable.serverEntities.OfType<IndustrialConveyor>())
                {
                    if (ApplyStackSize(conveyor)) count++;
                }

                string msg = $"<color=#00ffff>[SmartConveyor]</color> Speed updated to x{newSpeed}. Updated {count} conveyors.";
                PrintDebug($"Config updated via command. Updated {count} entities.");

                if (_config.EnableChatNotifications) Server.Broadcast(msg);
                else player.ChatMessage(msg);
            }
            else
            {
                player.ChatMessage("<color=#red>Error:</color> Invalid number. Usage: /conveyorspeed 100");
            }
        }
        #endregion

        #region Hooks
        private void OnServerInitialized()
        {
            permission.RegisterPermission(PermissionUse, this);

            if (_config.PluginEnabled) 
                PrintWarning($"SmartConveyor is ENABLED. Multiplier: {_config.SpeedMultiplier}x");
            else 
                PrintError("SmartConveyor is DISABLED in config.");

            if (!_config.PluginEnabled) return;

            foreach (var conveyor in BaseNetworkable.serverEntities.OfType<IndustrialConveyor>())
            {
                ApplyStackSize(conveyor);
            }
        }

        private void Unload()
        {
            foreach (var change in changedConveyors)
                RestoreStackSize(change.Key, change.Value);
            changedConveyors.Clear();
        }

        private void OnEntityKill(IndustrialConveyor conveyor)
        {
            if (!changedConveyors.ContainsKey(conveyor)) return;
            NextTick(() =>
            {
                if (conveyor == null || conveyor.IsDestroyed) changedConveyors.Remove(conveyor);
            });
        }

        private void OnEntitySpawned(IndustrialConveyor conveyor)
        {
            if (!_config.PluginEnabled) return;
            
            NextTick(() => {
                if (conveyor == null || conveyor.IsDestroyed) return;
                ApplyStackSize(conveyor);
            });
        }
        #endregion

        #region Helpers
        private bool CanUse(ulong ownerId)
        {
            if (!_config.UsePermission) return true;
            if (ownerId == 0) return false;
            return permission.UserHasPermission(ownerId.ToString(), PermissionUse);
        }

        private bool ApplyStackSize(IndustrialConveyor conveyor)
        {
            bool wasChanged = changedConveyors.TryGetValue(conveyor, out var change);
            if (!CanUse(conveyor.OwnerID))
            {
                if (wasChanged)
                {
                    RestoreStackSize(conveyor, change);
                    changedConveyors.Remove(conveyor);
                }
                return false;
            }

            float multiplier = _config.SpeedMultiplier;
            if (multiplier <= 0f || float.IsNaN(multiplier) || float.IsInfinity(multiplier)) return false;
            int targetAmount = (int)Math.Max(1d, Math.Min(60 * multiplier, 1000000d));
            int current = conveyor.MaxStackSizePerMove;
            if (current == targetAmount) return false;

            int original = wasChanged && current == change.Applied ? change.Original : current;
            changedConveyors[conveyor] = (original, targetAmount);
            conveyor.MaxStackSizePerMove = targetAmount;
            PrintDebug($"Updated Conveyor {conveyor.net.ID}: {targetAmount} items/move");
            return true;
        }

        private static void RestoreStackSize(IndustrialConveyor conveyor, (int Original, int Applied) change)
        {
            if (conveyor != null && !conveyor.IsDestroyed && conveyor.MaxStackSizePerMove == change.Applied)
                conveyor.MaxStackSizePerMove = change.Original;
        }

        // 修正: static を削除して Puts を使えるようにしました
        private void PrintDebug(string message)
        {
            if (_config.Debug) Puts($"[Debug] {message}");
        }
        #endregion
    }
}
