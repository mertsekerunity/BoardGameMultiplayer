using Mirror;
using Unity.VisualScripting;
using UnityEngine;

public class NetPlayer : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnPidChanged))]
    
    public int pid = -1;

    void OnPidChanged(int oldValue, int newValue)
    {
        if (isLocalPlayer)
        {
            // Tell the UI “this seat is mine”
            UIManager.Instance?.SetLocalPlayerId(newValue);
        } 
    }

    public override void OnStartClient()
    {
        if(isLocalPlayer && pid >= 0)
        {
            UIManager.Instance?.SetLocalPlayerId(pid);
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
        if (TurnManager.Instance.ActivePlayerId != pid)
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

    [TargetRpc]
    void TargetToast(string msg)
    {
        UIManager.Instance.ShowMessage(msg);
    }
}