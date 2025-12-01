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


    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        //DontDestroyOnLoad(transform.root.gameObject); //didnt work with mirror, still dont know why.
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

    public void AddMoney(int playerId, int amount)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player != null)
        {
            player.money += amount;
        }
    }

    public void RemoveMoney(int playerId, int amount)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player != null)
        {
            player.money = Mathf.Max(0, player.money - amount);
        }
    }

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
        }
    }

    public void RemoveStock(int playerId, StockType stockType, int count)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);
        if (player != null && player.stocks.ContainsKey(stockType))
        {
            player.stocks[stockType] = Mathf.Max(0, player.stocks[stockType] - count);
        }
    }

    [Server]
    public bool TryBuyStock(int playerId, StockType stock, int effectivePrice)
    {
        var player = players.FirstOrDefault(p => p.id == playerId);

        if (player == null)
        {
            return false;
        }

        RemoveMoney(playerId, effectivePrice);
        AddStock(playerId, stock, 1);
        StockMarketManager.Instance.BuyStock(stock);
        return true;
    }

    [Server]
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
                if (c <= 0)
                {
                    map.Remove(stock);
                }
                else
                {
                    map[stock] = c;
                }                    
            }
            if (map.Count == 0)
            {
                _pendingCloseSells.Remove(playerId);
            }
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
        }
    }
}