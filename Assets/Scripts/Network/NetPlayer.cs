using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using Unity.VisualScripting;
using UnityEngine;

public class NetPlayer : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnPidChanged))]
    
    public int pid = -1;

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();

        //string desiredName = ClientPlayerConfig.PlayerName;
        string desiredName = CustomNetworkManager.Instance.pendingPlayerName;

        CmdSetPlayerName(desiredName);
    }

    void OnPidChanged(int oldValue, int newValue)
    {
        if (!isLocalPlayer || newValue < 0) return;
        if (UIManager.Instance == null) return;

        UIManager.Instance.SetLocalPlayerId(newValue);
    }

    // ================== Commands (UI -> Server) ==================

    [Command]
    private void CmdSetPlayerName(string rawName)
    {
        string finalName = string.IsNullOrWhiteSpace(rawName)
            ? $"Player {pid + 1}"
            : rawName.Trim();

        PlayerManager.Instance.SetPlayerName(pid, finalName);

        if (GameManager.Instance != null)
        {
            GameManager.Instance.Server_SyncPlayerName(pid, finalName);
        }
    }

    [Command]
    public void CmdBuy(StockType stock)
    {
        if(TurnManager.Instance.ActivePlayerId != pid)
        {
            TargetToast("Not your turn.");
            return;
        }
        bool ok = TurnManager.Instance.TryBuyOne(stock);
        if (!ok)
        {
            TargetToast("Can't buy.");
        }
    }

    [Command]
    public void CmdSell(StockType stock, bool openSale)
    {
        if (TurnManager.Instance.ActivePlayerId != pid)
        {
            TargetToast("Not your turn.");
            return;
        }
        bool ok = TurnManager.Instance.TrySellOne(stock, openSale);
        if (!ok)
        {
            TargetToast("Can't sell.");
        }
    }

    [Command]
    public void CmdUseAbility()
    {
        if (TurnManager.Instance.ActivePlayerId != pid)
        {
            TargetToast("Not your turn.");
            return;
        }
        bool ok = TurnManager.Instance.TryUseAbility();
        if (!ok)
        {
            TargetToast("Ability already used / not available.");
        }
    }

    [Command]
    public void CmdEndTurn()
    {
        int active = TurnManager.Instance.ActivePlayerId;

        if (active != pid)
        {
            TargetToast("Not your turn.");
            return;
        }

        TurnManager.Instance.EndActivePlayerTurn();
    }

    [Command]
    public void CmdUndo()
    {
        if (TurnManager.Instance.ActivePlayerId != pid)
        {
            TargetToast("Not your turn.");
            return;
        }
        bool ok = TurnManager.Instance.UndoLast();
        if (!ok)
        {
            TargetToast("Nothing to undo.");
        }
    }

    [Command]
    public void CmdSubmitBid(int slotIndex)
    {
        TurnManager.Instance.SubmitBid_Server(pid, slotIndex);
    }

    [Command]
    public void CmdConfirmCharacterSelection(int cardId)
    {
        if (TurnManager.Instance == null)
        {
            return;
        }

        TurnManager.Instance.Server_ConfirmCharacterSelection(pid, cardId);
    }

    [Command]
    private void CmdConfirmStockTarget(int stockId)
    {
        TurnManager.Instance.Server_OnStockTargetChosen(pid, stockId);
    }

    [Command]
    private void CmdCancelStockTarget()
    {
        TurnManager.Instance.Server_OnStockTargetCancelled(pid);
    }

    [Command]
    public void CmdConfirmCharacterTarget(int chosenCharacterNumber)
    {
        TurnManager.Instance.Server_OnCharacterTargetChosen(pid, chosenCharacterNumber);
    }

    [Command]
    public void CmdCancelCharacterTarget()
    {
        TurnManager.Instance.Server_OnCharacterTargetCancelled(pid);
    }

    [Command]
    private void CmdConfirmGamble(int cardsToTake)
    {
        TurnManager.Instance.Server_OnGambleChosen(pid, cardsToTake);
    }

    [Command]
    private void CmdCancelGamble()
    {
        TurnManager.Instance.Server_OnGambleCancelled(pid);
    }

    [Command]
    public void CmdConfirmManipChoice(int manipId)
    {
        TurnManager.Instance.Server_OnManipOptionChosen(pid, manipId);
    }

    [Command]
    public void CmdCancelManipChoice()
    {
        TurnManager.Instance.Server_OnManipOptionCancelled(pid);
    }

    [TargetRpc]
    public void TargetBeginBidTurn(string playerName, int playerMoney)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.Bidding_BeginTurn(playerName, playerMoney);
    }

    [TargetRpc]
    public void TargetToastAbilityBlocked()
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.ShowLocalToast("Your ability is blocked for this round.");
        UIManager.Instance.SetAbilityButtonState(false);
        UIManager.Instance.SetUndoButtonInteractable(false);
    }

    [TargetRpc]
    public void TargetAskStockTarget(string prompt, string confirmPrefix, int[] stockTypeIds)
    {
        if (UIManager.Instance == null) return;

        var candidates = new HashSet<StockType>(stockTypeIds.Select(id => (StockType)id));

        UIManager.Instance.ShowStockTargetPanel(
            pid,          // NetPlayer's pid
            candidates,
            prompt,
            onChosen: stock =>
            {
                string label = stock.ToString();

                UIManager.Instance.ShowAbilityConfirm(
                pid,
                $"{confirmPrefix} {label}?",
                onYes: () =>
                {
                    CmdConfirmStockTarget((int)stock);
                },
                onNo: () =>
                {
                    CmdCancelStockTarget();
                });

            },
            onCancelled: () =>
            {
                CmdCancelStockTarget();
            });
    }

    [TargetRpc]
    public void TargetAskManipStockTarget(string prompt, int[] stockTypeIds)
    {
        if (UIManager.Instance == null) return;

        var candidates = new HashSet<StockType>(stockTypeIds.Select(id => (StockType)id));

        bool isProtect = prompt.IndexOf("protect", StringComparison.OrdinalIgnoreCase) >= 0;

        UIManager.Instance.ShowStockTargetPanel(
            pid,
            candidates,
            prompt,
            onChosen: stock =>
            {
                string label = stock.ToString();

                string confirmText = isProtect
                ? $"Apply protection to {label}?"
                : $"Apply this manipulation to {label}?";

                UIManager.Instance.ShowAbilityConfirm(
                    pid,
                    confirmText,
                    onYes: () =>
                    {
                        CmdConfirmStockTarget((int)stock);
                    },
                    onNo: () =>
                    {
                        CmdCancelStockTarget();
                    });
            },
            onCancelled: () =>
            {
                CmdCancelStockTarget();
            });
    }

    [TargetRpc]
    public void TargetAskCharacterTarget(string prompt, int[] enabledCharacterNumbers, int[] disabledCharacterNumbers, int abilityId)
    {
        if (UIManager.Instance == null) return;

        var enabled = new HashSet<int>(enabledCharacterNumbers);
        var disabled = new HashSet<int>(disabledCharacterNumbers);
        var ability = (CharacterAbilityType)abilityId;

        UIManager.Instance.ShowLocalToast(prompt);

        UIManager.Instance.ShowCharacterTargetPanel(
            pid,            // NetPlayer's pid
            enabled,
            disabled,
            onChosen: num =>
            {
                string label = ability.ToString();

                UIManager.Instance.ShowAbilityConfirm(
                    pid,
                    $"Use {label} on #{num}?",
                    onYes: () =>
                    {
                        CmdConfirmCharacterTarget(num);
                    },
                    onNo: () =>
                    {
                        CmdCancelCharacterTarget();
                    });
            });
    }

    [TargetRpc]
    public void TargetAskGamble(int money)
    {
        if (UIManager.Instance == null) return;

        if (money >= 6)
        {
            UIManager.Instance.ShowAbilityConfirm(
                pid,
                "Gamble: take 2 random stocks for 6$?",
                onYes: () =>
                {
                    CmdConfirmGamble(2);
                },
                onNo: () =>
                {
                    if (money >= 3)
                    {
                        UIManager.Instance.ShowAbilityConfirm(
                            pid,
                            "Take 1 random stock for 3$?",
                            onYes: () => CmdConfirmGamble(1),
                            onNo: () => CmdCancelGamble()
                        );
                    }
                    else
                    {
                        CmdCancelGamble();
                    }
                });
        }
        else if (money >= 3)
        {
            UIManager.Instance.ShowAbilityConfirm(
                pid,
                "Gamble: take 1 random stock for 3$?",
                onYes: () => CmdConfirmGamble(1),
                onNo: () => CmdCancelGamble()
            );
        }
        else
        {
            CmdCancelGamble();
        }
    }

    [TargetRpc]
    public void TargetSetUndoInteractable(bool interactable)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.SetUndoButtonInteractable(interactable);
    }

    [TargetRpc]
    public void TargetSetAbilityButtonState(bool enabled)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.SetAbilityButtonState(enabled);
    }

    [TargetRpc]
    public void TargetShowPrivateManipPeek(ManipulationType m)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.ShowPrivateManipPeek(pid, m);
    }

    [TargetRpc]
    public void TargetHidePrivateManipPeek()
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.HidePrivateManipPeek();
    }

    [TargetRpc]
    public void TargetOnManipQueued(ManipulationType m, StockType s)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.HandleManipQueued(pid, m, s);
    }

    [TargetRpc]
    public void TargetOnProtectionQueued(StockType s)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.HandleProtectionChosen(pid, s);
    }

    [TargetRpc]
    public void TargetAskManipChoice(string prompt, int[] manipIds)
    {
        if (UIManager.Instance == null) return;

        var options = manipIds.Select(id => (ManipulationType)id).ToList();

        UIManager.Instance.ShowManipulationChoice(
            pid,
            options,
            prompt,
            (chosen, discardIgnored, returnIgnored, cancelSentinel) =>
            {
                if ((int)cancelSentinel == -999)
                {
                    CmdCancelManipChoice();
                }
                else
                {
                    CmdConfirmManipChoice((int)chosen);
                }
            });
    }

    [TargetRpc]
    public void TargetToast(string msg)
    {
        UIManager.Instance.ShowLocalToast(msg);
    }
}