using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class DeckManager : MonoBehaviour
{
    public static DeckManager Instance { get; private set; }

    // Manipulation deck
    private readonly Stack<ManipulationType> _manipDiscard = new();

    // Tax deck
    private readonly Stack<TaxType> _taxDiscard = new();

    // Runtime shuffled decks
    private List<ManipulationType> _manipRuntime;
    private List<TaxType> _taxRuntime;

    // Lottery
    [SerializeField] private int lotteryPool = 2;
    [SerializeField] private int lotteryIncreaseAmount = 2;

    // Events for UI or game logic
    public event Action<ManipulationType> OnManipulationCardDrawn;
    public event Action<TaxType> OnTaxCardDrawn;
    public event Action OnDecksReshuffled;
    public event Action<int> OnLotteryChanged;

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

    // Call once at game start
    public void SetupDecks()
    {
        _manipDiscard.Clear();
        _taxDiscard.Clear();

        _manipRuntime = BuildManipulationLibrary();
        _taxRuntime = BuildTaxLibrary();

        Shuffle(_manipRuntime);
        Shuffle(_taxRuntime);
        OnDecksReshuffled?.Invoke();
    }


    // Called at end of each round to return played cards and reshuffle
    public void CleanupRound()
    {
        while (_manipDiscard.Count > 0)
        {
            _manipRuntime.Add(_manipDiscard.Pop());
        }
            
        while (_taxDiscard.Count > 0)
        {
            _taxRuntime.Add(_taxDiscard.Pop());
        }
            
        Shuffle(_manipRuntime);
        Shuffle(_taxRuntime);
        OnDecksReshuffled?.Invoke();
    }


    // Draws the top manipulation card
    public ManipulationType DrawManipulationCard()
    {
        if (_manipRuntime.Count == 0)
        {
            SetupDecks(); // NEEDS TO CHANGE !!!
        }
        var card = _manipRuntime[0];
        _manipRuntime.RemoveAt(0);
        OnManipulationCardDrawn?.Invoke(card);
        return card;
    }

    // Draws the top tax card
    public TaxType DrawTaxCard()
    {
        if (_taxRuntime.Count == 0)
        {
            SetupDecks();  // NEEDS TO CHANGE !!!
        }
        var card = _taxRuntime[0];
        _taxRuntime.RemoveAt(0);
        OnTaxCardDrawn?.Invoke(card);
        return card;
    }

    public void IncreaseLottery()
    {
        lotteryPool += lotteryIncreaseAmount;
        OnLotteryChanged?.Invoke(lotteryPool);
    }

    // Give the entire lottery pool to caller and reset to 0.
    public int ClaimLottery()
    {
        int amount = lotteryPool;
        lotteryPool = 0;
        OnLotteryChanged?.Invoke(lotteryPool);
        return amount;
    }

    // Put money back into the lottery pool (undo support).
    public void RestoreLottery(int amount)
    {
        lotteryPool += amount;
        OnLotteryChanged?.Invoke(lotteryPool);
    }

    // Random stock deck (infinite for now)
    // Return a random stock from availableStocks.
    public StockType DrawRandomStock()
    {
        var pool = StockMarketManager.Instance.availableStocks;
        return pool[UnityEngine.Random.Range(0, pool.Count)];
    }

    // Placeholder for returning a stock to the random pool.
    public void ReturnStockToRandom(StockType stock)
    {
        // No-op for now (we aren’t tracking a finite deck yet).
        // When you implement a real deck, push 'stock' back into it here.
    }

    private List<ManipulationType> BuildManipulationLibrary()
    {
        return new List<ManipulationType> {
        // +1 x2, +2 x1, -1 x2, -2 x1, -3 x1, +4 x1, Dividend x2
        ManipulationType.Plus1, ManipulationType.Plus1,
        ManipulationType.Plus2,
        ManipulationType.Minus1, ManipulationType.Minus1,
        ManipulationType.Minus2,
        ManipulationType.Minus3,
        ManipulationType.Plus4,
        ManipulationType.Dividend, ManipulationType.Dividend
        };
    }
    private List<TaxType> BuildTaxLibrary()
    {
        return new List<TaxType> {
        TaxType.Red, TaxType.Blue, TaxType.Green, TaxType.Yellow
        };
    }

    public ManipulationType DrawManipulation() => DrawManipulationCard();

    public void DiscardManipulation(ManipulationType m) => _manipDiscard.Push(m);
    public void ReturnManipulationToDeck(ManipulationType m) { _manipRuntime.Add(m); }

    public void DiscardTax(TaxType t) => _taxDiscard.Push(t);
    public void ReturnTaxToDeck(TaxType t) { _taxRuntime.Add(t); }


    // Generic in-place Fisher–Yates shuffle
    private void Shuffle<T>(List<T> list) // Fisher–Yates shuffle
    {
        for (int i = 0; i < list.Count; i++)
        {
            int r = UnityEngine.Random.Range(i, list.Count);
            var tmp = list[i];
            list[i] = list[r];
            list[r] = tmp;
        }
    }
}