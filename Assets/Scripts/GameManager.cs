using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;


public class GameManager : NetworkBehaviour
{
    public static GameManager Instance { get; private set; }

    private int requiredPlayers;
    private int maxRound = 7;

    private int currentRound = 1;


    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    [Server]
    public void TryStartGame()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        if (nm != null)
        {
            requiredPlayers = nm.requiredPlayers;
        }

        int connectedPlayers = NetworkServer.connections.Count;

        if (connectedPlayers < requiredPlayers)
        {
            return;
        }

        InitializeGame();
    }

    [Server]
    private void InitializeGame()
    {
        int playerCount = requiredPlayers;

        StockMarketManager.Instance.SetupMarket(playerCount);
        PlayerManager.Instance.Server_GiveInitialStocks(3);
        DeckManager.Instance.SetupDecks();
        TurnManager.Instance.SetupTurnOrder();

        SendInitialPlayerSnapshotToClients();

        for (int pid = 0; pid < PlayerManager.Instance.players.Count; pid++)
        {
            TurnManager.Instance.Server_SyncPlayerState(pid);
        }

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
        var ui = UIManager.Instance;

        if (sm == null || ui == null) return;

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
        TurnManager.Instance.RpcShowGlobalBanner($"Round {currentRound} started.");
        DeckManager.Instance.IncreaseLottery();
        TurnManager.Instance.RoundStartReset();
        TurnManager.Instance.RpcShowGlobalBanner("Discard phase started.");
        TurnManager.Instance.StartDiscardPhase();

        if (currentRound > 1)
        {
            TurnManager.Instance.BiddingFinished -= OnBiddingFinished;
            TurnManager.Instance.BiddingFinished += OnBiddingFinished;
            TurnManager.Instance.RpcShowGlobalBanner("Bidding phase started.");
            TurnManager.Instance.StartBiddingPhase();
            
            return;
        }
        
        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.SelectionFinished += OnSelectionFinished;
        Server_SyncRoundAndLottery();
        Server_SyncHideMarketTags();
        TurnManager.Instance.RpcShowGlobalBanner("Character selection phase started.");
        TurnManager.Instance.StartCharacterSelectionPhase();
    }

    [Server]
    public void EndRound()
    {
        TurnManager.Instance.CleanupRound();
        DeckManager.Instance.CleanupRound();

        currentRound++;

        if(currentRound <= maxRound)
        {
            StartRound();
        }
        else
        {
            PlayerManager.Instance.SettleRemainingHoldingsToCash();
            Server_SyncEndGame();
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

        RpcShowWinner(winner.playerName, winner.money);
    }

    [Server]
    private void OnBiddingFinished()
    {
        TurnManager.Instance.BiddingFinished -= OnBiddingFinished;
        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.SelectionFinished += OnSelectionFinished;
        Server_SyncRoundAndLottery();
        Server_SyncHideMarketTags();
        TurnManager.Instance.RpcShowGlobalBanner("Character selection phase started.");
        TurnManager.Instance.StartCharacterSelectionPhase();
    }

    [Server]
    private void OnSelectionFinished()
    {
        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.RpcShowGlobalBanner("Main phase started.");
        TurnManager.Instance.StartMainPhase();
    }

    [Server]
    public void Server_SyncRoundAndLottery()
    {
        int round = currentRound;
        int lottery = DeckManager.Instance.LotteryPool;

        RpcSyncRoundAndLottery(round, lottery);
    }

    [Server]
    public void Server_SyncPlayerName(int pid, string newName)
    {
        RpcSyncPlayerName(pid, newName);
    }

    [ClientRpc]
    private void RpcSyncPlayerName(int pid, string newName)
    {
        UIManager.Instance.UpdatePlayerName(pid, newName);
    }

    [ClientRpc]
    private void RpcSyncRoundAndLottery(int round, int lottery)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.SetRoundNumber(round);
        UIManager.Instance.SetLotteryAmount(lottery);
    }

    [Server]
    public void Server_SyncHideMarketTags()
    {
        RpcSyncHideMarketTags();
    }

    [ClientRpc]
    public void RpcSyncHideMarketTags()
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.ClearAllMarketSecretTags();
    }

    [ClientRpc]
    private void RpcShowWinner(string winnerName, int winnerMoney)
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.ShowWinner(winnerName, winnerMoney);
    }

    [Server]
    private void Server_SyncEndGame()
    {
        for (int pid = 0; pid < PlayerManager.Instance.players.Count; pid++)
        {
            int money = PlayerManager.Instance.players[pid].money;
            var stocks = PlayerManager.Instance.players[pid].stocks.ToList();
            int[] stockIds = stocks.Select(kv => (int)kv.Key).ToArray();
            int[] stockCounts = stocks.Select(kv => kv.Value).ToArray();

            RpcSyncEndGame(pid, money, stockIds, stockCounts);
        }
    }

    [ClientRpc]
    private void RpcSyncEndGame(int pid, int money, int[] stockTypeIds, int[] stockCounts)
    {
        if (UIManager.Instance == null) return;

        var stocks = new Dictionary<StockType, int>();

        for (int i = 0; i < stockTypeIds.Length && i < stockCounts.Length; i++)
        {
            stocks[(StockType)stockTypeIds[i]] = stockCounts[i];
        }

        UIManager.Instance.SyncEndGame(pid, money, stocks);
    }
}