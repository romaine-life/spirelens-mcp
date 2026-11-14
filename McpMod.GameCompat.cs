using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireLens.Mcp;

// Game APIs whose shape differs between the STS2 builds the bridge runs on
// (v0.111 stable, v0.113 beta). Each one is resolved by reflection, so a DLL
// built against either game assembly runs on the other, and a member that
// disappears in a later build turns into an error result instead of a throw.
public static partial class McpMod
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Sets an auto-property whether it has a (private) setter, as HoverTip.Id
    // did before v0.113, or is get-only with only a compiler backing field.
    private static bool TrySetAutoProperty(object target, string name, object? value)
    {
        var type = target.GetType();
        var setter = type.GetProperty(name, AnyInstance)?.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            setter.Invoke(target, new[] { value });
            return true;
        }

        var backingField = type.GetField($"<{name}>k__BackingField", AnyInstance);
        if (backingField == null)
            return false;
        backingField.SetValue(target, value);
        return true;
    }

    // v0.113 moved shop inventories off MerchantRoom onto RunManager.MerchantSynchronizer.
    private static object? GetMerchantSynchronizer() =>
        typeof(RunManager).GetProperty("MerchantSynchronizer", AnyInstance)?.GetValue(RunManager.Instance);

    private static MerchantInventory? GetLocalMerchantInventory(MerchantRoom merchantRoom)
    {
        var source = GetMerchantSynchronizer() ?? merchantRoom;
        var getLocal = source.GetType().GetMethod("GetLocalInventory", AnyInstance, Type.EmptyTypes);
        if (getLocal != null)
        {
            // Both throw while the local player's inventory is not populated yet.
            try { return getLocal.Invoke(source, null) as MerchantInventory; }
            catch (TargetInvocationException) { return null; }
        }

        return (merchantRoom.GetType().GetProperty("Inventories", AnyInstance)?.GetValue(merchantRoom)
            as IEnumerable<MerchantInventory>)?.FirstOrDefault();
    }

    // The order BuildShopState numbers shop items in. MerchantInventory.AllEntries
    // can't stand in for it: from v0.113 it puts Blitz and test entries before card removal.
    private static List<MerchantEntry> ShopEntriesInStateOrder(MerchantInventory inventory)
    {
        var entries = new List<MerchantEntry>();
        entries.AddRange(inventory.CardEntries);
        entries.AddRange(inventory.RelicEntries);
        entries.AddRange(inventory.PotionEntries);
        if (inventory.CardRemovalEntry is { } removal)
            entries.Add(removal);
        return entries;
    }

    // The same purchase a click on the shop slot makes: v0.113 sends it through
    // MerchantSynchronizer.PurchaseLocalEntry (synchronized with co-op peers),
    // earlier builds call MerchantEntry.OnTryPurchaseWrapper. Null when neither exists.
    private static Task? StartMerchantPurchase(MerchantEntry entry, MerchantInventory inventory)
    {
        var synchronizer = GetMerchantSynchronizer();
        var purchaseLocal = synchronizer?.GetType().GetMethod("PurchaseLocalEntry", AnyInstance, new[] { typeof(MerchantEntry) });
        if (purchaseLocal != null)
            return purchaseLocal.Invoke(synchronizer, new object[] { entry }) as Task;

        var wrapper = typeof(MerchantEntry).GetMethod(
            "OnTryPurchaseWrapper", AnyInstance, new[] { typeof(MerchantInventory), typeof(bool) });
        return wrapper?.Invoke(entry, new object?[] { inventory, false }) as Task;
    }

    // v0.113 takes a Player the caller builds plus an isCustomGame flag; earlier
    // builds take the CharacterModel. Arguments are bound by parameter name.
    private static Task? StartSingleplayerRun(
        CharacterModel character, IReadOnlyList<ActModel> acts, string seed, int ascension, out string? error)
    {
        error = null;
        var game = NGame.Instance;
        var method = typeof(NGame).GetMethods(AnyInstance).FirstOrDefault(m => m.Name == "StartNewSingleplayerRun");
        if (game == null || method == null)
        {
            error = "NGame.StartNewSingleplayerRun is not available on this game version.";
            return null;
        }

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(Player))
                args[i] = Player.CreateForNewRun(character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL);
            else if (parameter.ParameterType == typeof(CharacterModel))
                args[i] = character;
            else
            {
                switch (parameter.Name)
                {
                    case "shouldSave": args[i] = false; break;
                    case "acts": args[i] = acts; break;
                    case "modifiers": args[i] = Array.Empty<ModifierModel>(); break;
                    case "seed": args[i] = seed; break;
                    case "gameMode": args[i] = GameMode.Standard; break;
                    case "isCustomGame": args[i] = false; break;
                    case "ascensionLevel": args[i] = ascension; break;
                    default:
                        if (!parameter.HasDefaultValue)
                        {
                            error = $"NGame.StartNewSingleplayerRun has an unsupported parameter '{parameter.Name}' on this game version.";
                            return null;
                        }
                        args[i] = parameter.DefaultValue;
                        break;
                }
            }
        }

        return method.Invoke(game, args) as Task;
    }
}
