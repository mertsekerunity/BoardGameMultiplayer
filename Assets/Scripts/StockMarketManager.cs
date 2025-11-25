using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class StockMarketManager : MonoBehaviour
{
    public static StockMarketManager Instance { get; private set; }

    // Active stocks this game
    public List<StockType> availableStocks;
    // Current market prices
    public Dictionary<StockType, int> stockPrices;

    // Events (UI can subscribe)
    public event Action<StockType, int> OnStockPriceChanged;
    public event Action<StockType> OnStockBankrupt;
    public event Action<StockType> OnStockCeilingHit;

    [Header("Price Bounds")]
    public int minPrice = 0;
    public int maxPrice = 8;
    public int startingPrice = 4;

    private struct CloseSale
    {
        public int playerId;
        public StockType stock;
        public int price; // anchored gain locked on seller’s turn
        public int basePriceAtQueue;
    }
    private readonly List<CloseSale> _pendingCloseSales = new();

    private struct BankruptcyRecord
    {
        public bool active;
        public int preSalePrice; // price just before the open sale
        public Dictionary<int, int> destroyedByPlayer; // pid -> count destroyed
    }
    private readonly Dictionary<StockType, BankruptcyRecord> _lastBankruptcy = new();

    private struct CeilingRecord
    {
        public bool active;
        public int preBuyPrice;                          // price just before the buy
        public Dictionary<int, int> destroyedByPlayer;   // pid -> count destroyed
        public Dictionary<int, int> payoutByPlayer;      // pid -> cash paid for ceiling
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
        //DontDestroyOnLoad(transform.root.gameObject);
    }

    // Initialize market with 3 stocks for <=4 players, else 4 stocks
    public void SetupMarket(int playerCount)
    {
        availableStocks = (playerCount <= 4)
            ? new List<StockType> { StockType.Red, StockType.Blue, StockType.Green }
            : new List<StockType> { StockType.Red, StockType.Blue, StockType.Green, StockType.Yellow };

        stockPrices = availableStocks.ToDictionary(s => s, _ => startingPrice);

        // Notify initial prices
        foreach (var s in availableStocks)
            OnStockPriceChanged?.Invoke(s, startingPrice);
    }

    // Convenience
    public int GetPrice(StockType stock) => stockPrices.TryGetValue(stock, out var p) ? p : startingPrice;

    // Immediate buy: +1 and ceiling check
    public void BuyStock(StockType stock)
    {
        int before = stockPrices[stock]; // pre-buy price
        int after = Mathf.Clamp(before + 1, minPrice, maxPrice);
        stockPrices[stock] = after;
        OnStockPriceChanged?.Invoke(stock, after);

        CheckCeilingAfterBuy(stock, before); // << recordable path for Undo
    }

    // Immediate open sale: -1 and bankruptcy check
    public void SellStock(StockType stock, bool openSale)
    {
        if (openSale)
        {
            int before = stockPrices[stock]; // pre-sale price
            int after = Mathf.Clamp(before - 1, minPrice, maxPrice);
            stockPrices[stock] = after;
            OnStockPriceChanged?.Invoke(stock, after);

            CheckBankruptcyAfterOpenSell(stock, before); // << pass pre-sale
        }
        // else: close sale handled at end of round via queue
    }

    // Queue a close sale to resolve end-of-round
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

    // Undo helper
    public bool RemoveQueuedCloseSale(int playerId, StockType stock, int anchoredGain)
    {
        int idx = _pendingCloseSales.FindIndex(cs =>
            cs.playerId == playerId && cs.stock == stock && cs.price == anchoredGain);
        if (idx >= 0)
        {
            _pendingCloseSales.RemoveAt(idx);
            return true;
        }
        return false;
    }

    // End-of-round: pay anchored gains and tick market down per unit
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

    public void AdjustPrice(StockType stock, int delta)
    {
        stockPrices[stock] = Mathf.Clamp(stockPrices[stock] + delta, minPrice, maxPrice);
        OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);
        CheckBankruptcy(stock);
        CheckCeiling(stock);
    }

    // Reverts for Undo (guard with clamps)
    public void RevertBuy(StockType stock)
    {
        // If that buy had triggered a ceiling, fully roll it back
        if (_lastCeiling.TryGetValue(stock, out var rec) && rec.active)
        {
            // 1) Take back money paid to players
            foreach (var kv in rec.payoutByPlayer)
                PlayerManager.Instance.RemoveMoney(kv.Key, kv.Value); // clamps to >= 0 in your PM

            // 2) Restore destroyed holdings
            foreach (var kv in rec.destroyedByPlayer)
                PlayerManager.Instance.AddStock(kv.Key, stock, kv.Value);

            // 3) Restore exact pre-buy price (e.g., 7), not starting-1
            stockPrices[stock] = Mathf.Clamp(rec.preBuyPrice, minPrice, maxPrice);
            OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);

            // mark record consumed
            rec.active = false;
            _lastCeiling[stock] = rec;
            return;
        }

        // Normal undo when no ceiling was triggered
        stockPrices[stock] = Mathf.Clamp(stockPrices[stock] - 1, minPrice, maxPrice);
        OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);
    }

    public void RevertOpenSell(StockType stock)
    {
        if (_lastBankruptcy.TryGetValue(stock, out var rec) && rec.active)
        {
            // Restore destroyed holdings to every player
            foreach (var kv in rec.destroyedByPlayer)
                PlayerManager.Instance.AddStock(kv.Key, stock, kv.Value);

            // Restore price to the *pre-sale* value (e.g., 1), not starting+1
            stockPrices[stock] = Mathf.Clamp(rec.preSalePrice, minPrice, maxPrice);
            OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);

            // Mark record as consumed
            rec.active = false;
            _lastBankruptcy[stock] = rec;
        }
        else
        {
            // Normal undo path (no bankruptcy was triggered)
            stockPrices[stock] = Mathf.Clamp(stockPrices[stock] + 1, minPrice, maxPrice);
            OnStockPriceChanged?.Invoke(stock, stockPrices[stock]);
        }
    }

    // Batch check for end-of-round hooks
    public void CheckBankruptcyAndCeilingAll()
    {
        foreach (var s in availableStocks)
        {
            CheckBankruptcy(s);
            CheckCeiling(s);
        }
    }

    // If price hits zero, announce bankruptcy, destroy holdings, reset to starting
    private void CheckBankruptcy(StockType stock)
    {
        if (stockPrices[stock] <= minPrice)
        {
            OnStockBankrupt?.Invoke(stock);

            // Remove all holdings of this stock from all players
            foreach (var player in PlayerManager.Instance.players)
            {
                if (player.stocks.TryGetValue(stock, out var count) && count > 0)
                    PlayerManager.Instance.RemoveStock(player.id, stock, count);
            }

            stockPrices[stock] = startingPrice;
            UIManager.Instance.HidePrompt();
            OnStockPriceChanged?.Invoke(stock, startingPrice);
        }
    }

    // If price hits ceiling, pay startingPrice per card, destroy holdings, reset to starting
    private void CheckCeiling(StockType stock)
    {
        if (stockPrices[stock] >= maxPrice)
        {
            OnStockCeilingHit?.Invoke(stock);

            foreach (var player in PlayerManager.Instance.players)
            {
                if (player.stocks.TryGetValue(stock, out var count) && count > 0)
                {
                    PlayerManager.Instance.AddMoney(player.id, startingPrice * count);
                    PlayerManager.Instance.RemoveStock(player.id, stock, count);
                }
            }

            stockPrices[stock] = startingPrice;
            UIManager.Instance.HidePrompt();
            OnStockPriceChanged?.Invoke(stock, startingPrice);
        }
    }

    private void CheckBankruptcyAfterOpenSell(StockType stock, int preSalePrice)
    {
        if (stockPrices[stock] > minPrice) return;

        // Snapshot how many cards each player currently holds (after the sold card was removed)
        var destroyed = new Dictionary<int, int>();
        foreach (var pl in PlayerManager.Instance.players)
        {
            int cnt = pl.stocks.TryGetValue(stock, out var c) ? c : 0;
            if (cnt > 0) destroyed[pl.id] = cnt;
        }

        // Store record so Undo can put everything back and restore the exact price
        _lastBankruptcy[stock] = new BankruptcyRecord
        {
            active = true,
            preSalePrice = preSalePrice,
            destroyedByPlayer = destroyed
        };

        // Destroy holdings
        foreach (var kv in destroyed)
            PlayerManager.Instance.RemoveStock(kv.Key, stock, kv.Value);

        // Reset price
        stockPrices[stock] = startingPrice;
        OnStockPriceChanged?.Invoke(stock, startingPrice);
        OnStockBankrupt?.Invoke(stock);
    }

    private void CheckCeilingAfterBuy(StockType stock, int preBuyPrice)
    {
        if (stockPrices[stock] < maxPrice) return;

        // Snapshot holdings to destroy and payouts to grant
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

        // Record so Undo can restore both cards and money + restore price precisely
        _lastCeiling[stock] = new CeilingRecord
        {
            active = true,
            preBuyPrice = preBuyPrice,
            destroyedByPlayer = destroyed,
            payoutByPlayer = payouts
        };

        // Execute ceiling effects now
        foreach (var kv in payouts)
            PlayerManager.Instance.AddMoney(kv.Key, kv.Value);

        foreach (var kv in destroyed)
            PlayerManager.Instance.RemoveStock(kv.Key, stock, kv.Value);

        stockPrices[stock] = startingPrice;
        OnStockPriceChanged?.Invoke(stock, startingPrice);
        OnStockCeilingHit?.Invoke(stock);
    }

    public void RaiseStockPriceChanged(StockType stock, int newPrice)
    {
        OnStockPriceChanged?.Invoke(stock, newPrice);
    }

}
