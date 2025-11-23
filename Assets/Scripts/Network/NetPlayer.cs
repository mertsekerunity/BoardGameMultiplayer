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

        //UIManager.Instance?.SetLocalPlayerId(pid);
        Debug.Log($"[NetPlayer] OnStartLocalPlayer, pid={pid}");
        //UIManager.Instance.InitializeLocalPlayerUI(pid);
    }

    void OnPidChanged(int oldValue, int newValue)
    {
        if (isLocalPlayer && newValue >= 0 && UIManager.Instance != null)
        {
            UIManager.Instance?.SetLocalPlayerId(newValue);
        } 
    }

    // ================== Commands (UI -> Server) ==================

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

        TargetToast($"[NetPlayer] CmdEndTurn from pid={pid}, active={active}");

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
    public void CmdSubmitBid(int amount)
    {
        TurnManager.Instance.SubmitBid_Server(pid, amount);
    }

    [Command]
    public void CmdConfirmCharacterSelection(int cardId)
    {
        if (TurnManager.Instance == null)
        {
            Debug.LogWarning("[CmdConfirmCharacterSelection] TurnManager.Instance is null on server.");
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

    [TargetRpc]
    public void TargetBeginBidTurn(string playerName, int playerMoney)
    {
        UIManager.Instance.Bidding_BeginTurn(playerName, playerMoney);
    }

    [TargetRpc]
    public void TargetToastAbilityBlocked()
    {
        UIManager.Instance.ShowMessage("Your ability is blocked this round.");
        UIManager.Instance.SetAbilityButtonState(false);
        UIManager.Instance.SetUndoButtonInteractable(false);
    }

    [TargetRpc]
    public void TargetAskStockTarget(string prompt, int[] stockTypeIds)
    {
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
                $"Apply taxes to {label}?",
                onYes: () =>
                {
                    CmdConfirmStockTarget((int)stock);
                    UIManager.Instance.HidePrompt();
                },
                onNo: () =>
                {
                    CmdCancelStockTarget();
                    UIManager.Instance.HidePrompt();
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
        var enabled = new HashSet<int>(enabledCharacterNumbers);
        var disabled = new HashSet<int>(disabledCharacterNumbers);
        var ability = (CharacterAbilityType)abilityId;

        UIManager.Instance.ShowPrompt(prompt);

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
                        UIManager.Instance.HidePrompt();
                    },
                    onNo: () =>
                    {
                        CmdCancelCharacterTarget();
                        UIManager.Instance.HidePrompt();
                    });
            });
    }

    [TargetRpc]
    public void TargetAskGamble(int money)
    {
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
        UIManager.Instance.SetUndoButtonInteractable(interactable);
    }

    [TargetRpc]
    public void TargetSetAbilityButtonState(bool enabled)
    {
        UIManager.Instance.SetAbilityButtonState(enabled);
    }

    [TargetRpc]
    public void TargetToast(string msg)
    {
        UIManager.Instance.ShowMessage(msg);
    }
}