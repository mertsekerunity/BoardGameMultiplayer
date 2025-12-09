using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

public class StockMarketManager : MonoBehaviour
{
    public static StockMarketManager Instance { get; private set; }

    public List<StockType> availableStocks;
    public Dictionary<StockType, int> stockPrices;

    public event Action<StockType, int> OnStockPriceChanged;

    [Header("Price Bounds")]
    public int minPrice = 0;
    public int maxPrice = 8;
    public int startingPrice = 4;

    private struct CloseSale
    {
        public int playerId;
        public StockType stock;
        public int price;
        public int basePriceAtQueue;
    }
    private readonly List<CloseSale> _pendingCloseSales = new();

    private struct BankruptcyRecord
    {
        public bool active;
        public int preSalePrice;
        public Dictionary<int, int> destroyedByPlayer;
    }
    private readonly Dictionary<StockType, BankruptcyRecord> _lastBankruptcy = new();

    private struct CeilingRecord
    {
        public bool active;
        public int preBuyPrice;                          
        public Dictionary<int, int> destroyedByPlayer;   
        public Dictionary<int, int> payoutByPlayer;      
    }

    private readonly Dictionary<StockType, CeilingRecord> _lastCeiling = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    [Server]
    public void SetupMarket(int playerCount)
    {
        availableStocks = (playerCount <= 4)
            ? new List<StockType> { StockType.Red, StockType.Blue, StockType.Green }
            : new List<StockType> { StockType.Red, StockType.Blue, StockType.Green, StockType.Yellow };

        stockPrices = availableStocks.ToDictionary(s => s, _ => startingPrice);

        foreach (var s in availableStocks)
        {
            OnStockPriceChanged?.Invoke(s, startingPrice);
        }
    }

    public int GetPrice(StockType stock) => stockPrices.TryGetValue(stock, out var p) ? p : startingPrice;

    [Server]
    public void BuyStock(StockType stock)
    {
        int before = stockPrices[stock];
        int after = Mathf.Clamp(before + 1, minPrice, maxPrice);
        stockPrices[stock] = after;
        OnStockPriceChanged?.Invoke(stock, after);

        CheckCeilingAfterBuy(stock, before);
    }

    [Server]
    public void SellStock(StockType stock, bool openSale)
    {
        if (openSale)
        {
            int before = stockPrices[stock];
            int after = Mathf.Clamp(before - 1, minPrice, maxPrice);
            stockPrices[stock] = after;
            OnStockPriceChanged?.Invoke(stock, after);

            CheckBankruptcyAfterOpenSell(stock, before);
        }
    }

    [Server]
    public void QueueCloseSale(int playerId, StockType stock, int anchoredGain, int basePriceAtQueue)
    {
        _pendingCloseSales.Add(new CloseSale
        {
            playerId = playerId,
            stock = stock,
            price = anchoredGain,
            basePriceAtQueue = basePriceAtQueue
        });
    }

    [Server]
    public bool RemoveQueuedCloseSale(int playerId, StockType stock, int anchoredGain)
    {
        int idx = _pendingCloseSales.FindIndex(cs => cs.playerId == playerId && cs.stock == stock && cs.price == anchoredGain);

        if (idx >= 0)
        {
            _pendingCloseSales.RemoveAt(idx);
            return true;
        }
        return false;
    }

    [Server]
    public void ProcessCloseSales()
    {
        var finalBeforeSell = new Dictionary<StockType, int>(stockPrices);

        foreach (var cs in _pendingCloseSales)
        {
            if (!stockPrices.TryGetValue(cs.stock, out var cur)) continue;

            int anchored = cs.price;
            int basePrice = cs.basePriceAtQueue;

            finalBeforeSell.TryGetValue(cs.stock, out var finalPrice);

            int delta = finalPrice - basePrice;

            int finalPayout = Mathf.Max(0, anchored + delta);
            PlayerManager.Instance.AddMoney(cs.playerId, finalPayout);

            int newPrice = Mathf.Clamp(cur - 1, minPrice, maxPrice);
            stockPrices[cs.stock] = newPrice;

            TurnManager.Instance.Server_SyncStockPrice(cs.stock);
            OnStockPriceChanged?.Invoke(cs.stock, newPrice);

            CheckBankruptcy(cs.stock);
            CheckCeiling(cs.stock);

        }
        _pendingCloseSales.Clear();
    }

    [Server]
    public void AdjustPrice(StockType stock, int delta)
    {
        stockPrices[stock] = Mathf.Clamp(stockPrices[stock] + delta, minPrice, maxPrice);
        OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);
        CheckBankruptcy(stock);
        CheckCeiling(stock);
    }

    [Server]
    public void RevertBuy(int buyerPid, StockType stock)
    {
        if (_lastCeiling.TryGetValue(stock, out var rec) && rec.active)
        {
            foreach (var kv in rec.payoutByPlayer)
            {
                PlayerManager.Instance.RemoveMoney(kv.Key, kv.Value);
            }

            foreach (var kv in rec.destroyedByPlayer)
            {
                int restoreCount = kv.Value;

                if (kv.Key == buyerPid)
                {
                    restoreCount = Mathf.Max(0, restoreCount - 1);
                }

                if (restoreCount > 0)
                {
                    PlayerManager.Instance.AddStock(kv.Key, stock, restoreCount);
                }
            }

            stockPrices[stock] = Mathf.Clamp(rec.preBuyPrice, minPrice, maxPrice);
            OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);

            rec.active = false;
            _lastCeiling[stock] = rec;

            TurnManager.Instance.Server_SyncAllPlayers();
            TurnManager.Instance.Server_SyncStockPrice(stock);
            return;
        }

        stockPrices[stock] = Mathf.Clamp(stockPrices[stock] - 1, minPrice, maxPrice);
        OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);

        TurnManager.Instance.Server_SyncStockPrice(stock);
    }

    [Server]
    public void RevertOpenSell(StockType stock)
    {
        if (_lastBankruptcy.TryGetValue(stock, out var rec) && rec.active)
        {
            foreach (var kv in rec.destroyedByPlayer)
            {
                PlayerManager.Instance.AddStock(kv.Key, stock, kv.Value);
            }

            stockPrices[stock] = Mathf.Clamp(rec.preSalePrice, minPrice, maxPrice);
            OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);

            rec.active = false;
            _lastBankruptcy[stock] = rec;

            TurnManager.Instance.Server_SyncAllPlayers();
            TurnManager.Instance.Server_SyncStockPrice(stock);
        }
        else
        {
            stockPrices[stock] = Mathf.Clamp(stockPrices[stock] + 1, minPrice, maxPrice);
            OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);
        }
    }

    [Server]
    public void CheckBankruptcyAndCeilingAll()
    {
        foreach (var s in availableStocks)
        {
            CheckBankruptcy(s);
            CheckCeiling(s);
        }
    }

    [Server]
    private void CheckBankruptcy(StockType stock)
    {
        if (stockPrices[stock] <= minPrice)
        {
            foreach (var player in PlayerManager.Instance.players)
            {
                if (player.stocks.TryGetValue(stock, out var count) && count > 0)
                {
                    PlayerManager.Instance.RemoveStock(player.id, stock, count);
                }
            }

            stockPrices[stock] = startingPrice;

            TurnManager.Instance.Server_SyncAllPlayers();
            TurnManager.Instance.Server_SyncStockPrice(stock);
            TurnManager.Instance.Server_NotifyBankruptcy(stock);
        }
    }

    [Server]
    private void CheckCeiling(StockType stock)
    {
        if (stockPrices[stock] >= maxPrice)
        {
            foreach (var player in PlayerManager.Instance.players)
            {
                if (player.stocks.TryGetValue(stock, out var count) && count > 0)
                {
                    PlayerManager.Instance.AddMoney(player.id, startingPrice * count);
                    PlayerManager.Instance.RemoveStock(player.id, stock, count);
                }
            }

            stockPrices[stock] = startingPrice;

            TurnManager.Instance.Server_SyncAllPlayers();
            TurnManager.Instance.Server_SyncStockPrice(stock);
            TurnManager.Instance.Server_NotifyCeiling(stock);
        }
    }

    [Server]
    private void CheckBankruptcyAfterOpenSell(StockType stock, int preSalePrice)
    {
        if (stockPrices[stock] > minPrice) return;

        var destroyed = new Dictionary<int, int>();
        foreach (var pl in PlayerManager.Instance.players)
        {
            int cnt = pl.stocks.TryGetValue(stock, out var c) ? c : 0;
            if (cnt > 0) destroyed[pl.id] = cnt;
        }

        _lastBankruptcy[stock] = new BankruptcyRecord
        {
            active = true,
            preSalePrice = preSalePrice,
            destroyedByPlayer = destroyed
        };

        foreach (var kv in destroyed)
        {
            PlayerManager.Instance.RemoveStock(kv.Key, stock, kv.Value);
        }
            
        stockPrices[stock] = startingPrice;

        TurnManager.Instance.Server_SyncAllPlayers();
        TurnManager.Instance.Server_SyncStockPrice(stock);
        TurnManager.Instance.Server_NotifyBankruptcy(stock);
    }

    [Server]
    private void CheckCeilingAfterBuy(StockType stock, int preBuyPrice)
    {
        if (stockPrices[stock] < maxPrice) return;

        var destroyed = new Dictionary<int, int>();
        var payouts = new Dictionary<int, int>();
        foreach (var pl in PlayerManager.Instance.players)
        {
            int cnt = pl.stocks.TryGetValue(stock, out var c) ? c : 0;
            if (cnt > 0)
            {
                destroyed[pl.id] = cnt;
                payouts[pl.id] = startingPrice * cnt;
            }
        }

        _lastCeiling[stock] = new CeilingRecord
        {
            active = true,
            preBuyPrice = preBuyPrice,
            destroyedByPlayer = destroyed,
            payoutByPlayer = payouts
        };

        foreach (var kv in payouts)
        {
            PlayerManager.Instance.AddMoney(kv.Key, kv.Value);
        }

        foreach (var kv in destroyed)
        {
            PlayerManager.Instance.RemoveStock(kv.Key, stock, kv.Value);
        }
            
        stockPrices[stock] = startingPrice;

        TurnManager.Instance.Server_SyncAllPlayers();
        TurnManager.Instance.Server_SyncStockPrice(stock);
        TurnManager.Instance.Server_NotifyCeiling(stock);
    }

    public void RaiseStockPriceChanged(StockType stock, int newPrice)
    {
        OnStockPriceChanged?.Invoke(stock, newPrice);
    }

}
