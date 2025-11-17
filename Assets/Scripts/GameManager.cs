using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;

public class GameManager : NetworkBehaviour
{
    public static GameManager Instance { get; private set; }

    [SerializeField] int requiredPlayers = 4;
    private int maxRound = 7;

    private int currentRound = 1;

    [SerializeField] private TextMeshProUGUI roundText;
    [SerializeField] private TextMeshProUGUI winnerText;

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

    [Server]
    public void TryStartGame()
    {
        int connectedPlayers = NetworkServer.connections.Count;

        if (connectedPlayers < requiredPlayers)
        {
            Debug.Log($"[GM] Not starting yet. Players={connectedPlayers}/{requiredPlayers}");
            return;
        }

        Debug.Log("[GM] All players joined, initializing game.");
        InitializeGame();
    }

    [Server]
    private void InitializeGame()
    {
        int playerCount = requiredPlayers;

        // Set up core systems
        StockMarketManager.Instance.SetupMarket(playerCount);
        DeckManager.Instance.SetupDecks();
        TurnManager.Instance.SetupTurnOrder();

        // Now all game data is ready -> tell clients to build UI
        //RpcInitClientUI(playerCount);

        SendInitialPlayerSnapshotToClients();

        // Start the first round
        StartRound();
    }

    [Server]
    private void SendInitialPlayerSnapshotToClients()
    {
        var players = PlayerManager.Instance.players;
        int count = players.Count;

        int[] ids = new int[count];
        string[] names = new string[count];
        int[] money = new int[count];

        for (int i = 0; i < count; i++)
        {
            ids[i] = players[i].id;
            names[i] = players[i].playerName;
            money[i] = players[i].money;
        }

        var sm = StockMarketManager.Instance;

        var stocksList = sm.availableStocks;
        StockType[] stocksArr = stocksList.ToArray();

        int[] pricesArr = new int[stocksArr.Length];
        for (int i = 0; i < stocksArr.Length; i++)
        {
            pricesArr[i] = sm.stockPrices[stocksArr[i]];
        }

        RpcInitClientUI(ids, names, money, stocksArr, pricesArr);
    }

    [ClientRpc]
    public void RpcInitClientUI(
        int[] ids,
        string[] names,
        int[] money,
        StockType[] stocks,
        int[] prices)
    {
        var sm = StockMarketManager.Instance;

        sm.availableStocks = stocks.ToList();

        sm.stockPrices = new Dictionary<StockType, int>();
        for (int i = 0; i < stocks.Length; i++)
        {
            sm.stockPrices[stocks[i]] = prices[i];
        }

        UIManager.Instance.InitializeGameUI(ids, names, money);
    }


    [Server]
    public void StartRound()
    {
        roundText.text = currentRound.ToString();
        TurnManager.Instance.RoundStartReset();
        TurnManager.Instance.StartDiscardPhase();

        if (currentRound > 1)
        {
            TurnManager.Instance.BiddingFinished -= OnBiddingFinished;
            TurnManager.Instance.BiddingFinished += OnBiddingFinished;
            TurnManager.Instance.StartBiddingPhase();
            DeckManager.Instance.IncreaseLottery();
            return;
        }

        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.SelectionFinished += OnSelectionFinished;
        TurnManager.Instance.StartCharacterSelectionPhase();
    }

    // TurnManager.MainPhaseLoop (server) calls ResolveEndOfRound() and then:
    // GameManager.Instance.EndRound();

    [Server]
    public void EndRound()
    {
        // Cleanup round-specific data
        TurnManager.Instance.CleanupRound();
        DeckManager.Instance.CleanupRound();
        UIManager.Instance.ClearAllMarketSecretTags();

        // Advance round counter and begin the next round
        currentRound++;

        if(currentRound <= maxRound)
        {
            StartRound();
        }
        else
        {
            PlayerManager.Instance.SettleRemainingHoldingsToCash();
            DecideWinnerAndShow();
        }
    }

    [Server]
    private void DecideWinnerAndShow()
    {
        var players = PlayerManager.Instance.players;

        if (players.Count == 0) return;

        var winner = players[0];
        List<PlayerManager.Player> tied = new();

        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];

            if (p.money > winner.money)
            {
                winner = p;
                tied.Clear();
            }
            else if (p.money == winner.money && p != winner)
            {
                tied.Add(p);
            }
        }

        // Tie-breaker: lower total bid spend wins? (Or whatever your logic is)
        if (tied.Count > 0)
        {
            foreach (var p in tied)
            {
                if (winner.money == p.money)
                {
                    if (TurnManager.Instance.GetTotalBidSpend(p.id) >
                        TurnManager.Instance.GetTotalBidSpend(winner.id))
                    {
                        winner = p;
                    }
                }
            }
        }

        if (winnerText != null)
        {
            winnerText.text = $"Game is finished. The winner is {winner.playerName}";
            winnerText.gameObject.SetActive(true);
        }
    }

    [Server]
    private void OnBiddingFinished()
    {
        TurnManager.Instance.BiddingFinished -= OnBiddingFinished;
        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.SelectionFinished += OnSelectionFinished;
        TurnManager.Instance.StartCharacterSelectionPhase();
    }

    [Server]
    private void OnSelectionFinished()
    {
        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.StartMainPhase();
    }
}