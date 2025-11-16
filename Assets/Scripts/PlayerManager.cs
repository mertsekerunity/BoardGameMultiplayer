using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

        // Give starting stocks same way as SetupPlayers
        var available = StockMarketManager.Instance.availableStocks;
        for (int j = 0; j < 3; j++)
        {
            var stock = available[UnityEngine.Random.Range(0, available.Count)];
            if (!p.stocks.ContainsKey(stock))
            {
                p.stocks[stock] = 0;
            }
            p.stocks[stock]++;
        }

        players.Add(p);
        return p;
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


    public Action ExecuteAbilityWithUndo(int playerId, CharacterAbilityType abilityType)
    {
        switch (abilityType)
        {
            case CharacterAbilityType.Blocker: // #1
                {
                    // Character numbers present this round (as ints)
                    var candidates = TurnManager.Instance.characterAssignments
                        .Keys
                        .Select(k => (int)k.characterNumber)                  
                        .Where(num => num >= 2 && num <= 9                   
                                      && !TurnManager.Instance.IsCharacterBlocked(num))
                        .Distinct()
                        .ToList();

                    if (candidates.Count == 0)
                    {
                        UIManager.Instance.ShowMessage("No valid character to block.");
                        return null;
                    }

                    int targetChar = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                    TurnManager.Instance.BlockCharacter(targetChar);

                    return () => TurnManager.Instance.UnblockCharacter(targetChar);
                }

            case CharacterAbilityType.Thief: // #2
                {
                    // Thief targets a CHARACTER NUMBER that exists this round,
                    // cannot be Blocker (#1), and cannot be a character already blocked.
                    var targetEntryOpts = TurnManager.Instance.characterAssignments
                        .Where(kv =>
                        {
                            int num = (int)kv.Key.characterNumber;
                            return num != 1 && !TurnManager.Instance.IsCharacterBlocked(num) && kv.Value != playerId;
                        })
                        .ToList();

                    if (targetEntryOpts.Count == 0)
                    {
                        UIManager.Instance.ShowMessage("No valid target for Thief.");
                        return null;
                    }

                    var targetEntry = targetEntryOpts[UnityEngine.Random.Range(0, targetEntryOpts.Count)];
                    int victimPid = targetEntry.Value;

                    int victimMoney = PlayerManager.Instance.players[victimPid].money;
                    int stealAmount = victimMoney / 2;

                    // Schedule payout at the END of the Thief's turn
                    TurnManager.Instance.ScheduleThiefPayout(thiefPid: playerId, victimPid: victimPid, amount: stealAmount);

                    return () => TurnManager.Instance.UnscheduleThiefPayout(playerId, victimPid, stealAmount);
                }

            case CharacterAbilityType.Trader: // #3
                {
                    // Per-turn deltas: buy -1, sell +1 (your TurnManager holds the deltas)
                    TurnManager.Instance.EnableTraderForThisTurn();
                    return () => TurnManager.Instance.DisableTraderForThisTurn();
                }

            case CharacterAbilityType.Manipulator: // #4 (uses TWICE)
                {
                    var undos = new List<Action>();
                    int repeats = 2;

                    for (int k = 0; k < repeats; k++)
                    {
                        if (!TurnManager.Instance.TryReserveManipulationTarget(out var target))
                        {
                            UIManager.Instance.ShowMessage("No stocks left to manipulate this round.");
                            break;
                        }

                        // Draw 3, TEMP choose 1 (index 0), discard 1 (index 1), return 1 (index 2)
                        var a = DeckManager.Instance.DrawManipulation();
                        var b = DeckManager.Instance.DrawManipulation();
                        var c = DeckManager.Instance.DrawManipulation();
                        var chosen = a;
                        var discard = b;
                        var ret = c;

                        DeckManager.Instance.DiscardManipulation(discard);
                        DeckManager.Instance.ReturnManipulationToDeck(ret);

                        // Queue chosen (revealed at end of round)
                        var undoQueue = TurnManager.Instance.QueueManipulation(playerId, chosen, target);

                        undos.Add(() =>
                        {
                            // remove from queue, free reservation, return chosen to deck
                            undoQueue?.Invoke();
                            // (we keep discard/return as-is during undo to keep deck handling simple)
                        });
                    }

                    return () =>
                    {
                        for (int i = undos.Count - 1; i >= 0; i--) undos[i]?.Invoke();
                    };
                }

            case CharacterAbilityType.LotteryWinner: // #5
                {
                    int payout = DeckManager.Instance.ClaimLottery();
                    AddMoney(playerId, payout);

                    Action undoManip = null;
                    // Also queue 1 manipulation (if a target remains)
                    if (TurnManager.Instance.TryReserveManipulationTarget(out var target))
                    {
                        var m = DeckManager.Instance.DrawManipulation();
                        undoManip = TurnManager.Instance.QueueManipulation(playerId, m, target);
                    }
                    else
                    {
                        UIManager.Instance.ShowMessage("No stocks left to manipulate this round.");
                    }

                    return () =>
                    {
                        RemoveMoney(playerId, payout);
                        DeckManager.Instance.RestoreLottery(payout);
                        undoManip?.Invoke();
                    };
                }

            case CharacterAbilityType.Broker: // #6
                {
                    // Limits +1/+1 this turn
                    TurnManager.Instance.RaiseLimitsForThisTurn(buyLimit: 3, sellLimit: 4);

                    Action undoManip = null;
                    if (TurnManager.Instance.TryReserveManipulationTarget(out var target))
                    {
                        var m = DeckManager.Instance.DrawManipulation();
                        undoManip = TurnManager.Instance.QueueManipulation(playerId, m, target);
                    }
                    else
                    {
                        UIManager.Instance.ShowMessage("No stocks left to manipulate this round.");
                    }

                    return () =>
                    {
                        TurnManager.Instance.RaiseLimitsForThisTurn(buyLimit: 2, sellLimit: 3);
                        undoManip?.Invoke();
                    };
                }

            case CharacterAbilityType.Gambler: // #7
                {
                    // Up to 2 random stocks @ $3 each; if money < 6, try 1; if < 3, do nothing.
                    int money = PlayerManager.Instance.players[playerId].money;
                    int count = (money >= 6) ? 2 : (money >= 3 ? 1 : 0);
                    if (count == 0)
                    {
                        UIManager.Instance.ShowMessage("Not enough money to gamble.");
                        return null;
                    }

                    var taken = new List<StockType>();
                    for (int i = 0; i < count; i++)
                    {
                        var s = DeckManager.Instance.DrawRandomStock();
                        AddStock(playerId, s, 1);
                        taken.Add(s);
                    }
                    RemoveMoney(playerId, 3 * count);

                    return () =>
                    {
                        AddMoney(playerId, 3 * count);
                        foreach (var s in taken)
                        {
                            RemoveStock(playerId, s, 1);
                            DeckManager.Instance.ReturnStockToRandom(s);
                        }
                    };
                }

            case CharacterAbilityType.TaxCollector: // #8
                {
                    var t = DeckManager.Instance.DrawTaxCard();
                    var stock = (StockType)Enum.Parse(typeof(StockType), t.ToString());
                    TurnManager.Instance.ScheduleTaxCollector(playerId, stock);

                    return () =>
                    {
                        TurnManager.Instance.UnscheduleTaxCollector(playerId, stock);
                        DeckManager.Instance.ReturnTaxToDeck(t);
                    };
                }

            case CharacterAbilityType.Inheritor: // #9
                {
                    var stock = DeckManager.Instance.DrawRandomStock();
                    AddStock(playerId, stock, 1);

                    return () =>
                    {
                        RemoveStock(playerId, stock, 1);
                        DeckManager.Instance.ReturnStockToRandom(stock);
                    };
                }

            default:
                return null;
        }
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