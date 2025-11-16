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
    public void TargetBeginBidTurn(string playerName, int playerMoney)
    {
        UIManager.Instance.Bidding_BeginTurn(playerName, playerMoney);
    }

    [TargetRpc]
    public void TargetToast(string msg)
    {
        UIManager.Instance.ShowMessage(msg);
    }
}