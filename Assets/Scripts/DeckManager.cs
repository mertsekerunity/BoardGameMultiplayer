using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
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
    [SerializeField] int lotteryPool = 0;
    [SerializeField] int lotteryIncreaseAmount = 2;

    public int LotteryPool => lotteryPool;

    // Events for UI or game logic
    public event Action<ManipulationType> OnManipulationCardDrawn;
    public event Action<TaxType> OnTaxCardDrawn;
    public event Action OnDecksReshuffled;

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

    // Call once at game start
    [Server]
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
    [Server]
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
    [Server]
    public ManipulationType DrawManipulationCard()
    {
        if (_manipRuntime.Count == 0)
        {
            SetupDecks();
        }
        var card = _manipRuntime[0];
        _manipRuntime.RemoveAt(0);
        OnManipulationCardDrawn?.Invoke(card);
        return card;
    }

    // Draws the top tax card
    [Server]
    public TaxType DrawTaxCard()
    {
        if (_taxRuntime.Count == 0)
        {
            SetupDecks();
        }
        var card = _taxRuntime[0];
        _taxRuntime.RemoveAt(0);
        OnTaxCardDrawn?.Invoke(card);
        return card;
    }

    [Server]
    public void IncreaseLottery()
    {
        lotteryPool += lotteryIncreaseAmount;
    }

    [Server]
    public int ClaimLottery()
    {
        int amount = lotteryPool;
        lotteryPool = 0;
        return amount;
    }

    [Server]
    public void RestoreLottery(int amount)
    {
        lotteryPool += amount;
    }

    // Random stock deck (infinite for now)
    [Server]
    public StockType DrawRandomStock()
    {
        var pool = StockMarketManager.Instance.availableStocks;
        return pool[UnityEngine.Random.Range(0, pool.Count)];
    }

    // Placeholder for returning a stock to the random pool.
    [Server]
    public void ReturnStockToRandom(StockType stock)
    {
        
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

    [Server]
    public ManipulationType DrawManipulation() => DrawManipulationCard();

    [Server]
    public void DiscardManipulation(ManipulationType m) => _manipDiscard.Push(m);

    [Server]
    public void ReturnManipulationToDeck(ManipulationType m) { _manipRuntime.Add(m); }

    [Server]
    public void DiscardTax(TaxType t) => _taxDiscard.Push(t);

    [Server]
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