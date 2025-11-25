using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager Instance { get; private set; }

    public class Player
    {
        public int id;
        public string playerName;
        public int money;
        public Dictionary<StockType, int> stocks;
        public CharacterCardSO selectedCard;
    }

    public List<Player> players = new List<Player>();

    private readonly Dictionary<int, Dictionary<StockType, int>> _pendingCloseSells = new Dictionary<int, Dictionary<StockType, int>>();

    public event Action<int, int> OnMoneyChanged;
    public event Action<int, Dictionary<StockType, int>> OnStocksChanged;
    public event Action<int, StockType, int> OnPendingCloseChanged;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        //DontDestroyOnLoad(transform.root.gameObject);
    }

    public Player RegisterNetworkPlayer(int pid, string name = null)
    {
        // Avoid duplicates if something weird happens
        var existing = players.FirstOrDefault(p => p.id == pid);
        if (existing != null)
            return existing;

        var p = new Player
        {
            id = pid,
            playerName = name ?? $"Player {pid + 1}",
            money = 5,
            stocks = new Dictionary<StockType, int>()
        };

        players.Add(p);
        return p;
    }

    [Server]
    public void Server_GiveInitialStocks(int perPlayer = 3)
    {
        var available = StockMarketManager.Instance.availableStocks;

        foreach (var p in players)
        {
            for (int j = 0; j < perPlayer; j++)
            {
                var stock = available[UnityEngine.Random.Range(0, available.Count)];
                if (!p.stocks.ContainsKey(stock))
                    p.stocks[stock] = 0;
                p.stocks[stock]++;
            }

            TurnManager.Instance.Server_SyncPlayerState(p.id);
        }
    }

    // Single-player helper – not used in multiplayer flow anymore.
    public void SetupPlayers(int playerCount)
    {
        players.Clear();
        for (int i = 0; i < playerCount; i++)
        {
            var p = new Player { id = i, playerName = $"Player {i + 1}", money = 5, stocks = new Dictionary<StockType, int>() };
            var available = StockMarketManager.Instance.availableStocks;

            for (int j = 0; j < 3; j++) // Adding 3 random stocks
            {
                var stock = available[UnityEngine.Random.Range(0, available.Count)];
                if (!p.stocks.ContainsKey(stock))
                {
                    p.stocks[stock] = 0;
                }
                p.stocks[stock]++;
            }
            players.Add(p);
        }
    }

    // Increase player's money by amount
    public void AddMoney(int playerId, int amount)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player != null)
        {
            player.money += amount;
            OnMoneyChanged?.Invoke(playerId, player.money);
        }
    }

    // Decrease player's money by amount
    public void RemoveMoney(int playerId, int amount)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player != null)
        {
            player.money = Mathf.Max(0, player.money - amount);
            OnMoneyChanged?.Invoke(playerId, player.money);
        }
    }

    // Add count stocks of given type to player
    public void AddStock(int playerId, StockType stockType, int count)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player != null)
        {
            if (!player.stocks.ContainsKey(stockType))
            {
                player.stocks[stockType] = 0;
            }
            player.stocks[stockType] += count;
            OnStocksChanged?.Invoke(playerId, new Dictionary<StockType, int>(player.stocks));
        }
    }

    // Remove count stocks of given type from player
    public void RemoveStock(int playerId, StockType stockType, int count)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player != null && player.stocks.ContainsKey(stockType))
        {
            player.stocks[stockType] = Mathf.Max(0, player.stocks[stockType] - count);
            OnStocksChanged?.Invoke(playerId, new Dictionary<StockType, int>(player.stocks));
        }
    }

    public bool TryBuyStock(int playerId, StockType stock)
    {
        Debug.Log($"[PLAYER] TryBuyStock pid={playerId} stock={stock} @frame {Time.frameCount}"); // remove later

        var price = StockMarketManager.Instance.stockPrices[stock];
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player == null || player.money < price)
        {
            return false;
        }
        //player.money -= price;
        RemoveMoney(playerId, price);
        AddStock(playerId, stock, 1);
        StockMarketManager.Instance.BuyStock(stock);
        return true;
    }

    public bool TrySellStock(int playerId, StockType stock, bool openSale)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player == null)
        {
            return false;
        }
        int available = GetAvailableToSell(playerId, stock);
        if (available < 1)
        {
            return false;
        }

        if (openSale)
        {
            RemoveStock(playerId, stock, 1);
            int amount = StockMarketManager.Instance.stockPrices[stock];
            AddMoney(playerId, amount);
            StockMarketManager.Instance.SellStock(stock, openSale: true);
        }
        else
        {
            // Close sell: card stays in hand (hidden), money later, price later
            if (!_pendingCloseSells.TryGetValue(playerId, out var map))
            {
                map = new Dictionary<StockType, int>();
                _pendingCloseSells[playerId] = map;
            }
            map[stock] = map.TryGetValue(stock, out var cur) ? cur + 1 : 1;

            // Tell UI the pending count for this stock changed
            OnPendingCloseChanged?.Invoke(playerId, stock, map[stock]);
        }

        return true;
    }

    private int GetAvailableToSell(int playerId, StockType stock)
    {
        var p = players.FirstOrDefault(x => x.id == playerId);
        if (p == null)
        {
            return 0;
        }

        int owned = p.stocks.TryGetValue(stock, out var c) ? c : 0;
        int pending = (_pendingCloseSells.TryGetValue(playerId, out var map) &&
                       map.TryGetValue(stock, out var r)) ? r : 0;
        return owned - pending; // do NOT let this go below 0
    }

    public void CommitCloseSellsForEndOfRound()
    {
        foreach (var kv in _pendingCloseSells)
        {
            int pid = kv.Key;
            var player = players.FirstOrDefault(p => p.id == pid);
            if (player == null)
            {
                continue;
            }

            foreach (var sv in kv.Value)
            {
                var stock = sv.Key;
                int count = sv.Value;
                // Remove the sold copies from hand now
                RemoveStock(pid, stock, count);

                // pending for this stock is now zero
                OnPendingCloseChanged?.Invoke(pid, stock, 0);
            }
        }
        _pendingCloseSells.Clear();
    }

    // Helper to query pending count for UI
    public int GetPendingCloseCount(int playerId, StockType stock)
    {
        if (_pendingCloseSells.TryGetValue(playerId, out var map) &&
            map.TryGetValue(stock, out var cnt))
        {
            return cnt;
        }
        return 0;
    }

    public void CancelPendingCloseSell(int playerId, StockType stock, int count)
    {
        if (_pendingCloseSells.TryGetValue(playerId, out var map))
        {
            if (map.TryGetValue(stock, out var c))
            {
                c -= count;
                if (c <= 0) map.Remove(stock); else map[stock] = c;
                // notify UI of updated count
                OnPendingCloseChanged?.Invoke(playerId, stock, map.TryGetValue(stock, out var left) ? left : 0);
            }
            if (map.Count == 0) _pendingCloseSells.Remove(playerId);
        }
    }

    public bool ExecuteAbility_Targeted(int actingPid, CharacterAbilityType ability, int chosenNum)
    {
        var undo = ExecuteAbilityWithUndo_Targeted(actingPid, ability, chosenNum);
        return (undo != null);
    }

    public Action ExecuteAbilityWithUndo_Targeted(int playerId, CharacterAbilityType abilityType, int targetCharacterNumber)
    {
        switch (abilityType)
        {
            case CharacterAbilityType.Blocker: // #1
                {
                    // validate
                    if (TurnManager.Instance.IsCharacterBlocked(targetCharacterNumber))
                        return null;
                    if (TurnManager.Instance.GetPidByCharacterNumber(targetCharacterNumber) == null)
                        return null;

                    TurnManager.Instance.BlockCharacter(targetCharacterNumber);
                    
                    Debug.Log($"#{targetCharacterNumber} is blocked."); // REMOVE LATER !!!

                    return () => TurnManager.Instance.UnblockCharacter(targetCharacterNumber);
                }

            case CharacterAbilityType.Thief: // #2
                {
                    if (targetCharacterNumber == 1) return null; // cannot target Blocker
                    if (TurnManager.Instance.IsCharacterBlocked(targetCharacterNumber)) return null;
                    if (TurnManager.Instance.GetPidByCharacterNumber(targetCharacterNumber) is not int victimPid)
                        return null;

                    // cannot steal from self
                    if (victimPid == playerId) return null;

                    // mark stolen so Blocker can’t later pick the same number
                    TurnManager.Instance.MarkStolenThisRound(targetCharacterNumber);

                    int victimMoney = PlayerManager.Instance.players[victimPid].money;
                    int stealAmount = victimMoney / 2;

                    TurnManager.Instance.ScheduleThiefPayout(thiefPid: playerId, victimPid: victimPid, amount: stealAmount);

                    return () =>
                    {
                        TurnManager.Instance.UnscheduleThiefPayout(playerId, victimPid, stealAmount);
                        // optional: unmark; usually not needed because round reset clears it
                        // (kept consistent in case you add mid-round retargeting later)
                        // TurnManager.Instance.UnmarkStolenThisRound(targetCharacterNumber);
                    };
                }

            default:
                return null;
        }
    }

    public Dictionary<StockType, int> GetPendingCloseDict(int pid)
    {
        if (_pendingCloseSells.TryGetValue(pid, out var inner))
        {
            return new Dictionary<StockType, int>(inner);
        }

        return new Dictionary<StockType, int>();
    }

    public void SettleRemainingHoldingsToCash()
    {
        foreach (var p in players)
        {
            int gained = 0;
            foreach (var kv in p.stocks.ToList())
            {
                var stock = kv.Key;
                var count = kv.Value;
                if (count <= 0) continue;

                int priceNow = StockMarketManager.Instance.stockPrices.TryGetValue(stock, out var pr) ? pr : 0;
                int add = priceNow * count;
                if (add > 0) AddMoney(p.id, add);

                // zero the holding
                if (count > 0) RemoveStock(p.id, stock, count);
                gained += add;
            }
            // optional: Debug.Log($"[SETTLE] P{p.id} +${gained}");
        }
    }
}