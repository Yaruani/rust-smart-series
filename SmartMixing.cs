using System;
using System.Collections.Generic;
using System.Linq;
using System.IO; // 追加: ファイル操作用
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Smart Mixing", "Yaruani", "1.3.2")]
    [Description("Intelligent mixing table controller with batch processing and safety stops.")]
    class SmartMixing : RustPlugin
    {
        #region Configuration

        private static Configuration _config;

        private class Configuration
        {
            [JsonProperty("Plugin Enabled")] public bool PluginEnabled = true;
            [JsonProperty("Use Permission")] public bool UsePermission = true;
            [JsonProperty("Enable Chat Notifications")] public bool EnableChatNotifications = true;
            [JsonProperty("Speed Multiplier (Batch Size)")] public float SpeedMultiplier = 200.0f;
            [JsonProperty("Debug Mode (Logs)")] public bool Debug = false;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                // --- 移行ロジック (MixingSpeed -> SmartMixing) ---
                // Configファイルがまだ生成されていない（初回）場合のみチェック
                if (!File.Exists(Config.Filename))
                {
                    string oldConfigPath = Path.Combine(Interface.Oxide.ConfigDirectory, "MixingSpeed.json");
                    if (File.Exists(oldConfigPath))
                    {
                        // 古いプラグインはロジックが違う（時間短縮 vs バッチ処理）ため、
                        // 値を引き継ぐと危険なので、検知だけして「SmartMixingのデフォルト」で初期化します。
                        PrintWarning("Detected 'MixingSpeed.json'. Old configuration is incompatible with Batch logic.");
                        PrintWarning("Resetting to SmartMixing defaults (200x Batch Speed) for safety.");
                        
                        LoadDefaultConfig();
                        SaveConfig();
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

        #region Variables
        public static SmartMixing _instance;
        private const string PermissionUse = "smartmixing.use";
        private static readonly MethodInfo VanillaMixTick = typeof(MixingTable).GetMethod(
            "TickMix", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly FieldInfo VanillaProductionItem = typeof(MixingTable).GetField(
            "currentProductionItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        #endregion

        #region Init
        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PermissionUse, this);
        }

        private void Unload()
        {
            foreach (var table in BaseNetworkable.serverEntities.OfType<MixingTable>())
            {
                var component = table.GetComponent<MixingController>();
                if (component != null)
                {
                    if (component.IsBatchMixing)
                    {
                        component.StopBatchMixing();
                        table.StopMixing();
                    }
                    UnityEngine.Object.Destroy(component);
                }
            }
            _instance = null;
        }
        #endregion

        #region Commands
        [ChatCommand("mixspeed")]
        private void CmdMixSpeed(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (!_config.PluginEnabled)
            {
                player.ChatMessage($"<color=#red>[SmartMixing]</color> Plugin is currently DISABLED in config.");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage($"<color=#00ffff>[SmartMixing]</color> Current Speed Multiplier: x{_config.SpeedMultiplier}");
                return;
            }

            if (float.TryParse(args[0], out float newSpeed))
            {
                if (newSpeed <= 0) newSpeed = 1.0f;

                _config.SpeedMultiplier = newSpeed;
                SaveConfig();

                int count = 0;
                foreach (var table in BaseNetworkable.serverEntities.OfType<MixingTable>())
                {
                    var component = table.GetComponent<MixingController>();
                    if (component == null || !component.IsBatchMixing || !table.IsOn()) continue;
                    component.StartBatchMixing();
                    count++;
                }

                string msg = $"<color=#00ffff>[SmartMixing]</color> Speed updated to x{newSpeed}. Restarted {count} tables.";
                PrintDebug($"Config updated via command. Restarted {count} controllers.");

                if (_config.EnableChatNotifications) Server.Broadcast(msg);
                else player.ChatMessage(msg);
            }
            else
            {
                player.ChatMessage("<color=#red>Error:</color> Invalid number. Usage: /mixspeed 200");
            }
        }
        #endregion

        #region Hooks
        private void OnServerInitialized()
        {
            if (_config.PluginEnabled)
                PrintWarning("SmartMixing is ENABLED.");
            else
                PrintError("SmartMixing is DISABLED in config.");

            if (!_config.PluginEnabled) return;

            if (VanillaMixTick == null || VanillaProductionItem == null)
            {
                PrintWarning("Rust mixing internals are unavailable. Mixing tables will use vanilla processing.");
                return;
            }
            foreach (var table in BaseNetworkable.serverEntities.OfType<MixingTable>())
            {
                if (table.IsOn()) UpdateMixing(table);
            }
        }

        private void OnMixingTableToggle(MixingTable table, BasePlayer player)
        {
            if (!_config.PluginEnabled || table == null || VanillaMixTick == null || VanillaProductionItem == null) return;

            // The hook runs before Rust applies the requested switch state.
            NextTick(() => UpdateMixing(table));
        }

        private void UpdateMixing(MixingTable table)
        {
            if (table == null || table.IsDestroyed) return;

            var component = table.GetComponent<MixingController>();
            var recipe = table.currentRecipe;
            var starter = table.MixStartingPlayer;
            if (!table.IsOn() || recipe == null || recipe.ProducedItem == null || recipe.ProducedItemCount <= 0 ||
                (_config.UsePermission && starter != null && !permission.UserHasPermission(starter.UserIDString, PermissionUse)))
            {
                component?.StopBatchMixing();
                return;
            }

            table.CancelInvoke((Action)VanillaMixTick.CreateDelegate(typeof(Action), table));
            if (component == null) component = table.gameObject.AddComponent<MixingController>();
            if (!component.IsBatchMixing) component.StartBatchMixing();
        }
        #endregion

        #region Helpers
        private void PrintDebug(string message)
        {
            if (_config.Debug) Puts($"[Debug] {message}");
        }
        #endregion

        #region Controller
        public class MixingController : FacepunchBehaviour
        {
            private MixingTable _table;
            private MixingTable Table => _table ?? (_table = GetComponent<MixingTable>());
            private SmartMixing _plugin;

            private void Awake()
            {
                _plugin = SmartMixing._instance;
            }

            public void StartBatchMixing()
            {
                StopBatchMixing();
                InvokeRepeating(BatchMixProcess, 0.5f, 0.5f);
            }

            public void StopBatchMixing() => CancelInvoke(BatchMixProcess);

            public bool IsBatchMixing => IsInvoking(BatchMixProcess);

            private void StopAndTurnOff(string reason)
            {
                StopBatchMixing();
                Table.StopMixing();
                _plugin?.PrintDebug($"Stopped {Table.ShortPrefabName}: {reason}");
            }

            private void BatchMixProcess()
            {
                if (Table == null || !Table.IsOn() || Table.currentRecipe == null)
                {
                    StopBatchMixing();
                    if (Table != null) Table.StopMixing();
                    return;
                }

                Recipe recipe = Table.currentRecipe;
                var orderedItems = Table.GetOrderedContainerItems(Table.inventory, out bool contiguous);
                if (!contiguous || orderedItems == null || orderedItems.Count < recipe.Ingredients.Length)
                {
                    StopAndTurnOff("Recipe ingredients changed");
                    return;
                }

                var inputs = new (Item Item, int Cost)[recipe.Ingredients.Length];
                int maxPossibleByIngredients = int.MaxValue;

                for (int index = 0; index < inputs.Length; index++)
                {
                    var ingredient = recipe.Ingredients[index];
                    var item = orderedItems[index];
                    if (item == null || item.info != ingredient.Ingredient)
                    {
                        StopAndTurnOff("Recipe ingredients changed");
                        return;
                    }

                    int cost = ingredient.GetIngredientCount(recipe.ProducedItem);
                    inputs[index] = (item, cost);
                    if (cost <= 0) continue;
                    int sets = item.amount / cost;
                    if (sets < maxPossibleByIngredients) maxPossibleByIngredients = sets;
                }

                if (maxPossibleByIngredients <= 0)
                {
                    StopAndTurnOff("Ingredients empty");
                    return;
                }

                float speed = SmartMixing._config.SpeedMultiplier;
                int desiredBatch = speed >= 1f ? (int)Math.Min((double)speed, maxPossibleByIngredients) : 1;
                int actualBatch = TryMixBatch(recipe, inputs, desiredBatch);
                if (actualBatch == 0)
                    StopAndTurnOff("Output could not fit");
                else if (actualBatch == maxPossibleByIngredients)
                    StopAndTurnOff("Ingredients empty");
            }

            private int TryMixBatch(Recipe recipe, (Item Item, int Cost)[] inputs, int desiredBatch)
            {
                var product = ItemManager.Create(recipe.ProducedItem, 1);
                if (product == null) return 0;

                var table = Table;
                var inventory = table.inventory;
                var previousProductionItem = VanillaProductionItem.GetValue(table);

                // Rust admits the current product and normally stops the mix on inventory changes.
                table.SetFlagLocal(BaseEntity.Flags.On, false, false);
                try
                {
                    VanillaProductionItem.SetValue(table, recipe.ProducedItem);
                    int stackLimit = product.MaxStackable();
                    if (inventory.maxStackSize > 0) stackLimit = Math.Min(stackLimit, inventory.maxStackSize);

                    int outputSlot = -1;
                    int batch = 0;
                    for (int slot = inputs.Length; slot < inventory.capacity; slot++)
                    {
                        var existing = inventory.GetSlot(slot);
                        if (existing != null && !existing.CanStack(product)) continue;

                        int available = stackLimit - (existing?.amount ?? 0);
                        int fits = Math.Min(desiredBatch, available / recipe.ProducedItemCount);
                        if (fits <= batch || inventory.CanAcceptItem(null, product, slot) != ItemContainer.CanAcceptResult.CanAccept)
                            continue;

                        outputSlot = slot;
                        batch = fits;
                    }
                    if (outputSlot < 0) return 0;

                    product.amount = recipe.ProducedItemCount * batch;
                    if (!product.MoveToContainer(inventory, outputSlot, allowSwap: false)) return 0;
                    product = null;

                    foreach (var input in inputs)
                        if (input.Cost > 0) input.Item.UseItem(input.Cost * batch);
                    ItemManager.DoRemoves();
                    return batch;
                }
                finally
                {
                    product?.Remove();
                    VanillaProductionItem.SetValue(table, previousProductionItem);
                    table.SetFlagLocal(BaseEntity.Flags.On, true, false);
                }
            }
        }
        #endregion
    }
}
